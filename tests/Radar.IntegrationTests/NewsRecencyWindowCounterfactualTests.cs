using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.EntityResolution;
using Radar.Application.Scoring;
using Radar.Application.Signals;
using Radar.Application.SignalExtraction;
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
/// Spec 198 §4 (b) — the READ-ONLY PAIRED COUNTERFACTUAL for the news-feed recency window: what the
/// windowed feed would have done to <c>AttentionReach</c>, <c>AttentionScore</c> and
/// <c>OpportunityScore</c> over the live universe, at ONE fixed as-of instant, through the REAL
/// <see cref="ScoringEngine"/>, persisting nothing.
/// <para>
/// ⚠ <b>IT IS A PROJECTION OVER ACCRUED EVIDENCE, NOT A REPLAY OF A DIFFERENT COLLECTION HISTORY, AND THE
/// DIFFERENCE MATTERS.</b> Radar cannot re-run last month's feed; what it can do is ask "of the evidence
/// Radar actually holds, which items would a windowed query not have returned?" and score without them.
/// Two honest caveats follow, both rendered in the output rather than left here:
/// <list type="number">
/// <item>Cross-run dedupe means most of the EXCLUDED items contributed no NEW evidence on the night they
/// were re-read — they were re-read and discarded — so the projection OVERSTATES the loss.</item>
/// <item>The freed budget is not modelled: the whole point of the window is that the 25 retained slots go
/// to material the unfiltered query never reached, and no counterfactual over ACCRUED evidence can show
/// what Radar would then have found. The projection therefore also understates the gain.</item>
/// </list>
/// It is a bound on the downside, not a forecast.
/// </para>
/// <para>
/// <b>Nothing is persisted.</b> The durable stores are hydrated READ-ONLY (spec 142) and scores go to an
/// in-memory repository; the shared composition registers no score file store, no run store, no
/// scoring-config store, no report writer and no collector. The two arms differ ONLY in an evidence
/// ADMISSION filter — no scoring arithmetic is duplicated.
/// </para>
/// <para>
/// <b>ENV-GATED</b> on <c>RADAR_NEWS_RECENCY_COUNTERFACTUAL_DATA_ROOT</c>, skipped with a NAMED reason
/// otherwise (the spec-196 §7 precedent). Output is markdown on the test log. Deterministic (AD-3): fixed
/// instant, ordinal/id orderings, invariant formatting.
/// </para>
/// </summary>
public sealed class NewsRecencyWindowCounterfactualTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_NEWS_RECENCY_COUNTERFACTUAL_DATA_ROOT";

    internal const string SkipReason =
        "Spec 198 §4 paired counterfactual: set " + DataRootVariable
            + " to a Radar data root (companies.json, signals/, evidence/raw/, news-observations/) to run it.";

    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    /// <summary>
    /// The pinned as-of instant — the same one the spec-196 §7 counterfactual uses, so the two measurements
    /// describe the same corpus read at the same moment. Pinned because the boundary moves: re-measuring
    /// later ages evidence out and produces the same corpus at a different instant, not a different result.
    /// </summary>
    private static readonly DateTimeOffset AsOfUtc = DateTimeOffset.Parse(
        "2026-08-27T21:42:45.4943606Z",
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>The live baseline's <c>Radar:ScoringWindowDays</c> (60), NOT the 30-day code default.</summary>
    private static readonly TimeSpan ScoringWindow = TimeSpan.FromDays(60);

    /// <summary>The shipped recency window under measurement.</summary>
    private static readonly int RecencyWindowDays =
        NewsQueryScoringIdentity.DefaultRecencyWindowDays;

    [NewsRecencyCounterfactualFact]
    public async Task PairedCounterfactual_UnfilteredVersusWindowedNewsAdmission_OverTheLiveUniverse()
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

            // ONE memoized read of the window store, shared by BOTH arms. HISTORY (dated): before spec 203
            // §2 FileSignalStore.ReadApprovedInWindowAsync was a per-call month-scoped DISK SCAN, and
            // ScoreCompanyAsync reads the previous/velocity window through it per company, per arm —
            // measured on the live store (2026-08-29), unshared, this ran 14 minutes on ~2 seconds of CPU
            // (pure disk thrash over signal partitions of 14,115 / 7,905 / 7,140 / 4,611 / 2,747 files).
            // Spec 203 §2 moved that read onto the hydration index (no file is opened after hydration), so
            // today the memoizer only avoids repeating an in-memory filter + collapse + sort per arm while
            // GUARANTEEING both arms see the identical list.
            //
            // SHARING THE CACHE IS LEGITIMATE HERE BECAUSE THE WINDOWED ARM'S FILTER IS A PURE POST-READ
            // EXCLUSION BY EVIDENCE ID: it never changes the QUERY, so filtering a memoized result is
            // exactly equivalent to filtering a freshly-read one. That equivalence is the whole
            // justification — it would NOT hold for a filter that altered the window, the company or the
            // known-as-of instant, and such a filter must not be layered under this cache.
            var windowReads = new MemoizingSignalWindowReads(
                provider.GetRequiredService<ISignalFileStore>());

            var projection = await NewsAdmissionProjection.BuildAsync(
                signals, evidence, companies, RecencyWindowDays, ct);

            report.AppendLine("## Spec 198 §4 — paired news-recency counterfactual (read-only)");
            report.AppendLine();
            report.AppendLine(
                $"As-of `{AsOfUtc:O}` · scoring window {ScoringWindow.TotalDays:0} days · strategy `default` "
                    + $"(`radar-formula-v8`, default weights) · {companies.Count} companies seeded from "
                    + $"`companies.json` (seeder reported {seeded}) · recency window `when:{RecencyWindowDays}d`.");
            report.AppendLine();
            report.AppendLine(
                "Only the news evidence ADMISSION differs between the arms; the instant, the universe, the "
                    + "strategy, the weights and every non-news signal are identical. Nothing is persisted.");
            report.AppendLine();
            AppendCaveats(report);
            AppendProjectionSection(report, projection);

            // The windowed arm wraps the MEMOIZED store, never the raw one, so both arms hit one cache.
            var windowedSignals = new NewsWindowFilteredSignalRepository(signals, projection.ExcludedEvidenceIds);
            var windowedWindowReads =
                new NewsWindowFilteredSignalFileStore(windowReads, projection.ExcludedEvidenceIds);

            // (1) The distinct-publisher breadth AttentionReach actually consumes, before and after.
            var beforePublishers = await CollectBreadthPublishersAsync(signals, evidence, companies, ct);
            var afterPublishers =
                await CollectBreadthPublishersAsync(windowedSignals, evidence, companies, ct);
            AppendBreadthSection(report, beforePublishers, afterPublishers);

            // (2)/(3) The score distributions, from the REAL engine, one pass per arm.
            var before = await ScoreAllAsync(provider, companies, signals, windowReads, ct);
            var after = await ScoreAllAsync(
                provider, companies, windowedSignals, windowedWindowReads, ct);

            AppendDistribution(report, "`AttentionScore` distribution", before, after, s => s.AttentionScore);
            AppendDistribution(
                report, "`OpportunityScore` distribution", before, after, s => s.OpportunityScore);
            AppendCoverageSection(report, projection);

            output.WriteLine(report.ToString());

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
            "> 1. This is a **projection over accrued evidence**, not a replay of a different collection "
                + "history. Radar cannot re-run last month's feed; it can only ask which accrued items a "
                + "windowed query would not have returned, and score without them.");
        report.AppendLine(
            "> 2. **Cross-run dedupe means most excluded items contributed no new evidence** on the night "
                + "they were re-read — they were re-read and discarded — so the projected loss is an "
                + "OVERSTATEMENT of what the window actually costs.");
        report.AppendLine(
            "> 3. **The freed budget is not modelled.** The point of the window is that the 25 retained "
                + "slots go to material the unfiltered query never reached; no counterfactual over accrued "
                + "evidence can show what Radar would then have found, so the gain is UNDERSTATED.");
        report.AppendLine("> ");
        report.AppendLine(
            "> Treat it as a bound on the downside, not a forecast. The direction the spec predicts is "
                + "fewer redundant `MediaAttention` signals, so attention may fall and opportunity may rise; "
                + "what must NOT fall is COVERAGE — see the last section.");
        report.AppendLine();
    }

    /// <summary>
    /// Builds one arm's <see cref="ScoringEngine"/> — the REAL engine, over the supplied (possibly filtered)
    /// signal seams. Everything else is held constant, including the frozen source descriptor, so the one
    /// thing the descriptor could affect (the stamp) is identical on both arms.
    /// </summary>
    private static async Task<IReadOnlyList<CompanyScoreSnapshot>> ScoreAllAsync(
        ServiceProvider provider,
        IReadOnlyList<Company> companies,
        ISignalRepository signals,
        ISignalFileStore windowReads,
        CancellationToken ct)
    {
        var sourceWeights = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);
        var engine = new ScoringEngine(
            signals,
            windowReads,
            provider.GetRequiredService<IEvidenceRepository>(),
            new InMemoryScoreRepository(),
            provider.GetRequiredService<ICompanyRepository>(),
            new RadarScoreFormulaFactory(sourceWeights).Create(
                new ScoringStrategyDefinition("default", "default", new ScoringWeights(), IsPrimary: true)),
            new ScoringWeights(),
            sourceWeights,
            ReadOnlyHarnessSourceDescriptor.Instance,
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            new ScoringOptions { Window = ScoringWindow },
            NullLogger<ScoringEngine>.Instance,
            strategyName: "default");

        var snapshots = new List<CompanyScoreSnapshot>(companies.Count);
        foreach (var company in companies)
        {
            var result = await engine.ScoreCompanyAsync(company.Id, AsOfUtc, ct);
            snapshots.Add(result.Snapshot);
        }

        return snapshots;
    }

    /// <summary>
    /// The population <c>AttentionReach</c>'s breadth term actually sums (the spec-196 §7 method, verbatim):
    /// the DISTINCT third-party publishers per company over the scoring window, counting collapse survivors
    /// and collapsed-only outlets alike. The engine's window / known-at / review admission predicate is
    /// restated — tunable pipeline SCAFFOLDING, not formula math; the scores below come from the real engine
    /// and nothing here feeds them.
    /// <para>
    /// <b>Called once per arm, and that is cheap — confirmed, not assumed.</b> Both reads it makes are
    /// INDEX-BACKED: <c>FileSignalStore.GetByCompanyAsync</c> and <c>FileRawEvidenceStore.GetByIdAsync</c>
    /// each hydrate once per instance (spec 142) and then serve from a dictionary, so a second walk re-reads
    /// no file. Since spec 203 §2 <see cref="ISignalFileStore.ReadApprovedInWindowAsync"/> is index-backed
    /// too; it stays memoized (<see cref="MemoizingSignalWindowReads"/>) so the two arms are guaranteed the
    /// identical previous-window list, not because it still touches the disk.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>>
        CollectBreadthPublishersAsync(
            ISignalRepository signals,
            IEvidenceRepository evidence,
            IReadOnlyList<Company> companies,
            CancellationToken ct)
    {
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

    private static void AppendProjectionSection(StringBuilder report, NewsAdmissionProjection projection)
    {
        report.AppendLine("### 1. What the windowed arm excludes");
        report.AppendLine();
        report.AppendLine(
            $"A `NewsArticle` evidence item is excluded when `CollectedAt − PublishedAt` exceeds "
                + $"{RecencyWindowDays} days AND it was not collected on its company's EARLIEST recorded "
                + "collection date — the spec 198 §2 first-collection exemption, which keeps a newly seeded "
                + "company's back history. Items with no parseable `PublishedAt` are KEPT: an unknown age is "
                + "not evidence of staleness.");
        report.AppendLine();
        report.AppendLine("| quantity | count |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine($"| news evidence items examined | {projection.NewsEvidenceExamined} |");
        report.AppendLine($"| excluded (older than the window) | {projection.ExcludedEvidenceIds.Count} |");
        report.AppendLine($"| kept by the first-collection exemption | {projection.FirstCollectionExempt} |");
        report.AppendLine($"| kept — no parseable publication instant | {projection.NoPublishedInstant} |");
        report.AppendLine($"| non-news evidence (never affected) | {projection.NonNewsEvidence} |");
        report.AppendLine();
    }

    private static void AppendBreadthSection(
        StringBuilder report,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> before,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> after)
    {
        var weights = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);
        var beforeReach = before.Values.Select(p => ScoreSignalMath.TierWeightedReach(p, weights)).ToList();
        var afterReach = after.Values.Select(p => ScoreSignalMath.TierWeightedReach(p, weights)).ToList();

        report.AppendLine("### 2. Distinct publisher breadth actually consumed by `AttentionReach`");
        report.AppendLine();
        report.AppendLine(
            $"{before.Count} companies · {before.Values.Sum(p => p.Count)} company-publisher pairs before, "
                + $"{after.Values.Sum(p => p.Count)} after · "
                + $"{before.Values.SelectMany(p => p).Distinct(StringComparer.OrdinalIgnoreCase).Count()} → "
                + $"{after.Values.SelectMany(p => p).Distinct(StringComparer.OrdinalIgnoreCase).Count()} "
                + "distinct publishers universe-wide. **This is the population the score actually sees.**");
        report.AppendLine();
        report.AppendLine("| tier-weighted breadth per company | unfiltered | windowed |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine($"| min | {Stat(beforeReach, v => v.Min())} | {Stat(afterReach, v => v.Min())} |");
        report.AppendLine(
            $"| median | {Stat(beforeReach, Median)} | {Stat(afterReach, Median)} |");
        report.AppendLine(
            $"| mean | {Stat(beforeReach, v => v.Average())} | {Stat(afterReach, v => v.Average())} |");
        report.AppendLine($"| max | {Stat(beforeReach, v => v.Max())} | {Stat(afterReach, v => v.Max())} |");
        report.AppendLine();
    }

    private static void AppendDistribution(
        StringBuilder report,
        string heading,
        IReadOnlyList<CompanyScoreSnapshot> before,
        IReadOnlyList<CompanyScoreSnapshot> after,
        Func<CompanyScoreSnapshot, int> selector)
    {
        var b = before.Select(selector).Select(v => (double)v).ToList();
        var a = after.Select(selector).Select(v => (double)v).ToList();

        report.AppendLine("### " + heading);
        report.AppendLine();
        report.AppendLine("| statistic | unfiltered | windowed |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine($"| n | {b.Count} | {a.Count} |");
        report.AppendLine($"| min | {Stat(b, v => v.Min())} | {Stat(a, v => v.Min())} |");
        report.AppendLine($"| median | {Stat(b, Median)} | {Stat(a, Median)} |");
        report.AppendLine($"| mean | {Stat(b, v => v.Average())} | {Stat(a, v => v.Average())} |");
        report.AppendLine($"| max | {Stat(b, v => v.Max())} | {Stat(a, v => v.Max())} |");
        report.AppendLine(
            $"| spread (max−min) | {Stat(b, v => v.Max() - v.Min())} "
                + $"| {Stat(a, v => v.Max() - v.Min())} |");
        report.AppendLine();
        report.AppendLine("| decade | unfiltered | windowed |");
        report.AppendLine("| --- | ---: | ---: |");
        for (var decade = 0; decade <= 100; decade += 10)
        {
            var upper = decade == 100 ? 100 : decade + 9;
            var beforeCount = b.Count(v => v >= decade && v <= upper);
            var afterCount = a.Count(v => v >= decade && v <= upper);
            if (beforeCount == 0 && afterCount == 0)
            {
                continue;
            }

            report.AppendLine($"| {decade}–{upper} | {beforeCount} | {afterCount} |");
        }

        report.AppendLine();
    }

    private static void AppendCoverageSection(StringBuilder report, NewsAdmissionProjection projection)
    {
        report.AppendLine("### Coverage — the criterion that must NOT regress");
        report.AppendLine();
        report.AppendLine(
            $"Genuinely recent news evidence — collected within {RecencyWindowDays} days of publication — "
                + "admitted by each arm. **The windowed arm must admit at least as many as the unfiltered "
                + "arm.** It does so by construction here (the exclusion predicate only ever removes items "
                + "OLDER than the window), which is why the meaningful coverage check is the LIVE "
                + "measurement's, where the two arms query the provider independently.");
        report.AppendLine();
        report.AppendLine("| quantity | unfiltered | windowed |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine(
            $"| recent news evidence items | {projection.RecentNewsEvidence} | {projection.RecentNewsEvidence} |");
        report.AppendLine(
            $"| all news evidence items | {projection.NewsEvidenceExamined} "
                + $"| {projection.NewsEvidenceExamined - projection.ExcludedEvidenceIds.Count} |");
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
    /// The evidence-admission projection: which accrued <c>NewsArticle</c> evidence a windowed query would
    /// NOT have returned. Purely a set of evidence ids plus the counts that explain it — no scoring
    /// arithmetic, and nothing is written.
    /// </summary>
    private sealed record NewsAdmissionProjection(
        IReadOnlySet<Guid> ExcludedEvidenceIds,
        int NewsEvidenceExamined,
        int RecentNewsEvidence,
        int FirstCollectionExempt,
        int NoPublishedInstant,
        int NonNewsEvidence)
    {
        public static async Task<NewsAdmissionProjection> BuildAsync(
            ISignalRepository signals,
            IEvidenceRepository evidence,
            IReadOnlyList<Company> companies,
            int windowDays,
            CancellationToken ct)
        {
            // Evidence carries no company, so the company binding comes from the signals that reference it —
            // the same relation the score walks. An evidence item referenced by several companies' signals is
            // examined against EACH, and is excluded only when it is stale for every one of them.
            var companiesByEvidence = new Dictionary<Guid, HashSet<Guid>>();
            foreach (var company in companies)
            {
                foreach (var signal in await signals.GetByCompanyAsync(company.Id, ct))
                {
                    if (!companiesByEvidence.TryGetValue(signal.EvidenceId, out var set))
                    {
                        set = [];
                        companiesByEvidence[signal.EvidenceId] = set;
                    }

                    set.Add(company.Id);
                }
            }

            var all = await evidence.GetAllAsync(ct);
            var news = all
                .Where(e => e.SourceType == EvidenceSourceType.NewsArticle)
                .OrderBy(e => e.Id)
                .ToList();

            // Each company's EARLIEST recorded news-collection DATE — the spec 198 §2 first-collection
            // exemption, read from persisted state exactly as the collector reads it (never a clock).
            var earliestByCompany = new Dictionary<Guid, DateOnly>();
            foreach (var item in news)
            {
                if (!companiesByEvidence.TryGetValue(item.Id, out var owners))
                {
                    continue;
                }

                var date = DateOnly.FromDateTime(item.CollectedAtUtc.UtcDateTime);
                foreach (var owner in owners)
                {
                    if (!earliestByCompany.TryGetValue(owner, out var existing) || date < existing)
                    {
                        earliestByCompany[owner] = date;
                    }
                }
            }

            var excluded = new HashSet<Guid>();
            var recent = 0;
            var exempt = 0;
            var noInstant = 0;
            foreach (var item in news)
            {
                if (item.PublishedAtUtc is not { } published)
                {
                    // An unknown age is not evidence of staleness — keep it.
                    noInstant++;
                    continue;
                }

                if ((item.CollectedAtUtc - published).TotalDays <= windowDays)
                {
                    recent++;
                    continue;
                }

                if (!companiesByEvidence.TryGetValue(item.Id, out var owners))
                {
                    // Unreferenced evidence: no company binding, so no first-collection question and no
                    // signal to remove either. Excluding it changes nothing.
                    excluded.Add(item.Id);
                    continue;
                }

                var collectedOn = DateOnly.FromDateTime(item.CollectedAtUtc.UtcDateTime);
                if (owners.Any(o =>
                        earliestByCompany.TryGetValue(o, out var earliest) && collectedOn == earliest))
                {
                    exempt++;
                    continue;
                }

                excluded.Add(item.Id);
            }

            return new NewsAdmissionProjection(
                excluded, news.Count, recent, exempt, noInstant, all.Count - news.Count);
        }
    }

    /// <summary>
    /// The windowed arm's <see cref="ISignalRepository"/>: the same reads, minus the signals whose evidence
    /// the windowed query would not have returned. A pure admission filter — it duplicates no scoring
    /// arithmetic — and <see cref="AddAsync"/> THROWS, because this harness is read-only and must fail
    /// loudly if that ever stops being true.
    /// </summary>
    private sealed class NewsWindowFilteredSignalRepository(
        ISignalRepository inner, IReadOnlySet<Guid> excludedEvidenceIds) : ISignalRepository
    {
        public Task AddAsync(Signal signal, CancellationToken ct) =>
            throw new InvalidOperationException(
                "The spec-198 §4 counterfactual is read-only and must never write a signal.");

        public async Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            var signal = await inner.GetByIdAsync(id, ct);
            return signal is not null && excludedEvidenceIds.Contains(signal.EvidenceId) ? null : signal;
        }

        public async Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct) =>
            [.. (await inner.GetByCompanyAsync(companyId, ct))
                .Where(s => !excludedEvidenceIds.Contains(s.EvidenceId))];

        public async Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(
            DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct) =>
            [.. (await inner.GetObservedBetweenAsync(startUtc, endUtc, ct))
                .Where(s => !excludedEvidenceIds.Contains(s.EvidenceId))];
    }

    /// <summary>
    /// The windowed arm's <see cref="ISignalFileStore"/> — the engine's current-window read seam — filtered
    /// on the same evidence-id set, so the two seams cannot disagree about what the arm can see.
    /// </summary>
    private sealed class NewsWindowFilteredSignalFileStore(
        ISignalFileStore inner, IReadOnlySet<Guid> excludedEvidenceIds) : ISignalFileStore
    {
        public Task<DurableWriteResult> WriteAsync(Signal signal, SignalReview review, CancellationToken ct) =>
            throw new InvalidOperationException(
                "The spec-198 §4 counterfactual is read-only and must never write a signal.");

        public async Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct) =>
            [.. (await inner.ReadApprovedInWindowAsync(
                    companyId, startExclusiveUtc, endInclusiveUtc, knownAsOfUtc, ct))
                .Where(s => !excludedEvidenceIds.Contains(s.EvidenceId))];
    }
}

/// <summary>
/// Runs the spec-198 §4 counterfactual only when a live data root is supplied, and SKIPS WITH A NAMED
/// REASON otherwise (the spec-196 §7 precedent) — never silently.
/// </summary>
public sealed class NewsRecencyCounterfactualFactAttribute : FactAttribute
{
    public NewsRecencyCounterfactualFactAttribute()
    {
        if (NewsRecencyWindowCounterfactualTests.DataRoot() is null)
        {
            Skip = NewsRecencyWindowCounterfactualTests.SkipReason;
        }
    }
}
