using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.Persistence.InMemory;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// Spec 224 §4 — the READ-ONLY PAIRED COUNTERFACTUAL for the same-insider Form 4 collapse
/// (<c>insider-collapse-v1</c>): what collapsing repeat filings by one reporting owner does to the insider
/// negative mass, Trajectory and Opportunity over the live universe, at ONE as-of instant, through the REAL
/// <see cref="ScoringEngine"/> and the REAL <see cref="InsiderActivityCollapse"/>, persisting nothing.
/// <para>
/// <b>The AFTER arm is the PRODUCTION path over the live store, unmodified.</b> Every Form 4 evidence item
/// collected before spec 224 carries the reporting owner ONLY inside its title; the spec-224 amendment made
/// <see cref="InsiderActivityMetadata.TryRead"/> recover that owner from the collector's fixed title shape
/// (<see cref="InsiderActivityTitle"/>) at read time, so the production collapse now resolves accrued owners
/// itself. The AFTER arm therefore reads the raw store with NO decorator — the numbers are what the shipped
/// <see cref="InsiderActivityCollapse"/> does to the store as it is, including the title-vs-metadata split of
/// owner sources. Title-derived identities are NAME-only (a title carries no CIK), so two spellings of one
/// person with no CIK in the window stay two buckets. The BEFORE arm wraps the same repository with a
/// decorator that, IN MEMORY ONLY, strips both owner keys AND replaces each Form 4 title with a marker in no
/// collector shape, so no owner resolves — every directional filing passes through unbucketed, which is
/// exactly how the scorer counted insider filings before spec 224. The engine reads no evidence title for
/// scoring, so the marker changes nothing else.
/// </para>
/// <para>
/// <b>What this is and is not.</b> It is a read-only RE-SCORE of the live store at one as-of instant through
/// the real engine — measured over the live store, not a persisted run's snapshots. Nothing is written under
/// the data root: the durable stores are hydrated READ-ONLY (spec 142), scores go to an in-memory
/// repository, and the markdown is written to the system temp directory only.
/// </para>
/// <para>
/// <b>Mneg here is the INSIDER share of the trajectory's negative mass</b> — Σ confidence · recency ·
/// strength over Negative <c>InsiderBuying</c> signals, computed with the very
/// <see cref="ScoreSignalMath.RecencyFactors"/> + <see cref="ScoreSignalMath.DirectionalMasses"/> that
/// <see cref="ScoreSignalMath.TrajectoryScore"/> uses (recency is per-signal, so restricting the set to the
/// insider signals gives exactly their contribution to the whole-window Mneg). Trajectory and Opportunity
/// come from the real engine's snapshots.
/// </para>
/// <para>
/// <b>ENV-GATED</b> on <c>RADAR_INSIDER_COLLAPSE_DATA_ROOT</c>, skipped with a NAMED reason otherwise
/// (the spec-196 §7 / 198 §4 precedent). NO network. Deterministic (AD-3) given a data root: fixed
/// instant derived from the store, ordinal/id orderings, invariant formatting.
/// </para>
/// </summary>
public sealed class InsiderCollapseCounterfactualTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_INSIDER_COLLAPSE_DATA_ROOT";

    internal const string SkipReason =
        "Spec 224 §4 paired insider-collapse counterfactual: set " + DataRootVariable
            + " to a Radar data root (companies.json, signals/, evidence/raw/, scores/) to run it.";

    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    /// <summary>
    /// The live baseline's scoring window: <c>RadarWorkerOptions.ScoringWindowDays</c> defaults to 60 (the
    /// Worker is what a live run composes), NOT <see cref="ScoringOptions"/>' 30-day library default. This
    /// project cannot reference the Worker, so the value is restated here and named for what it is.
    /// </summary>
    private static readonly TimeSpan ScoringWindow = TimeSpan.FromDays(60);

    /// <summary>The companies the spec-224 diagnosis rests on, in the spec's order.</summary>
    private static readonly string[] NamedTickers = ["ATNI", "MRCY", "FLXS", "IDT", "DGII", "OOMA"];

    private static readonly string OutputPath =
        Path.Combine(Path.GetTempPath(), "radar-spec-224-insider-collapse.md");

    /// <summary>
    /// The BEFORE arm's in-memory title: in no <see cref="InsiderActivityTitle"/> shape, so the read-time owner
    /// fallback recovers nothing (asserted at use, so a future shape can never make it resolve silently).
    /// </summary>
    private const string BeforeArmTitle = "[spec 224 counterfactual BEFORE arm: owner withheld]";

    [InsiderCollapseCounterfactualFact]
    public async Task PairedCounterfactual_NoOwnerVersusProductionOwnerResolution_OverTheLiveUniverse()
    {
        var root = DataRoot()!;
        var ct = CancellationToken.None;

        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var report = new StringBuilder();

            await using var provider = AttentionPolicyCounterfactualTests.BuildReadOnlyProvider(root);
            var seeded = await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);
            var companies = (await provider.GetRequiredService<ICompanyRepository>().GetAllAsync(ct))
                .OrderBy(c => c.Id)
                .ToList();

            var signals = provider.GetRequiredService<ISignalRepository>();
            var evidence = provider.GetRequiredService<IEvidenceRepository>();
            var windowReads = new MemoizingSignalWindowReads(provider.GetRequiredService<ISignalFileStore>());

            var asOf = await ResolveAsOfAsync(root, signals, companies, ct);

            // The two arms differ ONLY in the evidence repository: the BEFORE arm withholds the owner (keys
            // stripped, title replaced, in memory); the AFTER arm is the raw store through the production
            // read path (the shared ReadOnlyEvidenceRepository, whose only job is to make a write impossible).
            // The instant, universe, strategy, weights, window and every other signal are identical. The
            // decorator is a pure, read-only map over the same store.
            var beforeEvidence = new OwnerWithholdingEvidenceRepository(evidence);
            IEvidenceRepository afterEvidence = new ReadOnlyEvidenceRepository(evidence);
            var collapse = new InsiderActivityCollapse(new InsiderCollapseOptions(), new InsiderMaterialityWeights());

            var projection = await InsiderProjection.BuildAsync(
                signals, evidence, beforeEvidence, afterEvidence, collapse, companies, asOf.Instant, ct);

            var before = await ScoreAllAsync(provider, companies, signals, beforeEvidence, windowReads, asOf.Instant, ct);
            var after = await ScoreAllAsync(provider, companies, signals, afterEvidence, windowReads, asOf.Instant, ct);

            report.AppendLine("## Spec 224 §4 — paired insider-collapse counterfactual (read-only re-score of the live store)");
            report.AppendLine();
            report.AppendLine(
                $"As-of `{asOf.Instant:O}` ({asOf.Source}) · scoring window {ScoringWindow.TotalDays:0} days "
                    + "(`RadarWorkerOptions.ScoringWindowDays`) · strategy `default` (`radar-formula-v8`, default "
                    + $"weights) · `{collapse.CanonicalDescriptor()}` · {companies.Count} companies seeded from "
                    + $"`companies.json` (seeder reported {seeded}).");
            report.AppendLine();
            AppendCaveats(report);
            AppendCollapseSection(report, projection);
            AppendNamedCompaniesSection(report, projection, companies, before, after);
            AppendUniverseSection(report, companies, before, after);

            var markdown = report.ToString();
            output.WriteLine(markdown);
            File.WriteAllText(OutputPath, markdown, Encoding.UTF8);
            output.WriteLine($"(written to {OutputPath})");

            // The measurement is the deliverable; these guard only that it measured SOMETHING.
            Assert.NotEmpty(companies);
            Assert.Equal(companies.Count, before.Count);
            Assert.Equal(before.Count, after.Count);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static void AppendCaveats(StringBuilder report)
    {
        report.AppendLine("> **⚠ Read these before reading the numbers.**");
        report.AppendLine("> ");
        report.AppendLine(
            "> 1. **MEASURED over the live store, through the production path — but a re-score, not a persisted "
                + "run.** The AFTER arm reads the raw store with no decorator: owners resolve through "
                + "`InsiderActivityMetadata.TryRead` (structured metadata, else the collector's title shape). "
                + "The scores are the real engine's at one as-of instant, held in memory; no live run's snapshot "
                + "is quoted.");
        report.AppendLine(
            "> 2. **Title-derived identities are NAME-only.** A title carries no CIK, so two spellings of one "
                + "person with no CIK-bearing filing in the window stay two buckets (under-bucketing, never "
                + "over-bucketing on spelling).");
        report.AppendLine(
            "> 3. **The BEFORE arm withholds the owner** (keys stripped and each Form 4 title replaced, in memory), "
                + "so no owner resolves and every directional filing passes through unbucketed — how the scorer "
                + "counted insider filings before spec 224.");
        report.AppendLine();
    }

    private static void AppendCollapseSection(StringBuilder report, InsiderProjection p)
    {
        report.AppendLine("### 1. What the collapse does to the directional insider filings in the window");
        report.AppendLine();
        report.AppendLine("| quantity | BEFORE (no owner) | AFTER (production owner resolution) |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine($"| directional insider signals scored | {p.DirectionalBefore} | {p.DirectionalAfter} |");
        report.AppendLine($"| filings collapsed (removed from the scored set) | {p.CollapsedBefore} | {p.CollapsedAfter} |");
        report.AppendLine($"| buckets formed (all sizes) | {p.BucketsBefore} | {p.BucketsAfter} |");
        report.AppendLine($"| buckets holding 1 filing | {p.Size1Before} | {p.Size1After} |");
        report.AppendLine($"| buckets holding 2 filings | {p.Size2Before} | {p.Size2After} |");
        report.AppendLine($"| buckets holding 3 filings | {p.Size3Before} | {p.Size3After} |");
        report.AppendLine($"| buckets holding 4+ filings | {p.Size4PlusBefore} | {p.Size4PlusAfter} |");
        report.AppendLine($"| owner unresolved (passed through unbucketed) | {p.UnresolvedBefore} | {p.UnresolvedAfter} |");
        report.AppendLine($"| of which: name shared by two or more CIKs (ambiguous) | {p.AmbiguousBefore} | {p.AmbiguousAfter} |");
        report.AppendLine($"| bucketed on a title-derived owner (`OwnerFromTitleCount`) | {p.FromTitleBefore} | {p.FromTitleAfter} |");
        report.AppendLine();
        report.AppendLine("Counted axes over the same reads (identical in both arms):");
        report.AppendLine();
        report.AppendLine("| axis | count |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine($"| companies scored | {p.Companies} |");
        report.AppendLine($"| companies with at least one directional insider filing in the window | {p.CompaniesWithDirectional} |");
        report.AppendLine($"| approved in-window signals of any type | {p.WindowedSignals} |");
        report.AppendLine($"| signals dropped — evidence id unresolvable (the engine's own drop rule) | {p.EvidenceUnresolvable} |");
        report.AppendLine($"| insider signals that are Neutral (plan / no-discretionary / mixed; never candidates) | {p.NeutralInsider} |");
        report.AppendLine($"| directional insider signals — owner name source `Metadata` (post-224 evidence) | {p.SourceMetadata} |");
        report.AppendLine($"| directional insider signals — owner name source `Title` (accrued pre-224 evidence) | {p.SourceTitle} |");
        report.AppendLine($"| directional insider signals — owner name source `NotRecorded` | {p.SourceNotRecorded} |");
        report.AppendLine($"| directional insider signals — evidence carries an owner CIK | {p.WithCik} |");
        report.AppendLine($"| directional insider signals — not a readable Form 4 envelope | {p.NotForm4} |");
        report.AppendLine();
        if (p.CollapsedAfter == 0)
        {
            report.AppendLine(
                "**Almost every bucket holds one filing: over this window the collapse changed nothing, and that "
                    + "is the finding.**");
            report.AppendLine();
        }
    }

    private static void AppendNamedCompaniesSection(
        StringBuilder report,
        InsiderProjection p,
        IReadOnlyList<Company> companies,
        IReadOnlyList<CompanyScoreSnapshot> before,
        IReadOnlyList<CompanyScoreSnapshot> after)
    {
        var beforeById = before.ToDictionary(s => s.CompanyId);
        var afterById = after.ToDictionary(s => s.CompanyId);

        report.AppendLine("### 2. The companies the diagnosis rests on");
        report.AppendLine();
        report.AppendLine(
            "Mneg is the INSIDER share of the trajectory's negative mass (Σ confidence·recency·strength over "
                + "Negative insider signals, via `ScoreSignalMath.RecencyFactors` + `DirectionalMasses`). OOMA is "
                + "the counter-example the spec names: positive mass dominates its trajectory, so collapsing sales "
                + "there should barely move it.");
        report.AppendLine();
        report.AppendLine("| ticker | directional insider signals before → after | Mneg (insider) before → after | Trajectory before → after | Opportunity before → after |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var ticker in NamedTickers)
        {
            var company = companies.FirstOrDefault(c => string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
            if (company is null)
            {
                report.AppendLine($"| {ticker} | not in this universe | — | — | — |");
                continue;
            }

            var row = p.PerCompany[company.Id];
            var b = beforeById[company.Id];
            var a = afterById[company.Id];
            report.AppendLine(
                $"| {ticker} | {row.DirectionalBefore} → {row.DirectionalAfter} "
                    + $"| {row.MnegBefore:0.###} → {row.MnegAfter:0.###} "
                    + $"| {b.TrajectoryScore} → {a.TrajectoryScore} "
                    + $"| {b.OpportunityScore} → {a.OpportunityScore} |");
        }

        report.AppendLine();
    }

    private static void AppendUniverseSection(
        StringBuilder report,
        IReadOnlyList<Company> companies,
        IReadOnlyList<CompanyScoreSnapshot> before,
        IReadOnlyList<CompanyScoreSnapshot> after)
    {
        var afterById = after.ToDictionary(s => s.CompanyId);
        var byId = companies.ToDictionary(c => c.Id);

        var trajBefore = before.Select(s => (double)s.TrajectoryScore).ToList();
        var trajAfter = after.Select(s => (double)s.TrajectoryScore).ToList();
        var oppBefore = before.Select(s => (double)s.OpportunityScore).ToList();
        var oppAfter = after.Select(s => (double)s.OpportunityScore).ToList();

        var movers = before
            .Select(b => (Company: byId[b.CompanyId], Delta: afterById[b.CompanyId].OpportunityScore - b.OpportunityScore,
                          Before: b, After: afterById[b.CompanyId]))
            .ToList();
        var movedMoreThan2 = movers.Count(m => Math.Abs(m.Delta) > 2);
        var max = movers.OrderByDescending(m => Math.Abs(m.Delta)).ThenBy(m => m.Company.Id).FirstOrDefault();

        report.AppendLine("### 3. Whole universe, before → after");
        report.AppendLine();
        report.AppendLine("| statistic | BEFORE | AFTER |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine($"| n | {before.Count} | {after.Count} |");
        report.AppendLine($"| median Trajectory | {Stat(trajBefore, Median)} | {Stat(trajAfter, Median)} |");
        report.AppendLine($"| median Opportunity | {Stat(oppBefore, Median)} | {Stat(oppAfter, Median)} |");
        report.AppendLine($"| companies whose Opportunity moved by more than 2 points | — | {movedMoreThan2} |");
        if (max.Company is not null)
        {
            var name = max.Company.Ticker ?? max.Company.Name;
            report.AppendLine(
                $"| max mover | — | {name}: Opportunity {max.Before.OpportunityScore} → {max.After.OpportunityScore} "
                    + $"({(max.Delta >= 0 ? "+" : string.Empty)}{max.Delta}), Trajectory {max.Before.TrajectoryScore} → {max.After.TrajectoryScore} |");
        }

        report.AppendLine();
        report.AppendLine(
            "A change that moves nothing is a finding; a change that moves everything needs explaining. The "
                + "spec expects the effect to be uneven and largest on quiet companies where insider filings are "
                + "most of the mass.");
        report.AppendLine();
    }

    private static string Stat(IReadOnlyList<double> values, Func<IReadOnlyList<double>, double> f) =>
        values.Count == 0 ? "n/a" : f(values).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Mean of the two central values on an even count — stated because the convention is ambiguous.</summary>
    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// The as-of instant: the latest <c>windowEndUtc</c> found in any snapshot file under <c>scores/</c>
    /// (so the projection describes the same window the last live run scored), else the newest approved
    /// signal's <c>ObservedAtUtc</c>. Which source was used is rendered.
    /// </summary>
    internal static async Task<(DateTimeOffset Instant, string Source)> ResolveAsOfAsync(
        string root, ISignalRepository signals, IReadOnlyList<Company> companies, CancellationToken ct)
    {
        DateTimeOffset? latest = null;
        var unreadable = 0;
        var scores = Path.Combine(root, "scores");
        if (Directory.Exists(scores))
        {
            foreach (var file in Directory.EnumerateFiles(scores, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("windowEndUtc", out var end)
                        && end.TryGetDateTimeOffset(out var value)
                        && (latest is null || value > latest))
                    {
                        latest = value;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    unreadable++;
                }
            }
        }

        if (latest is { } fromScores)
        {
            return (fromScores, $"latest `windowEndUtc` under `scores/`; {unreadable} unreadable snapshot file(s) skipped");
        }

        DateTimeOffset? newest = null;
        foreach (var company in companies)
        {
            foreach (var signal in await signals.GetByCompanyAsync(company.Id, ct))
            {
                if (newest is null || signal.ObservedAtUtc > newest)
                {
                    newest = signal.ObservedAtUtc;
                }
            }
        }

        return (newest ?? throw new InvalidOperationException("The data root holds no scores and no signals."),
            "no `scores/` snapshot found — newest signal's `ObservedAtUtc`");
    }

    /// <summary>
    /// One arm's REAL engine over the arm's evidence decorator; nothing else differs. <c>internal</c> since
    /// spec 225 so its read-only harness scores through the SAME composition (reuse over copy); it hands in
    /// its own in-memory score repository because it needs the persisted LINKS as well as the snapshots.
    /// </summary>
    internal static async Task<IReadOnlyList<CompanyScoreSnapshot>> ScoreAllAsync(
        ServiceProvider provider,
        IReadOnlyList<Company> companies,
        ISignalRepository signals,
        IEvidenceRepository armEvidence,
        ISignalFileStore windowReads,
        DateTimeOffset asOf,
        CancellationToken ct,
        IScoreRepository? scoreRepository = null)
    {
        var sourceWeights = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);
        var engine = new ScoringEngine(
            signals,
            windowReads,
            armEvidence,
            scoreRepository ?? new InMemoryScoreRepository(),
            provider.GetRequiredService<ICompanyRepository>(),
            new RadarScoreFormulaFactory(sourceWeights).Create(
                new ScoringStrategyDefinition("default", "default", new ScoringWeights(), IsPrimary: true)),
            new ScoringWeights(),
            sourceWeights,
            ReadOnlyHarnessSourceDescriptor.Instance,
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            new InsiderActivityCollapse(new InsiderCollapseOptions(), new InsiderMaterialityWeights()),
            new ScoringOptions { Window = ScoringWindow },
            NullLogger<ScoringEngine>.Instance,
            strategyName: "default");

        var snapshots = new List<CompanyScoreSnapshot>(companies.Count);
        foreach (var company in companies)
        {
            var result = await engine.ScoreCompanyAsync(company.Id, asOf, ct);
            snapshots.Add(result.Snapshot);
        }

        return snapshots;
    }

    /// <summary>
    /// The BEFORE arm's in-memory map: a readable Form 4 envelope loses both owner keys and its title is
    /// replaced with <see cref="BeforeArmTitle"/>, so <see cref="InsiderActivityMetadata.TryRead"/> resolves no
    /// owner. Any item that is not a readable Form 4 envelope is returned as-is.
    /// </summary>
    private static EvidenceItem WithholdOwner(EvidenceItem item)
    {
        if (InsiderActivityMetadata.TryRead(item) is null || string.IsNullOrWhiteSpace(item.MetadataJson))
        {
            return item;
        }

        if (JsonNode.Parse(item.MetadataJson) is not JsonObject root)
        {
            return item;
        }

        if (root["metadata"] is JsonObject metadata)
        {
            metadata.Remove(InsiderActivityMetadata.OwnerNameKey);
            metadata.Remove(InsiderActivityMetadata.OwnerCikKey);
        }

        var withheld = item with { MetadataJson = root.ToJsonString(), Title = BeforeArmTitle };
        var read = InsiderActivityMetadata.TryRead(withheld);
        if (read is null || read.OwnerName is not null || read.OwnerCik is not null)
        {
            throw new InvalidOperationException(
                "The spec-224 §4 BEFORE arm must withhold every owner, but the read still resolved one.");
        }

        return withheld;
    }

    /// <summary>The BEFORE arm: no owner resolvable on any Form 4 evidence ⇒ every directional filing unresolved.</summary>
    private sealed class OwnerWithholdingEvidenceRepository(IEvidenceRepository inner) : IEvidenceRepository
    {
        public Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct) =>
            throw new InvalidOperationException("The spec-224 §4 counterfactual is read-only and must never write evidence.");

        public async Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct) =>
            await inner.GetByIdAsync(id, ct) is { } item ? WithholdOwner(item) : null;

        public async Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct) =>
            await inner.GetByContentHashAsync(contentHash, ct) is { } item ? WithholdOwner(item) : null;

        public async Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct) =>
            [.. (await inner.GetAllAsync(ct)).Select(WithholdOwner)];
    }

    private sealed record CompanyRow(
        int DirectionalBefore,
        int DirectionalAfter,
        double MnegBefore,
        double MnegAfter);

    /// <summary>
    /// The collapse accounting over the whole universe, per arm, from the REAL <see cref="InsiderActivityCollapse"/>
    /// applied to each company's windowed, approved, evidence-joined insider signals — the same admission
    /// predicate the engine applies (tunable scaffolding restated; no scoring arithmetic is duplicated).
    /// </summary>
    private sealed record InsiderProjection(
        int Companies,
        int CompaniesWithDirectional,
        int WindowedSignals,
        int EvidenceUnresolvable,
        int NeutralInsider,
        int SourceMetadata,
        int SourceTitle,
        int SourceNotRecorded,
        int WithCik,
        int NotForm4,
        int DirectionalBefore,
        int DirectionalAfter,
        int CollapsedBefore,
        int CollapsedAfter,
        int BucketsBefore,
        int BucketsAfter,
        int Size1Before,
        int Size1After,
        int Size2Before,
        int Size2After,
        int Size3Before,
        int Size3After,
        int Size4PlusBefore,
        int Size4PlusAfter,
        int UnresolvedBefore,
        int UnresolvedAfter,
        int AmbiguousBefore,
        int AmbiguousAfter,
        int FromTitleBefore,
        int FromTitleAfter,
        IReadOnlyDictionary<Guid, CompanyRow> PerCompany)
    {
        public static async Task<InsiderProjection> BuildAsync(
            ISignalRepository signals,
            IEvidenceRepository rawEvidence,
            IEvidenceRepository beforeEvidence,
            IEvidenceRepository afterEvidence,
            InsiderActivityCollapse collapse,
            IReadOnlyList<Company> companies,
            DateTimeOffset asOf,
            CancellationToken ct)
        {
            var windowStart = asOf - ScoringWindow;
            var weights = new ScoringWeights();

            var companiesWithDirectional = 0;
            var windowed = 0;
            var evidenceUnresolvable = 0;
            var neutralInsider = 0;
            var sourceMetadata = 0;
            var sourceTitle = 0;
            var sourceNotRecorded = 0;
            var withCik = 0;
            var notForm4 = 0;
            var ambiguousBefore = 0;
            var ambiguousAfter = 0;
            var fromTitleBefore = 0;
            var fromTitleAfter = 0;
            var directionalBefore = 0;
            var directionalAfter = 0;
            var unresolvedBefore = 0;
            var unresolvedAfter = 0;
            var sizesBefore = new List<int>();
            var sizesAfter = new List<int>();
            var perCompany = new Dictionary<Guid, CompanyRow>();

            foreach (var company in companies)
            {
                var beforePairs = new List<ScoringSignal>();
                var afterPairs = new List<ScoringSignal>();
                foreach (var signal in (await signals.GetByCompanyAsync(company.Id, ct))
                    .Where(s => s.ObservedAtUtc > windowStart && s.ObservedAtUtc <= asOf)
                    .Where(s => s.CreatedAtUtc <= asOf)
                    .Where(s => s.ReviewStatus == SignalReviewStatus.Approved))
                {
                    windowed++;
                    if (signal.Type != SignalType.InsiderBuying)
                    {
                        continue;
                    }

                    var raw = await rawEvidence.GetByIdAsync(signal.EvidenceId, ct);
                    var b = await beforeEvidence.GetByIdAsync(signal.EvidenceId, ct);
                    var a = await afterEvidence.GetByIdAsync(signal.EvidenceId, ct);
                    if (raw is null || b is null || a is null)
                    {
                        evidenceUnresolvable++;
                        continue;
                    }

                    if (signal.Direction is not (SignalDirection.Positive or SignalDirection.Negative))
                    {
                        neutralInsider++;
                        continue;
                    }

                    // Counted against the RAW store through the production read: where each directional
                    // filing's owner name comes from (structured metadata vs the title fallback).
                    var rawRead = InsiderActivityMetadata.TryRead(raw);
                    if (rawRead is null)
                    {
                        notForm4++;
                    }
                    else
                    {
                        switch (rawRead.OwnerSource)
                        {
                            case InsiderOwnerSource.Metadata:
                                sourceMetadata++;
                                break;
                            case InsiderOwnerSource.Title:
                                sourceTitle++;
                                break;
                            default:
                                sourceNotRecorded++;
                                break;
                        }

                        if (rawRead.OwnerCik is not null)
                        {
                            withCik++;
                        }
                    }

                    beforePairs.Add(new ScoringSignal(signal, b));
                    afterPairs.Add(new ScoringSignal(signal, a));
                }

                if (beforePairs.Count > 0)
                {
                    companiesWithDirectional++;
                }

                var beforeResult = collapse.Collapse(beforePairs);
                var afterResult = collapse.Collapse(afterPairs);

                directionalBefore += beforeResult.Signals.Count;
                directionalAfter += afterResult.Signals.Count;
                unresolvedBefore += beforeResult.OwnerUnresolvedCount;
                unresolvedAfter += afterResult.OwnerUnresolvedCount;
                ambiguousBefore += beforeResult.OwnerNameAmbiguousCount;
                ambiguousAfter += afterResult.OwnerNameAmbiguousCount;
                fromTitleBefore += beforeResult.OwnerFromTitleCount;
                fromTitleAfter += afterResult.OwnerFromTitleCount;
                Tally(beforePairs.Count, beforeResult, sizesBefore);
                Tally(afterPairs.Count, afterResult, sizesAfter);

                var beforeRecency = ScoreSignalMath.RecencyFactors(beforeResult.Signals, windowStart, asOf, weights.RecencyFloor);
                var afterRecency = ScoreSignalMath.RecencyFactors(afterResult.Signals, windowStart, asOf, weights.RecencyFloor);
                perCompany[company.Id] = new CompanyRow(
                    beforeResult.Signals.Count,
                    afterResult.Signals.Count,
                    ScoreSignalMath.DirectionalMasses(beforeResult.Signals, beforeRecency).Negative,
                    ScoreSignalMath.DirectionalMasses(afterResult.Signals, afterRecency).Negative);
            }

            return new InsiderProjection(
                Companies: companies.Count,
                CompaniesWithDirectional: companiesWithDirectional,
                WindowedSignals: windowed,
                EvidenceUnresolvable: evidenceUnresolvable,
                NeutralInsider: neutralInsider,
                SourceMetadata: sourceMetadata,
                SourceTitle: sourceTitle,
                SourceNotRecorded: sourceNotRecorded,
                WithCik: withCik,
                NotForm4: notForm4,
                DirectionalBefore: directionalBefore,
                DirectionalAfter: directionalAfter,
                CollapsedBefore: sizesBefore.Sum(s => s - 1),
                CollapsedAfter: sizesAfter.Sum(s => s - 1),
                BucketsBefore: sizesBefore.Count,
                BucketsAfter: sizesAfter.Count,
                Size1Before: sizesBefore.Count(s => s == 1),
                Size1After: sizesAfter.Count(s => s == 1),
                Size2Before: sizesBefore.Count(s => s == 2),
                Size2After: sizesAfter.Count(s => s == 2),
                Size3Before: sizesBefore.Count(s => s == 3),
                Size3After: sizesAfter.Count(s => s == 3),
                Size4PlusBefore: sizesBefore.Count(s => s >= 4),
                Size4PlusAfter: sizesAfter.Count(s => s >= 4),
                UnresolvedBefore: unresolvedBefore,
                UnresolvedAfter: unresolvedAfter,
                AmbiguousBefore: ambiguousBefore,
                AmbiguousAfter: ambiguousAfter,
                FromTitleBefore: fromTitleBefore,
                FromTitleAfter: fromTitleAfter,
                PerCompany: perCompany);
        }

        /// <summary>
        /// Every RESOLVED candidate lands in exactly one bucket: the collapsed map names the multi-member
        /// buckets, and the remainder are singletons. Unresolved filings are not buckets at all.
        /// </summary>
        private static void Tally(int candidates, InsiderCollapseResult result, List<int> sizes)
        {
            var resolved = candidates - result.OwnerUnresolvedCount;
            var inMultiMember = 0;
            foreach (var bucket in result.Collapsed.Values)
            {
                sizes.Add(bucket.CollapsedCount + 1);
                inMultiMember += bucket.CollapsedCount + 1;
            }

            for (var i = 0; i < resolved - inMultiMember; i++)
            {
                sizes.Add(1);
            }
        }
    }
}

/// <summary>
/// Runs the spec-224 §4 counterfactual only when a live data root is supplied, and SKIPS WITH A NAMED
/// REASON otherwise — never silently.
/// </summary>
public sealed class InsiderCollapseCounterfactualFactAttribute : FactAttribute
{
    public InsiderCollapseCounterfactualFactAttribute()
    {
        if (InsiderCollapseCounterfactualTests.DataRoot() is null)
        {
            Skip = InsiderCollapseCounterfactualTests.SkipReason;
        }
    }
}
