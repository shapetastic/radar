using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Ai;
using Radar.Application.Collectors;
using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Filings;
using Radar.Application.Pipeline;
using Radar.Application.Prices;
using Radar.Application.Replay;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Domain.Signals;
using Radar.Infrastructure.Ai;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.Fda;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Gdelt;
using Radar.Infrastructure.Hiring;
using Radar.Infrastructure.News;
using Radar.Infrastructure.Patents;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.Infrastructure.Prices;
using Radar.Infrastructure.Replay;
using Radar.Infrastructure.Rss;
using Radar.Infrastructure.Sec;
using Radar.Infrastructure.Sources;
using Radar.Infrastructure.Trademarks;
using Radar.Infrastructure.UsaSpending;

using System.Globalization;
using System.Net;

namespace Radar.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the thread-safe in-memory repository implementations as singletons so the
    /// in-memory store persists for the lifetime of the run. Intended for deterministic tests
    /// and local pipeline runs; the Postgres/Dapper repositories arrive in a later task behind
    /// the same interfaces.
    /// </summary>
    public static IServiceCollection AddInMemoryRadarPersistence(this IServiceCollection services)
    {
        services.AddSingleton<IEvidenceRepository, InMemoryEvidenceRepository>();
        services.AddSingleton<ICompanyRepository, InMemoryCompanyRepository>();
        services.AddSingleton<ISignalRepository, InMemorySignalRepository>();
        services.AddSingleton<ISignalReviewRepository, InMemorySignalReviewRepository>();
        services.AddSingleton<IScoreRepository, InMemoryScoreRepository>();
        services.AddSingleton<IReportRepository, InMemoryReportRepository>();
        return services;
    }

    /// <summary>
    /// Registers the stateless application services as singletons: the deterministic
    /// <see cref="Radar.Application.EntityResolution.ICompanyResolver"/> and the deterministic
    /// keyword-based <see cref="Radar.Application.SignalExtraction.ISignalExtractor"/>
    /// (<see cref="KeywordSignalExtractor"/>). The resolver only depends on the singleton
    /// repositories and the extractor is dependency-free, so a singleton lifetime is correct and
    /// lets singleton consumers (e.g. a hosted service) resolve them from the root provider.
    /// Requires <see cref="AddInMemoryRadarPersistence"/> (or another registration of the
    /// repositories) to have been called for the resolver's dependencies.
    /// </summary>
    public static IServiceCollection AddRadarApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<ICompanyResolver, CompanyResolver>();
        services.AddSingleton<ISignalExtractor, KeywordSignalExtractor>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISignalReviewer, DeterministicSignalReviewer>();
        // Attention source-quality tiering (spec 88): register the curated default tier map BEFORE the formula
        // so a composition root that bound its own Radar:Attention options via AddSingleton wins over this
        // default (mirrors the ScoringOptions pattern). ConfiguredAttentionSourceWeights validates the bound
        // options in its ctor (fails fast on a weight outside [0,1]).
        services.TryAddSingleton(AttentionSourceTierOptions.Default);
        services.TryAddSingleton<IAttentionSourceWeights, ConfiguredAttentionSourceWeights>();
        // Scoring magnitude weights (spec 89): the default == the radar-formula-v4 constants, so a blank/absent
        // config yields byte-identical v4 output. TryAdd keeps a composition-root-registered concrete
        // ScoringWeights (bound via AddRadarScoringWeights) winning over this default (mirrors ScoringOptions /
        // AttentionSourceTierOptions).
        services.TryAddSingleton(new ScoringWeights());
        // Formula composition seam (spec 137): the formula HOLDS its magnitudes, so "one engine per strategy"
        // implies "one formula per strategy" — a single IScoreFormula singleton could only ever express one
        // strategy's weights. The human-owned formula boundary is unchanged (IScoreFormula keeps its exact
        // contract and RadarScoreFormulaV8 is still the only place scoring math lives); only how a formula is
        // OBTAINED moved behind a factory. TryAdd lets a composition root substitute its own factory.
        services.TryAddSingleton<IScoreFormulaFactory, RadarScoreFormulaFactory>();
        services.TryAddSingleton(new ScoringOptions());
        // Insider materiality magnitudes (spec 96): the default == the spec-93 buy/sell tiers + cluster boost,
        // so a blank/absent config yields byte-identical insider Strengths. TryAdd keeps a
        // composition-root-registered concrete InsiderMaterialityWeights (bound via AddRadarInsiderMateriality)
        // winning over this default (mirrors ScoringWeights). Injected into KeywordSignalExtractor and folded
        // into the ScoringConfigVersion fingerprint (via ScoringEngine).
        services.TryAddSingleton(new InsiderMaterialityWeights());
        // Same-event media-attention collapse (spec 109): the default window (3 days) collapses many
        // near-simultaneous outlets covering ONE event to a single representative MediaAttention signal before
        // scoring. TryAdd keeps a composition-root-registered concrete MediaCollapseOptions (bound via
        // AddRadarMediaCollapse) winning over this default (mirrors ScoringWeights). Folded into the
        // ScoringConfigVersion fingerprint (via ScoringEngine) so the window is re-stamped by value.
        services.TryAddSingleton(new MediaCollapseOptions());
        services.TryAddSingleton<MediaAttentionCollapse>();
        // Enabled-collector VOCABULARY (spec 147): the collector NAMES, with no capacity to collect. The
        // library-only default derives them from whatever collectors this composition registered — resolved
        // lazily INSIDE the factory, so it still sees collectors registered after this call. The Worker
        // registers the CONFIG-derived vocabulary BEFORE this method instead, which is what gives a
        // spec-144 score pass (zero registered collectors, deliberately) a truthful collector set.
        services.TryAddSingleton(sp => EnabledCollectorVocabulary.FromCollectors(
            sp.GetRequiredService<IEnumerable<IEvidenceCollector>>()));
        // Whether collection happened in THIS pass (spec 147) — a different fact from the vocabulary above.
        // The default (Collected) keeps every existing composition's provenance string byte-identical; the
        // Worker registers NoCollectionThisPass for the standalone score pass only.
        services.TryAddSingleton(new CollectionPassOptions());
        // Signal-source descriptor (spec 95, split by spec 141): folds the extractor rule-set identity into
        // the ScoringConfigVersion fingerprint and exposes the enabled collector NAMES separately as
        // CollectionProvenance — recorded on every snapshot, hashed into nothing, so a collector toggle no
        // longer re-stamps a strategy. The optional AI directional-filing source (spec 106) IS folded into
        // the identity when registered — its per-signal magnitudes contribute an ai=… segment, so enabling
        // the AI path (and tuning Strength/Novelty/MinConfidence/model) re-stamps the fingerprint
        // automatically; it is null (AI off) => byte-identical AI-off descriptor. Every dependency is
        // lazy-resolved INSIDE the factory (RESOLUTION time), so this sees the vocabulary AND the AI source
        // even though the Worker registers the AI seam AFTER AddRadarApplicationServices. TryAdd lets a
        // composition root substitute its own descriptor.
        services.TryAddSingleton<ISignalSourceDescriptor>(sp => new SignalSourceDescriptor(
            sp.GetRequiredService<EnabledCollectorVocabulary>(),
            sp.GetService<IDirectionalFilingSignalSource>(),
            sp.GetRequiredService<CollectionPassOptions>()));
        // Multi-strategy scoring (spec 137). One ScoringEngine instance IS one strategy (it resolves its whole
        // effective config + fingerprint once in its constructor), so plural strategies are purely a
        // COMPOSITION concern: the factory builds one engine per strategy over the SAME shared collection
        // pass. The default strategy set is the single synthesised "default" strategy carrying whatever
        // ScoringWeights are registered — i.e. byte-identical to the previous single-engine composition,
        // including the pinned default fingerprints. TryAdd lets AddRadarScoringStrategies (called from the
        // composition root BEFORE this method) register the config-bound set instead.
        services.TryAddSingleton(sp => ScoringStrategySet.SingleDefault(sp.GetRequiredService<ScoringWeights>()));
        // Per-strategy score repository: the primary keeps the shared registered IScoreRepository (the one the
        // weekly report reads), non-primary strategies get their own — see the type's remarks for why that
        // isolation is load-bearing rather than tidiness.
        services.TryAddSingleton<IScoreRepositoryFactory, StrategyScopedScoreRepositoryFactory>();
        services.TryAddSingleton<IScoringStrategyFactory, ScoringStrategyFactory>();
        // IScoringEngine stays resolvable and means exactly what it always did: THE engine the pipeline scores
        // the reported series with — now sourced from the primary strategy's runtime so there is only ever one
        // primary engine instance in the graph (no dormant second engine that silently diverges from it).
        services.AddSingleton<IScoringEngine>(sp =>
            sp.GetRequiredService<IScoringStrategyFactory>().Primary.Engine);
        services.TryAddSingleton<IReportActionPolicy, WeeklyReportActionPolicyV1>();
        services.TryAddSingleton<IWeeklyReportRenderer, MarkdownWeeklyReportRenderer>();
        services.TryAddSingleton(new WeeklyReportOptions());
        // Collection-health validation (spec 98): reconciles seed-declared vs reached feed-type
        // inventory and warns on shrinkage (regression guard for the spec-97 feed-Id collision).
        // Diagnostic only — never evidence/signal/scoring input. Depends on ICompanySeedSource
        // (registered by AddLocalFileCompanySeed).
        services.TryAddSingleton<ICollectionHealthValidator, SeedFeedInventoryValidator>();
        services.AddSingleton<IWeeklyReportBuilder, WeeklyReportBuilder>();
        // The mapper is a core pipeline service used regardless of which collector is wired, so its
        // IEvidenceNormalizer dependency is registered here. TryAdd keeps a collector-specific
        // registration (e.g. AddLocalFileCollector) from conflicting.
        services.TryAddSingleton<IEvidenceNormalizer, EvidenceNormalizer>();
        services.AddSingleton<CollectedEvidenceMapper>();
        return services;
    }

    /// <summary>
    /// Resolves the effective scoring-weight profile and registers the concrete <see cref="ScoringWeights"/>
    /// as a singleton so it wins over the library's <c>TryAddSingleton</c> default (call this BEFORE
    /// <see cref="AddRadarApplicationServices"/>, mirroring the <c>Radar:Attention</c> binding). Precedence:
    /// <list type="bullet">
    /// <item><c>Radar:Scoring:Profile</c> selects a named profile; blank/absent ⇒ <c>"default"</c>.</item>
    /// <item>If <c>Radar:Scoring:Profiles:{name}</c> exists, its present fields bind ONTO a fresh
    /// <see cref="ScoringWeights"/> (unspecified fields keep the code default == v4).</item>
    /// <item>A <b>named</b> (non-default) profile that is requested but absent <b>fails fast</b> — a silent
    /// fallthrough to defaults would mask a typo'd profile name in an experiment.</item>
    /// <item>A blank/absent profile, or an absent <c>"default"</c> profile, ⇒ all code defaults
    /// (⇒ byte-identical v4 output and the pinned default fingerprint).</item>
    /// </list>
    /// The resolved weights are validated (<see cref="ScoringWeights.Validate"/>) so an out-of-range weight
    /// (e.g. <c>OpportunityAttentionDivisor = 0</c>) fails fast at registration, never silently distorting
    /// scoring.
    /// </summary>
    public static IServiceCollection AddRadarScoringWeights(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var resolved = ResolveScoringProfile(
            configuration, configuration["Radar:Scoring:Profile"], "Radar:Scoring:Profile");

        services.AddSingleton(resolved.Weights);
        return services;
    }

    /// <summary>
    /// Resolves ONE named scoring-weight profile from configuration — the single shared implementation behind
    /// both <see cref="AddRadarScoringWeights"/> (the ambient <c>Radar:Scoring:Profile</c>) and
    /// <see cref="AddRadarScoringStrategies"/> (each strategy's <c>ScoringProfile</c>), so the two can never
    /// drift apart. Precedence and fail-fast behaviour are documented on <see cref="AddRadarScoringWeights"/>;
    /// <paramref name="requestingConfigKey"/> only names the offending key in the thrown message.
    /// </summary>
    private static (string EffectiveProfile, ScoringWeights Weights) ResolveScoringProfile(
        IConfiguration configuration, string? requestedProfile, string requestingConfigKey)
    {
        var effectiveName = string.IsNullOrWhiteSpace(requestedProfile) ? "default" : requestedProfile.Trim();
        var section = configuration.GetSection($"Radar:Scoring:Profiles:{effectiveName}");

        ScoringWeights weights;
        if (section.Exists())
        {
            weights = section.Get<ScoringWeights>() ?? new ScoringWeights();
        }
        else if (!string.IsNullOrWhiteSpace(requestedProfile)
            && !string.Equals(effectiveName, "default", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{requestingConfigKey} '{effectiveName}' was requested but no matching profile exists under "
                    + "Radar:Scoring:Profiles — a named-but-missing profile is almost certainly a typo. Add the "
                    + $"profile under Radar:Scoring:Profiles:{effectiveName} or clear {requestingConfigKey} to use "
                    + "the code defaults.");
        }
        else
        {
            weights = new ScoringWeights();
        }

        // Fail fast at registration on a nonsensical weight (also enforced in the formula ctor).
        weights.Validate();

        return (effectiveName, weights);
    }

    /// <summary>
    /// Resolves the <b>scoring strategies</b> (spec 137) — the N independently-stamped scorings a single
    /// collection pass feeds — and registers the concrete <see cref="ScoringStrategySet"/> as a singleton so
    /// it wins over the library's <c>TryAddSingleton</c> default (call this BEFORE
    /// <see cref="AddRadarApplicationServices"/>, mirroring <see cref="AddRadarScoringWeights"/>).
    /// <code language="jsonc">
    /// "Radar": {
    ///   "Strategies": [ { "Name": "baseline",  "ScoringProfile": "default" },
    ///                   { "Name": "low-media", "ScoringProfile": "low-media" },
    ///                   // Spec 138: an optional per-strategy signal-type set. Omitted/empty ⇒ ALL types.
    ///                   { "Name": "insider-only", "ScoringProfile": "default",
    ///                     "SignalTypes": [ "InsiderBuying" ] },
    ///                   // Spec 146: an optional per-strategy formula + channel budget. Omitted ⇒ v8, no channels.
    ///                   { "Name": "patents-led", "Formula": "radar-formula-v9",
    ///                     "Channels": [
    ///                       { "Name": "patents",   "Collectors": [ "patents" ],   "Weight": 0.50, "Saturation": 3 },
    ///                       { "Name": "insider",   "Collectors": [ "sec-form4" ], "Weight": 0.30, "Saturation": 2 },
    ///                       { "Name": "attention", "Kind": "breadth",             "Weight": 0.20, "Saturation": 3 } ] },
    ///                   // Spec 149: inline per-strategy weight overrides, applied ON TOP of ScoringProfile.
    ///                   { "Name": "attention-light", "ScoringProfile": "default",
    ///                     "Weights": { "FollowingTierDiscountWeight": 0.0,
    ///                                  "OpportunityAttentionDiscountWeight": 0.25 } } ],
    ///   "PrimaryStrategy": "baseline"
    /// }
    /// </code>
    /// <list type="bullet">
    /// <item><b>Absent or empty <c>Radar:Strategies</c></b> ⇒ exactly ONE synthesised strategy named
    /// <c>"default"</c>, carrying the weights of the ambient <c>Radar:Scoring:Profile</c> and treated as
    /// primary. This is what makes behaviour <b>byte-identical</b> for every existing config (including every
    /// <c>scripts/run-profiles/</c> profile) with the pinned default fingerprints unmoved.</item>
    /// <item>Each <c>ScoringProfile</c> resolves through the SAME logic as
    /// <see cref="AddRadarScoringWeights"/> (<see cref="ResolveScoringProfile"/>): a named-but-missing profile
    /// fails fast, a blank one means the code defaults, and the result is validated.</item>
    /// <item><c>Radar:PrimaryStrategy</c> selects the primary — the strategy whose snapshots keep the legacy
    /// storage location and which the weekly report renders. It is <b>required</b> whenever
    /// <c>Radar:Strategies</c> is non-empty: which strategy owns the reported series is load-bearing, so it is
    /// stated explicitly rather than silently defaulted to whichever entry happens to be listed first.</item>
    /// <item><c>SignalTypes</c> (spec 138) declares the <see cref="SignalType"/>s that strategy consumes.
    /// <b>Omitted or empty ⇒ every type</b>, which canonicalises onto <see cref="SignalTypeFilter.All"/> and
    /// hashes as a no-op — so the byte-identical default holds. Values are matched by EXACT enum member name
    /// (case-insensitively); numeric and unknown values are rejected rather than quietly accepted as a
    /// nonexistent type.</item>
    /// <item><c>Formula</c> (spec 146) names the <c>radar-formula-vN</c> that strategy scores with.
    /// <b>Omitted ⇒ <see cref="ScoreFormulaVersions.V8"/></b>, i.e. byte-identical to before the key existed,
    /// with the pinned default fingerprints unmoved.</item>
    /// <item><c>Channels</c> (spec 146) declares that strategy's weighted channel budget — required by, and
    /// only meaningful to, <see cref="ScoreFormulaVersions.V9"/>. Each entry needs a <c>Name</c>, a
    /// <c>Weight</c> and a <c>Saturation</c>; <c>Kind</c> is <c>"collector"</c> (the default) or
    /// <c>"breadth"</c>, and a collector channel additionally needs a <c>Collectors</c> array of registered
    /// <c>IEvidenceCollector.CollectorName</c>s. Weights must each lie in <c>[0,1]</c> and <b>sum to
    /// 1.0</b> — a sum that is not 1 silently rescales every score that strategy produces, so it is a startup
    /// failure naming the strategy and the actual sum.</item>
    /// <item><c>Weights</c> (spec 149) declares INLINE magnitude overrides for that strategy alone. The merge
    /// order is <b>code defaults → named <c>ScoringProfile</c> → inline <c>Weights</c>, last wins</b>, so a
    /// strategy can differ from a shared profile by a single number without a whole profile of its own. An
    /// <b>unknown key fails fast</b> naming the strategy and the key (the binder would otherwise ignore it
    /// silently, leaving a strategy stamped and ranked as tuned while scoring untuned), and
    /// <see cref="ScoringWeights.Validate"/> runs on the MERGED result. Omitted ⇒ byte-identical to before the
    /// key existed. See <see cref="ApplyInlineWeightOverrides"/>.</item>
    /// </list>
    /// Fails fast at startup — each message naming the offending config key or strategy — on an unknown
    /// <c>ScoringProfile</c>, a blank or unusable <c>Name</c>, duplicate <c>Name</c>s, a blank/unknown
    /// <c>SignalTypes</c> entry, an unknown <c>Formula</c>, a malformed or unbalanced <c>Channels</c> budget,
    /// an unknown, non-numeric or out-of-range inline <c>Weights</c> entry, or a <c>PrimaryStrategy</c> that is
    /// blank or not present in <c>Strategies</c>. Every one of those otherwise surfaces later as a confusing
    /// empty, mislabelled or silently rescaled score series.
    /// </summary>
    public static IServiceCollection AddRadarScoringStrategies(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var entries = configuration.GetSection("Radar:Strategies").GetChildren().ToList();

        if (entries.Count == 0)
        {
            // The byte-identical default: one strategy, the ambient profile, primary.
            var ambient = ResolveScoringProfile(
                configuration, configuration["Radar:Scoring:Profile"], "Radar:Scoring:Profile");
            services.AddSingleton(
                ScoringStrategySet.SingleDefault(ambient.Weights, ambient.EffectiveProfile));
            return services;
        }

        var primaryName = configuration["Radar:PrimaryStrategy"];
        if (string.IsNullOrWhiteSpace(primaryName))
        {
            throw new InvalidOperationException(
                "Radar:PrimaryStrategy must name one of the configured Radar:Strategies; the primary strategy "
                    + "owns the existing scores location and is the one the weekly report renders, so it is "
                    + "never inferred. Set Radar:PrimaryStrategy, or clear Radar:Strategies to run the single "
                    + "synthesised \"default\" strategy.");
        }

        primaryName = primaryName.Trim();

        var definitions = new List<ScoringStrategyDefinition>(entries.Count);
        foreach (var entry in entries)
        {
            var name = entry["Name"];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"{entry.Path}:Name is blank; every Radar:Strategies entry needs a Name (it is stamped on "
                        + "every snapshot and names the non-primary storage directory).");
            }

            name = name.Trim();

            var resolved = ResolveScoringProfile(
                configuration, entry["ScoringProfile"], $"{entry.Path}:ScoringProfile");

            // Spec 149: the LAST step of the merge — code defaults → named ScoringProfile → inline Weights.
            var weights = ApplyInlineWeightOverrides(entry, name, resolved.Weights);

            definitions.Add(new ScoringStrategyDefinition(
                Name: name,
                ScoringProfile: resolved.EffectiveProfile,
                Weights: weights,
                IsPrimary: string.Equals(name, primaryName, StringComparison.OrdinalIgnoreCase))
            {
                SignalTypes = ResolveSignalTypes(entry),
                // Spec 146: an omitted Formula keeps the strategy on radar-formula-v8 with no channels, which
                // is byte-identical to before this key existed. ScoringStrategySet does the cross-field
                // validation (unknown formula, v9-without-channels, channels-without-v9) so there is one
                // validation implementation regardless of how a definition is composed.
                Formula = ResolveFormula(entry),
                Channels = ResolveChannels(entry, name),
            });
        }

        if (!definitions.Any(d => d.IsPrimary))
        {
            throw new InvalidOperationException(
                $"Radar:PrimaryStrategy '{primaryName}' is not one of the configured Radar:Strategies "
                    + $"({string.Join(", ", definitions.Select(d => d.Name))}); a primary that names no "
                    + "configured strategy is almost certainly a typo and would leave the reported series "
                    + "undefined.");
        }

        // ScoringStrategySet owns the remaining invariants (unusable/duplicate names, exactly one primary) so
        // there is a single validation implementation regardless of how a set is composed.
        services.AddSingleton(new ScoringStrategySet(definitions));
        return services;
    }

    /// <summary>
    /// The public, settable <see cref="ScoringWeights"/> property names — the ONLY accepted keys under a
    /// strategy's inline <c>Weights</c> (spec 149). Built by reflection FROM the record, so a new weight is
    /// tunable inline the day it is added and this set can never drift from what the binder would actually
    /// bind.
    /// <para>
    /// <b>Case-INSENSITIVE, deliberately.</b> <c>ConfigurationBinder</c> matches config keys to properties
    /// case-insensitively, so a case-sensitive validator would disagree with the binder in both directions:
    /// it would reject <c>"recencyfloor"</c>, which binds perfectly well, and — far worse — its verdict on
    /// what is "unknown" would stop being the same question the binder answers. The validator must decide
    /// exactly what the binder decides, or the fail-fast guarantee is not a guarantee. A near-miss such as
    /// <c>RecencyFlooor</c> is unknown to BOTH and therefore still fails fast.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ScoringWeightNames =
        typeof(ScoringWeights)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Applies ONE strategy's inline <c>Weights</c> object (spec 149) on top of the weights its
    /// <c>ScoringProfile</c> resolved to. The merge order is <b>code defaults → named profile → inline
    /// Weights, last wins</b>: <paramref name="profileWeights"/> already carries the first two (a profile's
    /// present fields bound onto a fresh <see cref="ScoringWeights"/>), and each inline key then overwrites
    /// exactly the field it names and nothing else.
    /// <para>
    /// Why inline at all: tuning one magnitude used to mean defining a whole named profile under
    /// <c>Radar:Scoring:Profiles:{name}</c>, which is clumsy when the point is to run several near-identical
    /// strategies that differ in one number — the experiment spec 140's leaderboard exists to judge.
    /// </para>
    /// <para>
    /// <b>An unknown key fails fast, naming the strategy AND the key.</b> <c>ConfigurationBinder</c> silently
    /// ignores config keys that match no property, so a typo would leave the ambient value in place and
    /// produce a strategy that is stamped, scored and RANKED as tuned while being nothing of the sort. That
    /// fail-open is the exact shape this arc has been closing (spec 138 shipped one). Out-of-range values fail
    /// fast too: <see cref="ScoringWeights.Validate"/> runs on the MERGED result, so an inline override cannot
    /// smuggle past a check its profile would have failed. So does a known key that carries no number — a
    /// nested object, an array or an explicit <c>null</c> — for the same fail-open reason: every
    /// <see cref="ScoringWeights"/> field is a plain number, so such an entry can only leave the strategy
    /// untuned (or differently tuned) while it reads as tuned.
    /// </para>
    /// <para>
    /// Layering: this is the composition root, so <c>IConfiguration</c> stops here and
    /// <c>Radar.Application</c> receives an already-resolved, already-validated
    /// <see cref="ScoringWeights"/>. <see cref="ScoringStrategyDefinition"/> needs no new property — it
    /// already carries the resolved weights, and those are hashed into <c>ScoringConfigVersion</c> BY VALUE,
    /// so two strategies differing only in one inline weight get different fingerprints automatically.
    /// </para>
    /// </summary>
    private static ScoringWeights ApplyInlineWeightOverrides(
        IConfigurationSection entry, string strategyName, ScoringWeights profileWeights)
    {
        var section = entry.GetSection("Weights");
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            // Shape guard, mirroring ResolveSignalTypes/ResolveChannels: a SCALAR where an object was meant
            // (e.g. "Weights": "0.25") binds as a value with no children and would otherwise fall through as
            // "no overrides" — silently scoring an untuned strategy the operator wrote to be tuned.
            if (!string.IsNullOrWhiteSpace(section.Value))
            {
                throw new InvalidOperationException(
                    $"{section.Path} is the scalar '{section.Value}' for strategy '{strategyName}'; a "
                        + "strategy's Weights must be a JSON OBJECT of ScoringWeights field names to numbers "
                        + "(e.g. { \"FollowingTierDiscountWeight\": 0.0 }). Omit Weights entirely to use the "
                        + "ScoringProfile's values unchanged.");
            }

            // The byte-identical default: no inline block ⇒ exactly the profile's weights, same instance.
            return profileWeights;
        }

        foreach (var child in children)
        {
            if (!ScoringWeightNames.Contains(child.Key))
            {
                throw new InvalidOperationException(
                    $"{child.Path} names '{child.Key}', which is not a ScoringWeights field, so strategy "
                        + $"'{strategyName}' would be scored with the ambient value while appearing tuned. "
                        + "Inline Weights keys must each name a scoring weight (valid names: "
                        + $"{string.Join(", ", ScoringWeightNames.Order(StringComparer.Ordinal))}).");
            }

            // Per-ENTRY shape guard, the scalar guard's mirror image: every ScoringWeights field is a plain
            // number, so a known key that carries no scalar (a nested object/array, or an explicit JSON null)
            // is never a valid override. Left unguarded, binding such an entry would either leave the profile
            // value in place or produce a value the operator never wrote — and either way the strategy is
            // stamped, scored and RANKED as tuned while being nothing of the sort, which is exactly the
            // fail-open the unknown-key guard above exists to close.
            if (child.GetChildren().Any() || child.Value is null)
            {
                throw new InvalidOperationException(
                    $"{child.Path} carries no numeric value for strategy '{strategyName}'; every inline "
                        + "Weights entry must be a NUMBER (e.g. { \"FollowingTierDiscountWeight\": 0.0 }), not "
                        + "a nested object, an array or null. Omit the key entirely to keep the "
                        + "ScoringProfile's value.");
            }
        }

        // Bind the inline values ONTO a copy of the profile's weights, which is what makes "last wins"
        // literal: an absent inline key leaves the profile value untouched because nothing overwrites it.
        // ConfigurationBinder sets init-only properties through their (public) init accessors — the same
        // mechanism section.Get<ScoringWeights>() already relies on in ResolveScoringProfile — and a
        // non-numeric value throws here rather than binding to 0.
        var merged = profileWeights with { };
        try
        {
            section.Bind(merged);
        }
        catch (InvalidOperationException ex)
        {
            // ConfigurationBinder's own message names the PATH but not the strategy, and the path is indexed
            // (Radar:Strategies:3:Weights:RecencyFloor) rather than named — so with several near-identical
            // strategies the operator has to count array entries to find the broken one. Rethrow naming it,
            // keeping the binder's exception as InnerException so the offending key, target type and the
            // underlying FormatException all survive. Same treatment as the Validate() failure below, so
            // EVERY inline-Weights failure names the strategy, exactly as this method's contract promises.
            throw new InvalidOperationException(
                $"{section.Path}: strategy '{strategyName}' has an inline Weights entry that could not be "
                    + "bound; every entry must be a NUMBER (e.g. { \"FollowingTierDiscountWeight\": 0.0 }). "
                    + $"{ex.Message}",
                ex);
        }

        // Validate the MERGED result: an inline override is as capable of producing a nonsensical weight as a
        // profile is, and the combination of a valid profile and a valid-looking override can still break an
        // invariant that spans fields (the monotone tier ordering, say). Rethrown with the strategy named,
        // because with several near-identical strategies the field name alone does not say which one is broken.
        try
        {
            merged.Validate();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"{section.Path}: strategy '{strategyName}' resolves to an invalid scoring configuration once "
                    + $"its inline Weights are applied. {ex.Message}",
                ex);
        }

        return merged;
    }

    /// <summary>
    /// Resolves ONE strategy's <c>SignalTypes</c> array (spec 138) into a canonical
    /// <see cref="SignalTypeFilter"/>. An absent or empty array ⇒ <see cref="SignalTypeFilter.All"/> — the
    /// byte-identical "consume everything" default. This is the layering seam: <c>IConfiguration</c> never
    /// reaches <c>Radar.Application</c>, so the strings are parsed into domain <see cref="SignalType"/> values
    /// here and only resolved enum values cross the boundary. A <b>scalar</b> <c>SignalTypes</c> (the array
    /// brackets forgotten) is rejected rather than silently read as "all types".
    /// </summary>
    private static SignalTypeFilter ResolveSignalTypes(IConfigurationSection entry)
    {
        var section = entry.GetSection("SignalTypes");
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            // Shape guard: a SCALAR (e.g. "SignalTypes": "InsiderBuying" instead of [ "InsiderBuying" ]) binds
            // as a value with no children, which would otherwise fall through to "all types" — silently
            // stamping and scoring BROAD a strategy the operator wrote to be narrow. That is the exact failure
            // this slice exists to prevent, so it is a startup error rather than a quiet widening.
            //
            // The test is BLANK, not null. An EMPTY ARRAY is indistinguishable from "SignalTypes": "" once
            // bound: JsonConfigurationFileParser stores the empty STRING for "SignalTypes": [] (verified
            // against Microsoft.Extensions.Configuration.Json 10.0.7 — absent ⇒ null, [] ⇒ "", scalar ⇒ its
            // text), and the memory provider has no array shape at all. There is therefore no representation
            // in bound config that could let the two differ, so blank MUST mean "all types" — that is the
            // spec's "omitted OR EMPTY ⇒ all signal types", and it is also what the throw message below
            // advises the operator to do.
            if (!string.IsNullOrWhiteSpace(section.Value))
            {
                throw new InvalidOperationException(
                    $"{section.Path} is the scalar '{section.Value}'; a strategy's SignalTypes must be a JSON "
                        + "ARRAY of SignalType names (e.g. [ \"InsiderBuying\" ]). Omit SignalTypes, or give it "
                        + "an empty array, to consume every signal type.");
            }

            return SignalTypeFilter.All;
        }

        // Deliberately NOT Enum.TryParse: it accepts numeric strings ("5") and, worse, any numeric value at
        // all — including undeclared ones — as a valid SignalType, so a typo would bind to a type that does
        // not exist and silently produce a strategy that scores nothing. Matching against the declared member
        // NAMES makes every non-name a startup failure.
        var declaredNames = Enum.GetNames<SignalType>();
        var types = new List<SignalType>(children.Count);
        foreach (var child in children)
        {
            var raw = child.Value?.Trim();
            var match = string.IsNullOrEmpty(raw)
                ? null
                : Array.Find(declaredNames, n => string.Equals(n, raw, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                throw new InvalidOperationException(
                    $"{child.Path} is '{child.Value}', which is not a SignalType; a strategy's SignalTypes "
                        + "entries must each name a declared signal type (valid values: "
                        + $"{string.Join(", ", declaredNames)}). Remove SignalTypes entirely to consume every "
                        + "signal type.");
            }

            types.Add(Enum.Parse<SignalType>(match));
        }

        // Create() canonicalises: duplicates collapse, order is irrelevant, and a list naming EVERY declared
        // type returns All — so "all types spelled out" is byte-identical to omitting the key.
        return SignalTypeFilter.Create(types);
    }

    /// <summary>
    /// Resolves ONE strategy's <c>Formula</c> (spec 146). Absent or blank ⇒ <see cref="ScoreFormulaVersions.V8"/>,
    /// the byte-identical default. A non-blank value is canonicalised against the shipped
    /// <c>radar-formula-vN</c> list and rejected here (rather than defaulting) so a typo is a startup error
    /// instead of a strategy silently scored with a structure nobody asked for. <see cref="ScoringStrategySet"/>
    /// re-checks the resulting value, so a definition composed in code is held to the same rule.
    /// </summary>
    private static string ResolveFormula(IConfigurationSection entry)
    {
        var raw = entry["Formula"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ScoreFormulaVersions.V8;
        }

        return ScoreFormulaVersions.Canonicalize(raw)
            ?? throw new InvalidOperationException(
                $"{entry.Path}:Formula is '{raw}', which is not a known scoring formula (known formulas: "
                    + $"{ScoreFormulaVersions.KnownList}). Omit Formula to use the default "
                    + $"{ScoreFormulaVersions.V8}.");
    }

    /// <summary>
    /// Resolves ONE strategy's <c>Channels</c> array (spec 146) into a validated
    /// <see cref="ScoringChannelSet"/>. An absent or empty array ⇒ <see cref="ScoringChannelSet.Empty"/>. This
    /// is the layering seam: <c>IConfiguration</c> never reaches <c>Radar.Application</c>, so the strings and
    /// numbers are parsed here and only resolved types cross the boundary; the INVARIANTS (weights in [0,1],
    /// weights summing to 1, positive saturation, unique names, breadth-declares-no-collectors) belong to
    /// <see cref="ScoringChannelSet.Create"/> so they hold however a set is composed.
    /// <para>
    /// Shape guards mirror <see cref="ResolveSignalTypes"/>' reasoning: a scalar where an array was meant
    /// binds as a value with no children, which would otherwise fall through to "no channels" and silently
    /// score a v9 strategy 0 — exactly the failure this slice exists to prevent.
    /// </para>
    /// </summary>
    private static ScoringChannelSet ResolveChannels(IConfigurationSection entry, string strategyName)
    {
        var section = entry.GetSection("Channels");
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(section.Value))
            {
                throw new InvalidOperationException(
                    $"{section.Path} is the scalar '{section.Value}'; a strategy's Channels must be a JSON "
                        + "ARRAY of channel objects. Omit Channels entirely to declare none.");
            }

            return ScoringChannelSet.Empty;
        }

        var channels = new List<ScoringChannel>(children.Count);
        foreach (var child in children)
        {
            var name = child["Name"];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"{child.Path}:Name is blank; every channel needs a Name (it is recorded in the score "
                        + "explanation and in every consumed signal's contribution reason).");
            }

            name = name.Trim();

            var kind = ResolveChannelKind(child, name);
            var weight = RequireChannelNumber(child, "Weight", name);
            var saturation = RequireChannelNumber(child, "Saturation", name);
            // Read Collectors for BOTH kinds and hand them through. A breadth channel must declare none, and
            // ScoringChannelSet.Create is the single place that says so — dropping them here instead would
            // silently discard what the operator wrote, which is the failure mode this slice exists to close.
            var collectors = ResolveChannelCollectors(child, name);

            channels.Add(kind == ScoringChannelKind.Breadth
                ? new ScoringChannel(name, ScoringChannelKind.Breadth, collectors, weight, saturation)
                : ScoringChannel.Collector(name, collectors, weight, saturation));
        }

        // Create() validates the budget as a whole (the weight sum is a property of the SET, not of any one
        // channel) and canonicalises the ordering, so two strategies declaring the same budget in a different
        // order are the same strategy and hash identically.
        return ScoringChannelSet.Create(channels, strategyName);
    }

    private static ScoringChannelKind ResolveChannelKind(IConfigurationSection channel, string channelName)
    {
        var raw = channel["Kind"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Collector is the default kind: it is the common case, and the alternative (breadth) is the one
            // that has to be stated because it changes what the channel measures.
            return ScoringChannelKind.Collector;
        }

        var trimmed = raw.Trim();
        foreach (var kind in Enum.GetValues<ScoringChannelKind>())
        {
            if (string.Equals(ScoringChannelSet.KindToken(kind), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        throw new InvalidOperationException(
            $"{channel.Path}:Kind is '{raw}' for channel '{channelName}', which is not a channel kind (valid "
                + $"values: {string.Join(", ", Enum.GetValues<ScoringChannelKind>().Select(ScoringChannelSet.KindToken))}). "
                + "Omit Kind for a collector channel.");
    }

    private static IReadOnlyList<string> ResolveChannelCollectors(
        IConfigurationSection channel, string channelName)
    {
        var section = channel.GetSection("Collectors");
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(section.Value))
            {
                throw new InvalidOperationException(
                    $"{section.Path} is the scalar '{section.Value}' for channel '{channelName}'; a channel's "
                        + "Collectors must be a JSON ARRAY of collector names (e.g. [ \"sec-form4\" ]).");
            }

            // Empty is not an error HERE — ScoringChannelSet.Create reports it with the strategy name
            // attached, so there is exactly one message for "a collector channel names no collectors".
            return [];
        }

        var names = new List<string>(children.Count);
        foreach (var collector in children)
        {
            var value = collector.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(
                    $"{collector.Path} is blank for channel '{channelName}'; every entry must name a registered "
                        + "IEvidenceCollector.CollectorName.");
            }

            names.Add(value);
        }

        return names;
    }

    /// <summary>
    /// Reads a REQUIRED culture-invariant channel number. Absent and unparseable are both startup failures:
    /// a defaulted weight or saturation would silently rebalance a declared budget, which is the one thing
    /// this design cannot tolerate quietly.
    /// </summary>
    private static double RequireChannelNumber(
        IConfigurationSection channel, string key, string channelName)
    {
        var raw = channel[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                $"{channel.Path}:{key} is missing for channel '{channelName}'; a channel's Weight (its share of "
                    + "the composite) and Saturation (how much traffic counts as a full share) are both required "
                    + "— defaulting either would silently rebalance the declared budget.");
        }

        if (!double.TryParse(
                raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException(
                $"{channel.Path}:{key} is '{raw}' for channel '{channelName}', which is not a number. Channel "
                    + "numbers are parsed culture-invariantly, so use '.' as the decimal separator.");
        }

        return value;
    }

    /// <summary>
    /// Resolves the effective insider-materiality profile and registers the concrete
    /// <see cref="InsiderMaterialityWeights"/> as a singleton so it wins over the library's
    /// <c>TryAddSingleton</c> default (call this BEFORE <see cref="AddRadarApplicationServices"/>, mirroring
    /// <see cref="AddRadarScoringWeights"/>). Precedence:
    /// <list type="bullet">
    /// <item><c>Radar:Insider:Profile</c> selects a named profile; blank/absent ⇒ <c>"default"</c>.</item>
    /// <item>If <c>Radar:Insider:Profiles:{name}</c> exists, its present fields bind ONTO a fresh
    /// <see cref="InsiderMaterialityWeights"/> (unspecified fields keep the code default == spec 93).</item>
    /// <item>A <b>named</b> (non-default) profile that is requested but absent <b>fails fast</b> — a silent
    /// fallthrough to defaults would mask a typo'd profile name in an experiment.</item>
    /// <item>A blank/absent profile, or an absent <c>"default"</c> profile, ⇒ all code defaults
    /// (⇒ byte-identical spec-93 insider Strengths and the pinned default fingerprint).</item>
    /// </list>
    /// The resolved weights are validated (<see cref="InsiderMaterialityWeights.Validate"/>) so a
    /// misconfigured tier (an out-of-range Strength, a missing floor, a non-descending table) fails fast at
    /// registration, never silently producing a Strength that fails <c>SignalValidation</c> at runtime.
    /// </summary>
    public static IServiceCollection AddRadarInsiderMateriality(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var name = configuration["Radar:Insider:Profile"];
        var effectiveName = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim();
        var section = configuration.GetSection($"Radar:Insider:Profiles:{effectiveName}");

        InsiderMaterialityWeights weights;
        if (section.Exists())
        {
            // Bind each list sub-section into a FRESH list (Get<List<T>> starts empty), overriding a whole
            // table only when the profile supplies it — otherwise keep the code default. Binding the record
            // directly with Get<InsiderMaterialityWeights>() would APPEND the profile's tiers onto the
            // default 5-tier table (the binder preserves existing collection items), producing a non-descending
            // table that fails Validate(); binding the tables explicitly gives clean replace-or-default semantics.
            var defaults = new InsiderMaterialityWeights();
            weights = defaults with
            {
                BuyTiers = BindTiersOrDefault(section.GetSection("BuyTiers"), defaults.BuyTiers),
                SellTiers = BindTiersOrDefault(section.GetSection("SellTiers"), defaults.SellTiers),
                ClusterBoost = section.GetValue("ClusterBoost", defaults.ClusterBoost),
            };
        }
        else if (!string.IsNullOrWhiteSpace(name)
            && !string.Equals(effectiveName, "default", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Radar:Insider:Profile '{effectiveName}' was requested but no matching profile exists under "
                    + "Radar:Insider:Profiles — a named-but-missing profile is almost certainly a typo. Add the "
                    + $"profile under Radar:Insider:Profiles:{effectiveName} or clear Radar:Insider:Profile to use "
                    + "the code defaults.");
        }
        else
        {
            weights = new InsiderMaterialityWeights();
        }

        // Fail fast at registration on a misconfigured tier table (also enforced in the extractor ctor).
        weights.Validate();

        services.AddSingleton(weights);
        return services;
    }

    // Binds a tier table from its config sub-section into a FRESH list (clean replace semantics); returns the
    // supplied fallback (the code default) when the profile does not define the table at all.
    private static IReadOnlyList<InsiderMaterialityTier> BindTiersOrDefault(
        IConfigurationSection section, IReadOnlyList<InsiderMaterialityTier> fallback) =>
        section.Exists()
            ? section.Get<List<InsiderMaterialityTier>>() ?? fallback
            : fallback;

    /// <summary>
    /// Resolves the effective same-event media-collapse window (spec 109) and registers the concrete
    /// <see cref="MediaCollapseOptions"/> as a singleton so it wins over the library's <c>TryAddSingleton</c>
    /// default (call this BEFORE <see cref="AddRadarApplicationServices"/>, mirroring
    /// <see cref="AddRadarScoringWeights"/>). A straight bind of the <c>Radar:Scoring:MediaCollapse</c> section
    /// (no named-profile surface — the window is a single tunable magnitude): when the section exists its
    /// present fields bind ONTO a fresh <see cref="MediaCollapseOptions"/> (unspecified fields keep the code
    /// default == 3-day window); when absent, all code defaults (⇒ byte-identical default de-noising and the
    /// pinned default fingerprint). The resolved options are validated
    /// (<see cref="MediaCollapseOptions.Validate"/>) so a non-positive window fails fast at registration,
    /// never silently disabling the collapse.
    /// </summary>
    public static IServiceCollection AddRadarMediaCollapse(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Radar:Scoring:MediaCollapse");
        var options = section.Exists()
            ? section.Get<MediaCollapseOptions>() ?? new MediaCollapseOptions()
            : new MediaCollapseOptions();

        // Fail fast at registration on a non-positive window (also enforced in the collapse ctor).
        options.Validate();

        services.AddSingleton(options);
        return services;
    }

    /// <summary>
    /// Registers the deterministic local-file evidence collector along with the evidence
    /// normalizer the mapper depends on. The collector reads <c>*.json</c> evidence documents from
    /// <paramref name="sourceDirectory"/> and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> records (the
    /// <see cref="Radar.Application.Collectors.CollectedEvidenceMapper"/> normalizes/hashes them); it
    /// does not persist them. Intended for offline/test pipeline runs.
    /// </summary>
    public static IServiceCollection AddLocalFileCollector(
        this IServiceCollection services, string sourceDirectory)
    {
        services.TryAddSingleton<IEvidenceNormalizer, EvidenceNormalizer>();
        services.AddSingleton(new LocalFileEvidenceCollectorOptions { SourceDirectory = sourceDirectory });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, LocalFileEvidenceCollector>();
        return services;
    }

    /// <summary>
    /// Registers the RSS press-release collector and the typed <c>HttpClient</c> its
    /// <see cref="IRssFeedReader"/> uses. The collector reads the per-company RSS feeds supplied on the
    /// <see cref="Radar.Application.Collectors.CollectionContext"/> (populated by the runner from
    /// <see cref="ICompanyRepository.GetSourceFeedsAsync"/>) and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> press releases; it does not persist
    /// them. All HTTP/XML/Syndication code stays in Infrastructure (AD-5).
    /// </summary>
    public static IServiceCollection AddRssPressReleaseCollector(this IServiceCollection services)
    {
        services.AddHttpClient<IRssFeedReader, HttpRssFeedReader>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, RssPressReleaseCollector>();
        return services;
    }

    /// <summary>
    /// Registers the SEC EDGAR filing collector and the typed <c>HttpClient</c> its
    /// <see cref="ISecFilingReader"/> uses. The collector reads the per-company <c>sec</c> feeds supplied on
    /// the <see cref="Radar.Application.Collectors.CollectionContext"/> (each feed's <c>Url</c> is that
    /// company's EDGAR submissions JSON endpoint) and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> filings; it does not persist them. All
    /// HTTP/JSON/SEC code stays in Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="SecCollectorOptions.UserAgent"/> is null/blank (SEC returns HTTP 403 for
    /// every request without a compliant declared User-Agent), when
    /// <see cref="SecCollectorOptions.MaxFilingsPerCompany"/> is zero/negative, or when
    /// <see cref="SecCollectorOptions.Forms"/> is null/empty: each of those would let the collector run yet
    /// silently collect nothing, so they are treated as configuration errors. The named client sends the
    /// configured UA plus <c>Accept-Encoding: gzip, deflate</c> and enables automatic decompression (SEC
    /// recommends gzip).
    /// </para>
    /// </summary>
    public static IServiceCollection AddSecEdgarCollector(
        this IServiceCollection services, SecCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            throw new InvalidOperationException(
                "SEC EDGAR requires a compliant User-Agent (e.g. \"Radar Research <email>\"); configure "
                    + "Radar:Sec:UserAgent before enabling the \"sec\" collector — every request 403s without it.");
        }

        if (options.MaxFilingsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "SEC EDGAR MaxFilingsPerCompany must be greater than zero; configure Radar:Sec:MaxFilingsPerCompany "
                    + "to a positive cap (default 25) — a zero/negative value collects nothing while still running.");
        }

        if (options.Forms is null || options.Forms.Count == 0)
        {
            throw new InvalidOperationException(
                "SEC EDGAR requires at least one filing form to collect; configure Radar:Sec:Forms "
                    + "(default 8-K, 10-Q, 10-K) — an empty list collects nothing while still running.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<ISecFilingReader, HttpSecFilingReader>(client =>
            {
                // Use TryAddWithoutValidation: the SEC-recommended UA form ("Radar Research <email>") is not a
                // strict RFC product/comment token, so the strongly-typed UserAgent collection rejects it.
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
                // Disable the ambient pipeline timeout: SecRateLimitingHandler owns the per-fetch timeout and
                // starts it AFTER pacing, so a deep shared-pacer queue can never time a request out before it is
                // sent (see SecRateLimitOptions.FetchTimeout).
                client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            // Route through the shared global SEC pacer so this collector's requests count against the same
            // aggregate *.sec.gov rate budget as every other SEC client (see AddSecRequestPacing).
            .AddHttpMessageHandler<SecRateLimitingHandler>();

        services.AddSecRequestPacing();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, SecEdgarFilingCollector>();
        return services;
    }

    /// <summary>
    /// Registers the SEC Form 4 (insider-transaction) collector and the typed <c>HttpClient</c> its
    /// <see cref="ISecForm4Reader"/> uses. The collector reads the per-company <c>secform4</c> feeds supplied
    /// on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each feed's <c>Url</c> is that
    /// company's EDGAR submissions JSON endpoint), fetches each Form 4's raw ownership XML, classifies its
    /// insider transactions by SEC transaction code (deterministic, NO AI), and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> filings carrying an insider-activity
    /// direction; it does not persist them. All HTTP/JSON/XML/SEC code stays in Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="SecForm4CollectorOptions.UserAgent"/> is null/blank (SEC returns HTTP 403 for
    /// every request without a compliant declared User-Agent) or when
    /// <see cref="SecForm4CollectorOptions.MaxFilingsPerCompany"/> is zero/negative: each would let the
    /// collector run yet silently collect nothing, so they are treated as configuration errors. The named
    /// client sends the configured UA plus <c>Accept-Encoding: gzip, deflate</c> and enables automatic
    /// decompression (SEC recommends gzip).
    /// </para>
    /// </summary>
    public static IServiceCollection AddSecForm4Collector(
        this IServiceCollection services, SecForm4CollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            throw new InvalidOperationException(
                "SEC EDGAR requires a compliant User-Agent (e.g. \"Radar Research <email>\"); configure "
                    + "Radar:SecForm4:UserAgent before enabling the \"secform4\" collector — every request 403s without it.");
        }

        if (options.MaxFilingsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "SEC Form 4 MaxFilingsPerCompany must be greater than zero; configure "
                    + "Radar:SecForm4:MaxFilingsPerCompany to a positive cap (default 15) — a zero/negative value "
                    + "collects nothing while still running.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<ISecForm4Reader, HttpSecForm4Reader>(client =>
            {
                // Use TryAddWithoutValidation: the SEC-recommended UA form ("Radar Research <email>") is not a
                // strict RFC product/comment token, so the strongly-typed UserAgent collection rejects it.
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
                // Disable the ambient pipeline timeout: SecRateLimitingHandler owns the per-fetch timeout and
                // starts it AFTER pacing, so a deep shared-pacer queue can never time a request out before it is
                // sent (see SecRateLimitOptions.FetchTimeout).
                client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            // Route through the shared global SEC pacer so this collector's requests count against the same
            // aggregate *.sec.gov rate budget as every other SEC client (see AddSecRequestPacing).
            .AddHttpMessageHandler<SecRateLimitingHandler>();

        services.AddSecRequestPacing();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, SecForm4Collector>();
        return services;
    }

    /// <summary>
    /// Registers the SEC Schedule 13D/13G (institutional/activist beneficial-ownership) collector and the
    /// typed <c>HttpClient</c> its <see cref="ISec13DGReader"/> uses. The collector reads the per-company
    /// <c>sec13dg</c> feeds supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each
    /// feed's <c>Url</c> is that company's EDGAR submissions JSON endpoint), filters <c>filings.recent</c> to
    /// the 13D/13G form types, classifies each by form (deterministic, NO AI, metadata-only — no filing body
    /// fetch), and produces raw <see cref="Radar.Application.Collectors.CollectedEvidence"/> filings carrying
    /// the fixed spec-99 ownership phrases; it does not persist them. All HTTP/JSON/SEC code stays in
    /// Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="Sec13DGCollectorOptions.UserAgent"/> is null/blank (SEC returns HTTP 403 for
    /// every request without a compliant declared User-Agent) or when
    /// <see cref="Sec13DGCollectorOptions.MaxFilingsPerCompany"/> is zero/negative: each would let the
    /// collector run yet silently collect nothing, so they are treated as configuration errors. The named
    /// client sends the configured UA plus <c>Accept-Encoding: gzip, deflate</c> and enables automatic
    /// decompression (SEC recommends gzip).
    /// </para>
    /// </summary>
    public static IServiceCollection AddSec13DGCollector(
        this IServiceCollection services, Sec13DGCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            throw new InvalidOperationException(
                "SEC EDGAR requires a compliant User-Agent (e.g. \"Radar Research <email>\"); configure "
                    + "Radar:Sec13DG:UserAgent before enabling the \"sec13dg\" collector — every request 403s without it.");
        }

        if (options.MaxFilingsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "SEC 13D/13G MaxFilingsPerCompany must be greater than zero; configure "
                    + "Radar:Sec13DG:MaxFilingsPerCompany to a positive cap (default 20) — a zero/negative value "
                    + "collects nothing while still running.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<ISec13DGReader, HttpSec13DGReader>(client =>
            {
                // Use TryAddWithoutValidation: the SEC-recommended UA form ("Radar Research <email>") is not a
                // strict RFC product/comment token, so the strongly-typed UserAgent collection rejects it.
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
                // Disable the ambient pipeline timeout: SecRateLimitingHandler owns the per-fetch timeout and
                // starts it AFTER pacing, so a deep shared-pacer queue can never time a request out before it is
                // sent (see SecRateLimitOptions.FetchTimeout).
                client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            // Route through the shared global SEC pacer so this collector's requests count against the same
            // aggregate *.sec.gov rate budget as every other SEC client (see AddSecRequestPacing).
            .AddHttpMessageHandler<SecRateLimitingHandler>();

        services.AddSecRequestPacing();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, Sec13DGCollector>();
        return services;
    }

    /// <summary>
    /// Registers the SEC EDGAR earnings-release (EX-99.1) body reader and the typed <c>HttpClient</c> its
    /// <see cref="ISecEarningsReleaseReader"/> uses. Given a filing's CIK + dashed accession, the reader
    /// fetches the filing index, selects the <c>EX-99.1</c> earnings-release exhibit (with an <c>EX-99.*</c>
    /// fallback; never the primary 8-K), fetches it, and strips it to plain text via the shared
    /// <see cref="IEvidenceNormalizer"/>. This is a standalone service (the analyzer in a later slice injects
    /// it); it is <b>not</b> an <see cref="IEvidenceCollector"/> and is <b>not</b> added to
    /// <c>Radar:Collectors</c>, so default pipeline behaviour is unchanged. All HTTP/HTML/SEC code stays in
    /// Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="SecCollectorOptions.UserAgent"/> is null/blank (SEC returns HTTP 403 for
    /// every request without a compliant declared User-Agent). The named client sends the configured UA plus
    /// <c>Accept-Encoding: gzip, deflate</c> and enables automatic decompression (SEC recommends gzip).
    /// <see cref="SecCollectorOptions"/> and <see cref="IEvidenceNormalizer"/> are registered with
    /// <c>TryAdd</c> so this method coexists with <see cref="AddSecEdgarCollector"/> and
    /// <see cref="AddRadarApplicationServices"/> without a double-registration conflict, and resolves the
    /// reader's stripper dependency even when wired standalone.
    /// </para>
    /// <para>
    /// The optional <paramref name="readerOptions"/> tunes the reader's bounded HTTP 429 backoff-retry
    /// (spec 105) — SEC 429s the Archives burst this reader fires, starving the AI directional path. It
    /// defaults to <see cref="SecEarningsReleaseReaderOptions"/>'s defaults (2 retries, 2s base backoff) and is
    /// registered with <c>TryAdd</c> so the reader resolves it. Registration fails fast when
    /// <see cref="SecEarningsReleaseReaderOptions.MaxRetriesOn429"/> is negative or
    /// <see cref="SecEarningsReleaseReaderOptions.RetryBackoff"/> is negative.
    /// </para>
    /// </summary>
    public static IServiceCollection AddSecEarningsReleaseReader(
        this IServiceCollection services,
        SecCollectorOptions options,
        SecEarningsReleaseReaderOptions? readerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            throw new InvalidOperationException(
                "SEC EDGAR requires a compliant User-Agent (e.g. \"Radar Research <email>\"); configure "
                    + "Radar:Sec:UserAgent before enabling the SEC earnings-release reader — every request 403s without it.");
        }

        readerOptions ??= new SecEarningsReleaseReaderOptions();

        if (readerOptions.MaxRetriesOn429 < 0)
        {
            throw new InvalidOperationException(
                "SEC earnings-release MaxRetriesOn429 must not be negative; configure Radar:Sec:MaxRetriesOn429 "
                    + "to a non-negative retry count (default 2) — a negative value is nonsensical configuration.");
        }

        if (readerOptions.RetryBackoff < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "SEC earnings-release RetryBackoff must not be negative; configure Radar:Sec:RetryBackoffSeconds "
                    + "to a non-negative base delay (default 2s) — the reader doubles it per 429 retry.");
        }

        if (readerOptions.MinRequestInterval < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "SEC earnings-release MinRequestInterval must not be negative; configure Radar:Sec:MinRequestIntervalMs "
                    + "to a non-negative pace in milliseconds (default 250 ms) — a negative value is nonsensical configuration.");
        }

        services.AddHttpClient<ISecEarningsReleaseReader, HttpSecEarningsReleaseReader>(client =>
            {
                // Use TryAddWithoutValidation: the SEC-recommended UA form ("Radar Research <email>") is not a
                // strict RFC product/comment token, so the strongly-typed UserAgent collection rejects it.
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
                // Disable the ambient pipeline timeout: SecRateLimitingHandler owns the per-fetch timeout and
                // starts it AFTER pacing, so a deep shared-pacer queue can never time a request out before it is
                // sent (see SecRateLimitOptions.FetchTimeout).
                client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            // Route through the shared global SEC pacer so the earnings reader's www.sec.gov requests count
            // against the same aggregate *.sec.gov rate budget as the collectors (the burst that starves it).
            // This is layered ON TOP of the reader's own per-reader MinRequestInterval self-pacing (spec 107);
            // the global pacer bounds the whole run's SEC traffic, the reader's still bounds its own footprint.
            .AddHttpMessageHandler<SecRateLimitingHandler>();

        services.AddSecRequestPacing();
        services.TryAddSingleton(options);
        services.TryAddSingleton(readerOptions);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEvidenceNormalizer, EvidenceNormalizer>();
        return services;
    }

    /// <summary>
    /// Idempotently registers the shared, process-wide SEC request pacer (<see cref="SecRequestPacer"/>) and
    /// its <see cref="SecRateLimitingHandler"/> so every SEC <c>HttpClient</c> that adds the handler routes
    /// through ONE pacer instance — the AGGREGATE <c>*.sec.gov</c> request rate of a run (all collectors + the
    /// earnings-release reader), not each client in isolation, is what stays under SEC's per-IP fair-access
    /// ceiling. This is the fix for the observed failure mode where an unpaced collector burst trips SEC's
    /// mitigation and blocks <c>www.sec.gov</c>, starving the AI earnings-release path.
    /// <para>
    /// Every registration is <c>TryAdd</c>, so it is safe for each SEC <c>Add*</c> helper to call this
    /// unconditionally: the first call wins and the rest are no-ops, and a composition root that registered its
    /// own concrete <see cref="SecRateLimitOptions"/> (e.g. the Worker binding <c>Radar:Sec:GlobalMinIntervalMs</c>)
    /// keeps that value. The pacer is a singleton (shared pacing state); the handler is transient
    /// (<c>HttpClientFactory</c> owns handler lifetime) but its pacing state lives in the injected singleton.
    /// </para>
    /// </summary>
    private static IServiceCollection AddSecRequestPacing(this IServiceCollection services)
    {
        services.TryAddSingleton(new SecRateLimitOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SecRequestPacer>();
        services.TryAddTransient<SecRateLimitingHandler>();
        return services;
    }

    /// <summary>
    /// Registers the USASpending.gov government-contract collector and the typed <c>HttpClient</c> its
    /// <see cref="IUsaSpendingAwardReader"/> uses. The collector reads the per-company <c>usaspending</c>
    /// feeds supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each feed's
    /// <c>Url</c> is a <c>recipientId=...&amp;recipientSearchText=...</c> token) and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> contract awards; it does not persist
    /// them. All HTTP/JSON/USASpending code stays in Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="UsaSpendingCollectorOptions.AwardTypeCodes"/> is null/empty, when
    /// <see cref="UsaSpendingCollectorOptions.MaxAwardsPerCompany"/> is zero/negative, or when
    /// <see cref="UsaSpendingCollectorOptions.LookbackDays"/> is zero/negative: each of those would let the
    /// collector run yet silently collect nothing, so they are treated as configuration errors. The API needs
    /// no User-Agent or key; the named client only enables automatic gzip/deflate decompression (polite).
    /// </para>
    /// </summary>
    public static IServiceCollection AddUsaSpendingContractCollector(
        this IServiceCollection services, UsaSpendingCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.AwardTypeCodes is null || options.AwardTypeCodes.Count == 0)
        {
            throw new InvalidOperationException(
                "USASpending requires at least one award_type_code to query; configure "
                    + "Radar:UsaSpending:AwardTypeCodes (default A, B, C, D — the contracts group) — an empty "
                    + "list collects nothing while still running.");
        }

        if (options.MaxAwardsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "USASpending MaxAwardsPerCompany must be greater than zero; configure "
                    + "Radar:UsaSpending:MaxAwardsPerCompany to a positive cap (default 25) — a zero/negative "
                    + "value collects nothing while still running.");
        }

        if (options.LookbackDays <= 0)
        {
            throw new InvalidOperationException(
                "USASpending LookbackDays must be greater than zero; configure Radar:UsaSpending:LookbackDays "
                    + "to a positive window (default 365) — a zero/negative value collects nothing while still running.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<IUsaSpendingAwardReader, HttpUsaSpendingAwardReader>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, UsaSpendingContractCollector>();
        return services;
    }

    /// <summary>
    /// Registers the USPTO ODP granted-patent activity collector (spec 127, repointed to the USPTO Open Data
    /// Portal PFW Search API in spec 131) and the typed <c>HttpClient</c>
    /// its <see cref="IPatentSearchReader"/> uses. The collector reads the per-company <c>patents</c> feeds
    /// supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each feed's <c>Url</c> is
    /// an <c>assignee=...</c> token), counts recently-granted patents, and produces one raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> per company; it does not persist them. All
    /// HTTP/JSON/ODP code stays in Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="PatentCollectorOptions.LookbackDays"/>,
    /// <see cref="PatentCollectorOptions.MaxSampleTitles"/>, or <see cref="PatentCollectorOptions.MaxPageSize"/>
    /// is zero/negative, or when <see cref="PatentCollectorOptions.ApiKeyEnvVar"/> is blank: each of those
    /// would let the collector run yet silently collect nothing (or have nowhere to read the key from), so
    /// they are treated as configuration errors. The API key VALUE is never in config — it is read at runtime
    /// from the env var NAMED by <see cref="PatentCollectorOptions.ApiKeyEnvVar"/>; a missing key degrades
    /// every patents feed to a source failure (opt-in OFF ⇒ baseline untouched). The named client sets a
    /// generic User-Agent and enables automatic gzip/deflate decompression (polite).
    /// </para>
    /// </summary>
    public static IServiceCollection AddPatentActivityCollector(
        this IServiceCollection services, PatentCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.LookbackDays <= 0)
        {
            throw new InvalidOperationException(
                "Patents LookbackDays must be greater than zero; configure Radar:Patents:LookbackDays to a "
                    + "positive window (default 180) — a zero/negative value collects nothing while still running.");
        }

        if (options.MaxSampleTitles <= 0)
        {
            throw new InvalidOperationException(
                "Patents MaxSampleTitles must be greater than zero; configure Radar:Patents:MaxSampleTitles to a "
                    + "positive sample bound (default 5) — a zero/negative value is nonsensical configuration.");
        }

        if (options.MaxPageSize <= 0)
        {
            throw new InvalidOperationException(
                "Patents MaxPageSize must be greater than zero; configure Radar:Patents:MaxPageSize to a positive "
                    + "page cap (default 100) — a zero/negative value collects nothing while still running.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKeyEnvVar))
        {
            throw new InvalidOperationException(
                "Patents ApiKeyEnvVar must name the environment variable holding the USPTO ODP API key "
                    + "(default \"PATENTSVIEW_API_KEY\") — the key is never committed to config, so a blank env-var "
                    + "name leaves the collector no way to read it.");
        }

        // Scheme-checked via the shared helper — absoluteness alone is platform-dependent (see IsAbsoluteHttpUri).
        if (!IsAbsoluteHttpUri(options.BaseUrl))
        {
            throw new InvalidOperationException(
                "Patents BaseUrl must be a valid absolute http/https URL; configure Radar:Patents:BaseUrl to the "
                    + "USPTO ODP host (default \"https://api.uspto.gov\") — a blank/invalid value only surfaces "
                    + "later as a confusing \"unreachable\" failure.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<IPatentSearchReader, HttpPatentSearchReader>(client =>
            {
                // A generic, polite User-Agent (the API needs a key, not a specific UA). Set at the single
                // client-config site so the header is consistent for every patents request.
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Radar/1.0 (+https://github.com/shapetastic/radar)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, PatentActivityCollector>();
        return services;
    }

    /// <summary>
    /// Registers the openFDA device clearance/approval activity collector (spec 129) and the typed
    /// <c>HttpClient</c> its <see cref="IFdaClearanceReader"/> uses. The collector reads the per-company
    /// <c>fda</c> feeds supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each
    /// feed's <c>Url</c> is an <c>applicant=...</c> token), counts recently-cleared 510(k)/PMA devices, and
    /// produces one raw <see cref="Radar.Application.Collectors.CollectedEvidence"/> per company; it does not
    /// persist them. All HTTP/JSON/openFDA code stays in Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="FdaCollectorOptions.LookbackDays"/>,
    /// <see cref="FdaCollectorOptions.MaxSampleClearances"/>, or <see cref="FdaCollectorOptions.MaxPageSize"/>
    /// is zero/negative: each of those would let the collector run yet silently collect nothing, so they are
    /// treated as configuration errors. openFDA needs NO API key (opt-in OFF ⇒ baseline untouched). The named
    /// client sets a generic User-Agent and enables automatic gzip/deflate decompression (polite).
    /// </para>
    /// </summary>
    public static IServiceCollection AddFdaClearanceCollector(
        this IServiceCollection services, FdaCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.LookbackDays <= 0)
        {
            throw new InvalidOperationException(
                "FDA LookbackDays must be greater than zero; configure Radar:Fda:LookbackDays to a positive "
                    + "window (default 365) — a zero/negative value collects nothing while still running.");
        }

        if (options.MaxSampleClearances <= 0)
        {
            throw new InvalidOperationException(
                "FDA MaxSampleClearances must be greater than zero; configure Radar:Fda:MaxSampleClearances to a "
                    + "positive sample bound (default 5) — a zero/negative value is nonsensical configuration.");
        }

        if (options.MaxPageSize <= 0)
        {
            throw new InvalidOperationException(
                "FDA MaxPageSize must be greater than zero; configure Radar:Fda:MaxPageSize to a positive page "
                    + "cap (default 100) — a zero/negative value collects nothing while still running.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<IFdaClearanceReader, HttpFdaClearanceReader>(client =>
            {
                // A generic, polite User-Agent (openFDA needs no key or specific UA). Set at the single
                // client-config site so the header is consistent for every FDA request.
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Radar/1.0 (+https://github.com/shapetastic/radar)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, FdaClearanceCollector>();
        return services;
    }

    /// <summary>
    /// Registers the USPTO trademark-activity collector (spec 130) and the typed <c>HttpClient</c> its
    /// <see cref="ITrademarkSearchReader"/> uses. The collector reads the per-company <c>trademarks</c> feeds
    /// supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each feed's <c>Url</c> is
    /// an <c>owner=...</c> token), counts recently-filed trademark applications, and produces one raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> per company; it does not persist them. All
    /// HTTP/JSON code stays in Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="TrademarkCollectorOptions.LookbackDays"/>,
    /// <see cref="TrademarkCollectorOptions.MaxSampleMarks"/>, or <see cref="TrademarkCollectorOptions.MaxPageSize"/>
    /// is zero/negative, or when <see cref="TrademarkCollectorOptions.ApiKeyEnvVar"/> is blank: each of those
    /// would let the collector run yet silently collect nothing (or have nowhere to read the key from), so they
    /// are treated as configuration errors. The API key VALUE is never in config — it is read at runtime from
    /// the env var NAMED by <see cref="TrademarkCollectorOptions.ApiKeyEnvVar"/>; a missing key degrades every
    /// trademark feed to a source failure (opt-in OFF ⇒ baseline untouched). The named client sets a generic
    /// User-Agent and enables automatic gzip/deflate decompression (polite).
    /// </para>
    /// </summary>
    public static IServiceCollection AddTrademarkActivityCollector(
        this IServiceCollection services, TrademarkCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.LookbackDays <= 0)
        {
            throw new InvalidOperationException(
                "Trademarks LookbackDays must be greater than zero; configure Radar:Trademarks:LookbackDays to a "
                    + "positive window (default 365) — a zero/negative value collects nothing while still running.");
        }

        if (options.MaxSampleMarks <= 0)
        {
            throw new InvalidOperationException(
                "Trademarks MaxSampleMarks must be greater than zero; configure Radar:Trademarks:MaxSampleMarks to a "
                    + "positive sample bound (default 5) — a zero/negative value is nonsensical configuration.");
        }

        if (options.MaxPageSize <= 0)
        {
            throw new InvalidOperationException(
                "Trademarks MaxPageSize must be greater than zero; configure Radar:Trademarks:MaxPageSize to a "
                    + "positive page cap (default 100) — a zero/negative value collects nothing while still running.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKeyEnvVar))
        {
            throw new InvalidOperationException(
                "Trademarks ApiKeyEnvVar must name the environment variable holding the USPTO API key "
                    + "(default \"USPTO_API_KEY\") — the key is never committed to config, so a blank env-var "
                    + "name leaves the collector no way to read it.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<ITrademarkSearchReader, HttpTrademarkSearchReader>(client =>
            {
                // A generic, polite User-Agent (the API needs a key, not a specific UA). Set at the single
                // client-config site so the header is consistent for every trademark request.
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Radar/1.0 (+https://github.com/shapetastic/radar)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, TrademarkActivityCollector>();
        return services;
    }

    /// <summary>
    /// Registers the GDELT DOC 2.0 news collector (Radar's first third-party market-attention source) and the
    /// typed <c>HttpClient</c> its <see cref="IGdeltNewsReader"/> uses. The collector reads the per-company
    /// <c>news</c> feeds supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each
    /// feed's <c>Url</c> is a <c>query=...&amp;ticker=...</c> token) and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> news articles; it does not persist them.
    /// All HTTP/JSON/GDELT code stays in Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="GdeltCollectorOptions.MaxRecordsPerCompany"/> is zero/negative, when
    /// <see cref="GdeltCollectorOptions.Timespan"/> is null/blank, when
    /// <see cref="GdeltCollectorOptions.InterRequestDelay"/> is negative, or when
    /// <see cref="GdeltCollectorOptions.MaxRetriesOn429"/> is negative: each of those would let the collector
    /// run yet either collect nothing, hammer GDELT's aggressive rate limit, or carry nonsensical config, so
    /// they are treated as configuration errors. The API needs no User-Agent or key; the named client only enables automatic
    /// gzip/deflate decompression (polite).
    /// </para>
    /// </summary>
    public static IServiceCollection AddGdeltNewsCollector(
        this IServiceCollection services, GdeltCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxRecordsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "GDELT MaxRecordsPerCompany must be greater than zero; configure "
                    + "Radar:Gdelt:MaxRecordsPerCompany to a positive cap (default 25) — a zero/negative value "
                    + "collects nothing while still running.");
        }

        if (string.IsNullOrWhiteSpace(options.Timespan))
        {
            throw new InvalidOperationException(
                "GDELT requires a non-blank timespan window; configure Radar:Gdelt:Timespan (default 2w) — a "
                    + "blank value collects nothing while still running.");
        }

        if (options.InterRequestDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "GDELT InterRequestDelay must not be negative; configure Radar:Gdelt:InterRequestDelaySeconds "
                    + "to a non-negative pacing delay (default 6s) — GDELT allows ~1 request/5s per IP, so pacing is required.");
        }

        if (options.MaxRetriesOn429 < 0)
        {
            throw new InvalidOperationException(
                "GDELT MaxRetriesOn429 must not be negative; configure Radar:Gdelt:MaxRetriesOn429 to a "
                    + "non-negative retry count (default 2) — a negative value is nonsensical configuration.");
        }

        if (options.RetryBackoff < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "GDELT RetryBackoff must not be negative; configure Radar:Gdelt:RetryBackoffSeconds to a "
                    + "non-negative base cool-down (default 60s) — the reader doubles it per 429 retry.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<IGdeltNewsReader, HttpGdeltNewsReader>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, GdeltNewsCollector>();
        return services;
    }

    /// <summary>
    /// Registers the Google News RSS news-attention collector (Radar's third-party market-attention source
    /// that is NOT per-IP throttled — the fix for GDELT's per-IP quota) and the typed <c>HttpClient</c> its
    /// <see cref="INewsSearchReader"/> uses. The collector reads the per-company <c>newssearch</c> feeds
    /// supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each feed's <c>Url</c> is
    /// a <c>query=...&amp;ticker=...</c> token) and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> news articles; it does not persist them.
    /// All HTTP/XML/source specifics stay in Infrastructure behind the reader (AD-5). This is a DISTINCT kind
    /// from the GDELT <c>news</c> collector, so both can be enabled independently.
    /// <para>
    /// Fails fast when <see cref="NewsCollectorOptions.MaxRecordsPerCompany"/> is zero/negative or when
    /// <see cref="NewsCollectorOptions.InterRequestDelay"/> is negative: each would let the collector run yet
    /// either collect nothing or carry nonsensical config, so they are treated as configuration errors. The
    /// endpoint needs no User-Agent or key (Google News RSS is keyless); the named client only enables
    /// automatic gzip/deflate decompression (polite).
    /// </para>
    /// </summary>
    public static IServiceCollection AddNewsAttentionCollector(
        this IServiceCollection services, NewsCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxRecordsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "News search MaxRecordsPerCompany must be greater than zero; configure "
                    + "Radar:News:MaxRecordsPerCompany to a positive cap (default 25) — a zero/negative value "
                    + "collects nothing while still running.");
        }

        if (options.InterRequestDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "News search InterRequestDelay must not be negative; configure Radar:News:InterRequestDelaySeconds "
                    + "to a non-negative pacing delay (default 1s) — Google News RSS is not per-IP throttled, so a small polite pace suffices.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<INewsSearchReader, HttpNewsSearchReader>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, NewsAttentionCollector>();
        return services;
    }

    /// <summary>
    /// Registers the ATS job-board hiring collector (spec 103) and the two named typed <c>HttpClient</c>s
    /// its per-platform <see cref="IJobBoardReader"/>s use (Greenhouse and Lever have different JSON
    /// shapes, so each platform gets its own reader + client). The collector reads the per-company
    /// <c>hiringats</c> feeds supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/>
    /// (each feed's <c>Url</c> is a <c>platform=…&amp;board=…</c> token) and produces exactly one raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> open-role snapshot per board carrying
    /// the fixed spec-103 hiring phrase; it does not persist them. All HTTP/JSON code stays in
    /// Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="HiringCollectorOptions.MaxSampleTitles"/> is negative: a negative sample
    /// bound is nonsensical configuration (zero is valid — it simply omits the metadata title sample). The
    /// APIs need no User-Agent or key (verified keyless access); the named clients only enable automatic
    /// gzip/deflate decompression (polite).
    /// </para>
    /// </summary>
    public static IServiceCollection AddHiringBoardCollector(
        this IServiceCollection services, HiringCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxSampleTitles < 0)
        {
            throw new InvalidOperationException(
                "Hiring MaxSampleTitles must not be negative; configure Radar:Hiring:MaxSampleTitles to a "
                    + "non-negative sample bound (default 5) — a negative value is nonsensical configuration.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<GreenhouseBoardReader>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.AddHttpClient<LeverBoardReader>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        // Surface both typed-client readers through the IJobBoardReader seam the collector's
        // platform→reader map consumes.
        services.AddSingleton<IJobBoardReader>(sp => sp.GetRequiredService<GreenhouseBoardReader>());
        services.AddSingleton<IJobBoardReader>(sp => sp.GetRequiredService<LeverBoardReader>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, HiringBoardCollector>();
        return services;
    }

    /// <summary>
    /// Registers Radar's config-driven AI chat-client seam: the <see cref="IChatClientFactory"/> (singleton) and a
    /// factory-produced singleton provider-neutral <see cref="IChatClient"/>, so future consumers can inject either.
    /// The provider is fixed at startup by <see cref="AiClientOptions.Provider"/> (case-insensitive) — <c>"anthropic"</c>
    /// (hosted), <c>"ollama"</c> (local, keyless), or <c>"openai"</c> (OpenAI-compatible host, e.g. DeepInfra). All
    /// concrete provider SDK types stay in Infrastructure (AD-5).
    /// Uses plain <c>AddSingleton</c> — the provider SDKs manage their own HTTP transport, so no named <c>HttpClient</c>
    /// is wired. There is no consumer of the client yet; this only proves a config-selected client can be obtained.
    /// <para>
    /// Fails fast when <see cref="AiClientOptions.Provider"/> is blank or unknown, when <see cref="AiClientOptions.Model"/>
    /// is blank, when the <c>anthropic</c> provider has a blank <see cref="AiClientOptions.AnthropicApiKey"/>, when the
    /// <c>ollama</c> provider has a blank or non-absolute-URI <see cref="AiClientOptions.OllamaEndpoint"/>, or when the
    /// <c>openai</c> provider has a blank or non-absolute-URI <see cref="AiClientOptions.OpenAiBaseUrl"/> or a blank
    /// <see cref="AiClientOptions.OpenAiApiKey"/>: each of those is a configuration error that would otherwise surface as
    /// an opaque failure at first use. The provider is validated first so a blank provider yields the provider message,
    /// not a spurious key/endpoint message. The openai key value is never logged — only the config keys are named.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRadarAi(
        this IServiceCollection services, AiClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Normalize (trim) every config string once so validation and the registered singleton agree, and so trailing
        // whitespace from env vars / copied JSON can't defeat the URI parse or reach the provider SDK.
        options = new AiClientOptions
        {
            Provider = options.Provider?.Trim() ?? string.Empty,
            Model = options.Model?.Trim() ?? string.Empty,
            AnthropicApiKey = options.AnthropicApiKey?.Trim() ?? string.Empty,
            OllamaEndpoint = options.OllamaEndpoint?.Trim() ?? string.Empty,
            OpenAiBaseUrl = options.OpenAiBaseUrl?.Trim() ?? string.Empty,
            OpenAiApiKey = options.OpenAiApiKey?.Trim() ?? string.Empty,
        };

        var provider = options.Provider;
        var isAnthropic = string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase);
        var isOllama = string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase);
        var isOpenAi = string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase);

        if (!isAnthropic && !isOllama && !isOpenAi)
        {
            throw new InvalidOperationException(
                "Radar AI requires a supported provider; configure Radar:Ai:Provider to \"anthropic\" (hosted), "
                    + "\"ollama\" (local, keyless), or \"openai\" (OpenAI-compatible host, e.g. DeepInfra) — a "
                    + "blank/unknown value has no client to build.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException(
                "Radar AI requires a model id; configure Radar:Ai:Model (e.g. \"claude-opus-4-8\" for anthropic or "
                    + "an installed tag like \"llama3.1\" for ollama) — a blank value has no model to call.");
        }

        if (isAnthropic && string.IsNullOrWhiteSpace(options.AnthropicApiKey))
        {
            throw new InvalidOperationException(
                "Radar AI \"anthropic\" is a hosted provider and requires an API key; configure Radar:Ai:Anthropic:ApiKey "
                    + "before selecting the anthropic provider — every request fails without it.");
        }

        // Scheme-checked via the shared helper — absoluteness alone is platform-dependent (see IsAbsoluteHttpUri).
        if (isOllama && !IsAbsoluteHttpUri(options.OllamaEndpoint))
        {
            throw new InvalidOperationException(
                "Radar AI \"ollama\" requires an absolute http/https endpoint URI; configure Radar:Ai:Ollama:Endpoint "
                    + "(default http://localhost:11434) — a blank or relative value cannot address the local Ollama server.");
        }

        if (isOpenAi && string.IsNullOrWhiteSpace(options.OpenAiBaseUrl))
        {
            throw new InvalidOperationException(
                "Radar AI \"openai\" requires a base URL; configure Radar:Ai:OpenAi:BaseUrl (e.g. DeepInfra "
                    + "https://api.deepinfra.com/v1/openai) — a blank BaseUrl has no host to address.");
        }

        if (isOpenAi && !IsAbsoluteHttpUri(options.OpenAiBaseUrl))
        {
            throw new InvalidOperationException(
                "Radar AI \"openai\" requires an absolute http/https base URL; configure Radar:Ai:OpenAi:BaseUrl "
                    + "(e.g. DeepInfra https://api.deepinfra.com/v1/openai) — a relative or malformed URL cannot "
                    + "address the host.");
        }

        if (isOpenAi && string.IsNullOrWhiteSpace(options.OpenAiApiKey))
        {
            throw new InvalidOperationException(
                "Radar AI \"openai\" is a hosted provider and requires an API key; point Radar:Ai:OpenAi:ApiKeyEnvVar "
                    + "at a SET environment variable holding the key before selecting the openai provider — the key is "
                    + "never committed to config and its value is never logged.");
        }

        services.AddSingleton(options);
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<IChatClientFactory>().Create());
        return services;
    }

    /// <summary>
    /// Registers Radar's first real AI capability: the config-selected <see cref="IChatClient"/>-backed
    /// <see cref="IFilingAnalyzer"/> (<see cref="ChatFilingAnalyzer"/>, singleton), which turns an earnings-release
    /// plain text (spec 73) into a typed, validated <see cref="Radar.Domain.Filings.FilingSentiment"/> — a
    /// directional read AS REPORTED (improving vs deteriorating trajectory), never advice. It does NOT register an
    /// <see cref="IChatClient"/>: it depends on the singleton client that <see cref="AddRadarAi"/> already
    /// registered, so call this only after (and inside the same opt-in gate as) <see cref="AddRadarAi"/>. All
    /// model-calling code stays in Infrastructure and uses only <c>Microsoft.Extensions.AI</c> abstractions (AD-5).
    /// <para>
    /// Fails fast when <paramref name="options"/> is null, or when
    /// <see cref="FilingAnalyzerOptions.MaxInputLength"/> is zero/negative: a non-positive cap would truncate
    /// every release to nothing (or throw at the substring), so it is treated as a configuration error rather
    /// than surfacing as an opaque failure at first use.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRadarFilingAnalyzer(
        this IServiceCollection services, FilingAnalyzerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxInputLength <= 0)
        {
            throw new InvalidOperationException(
                "Radar AI filing analyzer requires a positive input cap; configure Radar:Ai:MaxInputLength "
                    + "to a positive character count (default 12000) — a zero/negative value would truncate every "
                    + "earnings release to nothing.");
        }

        services.AddSingleton(options);
        services.AddSingleton<IFilingAnalyzer, ChatFilingAnalyzer>();
        return services;
    }

    /// <summary>
    /// Registers the opt-in directional filing-signal source (<see cref="DirectionalFilingSignalSource"/>,
    /// singleton) behind the Application <see cref="IDirectionalFilingSignalSource"/> seam. For an
    /// in-window earnings 8-K (form 8-K + item 2.02) it composes the merged
    /// <see cref="ISecEarningsReleaseReader"/> (EX-99.1 body) and <see cref="IFilingAnalyzer"/> (typed
    /// directional read) into at most one confidence-gated directional <c>GuidanceChange</c> signal
    /// (Improving -&gt; Positive, Deteriorating -&gt; Negative). It depends on the reader and analyzer, so
    /// call this only inside the same opt-in gate as (and after) <see cref="AddSecEarningsReleaseReader"/>
    /// and <see cref="AddRadarFilingAnalyzer"/>; it does NOT register either of those here. All HTTP/AI
    /// specifics stay behind the injected interfaces (AD-5).
    /// <para>
    /// Fails fast when <paramref name="options"/> is null, when
    /// <see cref="DirectionalFilingSignalOptions.MinConfidence"/> is outside [0,1], when
    /// <see cref="DirectionalFilingSignalOptions.MaxFilingsPerRun"/> is zero/negative, when
    /// <see cref="DirectionalFilingSignalOptions.MaxConsecutiveRateLimited"/> is negative, or when
    /// <see cref="DirectionalFilingSignalOptions.Strength"/> / <see cref="DirectionalFilingSignalOptions.Novelty"/>
    /// is outside the signal's valid [1,10] range: each is a configuration error that would otherwise gate every
    /// read to nothing, emit a signal that fails <c>SignalValidation</c>, or surface as an opaque failure.
    /// </para>
    /// <para>
    /// <see cref="DirectionalFilingSignalOptions.ModelIdentity"/> (spec 119) is deliberately NOT validated: it is
    /// a provenance/comparability label folded into the scoring fingerprint, not a behaviour switch, so a blank
    /// value hashes as "model not declared" rather than failing registration for a caller that has no model
    /// identity to declare.
    /// </para>
    /// </summary>
    public static IServiceCollection AddDirectionalFilingSignals(
        this IServiceCollection services, DirectionalFilingSignalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MinConfidence is < 0m or > 1m)
        {
            throw new InvalidOperationException(
                "Radar directional filing signals require a confidence gate in [0,1]; configure "
                    + "Radar:Ai:MinConfidence (default 0.6) — a value outside [0,1] can never gate a signal.");
        }

        if (options.MaxFilingsPerRun <= 0)
        {
            throw new InvalidOperationException(
                "Radar directional filing signals require a positive per-run cap; configure "
                    + "Radar:Ai:MaxFilingsPerRun (default 5) — a zero/negative value analyzes nothing while still running.");
        }

        if (options.MaxConsecutiveRateLimited < 0)
        {
            throw new InvalidOperationException(
                "Radar directional filing signals require a non-negative 429 circuit-breaker threshold; configure "
                    + "Radar:Ai:MaxConsecutiveRateLimited (default 2, 0 disables) — a negative value is nonsensical configuration.");
        }

        if (options.Strength is < 1 or > 10)
        {
            throw new InvalidOperationException(
                "Radar directional filing signals require a signal strength in [1,10]; configure Radar:Ai:Strength "
                    + "(default 6) — a value outside the signal's valid range fails SignalValidation at runtime.");
        }

        if (options.Novelty is < 1 or > 10)
        {
            throw new InvalidOperationException(
                "Radar directional filing signals require a signal novelty in [1,10]; configure Radar:Ai:Novelty "
                    + "(default 6) — a value outside the signal's valid range fails SignalValidation at runtime.");
        }

        services.AddSingleton(options);
        services.AddSingleton<IDirectionalFilingSignalSource, DirectionalFilingSignalSource>();
        return services;
    }

    /// <summary>
    /// Registers the file-backed per-accession earnings-analysis-result cache (spec 107,
    /// <see cref="FileAnalyzedFilingCache"/>) behind the Application <see cref="IAnalyzedFilingCache"/> seam,
    /// writing one <c>{accession}.json</c> under <paramref name="rootDirectory"/> via the shared
    /// <c>GracefulFileWriter</c> + <c>RadarFileStoreJson.Options</c> scaffolding (fail-safe reads → cache miss).
    /// <see cref="DirectionalFilingSignalSource"/> consumes it to replay a previously-analyzed filing instead of
    /// re-fetching the same <c>www.sec.gov</c> exhibit every run. This is an AD-14 analogue: operational/reference
    /// data, NOT an <see cref="IEvidenceCollector"/>, not evidence, not a signal source, and not a
    /// scoring/fingerprint input — it only changes WHETHER a fetch happens, never the signal that is scored.
    /// <para>
    /// The optional <paramref name="modelIdentity"/> (spec 118) scopes the cache files to a filename-safe
    /// per-model sub-directory segment, so switching the earnings-read provider/model re-analyzes filings (a clean
    /// cache MISS) instead of replaying another model's cached reads. Blank/null ⇒ files live directly under
    /// <paramref name="rootDirectory"/> (byte-identical to the pre-spec-118 layout).
    /// </para>
    /// </summary>
    public static IServiceCollection AddFileAnalyzedFilingCache(
        this IServiceCollection services, string rootDirectory, string? modelIdentity = null)
    {
        services.AddSingleton(new FileAnalyzedFilingCacheOptions
        {
            RootDirectory = rootDirectory,
            ModelSegment = CacheModelSegment(modelIdentity),
        });
        services.AddSingleton<IAnalyzedFilingCache, FileAnalyzedFilingCache>();
        return services;
    }

    // Builds a filename-safe, collision-resistant per-model cache-folder segment from a provider/model identity. A
    // readable lower-cased token (letters/digits/.-_ kept, everything else → '-') plus a stable 64-bit (16-hex) hash
    // of the EXACT raw identity, so two ids that sanitize to the same readable token (e.g. "a/b" vs "a-b") are
    // extremely unlikely to collide into one folder (which would let a switch between them replay). Blank/null ⇒
    // empty segment (files live at the root, back-compat).
    private static string CacheModelSegment(string? modelIdentity)
    {
        if (string.IsNullOrWhiteSpace(modelIdentity))
        {
            return string.Empty;
        }

        var raw = modelIdentity.Trim();
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw.ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-');
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        var suffix = Convert.ToHexStringLower(hash)[..16];
        return sb.ToString() + "-" + suffix;
    }

    /// <summary>
    /// Registers the opt-in file-backed AI filing-read debug store (spec 115,
    /// <see cref="FileFilingReadDebugStore"/>) behind the Application <see cref="IFilingReadDebugSink"/> seam,
    /// writing one <c>{accession}.json</c> under <paramref name="rootDirectory"/> via the shared
    /// <c>GracefulFileWriter</c> + <c>RadarFileStoreJson.Options</c> scaffolding (best-effort: a write failure
    /// logs and never aborts a run). This is diagnostic-only (AD-14 read-side discipline): consumed by NOTHING
    /// in the evidence/signal/scoring/report path and never a fingerprint input — it only records what each AI
    /// filing-read attempt saw and concluded (including no-signal and empty-body outcomes) so the model's
    /// behaviour is inspectable without re-running the pipeline. When this is NOT called (the default),
    /// <see cref="DirectionalFilingSignalSource"/>'s optional sink stays null and behaviour is byte-for-byte
    /// unchanged.
    /// </summary>
    public static IServiceCollection AddFileFilingReadDebugStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileFilingReadDebugStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IFilingReadDebugSink, FileFilingReadDebugStore>();
        return services;
    }

    /// <summary>
    /// Registers the local-file company watch-universe seed source and the idempotent seeder. The seed file
    /// at <paramref name="filePath"/> defines the companies/aliases that entity resolution can match
    /// against. Safe to invoke the seeder on every startup (upsert-by-Id, AD-1).
    /// </summary>
    public static IServiceCollection AddLocalFileCompanySeed(
        this IServiceCollection services, string filePath)
    {
        services.AddSingleton(new LocalFileCompanySeedOptions { FilePath = filePath });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICompanySeedSource, LocalFileCompanySeedSource>();
        services.AddSingleton<ICompanyUniverseSeeder, CompanyUniverseSeeder>();
        return services;
    }

    /// <summary>
    /// Registers the insert-only file raw-evidence store that mirrors each newly-stored
    /// <see cref="Radar.Domain.Evidence.EvidenceItem"/> to
    /// <c>{rootDirectory}/{sourceType}/{yyyy}/{MM}/{contentHash}.json</c> (AD-8). The pipeline runner
    /// requires <see cref="Radar.Application.Evidence.IRawEvidenceStore"/>; all file I/O stays in
    /// Infrastructure. Existing raw files are never overwritten (provenance, AD-1).
    /// </summary>
    public static IServiceCollection AddFileRawEvidenceStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileRawEvidenceStoreOptions { RootDirectory = rootDirectory });
        // Registered as the CONCRETE type first and exposed through the interface by delegation, so
        // AddDurableRadarSignalHistory can point IEvidenceRepository at the SAME singleton instance (one
        // instance ⇒ one hydration cache). Behaviourally identical to the previous direct registration for
        // every existing caller.
        services.AddSingleton<FileRawEvidenceStore>();
        services.AddSingleton<IRawEvidenceStore>(sp => sp.GetRequiredService<FileRawEvidenceStore>());
        return services;
    }

    /// <summary>
    /// Registers the file signal store that mirrors each reviewed
    /// <see cref="Radar.Domain.Signals.Signal"/> (with its embedded review) to
    /// <c>{rootDirectory}/{yyyy}/{MM}/{signalId}.json</c> (AD-8). The pipeline runner requires
    /// <see cref="Radar.Application.Signals.ISignalFileStore"/>; all file I/O stays in Infrastructure.
    /// Signals are upsert-by-Id, so an existing file is overwritten last-write-wins (AD-1 governs
    /// evidence immutability only).
    /// </summary>
    public static IServiceCollection AddFileSignalStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileSignalStoreOptions { RootDirectory = rootDirectory });
        // Registered as the CONCRETE type first and exposed through the interface by delegation, so
        // AddDurableRadarSignalHistory can point ISignalRepository at the SAME singleton instance (one
        // instance ⇒ one hydration cache). Behaviourally identical to the previous direct registration for
        // every existing caller.
        services.AddSingleton<FileSignalStore>();
        services.AddSingleton<ISignalFileStore>(sp => sp.GetRequiredService<FileSignalStore>());
        return services;
    }

    /// <summary>
    /// Repoints the scoring path's <see cref="ISignalRepository"/> / <see cref="IEvidenceRepository"/> at
    /// the DURABLE file stores (spec 142) — the slice that lets scoring read accrued history at all.
    /// <para>
    /// Before this, both interfaces resolved to in-memory singletons that started EMPTY every process,
    /// while the durable history was written through a different, disconnected abstraction. Scoring
    /// therefore only ever saw what the current process had just collected, which made spec 136's
    /// point-in-time predicate near-vacuous and spec 139's replay inert. The reconciliation choice, recorded
    /// on the store types themselves, is that <b>the repository IS the file store</b>: no third abstraction,
    /// no second copy of the persisted shape.
    /// </para>
    /// <para>
    /// Requires <see cref="AddFileRawEvidenceStore"/> and <see cref="AddFileSignalStore"/> to have been
    /// called (this resolves the very singletons they register). The two interfaces are
    /// <see cref="ServiceCollectionDescriptorExtensions.RemoveAll{T}(IServiceCollection)"/>'d first so a
    /// previously-registered in-memory implementation is gone from the <c>IEnumerable&lt;T&gt;</c> view too,
    /// not merely shadowed by a later "last registration wins" descriptor.
    /// </para>
    /// <para>
    /// <b>Behaviour change, stated plainly:</b> with a durable evidence repository,
    /// <see cref="IEvidenceRepository.AddIfNewAsync"/> returns <c>false</c> for evidence collected in a
    /// PREVIOUS run, so re-running collection no longer re-extracts signals from already-seen evidence.
    /// That is the spec's idempotency criterion.
    /// </para>
    /// </summary>
    public static IServiceCollection AddDurableRadarSignalHistory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<ISignalRepository>();
        services.RemoveAll<IEvidenceRepository>();

        services.AddSingleton<ISignalRepository>(sp => sp.GetRequiredService<FileSignalStore>());
        services.AddSingleton<IEvidenceRepository>(sp => sp.GetRequiredService<FileRawEvidenceStore>());
        return services;
    }

    /// <summary>
    /// Registers the file score-snapshot store that mirrors each
    /// <see cref="Radar.Domain.Scoring.CompanyScoreSnapshot"/> together with its
    /// <see cref="Radar.Domain.Scoring.ScoreEvidenceLink"/>s to
    /// <c>{rootDirectory}/{companyId}/{snapshotId}.json</c> (AD-8). The pipeline runner requires
    /// <see cref="Radar.Application.Scoring.IScoreSnapshotFileStore"/>; all file I/O stays in
    /// Infrastructure. Snapshots are upsert-by-Id, so an existing file is overwritten last-write-wins
    /// (AD-1 governs evidence immutability only).
    /// <para>
    /// Also registers the <see cref="IScoreSnapshotFileStoreFactory"/> the pipeline runner uses to route each
    /// scoring strategy's snapshots (spec 137): the PRIMARY strategy gets this very store at
    /// <paramref name="rootDirectory"/> (unchanged), and every non-primary strategy gets one rooted at
    /// <c>{rootDirectory}/strategies/{strategyName}/</c> so the series never collide.
    /// </para>
    /// </summary>
    public static IServiceCollection AddFileScoreStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileScoreSnapshotStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IScoreSnapshotFileStore, FileScoreSnapshotStore>();
        services.TryAddSingleton<IScoreSnapshotFileStoreFactory, StrategyScopedScoreSnapshotFileStoreFactory>();
        return services;
    }

    /// <summary>
    /// Registers the file report writer that writes each built weekly report's markdown to
    /// <c>{rootDirectory}/weekly/radar-weekly-{yyyy-MM-dd}.md</c>. The pipeline runner requires
    /// <see cref="Radar.Application.Reporting.IReportFileWriter"/>; all file I/O stays in
    /// Infrastructure. Reports are derived views, so an existing file may be overwritten (AD-1 governs
    /// evidence immutability only).
    /// </summary>
    public static IServiceCollection AddFileReportWriter(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileReportWriterOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IReportFileWriter, FileReportWriter>();
        return services;
    }

    /// <summary>
    /// Registers the file pipeline-run store that writes one <see cref="PipelineRunRecord"/> per
    /// completed run to <c>{rootDirectory}/{yyyy}/{MM}/run-...json</c> (AD-8), the append-only run log.
    /// The pipeline runner requires <see cref="IPipelineRunStore"/>; all file I/O stays in Infrastructure.
    /// Each run carries a fresh id, so files never collide and prior runs are never overwritten.
    /// </summary>
    public static IServiceCollection AddFilePipelineRunStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FilePipelineRunStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IPipelineRunStore, FilePipelineRunStore>();
        return services;
    }

    /// <summary>
    /// Registers the content-addressed effective-scoring-config store that writes the resolved scoring
    /// config to <c>{rootDirectory}/{fingerprint}.json</c> once per distinct config (spec 91), completing
    /// the spec-89 provenance chain: a snapshot's <c>ScoringConfigVersion</c> stamp dereferences back to the
    /// weights that produced it. The pipeline runner requires <see cref="IScoringConfigStore"/>; all file
    /// I/O stays in Infrastructure. Insert-if-new (immutable, AD-1 mirror): an existing file is never
    /// overwritten.
    /// </summary>
    public static IServiceCollection AddFileScoringConfigStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileScoringConfigStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IScoringConfigStore, FileScoringConfigStore>();
        return services;
    }

    /// <summary>
    /// Registers the keyless Yahoo chart v8 daily price-history reader behind the Application
    /// <see cref="IPriceHistoryReader"/> seam (AD-14), plus the typed <c>HttpClient</c> it uses (browser
    /// <c>User-Agent</c> + gzip/deflate decompression, mirroring <see cref="AddSecEarningsReleaseReader"/>). This
    /// is a SEPARATE seam from the evidence collectors: it is NOT an <see cref="IEvidenceCollector"/>, produces no
    /// <c>CollectedEvidence</c>, and is not added to <c>Radar:Collectors</c>. All HTTP/JSON/Yahoo specifics stay in
    /// Infrastructure (AD-5). No key/secret/paid service.
    /// <para>
    /// Fails fast when <paramref name="range"/> is not a known Yahoo <c>validRanges</c> value — a typo'd range
    /// would otherwise silently return an empty series. <c>PriceReaderOptions</c> stays Infrastructure-internal
    /// (AD-5); the caller supplies only the range token.
    /// </para>
    /// </summary>
    public static IServiceCollection AddHttpPriceHistoryReader(
        this IServiceCollection services, string range)
    {
        var options = new PriceReaderOptions { Range = range };

        // Fail fast at registration on an invalid range.
        options.Validate();

        services.AddSingleton(options);

        services.AddHttpClient<IPriceHistoryReader, HttpPriceHistoryReader>(client =>
            {
                // The Yahoo chart endpoint requires a browser-like User-Agent (verified) but no cookie/crumb.
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        return services;
    }

    /// <summary>
    /// Registers the file price-history reference store that persists each ticker's daily bars to
    /// <c>{rootDirectory}/{ticker}.json</c> (AD-14) via the shared <c>GracefulFileWriter</c> +
    /// <c>RadarFileStoreJson.Options</c> scaffolding, merging/deduping bars by <c>Date</c> (last-write-wins per
    /// date, ascending). Consumers require the Application <see cref="IPriceHistoryStore"/>; all file I/O stays in
    /// Infrastructure. This store is consumed by NOTHING in the scoring/evidence/signal/report path — it exists
    /// solely for a future price-efficacy validation/backtest spec.
    /// </summary>
    public static IServiceCollection AddFilePriceHistoryStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FilePriceHistoryStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IPriceHistoryStore, FilePriceHistoryStore>();
        return services;
    }

    /// <summary>
    /// Registers the file efficacy-artifact store that writes each company's price-efficacy SVG + CSV to
    /// <c>{rootDirectory}/{ticker}.{svg,csv}</c> (AD-14 read side) via the shared <c>GracefulFileWriter</c>,
    /// keyed by the shared <c>FileTickerKey</c> (the same on-disk ticker key the price store uses). Consumers
    /// require the Application <see cref="IEfficacyArtifactStore"/>; all file I/O stays in Infrastructure. This
    /// store writes ONLY efficacy artifacts — never evidence/signal/score.
    /// </summary>
    public static IServiceCollection AddFileEfficacyArtifactStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileEfficacyArtifactStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IEfficacyArtifactStore, FileEfficacyArtifactStore>();
        return services;
    }

    /// <summary>
    /// Registers the opt-in price-efficacy reporting step (AD-14 read side): the <see cref="EfficacyDatasetBuilder"/>
    /// (the deterministic no-look-ahead JOIN over score history + price), the pure <see cref="EfficacySvgRenderer"/>
    /// + <see cref="EfficacyCsvRenderer"/>, and the <see cref="IEfficacyReportGenerator"/> that composes them. It
    /// depends on <see cref="ICompanyRepository"/>, <see cref="IScoreSnapshotFileStore"/>,
    /// <see cref="IPriceHistoryStore"/> (all read-only) and <see cref="IEfficacyArtifactStore"/>; call
    /// <see cref="AddFileEfficacyArtifactStore"/> alongside it. It has NO evidence/signal/scoring write dependency
    /// and runs OUTSIDE <c>IRadarPipeline</c>.
    /// </summary>
    public static IServiceCollection AddRadarEfficacyReport(this IServiceCollection services)
    {
        services.AddSingleton<EfficacyDatasetBuilder>();
        services.AddSingleton<EfficacySvgRenderer>();
        services.AddSingleton<EfficacyCsvRenderer>();
        services.AddSingleton<IEfficacyReportGenerator, EfficacyReportGenerator>();
        return services;
    }

    /// <summary>
    /// Registers the opt-in <b>strategy-vs-price comparison</b> (spec 140, AD-14 read side): the pure
    /// <see cref="StrategyComparisonHarness"/> + <see cref="StrategyLeaderboardRenderer"/> and the
    /// <see cref="IStrategyComparisonReportGenerator"/> that composes them over each configured strategy's
    /// persisted score series.
    /// <para>
    /// It reuses <see cref="AddRadarEfficacyReport"/>'s <see cref="EfficacyDatasetBuilder"/> (the SAME
    /// no-look-ahead join, run once per strategy) and the SAME <see cref="IEfficacyArtifactStore"/>, so call
    /// this alongside those. No second price source is registered — spec 140 forbids one, and the existing
    /// <see cref="IPriceHistoryStore"/> is sufficient.
    /// </para>
    /// <para>
    /// The score series read defaults to the LIVE forward one
    /// (<see cref="LiveStrategyScoreSnapshotStoreSelector"/> over the registered
    /// <see cref="IScoreSnapshotFileStoreFactory"/>). It is registered with <c>TryAdd</c>, so a caller that has
    /// already registered <see cref="AddRadarStrategyComparisonOverReplay"/>'s replay-scoped selector keeps it.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRadarStrategyComparison(
        this IServiceCollection services, StrategyComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.TryAddSingleton<IStrategyScoreSnapshotStoreSelector, LiveStrategyScoreSnapshotStoreSelector>();
        services.AddSingleton<StrategyComparisonHarness>();
        services.AddSingleton<StrategyLeaderboardRenderer>();
        services.AddSingleton<IStrategyComparisonReportGenerator, StrategyComparisonReportGenerator>();
        return services;
    }

    /// <summary>
    /// Registers the spec-140 comparison reading ONE spec-139 <b>replay</b> run's per-strategy output instead
    /// of the live forward series (<c>Radar:Efficacy:Comparison:ReplayLabel</c>). Everything else — the join,
    /// the harness, the renderer, the artifact store — is identical; only WHICH persisted series is read
    /// changes.
    /// <para>
    /// It registers its own <see cref="IReplayScoreSnapshotFileStoreFactory"/> rooted at
    /// <paramref name="replayRootDirectory"/> because <see cref="AddRadarReplay"/> is registered only when the
    /// run IS a replay — and a replay run replaces the pipeline run entirely, so it never reaches the efficacy
    /// step. Both use <c>TryAdd</c> and construct the same factory type over the same root, so a graph that
    /// somehow had both is consistent either way.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRadarStrategyComparisonOverReplay(
        this IServiceCollection services,
        StrategyComparisonOptions options,
        string replayRootDirectory,
        string replayLabel)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(replayRootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(replayLabel);

        services.TryAddSingleton<IReplayScoreSnapshotFileStoreFactory>(sp =>
            new ReplayScopedScoreSnapshotFileStoreFactory(
                replayRootDirectory,
                sp.GetRequiredService<ILogger<FileScoreSnapshotStore>>()));

        services.TryAddSingleton<IStrategyScoreSnapshotStoreSelector>(sp =>
            new ReplayLabelStrategyScoreSnapshotStoreSelector(
                sp.GetRequiredService<IReplayScoreSnapshotFileStoreFactory>(),
                replayLabel));

        return services.AddRadarStrategyComparison(options);
    }

    /// <summary>
    /// Registers the opt-in <b>replay</b> harness (spec 139): scoring the configured strategies across a series
    /// of historical as-of instants from the ALREADY-STORED signals, into a replay-scoped, labelled location
    /// under <paramref name="replayRootDirectory"/>. Read-only over signals/evidence; it never collects,
    /// extracts, re-runs the AI read, reports, or writes a run record.
    /// <para>
    /// The replay engines are built by the SAME <see cref="ScoringStrategyFactory"/> the live pipeline uses,
    /// over the SAME <see cref="ScoringStrategySet"/> and every other scoring dependency resolved from this
    /// container — so a replayed strategy is configured EXACTLY like its live counterpart (same weights, same
    /// <c>ScoringConfigVersion</c> fingerprint, same signal-type filter). That identity is what the
    /// replay⊆forward invariant rests on, so nothing here re-derives or substitutes a scoring input.
    /// </para>
    /// <para>
    /// Exactly TWO things are swapped, and both are about isolation rather than scoring:
    /// </para>
    /// <list type="bullet">
    /// <item>a <see cref="ReplayScoreRepositoryFactory"/>, so every strategy — the primary included — writes
    /// into its own in-memory repository instead of the shared one the weekly report renders;</item>
    /// <item>a <see cref="ReplayScopedScoreSnapshotFileStoreFactory"/> rooted at
    /// <paramref name="replayRootDirectory"/> (NOT under the scores root), so a replay can never write into
    /// the forward efficacy series.</item>
    /// </list>
    /// <para>
    /// Requires the persistence registration (<see cref="AddInMemoryRadarPersistence"/>), the application
    /// services (<see cref="AddRadarApplicationServices"/>), a signal file store
    /// (<see cref="AddFileSignalStore"/>), a scoring-config store
    /// (<see cref="AddFileScoringConfigStore"/> — spec 148: the runner records each strategy's effective
    /// config and runs the startup identity tripwire, exactly as the forward runners do), and a registered
    /// <see cref="ReplayPlan"/> — the composition root
    /// owns the plan because parsing a <c>from/to/step</c> series out of configuration is a config concern that
    /// must not leak into <c>Radar.Application</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRadarReplay(
        this IServiceCollection services, string replayRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayRootDirectory);

        services.TryAddSingleton<IReplayScoreSnapshotFileStoreFactory>(sp =>
            new ReplayScopedScoreSnapshotFileStoreFactory(
                replayRootDirectory,
                sp.GetRequiredService<ILogger<FileScoreSnapshotStore>>()));

        services.TryAddSingleton<IReplayScoringStrategyFactory>(sp => new ReplayScoringStrategyFactory(
            new ScoringStrategyFactory(
                sp.GetRequiredService<ScoringStrategySet>(),
                sp.GetRequiredService<ISignalRepository>(),
                sp.GetRequiredService<ISignalFileStore>(),
                sp.GetRequiredService<IEvidenceRepository>(),
                // The ONE scoring-graph substitution: replay-scoped score repositories.
                new ReplayScoreRepositoryFactory(),
                sp.GetRequiredService<ICompanyRepository>(),
                sp.GetRequiredService<IScoreFormulaFactory>(),
                sp.GetRequiredService<IAttentionSourceWeights>(),
                sp.GetRequiredService<ISignalSourceDescriptor>(),
                sp.GetRequiredService<InsiderMaterialityWeights>(),
                sp.GetRequiredService<MediaAttentionCollapse>(),
                sp.GetRequiredService<ScoringOptions>(),
                sp.GetRequiredService<ILogger<ScoringEngine>>())));

        services.TryAddSingleton<IReplayRunner, ReplayRunner>();
        return services;
    }

    /// <summary>
    /// Registers the end-to-end (COMBINED) pipeline runner — collect → … → score → report in one pass, which
    /// is what <c>Radar:RunMode</c> <c>full</c> (the default) runs and what every existing composition means.
    /// Requires the persistence registration (<see cref="AddInMemoryRadarPersistence"/>), the application
    /// services (<see cref="AddRadarApplicationServices"/>), and an evidence collector
    /// (e.g. <see cref="AddLocalFileCollector"/>) to also be registered.
    /// <para>
    /// Spec 144 split the runner's body into <see cref="ICollectionPass"/> (stages 1–5) and
    /// <see cref="IScoringPass"/> (stage 6), both registered here. The combined runner's observable behaviour
    /// — stage order, counters, log line and run record — is unchanged; see
    /// <see cref="AddRadarCollectOnlyPipeline"/> / <see cref="AddRadarScoreOnlyPipeline"/> for the two
    /// standalone verbs.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRadarPipeline(this IServiceCollection services)
    {
        services.TryAddSingleton(new PipelineOptions());
        services.TryAddSingleton<ICollectionPass, CollectionPass>();
        services.TryAddSingleton<IScoringPass, ScoringPass>();
        services.AddSingleton<IRadarPipeline, RadarPipelineRunner>();
        return services;
    }

    /// <summary>
    /// Registers the standalone <c>collect</c> pipeline (spec 144): stages 1–5 only — collect, extract,
    /// resolve, review, store — then the run record. It runs the SAME <see cref="ICollectionPass"/> the
    /// combined runner does; it simply never scores and never reports.
    /// <para>
    /// Same prerequisites as <see cref="AddRadarPipeline"/>, including at least one registered
    /// <see cref="IEvidenceCollector"/>. <see cref="IScoringPass"/> is deliberately NOT registered — a collect
    /// pass has no scoring stage to reach.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRadarCollectOnlyPipeline(this IServiceCollection services)
    {
        services.TryAddSingleton(new PipelineOptions());
        services.TryAddSingleton<ICollectionPass, CollectionPass>();
        services.AddSingleton<IRadarPipeline, CollectOnlyPipelineRunner>();
        return services;
    }

    /// <summary>
    /// Registers the standalone <c>score</c> pipeline (spec 144): stage 6 (+ optionally 7) over the accrued
    /// durable stores, with no collection and no AI read. It runs the SAME <see cref="IScoringPass"/> the
    /// combined runner does, so there is exactly one stage-6 loop.
    /// <para>
    /// <see cref="ICollectionPass"/> is deliberately NOT registered, and the composition root registers no
    /// <see cref="IEvidenceCollector"/> at all in this mode, so no collector is even constructed. Requires
    /// the DURABLE signal/evidence read path (<see cref="AddDurableRadarSignalHistory"/>, spec 142) to be
    /// meaningful: without it the scoring repositories start empty every process and a standalone score pass
    /// would score nothing.
    /// </para>
    /// <para>
    /// A <see cref="ScoringPassOptions"/> registered by the composition root selects the as-of instant; the
    /// <c>TryAdd</c> default here means "now".
    /// </para>
    /// </summary>
    public static IServiceCollection AddRadarScoreOnlyPipeline(this IServiceCollection services)
    {
        services.TryAddSingleton(new PipelineOptions());
        services.TryAddSingleton(new ScoringPassOptions());
        services.TryAddSingleton<IScoringPass, ScoringPass>();
        services.AddSingleton<IRadarPipeline, ScoreOnlyPipelineRunner>();
        return services;
    }

    /// <summary>
    /// True when <paramref name="value"/> is a non-blank absolute URI with an <c>http</c> or <c>https</c>
    /// scheme — the single shared check behind every "must be an absolute URL" DI fail-fast in this class
    /// (patents BaseUrl, Ollama endpoint, OpenAI base URL).
    /// </summary>
    /// <remarks>
    /// The scheme test is load-bearing, not defensive. <see cref="UriKind.Absolute"/> on its own is
    /// PLATFORM-DEPENDENT for a rooted path such as <c>/api/v1/patent</c>: it is rejected on Windows but
    /// ACCEPTED on Unix, where it parses as the absolute file URI <c>file:///api/v1/patent</c>. A validation
    /// built on absoluteness alone therefore passes on a Windows dev machine and silently admits a broken
    /// value on a Linux runner — exactly how a rooted-path case reached CI green locally and red on ubuntu.
    /// Constraining to http/https makes the result identical on every platform and matches what an
    /// <see cref="System.Net.Http.HttpClient"/> base address actually requires.
    /// </remarks>
    private static bool IsAbsoluteHttpUri(string? value) =>
        !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
