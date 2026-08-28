using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.EntityResolution;
using Radar.Application.News;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Companies;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.News;
using Radar.Infrastructure.Persistence.InMemory;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// Spec 196 §7 — the READ-ONLY PAIRED COUNTERFACTUAL that reports what the corrected attention measure
/// actually produces over the live universe (CLAUDE.md's "no measure ships without its live distribution").
/// <para>
/// <b>Why paired and not two nightly runs.</b> Comparing consecutive baselines is confounded: the tier
/// policy and the underlying evidence both change, so the delta shows their sum rather than the policy's
/// effect. Here ONE fixed as-of instant, ONE evidence/signal input set, ONE universe and ONE strategy are
/// held constant and <b>only the <see cref="IAttentionSourceWeights"/> instance differs</b> between the two
/// arms.
/// </para>
/// <para>
/// <b>Nothing is persisted.</b> The durable signal and raw-evidence stores are hydrated READ-ONLY (spec 142:
/// <c>FileSignalStore</c>/<c>FileRawEvidenceStore</c> also implement the repositories) and scores go to an
/// in-memory score repository — no snapshot, no run record, no scoring-config identity record, no report.
/// The composition registers no score file store, no run store, no scoring-config store, no report writer
/// and no collector, so there is nothing this harness could write even by accident. It mirrors the spec-139
/// replay discipline and the spec-183 affine-invariance proof; it is not a new mechanism.
/// </para>
/// <para>
/// <b>ENV-GATED so CI stays green and fast</b> (the <c>WindowsPowerShellFactAttribute</c> precedent — skipped
/// with a NAMED reason, never silently). Set <c>RADAR_ATTENTION_COUNTERFACTUAL_DATA_ROOT</c> to a Radar data
/// root (the one holding <c>companies.json</c>, <c>signals/</c>, <c>evidence/raw/</c> and
/// <c>news-observations/</c>) to run it. The output is written to the test log as markdown, ready to paste
/// into a PR body. Deterministic (AD-3): fixed instant, ordinal/id orderings, invariant formatting.
/// </para>
/// </summary>
public sealed class AttentionPolicyCounterfactualTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_ATTENTION_COUNTERFACTUAL_DATA_ROOT";

    internal const string SkipReason =
        "Spec 196 §7 paired counterfactual: set " + DataRootVariable
            + " to a Radar data root (companies.json, signals/, evidence/raw/, news-observations/) to run it.";

    /// <summary>The data root under measurement, or null when the variable is unset/not a directory.</summary>
    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    /// <summary>
    /// The pinned as-of instant: the last completed baseline's <c>windowEndUtc</c>, the same instant the
    /// spec-196 corpus measurement and the committed publisher audit use. Pinned because the boundary moves
    /// — re-measuring later ages observations out and produces the same corpus read at a different moment,
    /// not a different result.
    /// </summary>
    private static readonly DateTimeOffset AsOfUtc = DateTimeOffset.Parse(
        "2026-08-27T21:42:45.4943606Z",
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>The live baseline's <c>Radar:ScoringWindowDays</c> (60), NOT the 30-day code default.</summary>
    private static readonly TimeSpan ScoringWindow = TimeSpan.FromDays(60);

    /// <summary>
    /// The PRE-spec-196 tier map, reconstructed here so the control arm is self-contained and cannot drift
    /// when the shipped default is next curated. Verbatim from the pre-196
    /// <see cref="AttentionSourceTierOptions.Default"/>: unknown 0.25, two tiers.
    /// </summary>
    private static AttentionSourceTierOptions OldPolicy() => new()
    {
        UnknownWeight = 0.25,
        SourceTiers = new Dictionary<string, AttentionSourceTierOptions.SourceTier>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Mill"] = new AttentionSourceTierOptions.SourceTier
            {
                Weight = 0.1,
                Publishers = new[]
                {
                    "MarketBeat", "Zacks", "Simply Wall St", "StockStory", "Moomoo", "TradingView",
                    "Stock Titan", "GuruFocus", "Defense World", "Pluang", "MarketScreener",
                    "Finviz", "Investing.com", "Insider Monkey", "Benzinga", "TipRanks", "StockAnalysis",
                    "Simplywall.st",
                },
            },
            ["Genuine"] = new AttentionSourceTierOptions.SourceTier
            {
                Weight = 1.0,
                Publishers = new[]
                {
                    "Reuters", "Bloomberg", "The Wall Street Journal", "CNBC", "Associated Press",
                    "Financial Times", "SpaceNews",
                },
            },
        },
    };

    [LiveDataRootFact]
    public async Task PairedCounterfactual_OldVersusNewPublisherTierPolicy_OverTheLiveUniverse()
    {
        var root = DataRoot()!;
        var ct = CancellationToken.None;

        // Every number below is rendered invariant (AD-3): a de-DE test host must produce the same report.
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var report = new StringBuilder();

            await using var provider = BuildReadOnlyProvider(root);
            var seeded = await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);
            var companies = await provider.GetRequiredService<ICompanyRepository>().GetAllAsync(ct);
            var ordered = companies.OrderBy(c => c.Id).ToList();

            report.AppendLine("## Spec 196 §7 — paired attention-policy counterfactual (read-only)");
            report.AppendLine();
            report.AppendLine(
                $"As-of `{AsOfUtc:O}` · scoring window {ScoringWindow.TotalDays:0} days · strategy `default` "
                    + $"(`radar-formula-v8`, default weights) · {ordered.Count} companies seeded from "
                    + $"`companies.json` (seeder reported {seeded}).");
            report.AppendLine();
            report.AppendLine(
                "Only the `IAttentionSourceWeights` instance differs between the arms; the evidence, the "
                    + "signals, the instant, the universe and the strategy are identical. Nothing is persisted.");
            report.AppendLine();

            var oldWeights = new ConfiguredAttentionSourceWeights(OldPolicy());
            var newWeights = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);

            // (1) Raw observation coverage — for publisher-map maintenance, NOT the scoring unit.
            await AppendObservationCoverageAsync(report, provider, ordered, oldWeights, newWeights, ct);

            // (2) The distinct publisher/company breadth AttentionReach actually consumes.
            var publishersByCompany = await CollectBreadthPublishersAsync(provider, ordered, ct);
            AppendBreadthSection(report, publishersByCompany, oldWeights, newWeights);

            // (3)/(4) The score distributions, from the REAL engine, one pass per arm.
            // Both arms ask the previous-window store the IDENTICAL question, so the read is memoized
            // once rather than rescanned per arm. Read-through and answer-preserving; it changes no result,
            // only how many times the same files are opened.
            var windowReads = new MemoizingSignalWindowReads(
                provider.GetRequiredService<ISignalFileStore>());
            var oldScores = await ScoreAllAsync(provider, ordered, oldWeights, windowReads, ct);
            var newScores = await ScoreAllAsync(provider, ordered, newWeights, windowReads, ct);

            AppendDistribution(
                report, "3. `AttentionScore` distribution", oldScores, newScores, s => s.AttentionScore);
            AppendDistribution(
                report, "4. `OpportunityScore` distribution", oldScores, newScores, s => s.OpportunityScore);

            output.WriteLine(report.ToString());

            // The measurement is the deliverable; these guard only that it measured SOMETHING, so a silently
            // empty data root can never be reported as a result.
            Assert.NotEmpty(ordered);
            Assert.Equal(ordered.Count, newScores.Count);
            Assert.Equal(oldScores.Count, newScores.Count);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>
    /// A read-only composition over the live data root: durable signal history + raw evidence hydrated from
    /// disk (spec 142), the company seed, the news-observation archive, and an IN-MEMORY score repository.
    /// </summary>
    private static ServiceProvider BuildReadOnlyProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        services.AddLocalFileCompanySeed(Path.Combine(root, "companies.json"));
        services.AddFileRawEvidenceStore(Path.Combine(root, "evidence", "raw"));
        services.AddFileSignalStore(Path.Combine(root, "signals"));
        services.AddDurableRadarSignalHistory();
        services.AddFileNewsObservationArchive(Path.Combine(root, "news-observations"));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds one arm's <see cref="ScoringEngine"/> — the REAL engine, no second copy of any scoring logic,
    /// with a fresh in-memory score repository so nothing leaves the process and the arm's tier policy as
    /// the ONLY difference.
    /// </summary>
    private static ScoringEngine BuildEngine(
        ServiceProvider provider, IAttentionSourceWeights sourceWeights, ISignalFileStore windowReads) =>
        new(
            provider.GetRequiredService<ISignalRepository>(),
            windowReads,
            provider.GetRequiredService<IEvidenceRepository>(),
            new InMemoryScoreRepository(),
            provider.GetRequiredService<ICompanyRepository>(),
            new RadarScoreFormulaFactory(sourceWeights).Create(
                new ScoringStrategyDefinition("default", "default", new ScoringWeights(), IsPrimary: true)),
            new ScoringWeights(),
            sourceWeights,
            // The signal-source descriptor is recorded provenance, never a scoring input; both arms get the
            // same frozen stub, so the one thing it could affect (the stamp) is identical on both sides and
            // a read-only harness never has to register a collector or an AI seam it must not use.
            CounterfactualSourceDescriptor.Instance,
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            new ScoringOptions { Window = ScoringWindow },
            NullLogger<ScoringEngine>.Instance,
            strategyName: "default");

    private static async Task<IReadOnlyList<CompanyScoreSnapshot>> ScoreAllAsync(
        ServiceProvider provider,
        IReadOnlyList<Company> companies,
        IAttentionSourceWeights sourceWeights,
        ISignalFileStore windowReads,
        CancellationToken ct)
    {
        var engine = BuildEngine(provider, sourceWeights, windowReads);
        var snapshots = new List<CompanyScoreSnapshot>(companies.Count);
        foreach (var company in companies)
        {
            var result = await engine.ScoreCompanyAsync(company.Id, AsOfUtc, ct);
            snapshots.Add(result.Snapshot);
        }

        return snapshots;
    }

    /// <summary>
    /// (2) The population <c>AttentionReach</c>'s breadth term actually sums: the DISTINCT third-party
    /// publishers per company over the scoring window, counting both the collapse survivors and the
    /// collapsed-only outlets.
    /// <para>
    /// Read through the SAME <see cref="ISignalRepository"/> and <see cref="IEvidenceRepository"/> the engine
    /// reads its current window from (spec 142: the durable file stores implement both), with the engine's
    /// window / known-at / review admission predicate restated. That predicate is tunable pipeline
    /// SCAFFOLDING, not formula math — no scoring arithmetic is duplicated here; the scores in sections 3
    /// and 4 come from the real engine and nothing in this method feeds them.
    /// </para>
    /// <para>
    /// It is deliberately taken over the PRE-collapse set, because at the default
    /// <c>ScoringWeights.CollapsedBreadthCredit</c> of 1.0 the breadth term is exactly
    /// <see cref="ScoreSignalMath.TierWeightedReach"/> over the UNION of survivors and collapsed-only
    /// publishers. The three read-side transforms between here and the collapse cannot change that union:
    /// the guidance supersede only removes <c>GuidanceChange</c> signals (first-party filings, which are
    /// never breadth publishers), the news-judgment supersede only removes a duplicate over the SAME
    /// evidence id (hence the same publisher), and the legacy-inheritance neutralization changes direction
    /// only.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>>
        CollectBreadthPublishersAsync(
            ServiceProvider provider, IReadOnlyList<Company> companies, CancellationToken ct)
    {
        var signals = provider.GetRequiredService<ISignalRepository>();
        var evidence = provider.GetRequiredService<IEvidenceRepository>();
        var windowStart = AsOfUtc - ScoringWindow;

        var result = new Dictionary<Guid, IReadOnlyCollection<string>>();
        foreach (var company in companies)
        {
            var publishers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var signal in await signals.GetByCompanyAsync(company.Id, ct))
            {
                if (signal.ObservedAtUtc <= windowStart
                    || signal.ObservedAtUtc > AsOfUtc
                    || signal.CreatedAtUtc > AsOfUtc
                    || signal.ReviewStatus != SignalReviewStatus.Approved)
                {
                    continue;
                }

                var item = await evidence.GetByIdAsync(signal.EvidenceId, ct);
                if (item is null)
                {
                    // The engine drops these too (unresolvable provenance) — spec 142's accrued residue.
                    continue;
                }

                if (ScoreSignalMath.IsBreadthPublisher(new ScoringSignal(signal, item)))
                {
                    publishers.Add(item.SourceName);
                }
            }

            result[company.Id] = publishers;
        }

        return result;
    }

    private static async Task AppendObservationCoverageAsync(
        StringBuilder report,
        ServiceProvider provider,
        IReadOnlyList<Company> companies,
        IAttentionSourceWeights oldWeights,
        IAttentionSourceWeights newWeights,
        CancellationToken ct)
    {
        var archive = provider.GetRequiredService<FileNewsObservationArchive>();
        var universe = companies.Select(c => c.Id).ToHashSet();
        var windowStart = AsOfUtc - ScoringWindow;

        var observations = (await archive.GetAllAsync(ct))
            .Where(o => o.CompanyId is not null && universe.Contains(o.CompanyId.Value))
            .Where(o => o.PublishedAtUtc is not null
                && o.PublishedAtUtc > windowStart
                && o.PublishedAtUtc <= AsOfUtc)
            .ToList();

        var distinctNames = observations
            .Select(o => o.Publisher ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        report.AppendLine("### 1. Raw observation coverage (publisher-map maintenance, NOT the scoring unit)");
        report.AppendLine();
        report.AppendLine(
            $"{observations.Count} in-window observations over {distinctNames} distinct publisher names. "
                + "Article VOLUME, which is not what the score consumes — see section 2.");
        report.AppendLine();
        report.AppendLine("| tier | old policy | new policy |");
        report.AppendLine("| --- | ---: | ---: |");

        var oldTiers = TierShares(observations, oldWeights);
        var newTiers = TierShares(observations, newWeights);
        foreach (var tier in oldTiers.Keys.Concat(newTiers.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(t => t, StringComparer.Ordinal))
        {
            report.AppendLine(
                $"| `{tier}` | {Share(oldTiers, tier, observations.Count)} "
                    + $"| {Share(newTiers, tier, observations.Count)} |");
        }

        report.AppendLine();
        foreach (var (label, weights) in new[] { ("old", oldWeights), ("new", newWeights) })
        {
            var unclassified = observations
                .Where(o => !weights.Resolve(o.Publisher).IsExplicitlyMapped)
                .GroupBy(
                    o => string.IsNullOrWhiteSpace(o.Publisher)
                        ? UnclassifiedPublisherCoverage.Unattributed
                        : o.Publisher.Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => (Publisher: g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Publisher, StringComparer.Ordinal)
                .ToList();

            report.AppendLine(
                $"- **{label} policy — unclassified publishers remaining: {unclassified.Count}** "
                    + $"({unclassified.Count(u => u.Count == 1)} singletons, "
                    + $"{unclassified.Sum(u => u.Count)} observations). Top 15: "
                    + string.Join(", ", unclassified.Take(15).Select(u => $"{u.Publisher} {u.Count}")));
        }

        report.AppendLine();

        static Dictionary<string, int> TierShares(
            IEnumerable<NewsObservationRecord> observations, IAttentionSourceWeights weights)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var o in observations)
            {
                var tier = weights.Resolve(o.Publisher).TierName;
                counts[tier] = counts.GetValueOrDefault(tier) + 1;
            }

            return counts;
        }

        static string Share(IReadOnlyDictionary<string, int> counts, string tier, int total)
        {
            var n = counts.GetValueOrDefault(tier);
            return total == 0 ? "0" : $"{n} ({(double)n / total:P1})";
        }
    }

    private static void AppendBreadthSection(
        StringBuilder report,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> publishersByCompany,
        IAttentionSourceWeights oldWeights,
        IAttentionSourceWeights newWeights)
    {
        var distinct = publishersByCompany.Values
            .SelectMany(p => p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var oldReach = publishersByCompany.Values
            .Select(p => ScoreSignalMath.TierWeightedReach(p, oldWeights)).ToList();
        var newReach = publishersByCompany.Values
            .Select(p => ScoreSignalMath.TierWeightedReach(p, newWeights)).ToList();

        report.AppendLine("### 2. Distinct publisher/company breadth actually consumed by `AttentionReach`");
        report.AppendLine();
        report.AppendLine(
            $"{publishersByCompany.Count} companies · {publishersByCompany.Values.Sum(p => p.Count)} "
                + $"company-publisher pairs · {distinct.Count} distinct publishers universe-wide. Survivors "
                + "AND collapsed-only publishers are both counted (a publisher appearing forty times counts "
                + "once). **This is the population the score actually sees.**");
        report.AppendLine();
        report.AppendLine("| tier-weighted breadth per company | old policy | new policy |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine($"| min | {Min(oldReach):F3} | {Min(newReach):F3} |");
        report.AppendLine($"| mean | {Mean(oldReach):F3} | {Mean(newReach):F3} |");
        report.AppendLine($"| max | {Max(oldReach):F3} | {Max(newReach):F3} |");
        report.AppendLine();
        report.AppendLine("Distinct publishers universe-wide, by tier:");
        report.AppendLine();
        report.AppendLine("| tier | old policy | new policy |");
        report.AppendLine("| --- | ---: | ---: |");
        var tiers = distinct.Select(p => oldWeights.Resolve(p).TierName)
            .Concat(distinct.Select(p => newWeights.Resolve(p).TierName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal);
        foreach (var tier in tiers)
        {
            report.AppendLine(
                $"| `{tier}` | {distinct.Count(p => oldWeights.Resolve(p).TierName == tier)} "
                    + $"| {distinct.Count(p => newWeights.Resolve(p).TierName == tier)} |");
        }

        report.AppendLine();

        static double Min(IReadOnlyCollection<double> v) => v.Count == 0 ? 0 : v.Min();

        static double Max(IReadOnlyCollection<double> v) => v.Count == 0 ? 0 : v.Max();

        static double Mean(IReadOnlyCollection<double> v) => v.Count == 0 ? 0 : v.Average();
    }

    private static void AppendDistribution(
        StringBuilder report,
        string heading,
        IReadOnlyList<CompanyScoreSnapshot> oldScores,
        IReadOnlyList<CompanyScoreSnapshot> newScores,
        Func<CompanyScoreSnapshot, int> selector)
    {
        var oldValues = oldScores.Select(selector).ToList();
        var newValues = newScores.Select(selector).ToList();

        report.AppendLine("### " + heading);
        report.AppendLine();
        report.AppendLine("| statistic | old policy | new policy |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine($"| n | {oldValues.Count} | {newValues.Count} |");
        report.AppendLine($"| min | {oldValues.Min()} | {newValues.Min()} |");
        report.AppendLine($"| mean | {oldValues.Average():F1} | {newValues.Average():F1} |");
        report.AppendLine($"| max | {oldValues.Max()} | {newValues.Max()} |");
        report.AppendLine(
            $"| spread (max−min) | {oldValues.Max() - oldValues.Min()} "
                + $"| {newValues.Max() - newValues.Min()} |");
        report.AppendLine($"| populated decades | {Decades(oldValues)} | {Decades(newValues)} |");
        report.AppendLine($"| largest decade | {LargestDecade(oldValues)} | {LargestDecade(newValues)} |");
        report.AppendLine();
        report.AppendLine("| decade | old policy | new policy |");
        report.AppendLine("| --- | ---: | ---: |");
        for (var decade = 0; decade <= 100; decade += 10)
        {
            var upper = decade == 100 ? 100 : decade + 9;
            var oldCount = oldValues.Count(v => v >= decade && v <= upper);
            var newCount = newValues.Count(v => v >= decade && v <= upper);
            if (oldCount == 0 && newCount == 0)
            {
                continue;
            }

            report.AppendLine($"| {decade}–{upper} | {oldCount} | {newCount} |");
        }

        report.AppendLine();

        static int Decades(IEnumerable<int> values) =>
            values.Select(v => Math.Min(v / 10, 10)).Distinct().Count();

        static string LargestDecade(IReadOnlyCollection<int> values)
        {
            var group = values
                .GroupBy(v => Math.Min(v / 10, 10))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .First();
            var lower = group.Key * 10;
            var upper = group.Key == 10 ? 100 : lower + 9;
            return $"{lower}–{upper}: {group.Count()} of {values.Count}";
        }
    }

    /// <summary>
    /// A read-through memoization of <see cref="ISignalFileStore.ReadApprovedInWindowAsync"/> keyed by its
    /// EXACT arguments. The two arms differ only in the attention tier map, so they ask this store the
    /// identical question for every company; memoizing it removes one full rescan of the signal store per
    /// company without changing a single answer. <see cref="WriteAsync"/> throws — this harness is
    /// read-only and must fail loudly if that ever stops being true.
    /// </summary>
    private sealed class MemoizingSignalWindowReads(ISignalFileStore inner) : ISignalFileStore
    {
        private readonly Dictionary<(Guid, DateTimeOffset, DateTimeOffset, DateTimeOffset),
            IReadOnlyList<Radar.Domain.Signals.Signal>> _cache = [];

        public Task<DurableWriteResult> WriteAsync(
            Radar.Domain.Signals.Signal signal,
            Radar.Domain.Signals.SignalReview review,
            CancellationToken ct) =>
            throw new InvalidOperationException(
                "The spec-196 §7 counterfactual is read-only and must never write a signal.");

        public async Task<IReadOnlyList<Radar.Domain.Signals.Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct)
        {
            var key = (companyId, startExclusiveUtc, endInclusiveUtc, knownAsOfUtc);
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var read = await inner.ReadApprovedInWindowAsync(
                companyId, startExclusiveUtc, endInclusiveUtc, knownAsOfUtc, ct);
            _cache[key] = read;
            return read;
        }
    }

    /// <summary>
    /// A frozen signal-source descriptor. This harness compares SCORES between two attention policies; the
    /// descriptor contributes only to the recorded stamp, which is identical on both arms.
    /// </summary>
    private sealed class CounterfactualSourceDescriptor : ISignalSourceDescriptor
    {
        public static readonly CounterfactualSourceDescriptor Instance = new();

        public string CanonicalDescriptor() => "counterfactual-src-desc";

        public string CollectionProvenance() => "collectors=;collection=none-this-pass;";

        public IReadOnlyList<string> EnabledCollectors() => [];
    }
}

/// <summary>
/// Runs the spec-196 §7 counterfactual only when a live data root is supplied, and SKIPS WITH A NAMED
/// REASON otherwise (the <c>WindowsPowerShellFactAttribute</c> precedent) — never silently.
/// </summary>
public sealed class LiveDataRootFactAttribute : FactAttribute
{
    public LiveDataRootFactAttribute()
    {
        if (AttentionPolicyCounterfactualTests.DataRoot() is null)
        {
            Skip = AttentionPolicyCounterfactualTests.SkipReason;
        }
    }
}
