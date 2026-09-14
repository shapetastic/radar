using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Acquisitions;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.Infrastructure.Acquisitions;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// Spec 226 §4 — the READ-ONLY PAIRED COUNTERFACTUAL for <c>radar-keyword-rules-v9</c>: what removing the invented
/// direction from the SEC 8-K Item 1.01 / 2.01 heading rules does to Trajectory and Opportunity over the live
/// universe, at ONE as-of instant, for the `default` arm AND the effective Lead, through the REAL <see cref="ScoringEngine"/>, the REAL
/// <see cref="KeywordSignalExtractor"/> and the REAL <see cref="CorporateActionSupersede"/> over the live
/// acquisitions store, persisting nothing.
/// <para>
/// <b>BEFORE</b> is the store as it is: every accrued heading read is a v8 Positive
/// <c>StrategicPartnership</c>, and the corporate-action supersede runs over the live recognitions (over v8 data
/// the v2 match rewrites exactly what v1 did, because no CorporateAction is on disk). <b>AFTER</b> wraps the signal
/// repository and the previous-window read with an IN-MEMORY projection that answers "what would v9 have
/// persisted for this evidence?" by RE-RUNNING THE PRODUCTION v9 EXTRACTOR over each accrued partnership's
/// evidence: an accrued partnership whose Reason is the v8 heading-phrase Reason is replaced by the v9
/// extractor's CorporateAction (type, direction and Reason from the extractor; id, company, instants, review
/// status and the reviewer-adjusted confidence kept — the deterministic reviewer's decision reads strength,
/// novelty, confidence and source quality only, which the rule change does not touch, and the harness asserts
/// the extractor's strength/novelty equal the stored ones); a partnership minted by a DIFFERENT phrase whose
/// evidence the v9 extractor also gives a CorporateAction gains that CorporateAction beside it (reviewed by the
/// production <see cref="DeterministicSignalReviewer"/>). Every other signal is passed through unchanged.
/// </para>
/// <para>
/// <b>What this is and is not.</b> A re-score at ONE instant held in memory, NOT a persisted run. It projects
/// what the v9 rule table would have produced on the accrued evidence; the live store itself will NOT change —
/// accrued v8 signals stay as written (AD-8) and the change reaches only evidence extracted after the merge.
/// </para>
/// <para>
/// <b>ENV-GATED</b> on <c>RADAR_ITEM_HEADING_DATA_ROOT</c>, skipped with a NAMED reason otherwise. NO network.
/// Writes throw; the report goes to a per-process temp path; the harness records every file's length and
/// last-write time under the data root before and after AND lists every file or directory whose last-write or
/// creation time is at or after its own start (the <c>find -newer marker</c> check), asserting both are empty.
/// </para>
/// <para>
/// <b>Two arms</b> (spec 226 review): <c>default</c> and the effective Lead named by the store's
/// <c>strategy-operating-calls.json</c>, each bound from <c>scripts/run-profiles/default.json</c> through the
/// Worker's own strategy binder (formula, weights, signal types, channels, label lines), so the Lead's label-line
/// crossings are reported against the lines the profile actually declares.
/// </para>
/// </summary>
public sealed class ItemHeadingDirectionCounterfactualTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_ITEM_HEADING_DATA_ROOT";

    internal const string SkipReason =
        "Spec 226 §4 item-heading counterfactual: set " + DataRootVariable
            + " to a Radar data root (companies.json, signals/, evidence/raw/, scores/, acquisitions/) to run it.";

    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    /// <summary>The live baseline window (<c>RadarWorkerOptions.ScoringWindowDays</c> = 60), restated as in spec 224's harness.</summary>
    private static readonly TimeSpan ScoringWindow = TimeSpan.FromDays(60);

    /// <summary>The companies spec 226's Overview names, in its order, plus HZO for the spec-217 interaction.</summary>
    private static readonly string[] NamedTickers = ["ESQ", "DGII", "EOSE", "CAT", "CALM", "MYRG", "ASIX", "NOVT", "THRM", "HZO"];

    private static readonly string OutputPath =
        Path.Combine(
            Path.GetTempPath(),
            "radar-spec-226-item-heading-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".md");

    [ItemHeadingDirectionCounterfactualFact]
    public async Task PairedCounterfactual_V8HeadingDirectionVersusV9NeutralCorporateAction_OverTheLiveUniverse()
    {
        var root = DataRoot()!;
        var ct = CancellationToken.None;

        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var harnessStartedUtc = DateTime.UtcNow;
            var filesBefore = SnapshotFiles(root);

            var report = new StringBuilder();
            await using var provider = AttentionPolicyCounterfactualTests.BuildReadOnlyProvider(root);
            var seeded = await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);
            var companies = (await provider.GetRequiredService<ICompanyRepository>().GetAllAsync(ct))
                .OrderBy(c => c.Id)
                .ToList();

            var signals = provider.GetRequiredService<ISignalRepository>();
            var evidence = new ReadOnlyEvidenceRepository(provider.GetRequiredService<IEvidenceRepository>());
            var windowReads = new MemoizingSignalWindowReads(provider.GetRequiredService<ISignalFileStore>());

            var asOf = await InsiderCollapseCounterfactualTests.ResolveAsOfAsync(root, signals, companies, ct);
            var windowStart = asOf.Instant - ScoringWindow;

            var acquisitionStore = new FileAcquisitionStore(
                new FileAcquisitionStoreOptions { RootDirectory = Path.Combine(root, "acquisitions") },
                NullLogger<FileAcquisitionStore>.Instance);
            var acquisitionSource = new AcquisitionStorePendingSource(new ReadOnlyAcquisitionStore(acquisitionStore));
            var acquisitions = await acquisitionSource.GetAsync(ct);

            var extractor = new KeywordSignalExtractor(NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights());
            var reviewer = new DeterministicSignalReviewer(new FakeTimeProvider(asOf.Instant), NullLogger<DeterministicSignalReviewer>.Instance);
            var projection = await V9Projection.BuildAsync(companies, signals, evidence, extractor, reviewer, asOf.Instant, ct);

            var afterSignals = new ProjectedSignalRepository(signals, projection);
            var afterWindowReads = new ProjectedSignalWindowReads(windowReads, projection);

            // Spec 226 review: BOTH the `default` arm (radar-formula-v8, the storage primary) and the effective
            // LEAD arm are re-scored. The Lead is read from the store's own operating-call journal and its
            // definition (formula, weights, signal types, channels, label lines) is BOUND FROM
            // scripts/run-profiles/default.json through the Worker's own binder (RunProfileMirror +
            // AddRadarScoringStrategies / AddRadarCollectorAttribution) — nothing about the arm is typed in here.
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(RunProfileMirror.Compose()).Build();
            var bindServices = new ServiceCollection();
            bindServices.AddRadarScoringStrategies(configuration);
            bindServices.AddRadarCollectorAttribution(configuration);
            using var bound = bindServices.BuildServiceProvider();
            var strategySet = bound.GetRequiredService<ScoringStrategySet>();
            var attribution = bound.GetRequiredService<ICollectorAttributionResolver>();
            var leadName = ReadLeadStrategy(root);
            var arms = new List<ScoringStrategyDefinition>
            {
                strategySet.Strategies.Single(d => string.Equals(d.Name, "default", StringComparison.OrdinalIgnoreCase)),
            };
            var lead = strategySet.Strategies.SingleOrDefault(d => string.Equals(d.Name, leadName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The journal's Lead '{leadName}' is not a configured strategy in default.json.");
            if (!arms.Contains(lead))
            {
                arms.Add(lead);
            }

            report.AppendLine("## Spec 226 §4 — paired item-heading counterfactual (read-only re-score of the live store)");
            report.AppendLine();
            report.AppendLine(
                $"As-of `{asOf.Instant:O}` ({asOf.Source}) · scoring window {ScoringWindow.TotalDays:0} days "
                    + $"(`{windowStart:O}` exclusive → as-of inclusive) · extractor `{KeywordSignalExtractor.RuleSetVersion}` · "
                    + $"supersede `{CorporateActionSupersede.Version}` · {companies.Count} companies seeded from `companies.json` "
                    + $"(seeder reported {seeded}) · {acquisitions.Records.Count} recognised pending acquisition(s) read "
                    + $"({acquisitions.Unreadable} unreadable) · arms: {string.Join(", ", arms.Select(a => $"`{a.Name}` (`{a.Formula}`)"))}; "
                    + $"Lead per `strategy-operating-calls.json`: `{leadName}` · collector attribution `{attribution.GetType().Name}`.");
            report.AppendLine();
            report.AppendLine(
                "> ⚠ A re-score at one instant held in memory, NOT a persisted run. AFTER projects what the v9 rule table "
                    + "would have produced on the accrued evidence (production extractor re-run per evidence). The live "
                    + "store will not change: accrued v8 signals stay as written (AD-8) and only evidence extracted after "
                    + "the merge carries the v9 read.");
            report.AppendLine();

            AppendChangedSignals(report, projection, companies, windowStart, asOf.Instant);
            await AppendAcquisitionCountersAsync(
                report, companies, acquisitions, signals, afterSignals, evidence, windowStart, asOf.Instant, ct);

            var scored = new List<(ScoringStrategyDefinition Arm, IReadOnlyList<CompanyScoreSnapshot> Before, IReadOnlyList<CompanyScoreSnapshot> After)>();
            foreach (var arm in arms)
            {
                var beforeResults = new List<CompanyScoreResult>();
                var afterResults = new List<CompanyScoreResult>();
                var before = await InsiderCollapseCounterfactualTests.ScoreAllAsync(
                    provider, companies, signals, evidence, windowReads, asOf.Instant, ct, null, acquisitionSource, beforeResults, arm, attribution);
                var after = await InsiderCollapseCounterfactualTests.ScoreAllAsync(
                    provider, companies, afterSignals, evidence, afterWindowReads, asOf.Instant, ct, null, acquisitionSource, afterResults, arm, attribution);
                scored.Add((arm, before, after));

                var isLead = ReferenceEquals(arm, lead);
                report.AppendLine($"## Arm `{arm.Name}` — `{arm.Formula}`{(isLead ? " (the effective LEAD)" : string.Empty)}");
                report.AppendLine();
                report.AppendLine(
                    $"Bound from default.json: profile `{arm.ScoringProfile}`, signal types `{arm.SignalTypes}`, channels "
                        + $"{(arm.Channels.Channels.Count == 0 ? "none" : string.Join("; ", arm.Channels.Channels.Select(c => $"`{c.Name}` [{string.Join(",", c.Collectors)}] weight {c.Weight} saturation {c.Saturation}")))}, "
                        + $"label lines {(arm.Labels is { } l ? $"Investigate {l.Investigate} / Watch {l.Watch}" : "not declared")}.");
                report.AppendLine();

                var ranksBefore = Ranks(before, companies);
                var ranksAfter = Ranks(after, companies);
                AppendNamedCompanies(report, projection, companies, before, after, ranksBefore, ranksAfter, windowStart, asOf.Instant);
                AppendAffectedCompanies(report, projection, companies, before, after, ranksBefore, ranksAfter, windowStart, asOf.Instant);
                AppendUniverse(report, companies, before, after, ranksBefore, ranksAfter);
                AppendAcquisitionInteraction(
                    report, companies, acquisitions, signals, afterSignals, evidence, beforeResults, afterResults, windowStart, asOf.Instant, ct);
                AppendCorroborationProxy(report, companies, beforeResults, afterResults, arm.Name);
                if (arm.Labels is { } lines)
                {
                    AppendLabelLineCrossings(report, companies, acquisitions, before, after, lines, arm.Name, asOf.Instant);
                }
            }

            var filesAfter = SnapshotFiles(root);
            var changedFiles = filesBefore.Where(kv => !filesAfter.TryGetValue(kv.Key, out var a) || a != kv.Value).Select(kv => kv.Key)
                .Concat(filesAfter.Keys.Where(k => !filesBefore.ContainsKey(k)))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // The `find data -newer marker` check, performed by the harness itself: every file or directory under
            // the root whose last-write or creation time is at or after the instant the harness started.
            var newerThanStart = new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                .Where(i => i.LastWriteTimeUtc >= harnessStartedUtc || i.CreationTimeUtc >= harnessStartedUtc)
                .Select(i => Path.GetRelativePath(root, i.FullName))
                .Order(StringComparer.Ordinal)
                .ToList();

            report.AppendLine("## No-write confirmation");
            report.AppendLine();
            report.AppendLine(
                $"(a) Files under the data root before: {filesBefore.Count}; after: {filesAfter.Count}; files added, removed or "
                    + $"changed (length or last-write time): **{changedFiles.Count}**.");
            report.AppendLine(
                $"(b) Files or directories under the data root with a last-write or creation time at or after the harness "
                    + $"start (`{harnessStartedUtc:O}`; the `find -newer marker` check): **{newerThanStart.Count}**.");
            foreach (var path in changedFiles.Concat(newerThanStart).Distinct(StringComparer.Ordinal).Take(20))
            {
                report.AppendLine($"- `{path}`");
            }

            report.AppendLine();

            var markdown = report.ToString();
            output.WriteLine(markdown);
            File.WriteAllText(OutputPath, markdown, Encoding.UTF8);
            output.WriteLine($"(written to {OutputPath})");

            Assert.NotEmpty(companies);
            Assert.All(scored, arm =>
            {
                Assert.Equal(companies.Count, arm.Before.Count);
                Assert.Equal(arm.Before.Count, arm.After.Count);
            });
            Assert.Equal(0, projection.StrengthOrNoveltyMismatches);
            Assert.Empty(changedFiles);
            Assert.Empty(newerThanStart);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static bool InWindow(Signal s, DateTimeOffset windowStart, DateTimeOffset asOf) =>
        s.ObservedAtUtc > windowStart && s.ObservedAtUtc <= asOf && s.CreatedAtUtc <= asOf
        && s.ReviewStatus == SignalReviewStatus.Approved;

    private static string ItemsLabel(IReadOnlyList<SecItemHeadingPhrase> items) =>
        items.Count == 0 ? "(none named)" : string.Join(" + ", items.Select(i => i.Item));

    private static void AppendChangedSignals(
        StringBuilder report, V9Projection p, IReadOnlyList<Company> companies, DateTimeOffset windowStart, DateTimeOffset asOf)
    {
        var inWindow = p.Rewrites.Where(r => InWindow(r.Before, windowStart, asOf)).ToList();
        var previousStart = windowStart - ScoringWindow;
        var inPrevious = p.Rewrites.Count(r => r.Before.ObservedAtUtc > previousStart && r.Before.ObservedAtUtc <= windowStart
            && r.Before.CreatedAtUtc <= asOf && r.Before.ReviewStatus == SignalReviewStatus.Approved);
        var addedInWindow = p.Additions.Count(a => InWindow(a.After, windowStart, asOf));

        report.AppendLine("### 1. Signals that change — `StrategicPartnership (Positive)` → `CorporateAction (Neutral)`");
        report.AppendLine();
        report.AppendLine("Current 60-day window, approved, known at the as-of (the engine's own admission predicates):");
        report.AppendLine();
        report.AppendLine("| split | filing evidence | non-filing evidence | total |");
        report.AppendLine("| --- | ---: | ---: | ---: |");
        foreach (var label in new[] { "1.01", "2.01", "1.01 + 2.01" })
        {
            var rows = inWindow.Where(r => ItemsLabel(r.Items) == label).ToList();
            report.AppendLine($"| item heading {label} | {rows.Count(r => r.SourceType == EvidenceSourceType.Filing)} | {rows.Count(r => r.SourceType != EvidenceSourceType.Filing)} | {rows.Count} |");
        }

        report.AppendLine($"| **all** | {inWindow.Count(r => r.SourceType == EvidenceSourceType.Filing)} | {inWindow.Count(r => r.SourceType != EvidenceSourceType.Filing)} | {inWindow.Count} |");
        report.AppendLine();
        report.AppendLine($"- companies with at least one changed signal in the window: {inWindow.Select(r => r.Before.CompanyId).Distinct().Count()}");
        report.AppendLine($"- non-filing in-window changes by source type: {Breakdown(inWindow.Where(r => r.SourceType != EvidenceSourceType.Filing).Select(r => r.SourceType.ToString()))}");
        report.AppendLine($"- previous/velocity window (activity only): {inPrevious} changed signal(s)");
        report.AppendLine($"- CorporateAction ADDED beside a partnership minted by another phrase (in window): {addedInWindow}; all time: {p.Additions.Count}");
        report.AppendLine();
        report.AppendLine("All time over the accrued store (every review status), the non-filing scope measurement:");
        report.AppendLine();
        report.AppendLine("| accrued v8 heading-phrase partnership reads | count |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine($"| total | {p.Rewrites.Count} |");
        report.AppendLine($"| over Filing evidence | {p.Rewrites.Count(r => r.SourceType == EvidenceSourceType.Filing)} |");
        report.AppendLine($"| over non-filing evidence | {p.Rewrites.Count(r => r.SourceType != EvidenceSourceType.Filing)} ({Breakdown(p.Rewrites.Where(r => r.SourceType != EvidenceSourceType.Filing).Select(r => r.SourceType.ToString()))}) |");
        report.AppendLine($"| item 1.01 / 2.01 / both named | {p.Rewrites.Count(r => ItemsLabel(r.Items) == "1.01")} / {p.Rewrites.Count(r => ItemsLabel(r.Items) == "2.01")} / {p.Rewrites.Count(r => ItemsLabel(r.Items) == "1.01 + 2.01")} |");
        report.AppendLine();
        report.AppendLine("Counted axes of the projection:");
        report.AppendLine();
        report.AppendLine("| axis | count |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine($"| accrued StrategicPartnership signals inspected (all companies, all time) | {p.PartnershipsInspected} |");
        report.AppendLine($"| of which kept (another phrase, still a partnership under v9) | {p.PartnershipsKept} |");
        report.AppendLine($"| of which evidence unresolvable (passed through unchanged; the engine drops these before scoring) | {p.EvidenceUnresolvable} |");
        report.AppendLine($"| of those, carrying a v8 heading-phrase Reason (source type NOT knowable; all time / in window) | {p.UnresolvableHeadingReads.Count} / {p.UnresolvableHeadingReads.Count(sig => InWindow(sig, windowStart, asOf))} |");
        report.AppendLine($"| of which over NewsArticle evidence (never reaches the rule table) | {p.OverNewsArticle} |");
        report.AppendLine($"| heading-phrase partnerships the v9 extractor gave NO CorporateAction (passed through; a finding if non-zero) | {p.HeadingReadWithoutV9CorporateAction} |");
        report.AppendLine($"| v9 strength/novelty differing from the stored v8 read (asserted zero) | {p.StrengthOrNoveltyMismatches} |");
        report.AppendLine($"| further copies of an already-projected heading read (same company + evidence; projected, not re-counted) | {p.CopiesOfAnAlreadyProjectedRead} |");
        report.AppendLine();

        var byCompany = companies.ToDictionary(c => c.Id);
        report.AppendLine("In-window changed signals, one row each:");
        report.AppendLine();
        report.AppendLine("| observed | ticker | source | items named | evidence title |");
        report.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var r in inWindow.OrderBy(r => r.Before.ObservedAtUtc).ThenBy(r => r.Before.Id))
        {
            var ticker = r.Before.CompanyId is { } id && byCompany.TryGetValue(id, out var c) ? c.Ticker ?? c.Name : "(unresolved)";
            report.AppendLine($"| {r.Before.ObservedAtUtc:yyyy-MM-dd} | {ticker} | {r.SourceType} | {ItemsLabel(r.Items)} | {Md(r.EvidenceTitle)} |");
        }

        report.AppendLine();
    }

    private static string Breakdown(IEnumerable<string> values)
    {
        var grouped = values.GroupBy(v => v, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        return grouped.Count == 0 ? "none" : string.Join(", ", grouped.Select(g => $"{g.Key} {g.Count()}"));
    }

    private static string Md(string? text) =>
        (text ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>Rank: descending Opportunity, then descending Trajectory, then ticker (ordinal, null last), then id.</summary>
    private static Dictionary<Guid, int> Ranks(IReadOnlyList<CompanyScoreSnapshot> snapshots, IReadOnlyList<Company> companies)
    {
        var byId = companies.ToDictionary(c => c.Id);
        return snapshots
            .OrderByDescending(s => s.OpportunityScore)
            .ThenByDescending(s => s.TrajectoryScore)
            .ThenBy(s => byId[s.CompanyId].Ticker is null ? 1 : 0)
            .ThenBy(s => byId[s.CompanyId].Ticker, StringComparer.Ordinal)
            .ThenBy(s => s.CompanyId)
            .Select((s, i) => (s.CompanyId, Rank: i + 1))
            .ToDictionary(x => x.CompanyId, x => x.Rank);
    }

    private static void AppendNamedCompanies(
        StringBuilder report, V9Projection p, IReadOnlyList<Company> companies,
        IReadOnlyList<CompanyScoreSnapshot> before, IReadOnlyList<CompanyScoreSnapshot> after,
        Dictionary<Guid, int> ranksBefore, Dictionary<Guid, int> ranksAfter, DateTimeOffset windowStart, DateTimeOffset asOf)
    {
        report.AppendLine("### 2. The companies spec 226 names");
        report.AppendLine();
        AppendCompanyTable(report, NamedTickers.Select(t => (t, companies.FirstOrDefault(c => string.Equals(c.Ticker, t, StringComparison.OrdinalIgnoreCase)))),
            p, before, after, ranksBefore, ranksAfter, windowStart, asOf);
    }

    private static void AppendAffectedCompanies(
        StringBuilder report, V9Projection p, IReadOnlyList<Company> companies,
        IReadOnlyList<CompanyScoreSnapshot> before, IReadOnlyList<CompanyScoreSnapshot> after,
        Dictionary<Guid, int> ranksBefore, Dictionary<Guid, int> ranksAfter, DateTimeOffset windowStart, DateTimeOffset asOf)
    {
        var affected = p.Rewrites.Where(r => InWindow(r.Before, windowStart, asOf)).Select(r => r.Before.CompanyId)
            .Concat(p.Additions.Where(a => InWindow(a.After, windowStart, asOf)).Select(a => a.After.CompanyId))
            .OfType<Guid>()
            .ToHashSet();
        report.AppendLine("### 3. Every company with a changed in-window signal");
        report.AppendLine();
        AppendCompanyTable(report,
            companies.Where(c => affected.Contains(c.Id)).OrderBy(c => c.Ticker, StringComparer.Ordinal).Select(c => (c.Ticker ?? c.Name, (Company?)c)),
            p, before, after, ranksBefore, ranksAfter, windowStart, asOf);
    }

    private static void AppendCompanyTable(
        StringBuilder report, IEnumerable<(string Label, Company? Company)> rows, V9Projection p,
        IReadOnlyList<CompanyScoreSnapshot> before, IReadOnlyList<CompanyScoreSnapshot> after,
        Dictionary<Guid, int> ranksBefore, Dictionary<Guid, int> ranksAfter, DateTimeOffset windowStart, DateTimeOffset asOf)
    {
        var beforeById = before.ToDictionary(s => s.CompanyId);
        var afterById = after.ToDictionary(s => s.CompanyId);
        report.AppendLine("| ticker | changed in-window signals (items) | Trajectory before → after | Opportunity before → after | rank before → after | EvidenceConfidence before → after | Velocity before → after |");
        report.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var (label, company) in rows)
        {
            if (company is null)
            {
                report.AppendLine($"| {label} | not in this universe | — | — | — | — | — |");
                continue;
            }

            var changed = p.Rewrites.Where(r => r.Before.CompanyId == company.Id && InWindow(r.Before, windowStart, asOf)).ToList();
            var added = p.Additions.Count(a => a.After.CompanyId == company.Id && InWindow(a.After, windowStart, asOf));
            var b = beforeById[company.Id];
            var a = afterById[company.Id];
            var items = changed.Count == 0 ? "0" : $"{changed.Count} ({string.Join("; ", changed.Select(r => ItemsLabel(r.Items)))})";
            if (added > 0)
            {
                items += $" + {added} added";
            }

            report.AppendLine(
                $"| {label} | {items} | {b.TrajectoryScore} → {a.TrajectoryScore} | {b.OpportunityScore} → {a.OpportunityScore} "
                    + $"| {ranksBefore[company.Id]} → {ranksAfter[company.Id]} | {b.EvidenceConfidenceScore} → {a.EvidenceConfidenceScore} "
                    + $"| {b.SignalVelocityScore} → {a.SignalVelocityScore} |");
        }

        report.AppendLine();
    }

    private static void AppendUniverse(
        StringBuilder report, IReadOnlyList<Company> companies, IReadOnlyList<CompanyScoreSnapshot> before,
        IReadOnlyList<CompanyScoreSnapshot> after, Dictionary<Guid, int> ranksBefore, Dictionary<Guid, int> ranksAfter)
    {
        var afterById = after.ToDictionary(s => s.CompanyId);
        var byId = companies.ToDictionary(c => c.Id);
        var deltas = before.Select(b => (Company: byId[b.CompanyId], Before: b, After: afterById[b.CompanyId],
            Delta: afterById[b.CompanyId].OpportunityScore - b.OpportunityScore)).ToList();
        var max = deltas.OrderByDescending(d => Math.Abs(d.Delta)).ThenBy(d => d.Company.Id).First();
        var rankChanged = before.Count(b => ranksBefore[b.CompanyId] != ranksAfter[b.CompanyId]);
        var trajectoryChanged = deltas.Count(d => d.Before.TrajectoryScore != d.After.TrajectoryScore);
        var opportunityChanged = deltas.Count(d => d.Delta != 0);
        var top10Before = ranksBefore.Where(kv => kv.Value <= 10).Select(kv => kv.Key).ToHashSet();
        var top10After = ranksAfter.Where(kv => kv.Value <= 10).Select(kv => kv.Key).ToHashSet();

        report.AppendLine("### 4. Whole universe, before → after");
        report.AppendLine();
        report.AppendLine("| statistic | BEFORE | AFTER |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine($"| n | {before.Count} | {after.Count} |");
        report.AppendLine($"| median Trajectory | {Median(before.Select(s => (double)s.TrajectoryScore))} | {Median(after.Select(s => (double)s.TrajectoryScore))} |");
        report.AppendLine($"| median Opportunity | {Median(before.Select(s => (double)s.OpportunityScore))} | {Median(after.Select(s => (double)s.OpportunityScore))} |");
        report.AppendLine($"| companies whose Trajectory changed | — | {trajectoryChanged} |");
        report.AppendLine($"| companies whose Opportunity changed | — | {opportunityChanged} |");
        report.AppendLine($"| companies whose Opportunity moved by more than 2 points | — | {deltas.Count(d => Math.Abs(d.Delta) > 2)} |");
        report.AppendLine($"| companies whose rank changed | — | {rankChanged} |");
        string Names(IEnumerable<Guid> ids) =>
            string.Join(", ", ids.Select(id => byId[id].Ticker ?? byId[id].Name).Order(StringComparer.Ordinal)) is { Length: > 0 } n ? n : "none";
        report.AppendLine($"| top-10 entries / exits | — | {top10After.Except(top10Before).Count()} ({Names(top10After.Except(top10Before))}) / {top10Before.Except(top10After).Count()} ({Names(top10Before.Except(top10After))}) |");
        report.AppendLine(
            $"| max Opportunity mover | — | {max.Company.Ticker ?? max.Company.Name}: {max.Before.OpportunityScore} → {max.After.OpportunityScore} "
                + $"({(max.Delta >= 0 ? "+" : string.Empty)}{max.Delta}), Trajectory {max.Before.TrajectoryScore} → {max.After.TrajectoryScore} |");
        report.AppendLine();
        report.AppendLine(
            "Expected shape: each affected company loses strength-4 positive mass, so the effect is largest where these "
                + "headings are a big share of positive evidence and small where positive mass is large. Activity/velocity "
                + "and EvidenceConfidence are expected unchanged (same strength, confidence and evidence).");
        report.AppendLine();
    }

    private static string Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return "n/a";
        }

        var mid = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        return median.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static void AppendAcquisitionInteraction(
        StringBuilder report, IReadOnlyList<Company> companies, PendingAcquisitions acquisitions,
        ISignalRepository beforeSignals, ISignalRepository afterSignals, IEvidenceRepository evidence,
        IReadOnlyList<CompanyScoreResult> beforeResults, IReadOnlyList<CompanyScoreResult> afterResults,
        DateTimeOffset windowStart, DateTimeOffset asOf, CancellationToken ct)
    {
        var byId = companies.ToDictionary(c => c.Id);
        var beforeById = beforeResults.ToDictionary(r => r.Snapshot.CompanyId);
        var afterById = afterResults.ToDictionary(r => r.Snapshot.CompanyId);

        report.AppendLine("### 5. Interaction with spec 217 (every recognised pending acquisition)");
        report.AppendLine();
        report.AppendLine("Scored links over the recognised filing's evidence, from the real engine's persisted-in-memory links:");
        report.AppendLine();
        report.AppendLine("| company | accession | announced | links over the filing BEFORE | links over the filing AFTER | CorporateAction links AFTER | nothing-rewritten diagnostic before / after |");
        report.AppendLine("| --- | --- | --- | --- | --- | ---: | --- |");
        foreach (var record in acquisitions.Records)
        {
            var name = byId.TryGetValue(record.CompanyId, out var c) ? c.Ticker ?? c.Name : $"(not in universe: {record.CompanyId})";
            if (!beforeById.TryGetValue(record.CompanyId, out var b) || !afterById.TryGetValue(record.CompanyId, out var a))
            {
                report.AppendLine($"| {name} | {record.Accession} | {record.AnnouncedOnUtc:yyyy-MM-dd} | (not scored) | (not scored) | — | — |");
                continue;
            }

            var linksBefore = b.Links.Where(l => l.EvidenceId == record.EvidenceId).Select(l => Md(l.ContributionReason)).ToList();
            var linksAfter = a.Links.Where(l => l.EvidenceId == record.EvidenceId).Select(l => Md(l.ContributionReason)).ToList();
            report.AppendLine(
                $"| {name} | {record.Accession} | {record.AnnouncedOnUtc:yyyy-MM-dd} | {(linksBefore.Count == 0 ? "none" : string.Join("<br>", linksBefore))} "
                    + $"| {(linksAfter.Count == 0 ? "none" : string.Join("<br>", linksAfter))} "
                    + $"| {linksAfter.Count(l => l.StartsWith("CorporateAction", StringComparison.Ordinal))} "
                    + $"| {b.Diagnostics.RecognisedAcquisitionNothingRewritten} / {a.Diagnostics.RecognisedAcquisitionNothingRewritten} |");
        }

        report.AppendLine();
    }

    private static async Task AppendAcquisitionCountersAsync(
        StringBuilder report, IReadOnlyList<Company> companies, PendingAcquisitions acquisitions,
        ISignalRepository beforeSignals, ISignalRepository afterSignals, IEvidenceRepository evidence,
        DateTimeOffset windowStart, DateTimeOffset asOf, CancellationToken ct)
    {
        var byId = companies.ToDictionary(c => c.Id);
        report.AppendLine("Supersede counters over each arm's current-window approved signal+evidence pairs (the production `CorporateActionSupersede.Apply`):");
        report.AppendLine();
        report.AppendLine("| company | arm | outcome | rewritten | from StrategicPartnership | from CorporateAction | duplicates collapsed | CorporateAction signals over the filing after apply |");
        report.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var record in acquisitions.Records)
        {
            var name = byId.TryGetValue(record.CompanyId, out var c) ? c.Ticker ?? c.Name : record.CompanyId.ToString();
            foreach (var (arm, repo) in new[] { ("BEFORE", beforeSignals), ("AFTER", afterSignals) })
            {
                var pairs = new List<ScoringSignal>();
                foreach (var s in (await repo.GetByCompanyAsync(record.CompanyId, ct)).Where(s => InWindow(s, windowStart, asOf)))
                {
                    if (await evidence.GetByIdAsync(s.EvidenceId, ct) is { } e)
                    {
                        pairs.Add(new ScoringSignal(s, e));
                    }
                }

                var result = CorporateActionSupersede.Apply(pairs, acquisitions, record.CompanyId);
                var corporateActions = result.Signals.Count(s => s.Signal.EvidenceId == record.EvidenceId && s.Signal.Type == SignalType.CorporateAction);
                report.AppendLine(
                    $"| {name} | {arm} | {result.Outcome} | {result.TotalSuperseded} | {result.SupersededFromStrategicPartnership} "
                        + $"| {result.SupersededFromCorporateAction} | {result.DuplicatesCollapsed} | {corporateActions} |");
            }
        }

        report.AppendLine();
    }

    /// <summary>
    /// A report-layer PROXY, not the report: <c>weekly-report-action</c>'s corroboration floor counts DISTINCT positive
    /// signal types among a Small/Mid, Trajectory ≥ 50 company's contributing signals and floors it to Watch at 2.
    /// The v8 heading partnership was one of those types. This counts, over the `default` re-score's links (the
    /// labelled Lead arm is a different strategy with its own lines, so no label is asserted), the companies that
    /// meet the tier/trajectory gate with ≥ 2 positive types BEFORE and fewer AFTER.
    /// </summary>
    private static void AppendCorroborationProxy(
        StringBuilder report, IReadOnlyList<Company> companies,
        IReadOnlyList<CompanyScoreResult> beforeResults, IReadOnlyList<CompanyScoreResult> afterResults, string armName)
    {
        var byId = companies.ToDictionary(c => c.Id);
        var afterById = afterResults.ToDictionary(r => r.Snapshot.CompanyId);

        static HashSet<string> PositiveTypes(CompanyScoreResult r) =>
            [.. r.Links
                .Select(l => l.ContributionReason)
                .Where(reason => reason.Contains(" (Positive)", StringComparison.Ordinal))
                .Select(reason => reason[..reason.IndexOf(" (Positive)", StringComparison.Ordinal)])];

        var crossed = new List<string>();
        var eligibleBefore = 0;
        foreach (var b in beforeResults)
        {
            var company = byId[b.Snapshot.CompanyId];
            if (company.FollowingTier is not (FollowingTier.Small or FollowingTier.Mid))
            {
                continue;
            }

            var a = afterById[company.Id];
            var typesBefore = PositiveTypes(b);
            var typesAfter = PositiveTypes(a);
            var gateBefore = b.Snapshot.TrajectoryScore >= 50 && typesBefore.Count >= 2;
            var gateAfter = a.Snapshot.TrajectoryScore >= 50 && typesAfter.Count >= 2;
            if (gateBefore)
            {
                eligibleBefore++;
            }

            if (gateBefore && !gateAfter)
            {
                crossed.Add($"{company.Ticker ?? company.Name} ({string.Join(" + ", typesBefore.Order(StringComparer.Ordinal))} → "
                    + $"{(typesAfter.Count == 0 ? "none" : string.Join(" + ", typesAfter.Order(StringComparer.Ordinal)))}; Trajectory {b.Snapshot.TrajectoryScore} → {a.Snapshot.TrajectoryScore})");
            }
        }

        report.AppendLine($"### 5b. Report-layer proxy — the corroboration floor's positive-type gate (`{armName}` re-score)");
        report.AppendLine();
        report.AppendLine(
            $"Small/Mid companies meeting Trajectory ≥ 50 with ≥ 2 distinct positive contributing types BEFORE: {eligibleBefore}; "
                + $"of those, no longer meeting it AFTER: **{crossed.Count}**{(crossed.Count == 0 ? "." : ": " + string.Join("; ", crossed) + ".")} "
                + "Whether a label actually changes also depends on the arm's Opportunity lines and the policy's earlier rules, which this proxy does not apply.");
        report.AppendLine();
    }

    /// <summary>The single current <c>Lead</c> call in the store's operating-call journal (latest <c>asOfUtc</c> if several).</summary>
    private static string ReadLeadStrategy(string root)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "strategy-operating-calls.json")));
        return doc.RootElement.GetProperty("calls").EnumerateArray()
            .Where(c => string.Equals(c.GetProperty("call").GetString(), "Lead", StringComparison.Ordinal))
            .OrderByDescending(c => c.GetProperty("asOfUtc").GetDateTimeOffset())
            .Select(c => c.GetProperty("strategy").GetString())
            .FirstOrDefault()
            ?? throw new InvalidOperationException("strategy-operating-calls.json declares no Lead call.");
    }

    /// <summary>
    /// LABEL-LINE crossings BY SCORE for an arm that declares label lines: the band an Opportunity falls in against
    /// the arm's own Investigate / Watch lines (read from the bound definition), before vs after. This is NOT the
    /// report label: <c>weekly-report-action</c> applies earlier rules first — a pending acquisition forces Ignore,
    /// an EvidenceConfidence below its floor yields Needs more evidence, and a Trajectory move of its thesis delta
    /// versus the PRIOR persisted snapshot yields Thesis improving / deteriorating — and a sub-Watch Small/Mid
    /// company can be floored to Watch by corroboration. None of those is applied here; each crossing row carries
    /// the EvidenceConfidence and pending-acquisition state so a reader can see which gates could intervene.
    /// </summary>
    private static void AppendLabelLineCrossings(
        StringBuilder report, IReadOnlyList<Company> companies, PendingAcquisitions acquisitions,
        IReadOnlyList<CompanyScoreSnapshot> before, IReadOnlyList<CompanyScoreSnapshot> after,
        LabelThresholds lines, string armName, DateTimeOffset asOf)
    {
        string Band(int opportunity) =>
            opportunity >= lines.Investigate ? "Investigate line" : opportunity >= lines.Watch ? "Watch line" : "below Watch";

        var byId = companies.ToDictionary(c => c.Id);
        var afterById = after.ToDictionary(s => s.CompanyId);
        var rows = before
            .Select(b => (Company: byId[b.CompanyId], Before: b, After: afterById[b.CompanyId]))
            .ToList();

        report.AppendLine($"### 5c. Label-line crossings by score — `{armName}` (Investigate {lines.Investigate} / Watch {lines.Watch})");
        report.AppendLine();
        report.AppendLine("| band | BEFORE | AFTER |");
        report.AppendLine("| --- | ---: | ---: |");
        foreach (var band in new[] { "Investigate line", "Watch line", "below Watch" })
        {
            report.AppendLine($"| {band} | {rows.Count(r => Band(r.Before.OpportunityScore) == band)} | {rows.Count(r => Band(r.After.OpportunityScore) == band)} |");
        }

        report.AppendLine();
        var crossings = rows.Where(r => Band(r.Before.OpportunityScore) != Band(r.After.OpportunityScore))
            .OrderBy(r => r.Company.Ticker, StringComparer.Ordinal)
            .ToList();
        if (crossings.Count == 0)
        {
            report.AppendLine("**No company crosses either label line.**");
        }
        else
        {
            report.AppendLine("| ticker | Opportunity before → after | band before → after | EvidenceConfidence (after) | pending acquisition at as-of |");
            report.AppendLine("| --- | ---: | --- | ---: | --- |");
            foreach (var r in crossings)
            {
                report.AppendLine(
                    $"| {r.Company.Ticker ?? r.Company.Name} | {r.Before.OpportunityScore} → {r.After.OpportunityScore} "
                        + $"| {Band(r.Before.OpportunityScore)} → {Band(r.After.OpportunityScore)} | {r.After.EvidenceConfidenceScore} "
                        + $"| {(acquisitions.At(r.Company.Id, asOf) is not null ? "yes (rule 0 forces Ignore)" : "no")} |");
            }
        }

        report.AppendLine();
        report.AppendLine(
            "These are crossings of the arm's label LINES by Opportunity only. The report policy's earlier rules "
                + "(pending acquisition, EvidenceConfidence floor, thesis delta vs the prior snapshot) and the corroboration "
                + "floor are not applied, so a crossing is not a label change on its own.");
        report.AppendLine();
    }

    /// <summary>Every file under the root, keyed by relative path → (length, last-write UTC ticks).</summary>
    private static Dictionary<string, (long Length, long LastWriteTicks)> SnapshotFiles(string root)
    {
        var result = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            result[Path.GetRelativePath(root, file.FullName)] = (file.Length, file.LastWriteTimeUtc.Ticks);
        }

        return result;
    }

    internal sealed record Rewrite(Signal Before, Signal After, EvidenceSourceType SourceType, IReadOnlyList<SecItemHeadingPhrase> Items, string EvidenceTitle);

    internal sealed record Addition(Signal Beside, Signal After, EvidenceSourceType SourceType);

    /// <summary>The in-memory v9 projection of the accrued store: per signal id, its v9 replacement; per company, additions.</summary>
    internal sealed class V9Projection
    {
        /// <summary>
        /// Keyed on (company, evidence) rather than signal id: the scoring read and the previous-window read each
        /// collapse cross-run copies and may keep DIFFERENT copies, so an id key could miss the copy one of them kept.
        /// </summary>
        public Dictionary<(Guid? CompanyId, Guid EvidenceId), (SignalDirection Direction, string Reason)> HeadingRewriteByKey { get; } = [];

        public int CopiesOfAnAlreadyProjectedRead { get; private set; }

        public Dictionary<Guid, List<Signal>> AdditionsByCompany { get; } = [];

        public List<Rewrite> Rewrites { get; } = [];

        public List<Addition> Additions { get; } = [];

        public int PartnershipsInspected { get; private set; }

        public int PartnershipsKept { get; private set; }

        public int EvidenceUnresolvable { get; private set; }

        /// <summary>
        /// Heading-phrase partnership reads whose evidence id resolves to nothing (the spec-145 identity residue):
        /// their source type cannot be known, and the engine drops them before scoring anyway.
        /// </summary>
        public List<Signal> UnresolvableHeadingReads { get; } = [];

        public int OverNewsArticle { get; private set; }

        public int HeadingReadWithoutV9CorporateAction { get; private set; }

        public int StrengthOrNoveltyMismatches { get; private set; }

        private static readonly HashSet<string> V8HeadingReasons =
            [.. SecItemHeadingPhrases.All.Select(h => KeywordSignalReasons.MatchedPhrase(h.Phrase))];

        public static async Task<V9Projection> BuildAsync(
            IReadOnlyList<Company> companies, ISignalRepository signals, IEvidenceRepository evidence,
            KeywordSignalExtractor extractor, DeterministicSignalReviewer reviewer, DateTimeOffset asOf, CancellationToken ct)
        {
            var p = new V9Projection();
            foreach (var company in companies)
            {
                foreach (var signal in (await signals.GetByCompanyAsync(company.Id, ct))
                    .Where(s => s.Type == SignalType.StrategicPartnership)
                    .OrderBy(s => s.Id))
                {
                    p.PartnershipsInspected++;
                    var item = await evidence.GetByIdAsync(signal.EvidenceId, ct);
                    if (item is null)
                    {
                        p.EvidenceUnresolvable++;
                        if (V8HeadingReasons.Contains(signal.Reason ?? string.Empty))
                        {
                            p.UnresolvableHeadingReads.Add(signal);
                        }

                        continue;
                    }

                    if (item.SourceType == EvidenceSourceType.NewsArticle)
                    {
                        p.OverNewsArticle++;
                        continue;
                    }

                    var v9 = (await extractor.ExtractAsync(item, ct)).Signals
                        .FirstOrDefault(s => s.SignalType == nameof(SignalType.CorporateAction));

                    if (V8HeadingReasons.Contains(signal.Reason ?? string.Empty))
                    {
                        if (v9 is null)
                        {
                            p.HeadingReadWithoutV9CorporateAction++;
                            continue;
                        }

                        if (v9.Strength != signal.Strength || v9.Novelty != signal.Novelty)
                        {
                            p.StrengthOrNoveltyMismatches++;
                        }

                        var key = (signal.CompanyId, signal.EvidenceId);
                        if (p.HeadingRewriteByKey.ContainsKey(key))
                        {
                            p.CopiesOfAnAlreadyProjectedRead++;
                            continue;
                        }

                        p.HeadingRewriteByKey[key] = (Enum.Parse<SignalDirection>(v9.Direction), v9.Reason);
                        p.Rewrites.Add(new Rewrite(signal, p.Project(signal), item.SourceType, KeywordSignalReasons.NamedItemHeadings(v9.Reason), item.Title));
                        continue;
                    }

                    p.PartnershipsKept++;
                    if (v9 is null)
                    {
                        continue;
                    }

                    // A partnership from ANOTHER phrase on evidence that also carries a heading: v9 mints the
                    // partnership AND a CorporateAction. Reviewed by the production reviewer for its status and
                    // confidence; a deterministic id so the projection is reproducible (AD-3).
                    var candidate = signal with
                    {
                        Id = DeterministicId(signal.Id),
                        Type = SignalType.CorporateAction,
                        Direction = Enum.Parse<SignalDirection>(v9.Direction),
                        Strength = v9.Strength,
                        Novelty = v9.Novelty,
                        Confidence = v9.Confidence,
                        SupportingExcerpt = v9.SupportingExcerpt,
                        Reason = v9.Reason,
                        ReviewStatus = SignalReviewStatus.Pending,
                    };
                    var reviewed = (await reviewer.ReviewAsync(candidate, item, ct)).ReviewedSignal;
                    if (!p.AdditionsByCompany.TryGetValue(company.Id, out var list))
                    {
                        p.AdditionsByCompany[company.Id] = list = [];
                    }

                    if (list.All(s => s.EvidenceId != reviewed.EvidenceId))
                    {
                        list.Add(reviewed);
                        p.Additions.Add(new Addition(signal, reviewed, item.SourceType));
                    }
                }
            }

            return p;
        }

        private static Guid DeterministicId(Guid source)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("spec-226-v9-corporate-action:" + source.ToString("N")));
            return new Guid(hash.AsSpan(0, 16));
        }

        public Signal Project(Signal signal) =>
            signal.Type == SignalType.StrategicPartnership
            && V8HeadingReasons.Contains(signal.Reason ?? string.Empty)
            && HeadingRewriteByKey.TryGetValue((signal.CompanyId, signal.EvidenceId), out var v9)
                ? signal with { Type = SignalType.CorporateAction, Direction = v9.Direction, Reason = v9.Reason }
                : signal;
    }

    /// <summary>The AFTER arm's signal repository: every read projected through <see cref="V9Projection"/>; writes throw.</summary>
    private sealed class ProjectedSignalRepository(ISignalRepository inner, V9Projection projection) : ISignalRepository
    {
        public Task AddAsync(Signal signal, CancellationToken ct) =>
            throw new InvalidOperationException("The spec-226 §4 counterfactual is read-only and must never write a signal.");

        public async Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct) =>
            await inner.GetByIdAsync(id, ct) is { } s ? projection.Project(s) : null;

        public async Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct)
        {
            var projected = (await inner.GetByCompanyAsync(companyId, ct)).Select(projection.Project).ToList();
            if (projection.AdditionsByCompany.TryGetValue(companyId, out var added))
            {
                projected.AddRange(added);
            }

            return projected;
        }

        public async Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct) =>
            [.. (await inner.GetObservedBetweenAsync(startUtc, endUtc, ct)).Select(projection.Project)];
    }

    /// <summary>The AFTER arm's previous-window read, projected the same way (additions filtered by the read's own predicates).</summary>
    private sealed class ProjectedSignalWindowReads(ISignalFileStore inner, V9Projection projection) : ISignalFileStore
    {
        public Task<DurableWriteResult> WriteAsync(Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            throw new InvalidOperationException("The spec-226 §4 counterfactual is read-only and must never write a signal.");

        public async Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId, DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc, DateTimeOffset knownAsOfUtc, CancellationToken ct)
        {
            var projected = (await inner.ReadApprovedInWindowAsync(companyId, startExclusiveUtc, endInclusiveUtc, knownAsOfUtc, ct))
                .Select(projection.Project)
                .ToList();
            if (projection.AdditionsByCompany.TryGetValue(companyId, out var added))
            {
                projected.AddRange(added.Where(s => s.ObservedAtUtc > startExclusiveUtc && s.ObservedAtUtc <= endInclusiveUtc
                    && s.CreatedAtUtc <= knownAsOfUtc && s.ReviewStatus == SignalReviewStatus.Approved));
            }

            return projected;
        }
    }

    /// <summary>Read-only pass-through over the acquisitions store: the harness may read recognitions, never write one.</summary>
    private sealed class ReadOnlyAcquisitionStore(IAcquisitionStore inner) : IAcquisitionStore
    {
        public Task<DurableWriteResult> WriteIfNewAsync(PendingAcquisitionRecord record, CancellationToken ct) =>
            throw new InvalidOperationException("The spec-226 §4 counterfactual is read-only and must never write an acquisition.");

        public Task<AcquisitionStoreReadResult> GetAllAsync(CancellationToken ct) => inner.GetAllAsync(ct);

        public Task<bool> ExistsAsync(Guid companyId, string accession, CancellationToken ct) => inner.ExistsAsync(companyId, accession, ct);
    }
}

/// <summary>Runs the spec-226 §4 counterfactual only when a live data root is supplied; SKIPS WITH A NAMED REASON otherwise.</summary>
public sealed class ItemHeadingDirectionCounterfactualFactAttribute : FactAttribute
{
    public ItemHeadingDirectionCounterfactualFactAttribute()
    {
        if (ItemHeadingDirectionCounterfactualTests.DataRoot() is null)
        {
            Skip = ItemHeadingDirectionCounterfactualTests.SkipReason;
        }
    }
}
