using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.NewsRisk;
using Radar.Infrastructure.NewsTyping;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// Spec 214 §3 — the READ-ONLY LIVE distribution of <see cref="StatementComparisonClassifier"/>
/// (<c>comparison-basis-v1</c>) over the accrued store (CLAUDE.md's "no measure ships without its live
/// distribution"). It reads the typing store, the judgment store and the signal store through the
/// PRODUCTION file stores — never a second parser — and reports:
/// <list type="bullet">
/// <item>every typed fact by <see cref="NewsFactComparisonBasis"/> (counts + shares);</item>
/// <item>among Judged DIRECTIONAL judgments, how many cite ONLY LevelOnly/NotQuantified facts (the would-be
/// <see cref="NewsTrajectoryBasis.LevelOnly"/> set), out of the directional total;</item>
/// <item>of those, how many currently hold a materialized <c>news-judgment-signal-v2</c> (or v1) signal —
/// i.e. would have minted nothing under v3;</item>
/// <item>the AGX 2026-09-07 judgment's cited facts, each with its classification.</item>
/// </list>
/// <para>
/// <b>Nothing is written.</b> The stores are opened read-only by construction (hydration only; no write is
/// invoked). The sanity bounds of spec 214 §3 are PRINTED and labelled, never asserted: a breach is an
/// instruction to inspect a sample and report what the tables did — a genuinely level-heavy corpus is a
/// finding to record, and the tables must never be tuned to preserve signal volume.
/// </para>
/// <para>
/// ENV-GATED and skipped with a NAMED reason otherwise (the spec-198 precedent): set
/// <c>RADAR_COMPARISON_BASIS_LIVE_DATA_ROOT</c> to a Radar data root holding <c>news-typing/</c>,
/// <c>news-risk/</c> and <c>signals/</c>. The output is markdown on the test log, ready to paste into a PR
/// body. Deterministic: fixed ordering, invariant formatting, no clock.
/// </para>
/// </summary>
public sealed class ComparisonBasisLiveMeasurementTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_COMPARISON_BASIS_LIVE_DATA_ROOT";

    internal const string SkipReason =
        "Spec 214 §3 live comparison-basis distribution (reads a live Radar data root): set "
            + DataRootVariable + " to a Radar data root (news-typing/, news-risk/, signals/) to run it.";

    /// <summary>The AGX 2026-09-07 judgment spec 214 opens with (Improving on "backlog hits $2.5B").</summary>
    private static readonly Guid ArganJudgmentId = new("928eb9f8-380b-7838-e80d-ad9283cbf066");

    /// <summary>The crude upper bound spec 214 §3 says the scoped classifier must be measured AGAINST.</summary>
    private const double CrudeLevelOnlyJudgmentShareUpperBound = 0.148;

    /// <summary>How many statements per class the inspection sample prints (first N in store order).</summary>
    private const int SampleSize = 12;

    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    [ComparisonBasisLiveFact]
    public async Task LiveDistribution_OfComparisonBasis_AcrossTypedFactsAndDirectionalJudgments()
    {
        var root = DataRoot()!;
        var ct = CancellationToken.None;

        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var typingStore = new FileNewsTypingStore(
                new FileNewsTypingStoreOptions { RootDirectory = Path.Combine(root, "news-typing") },
                NullLogger<FileNewsTypingStore>.Instance);
            var judgmentStore = new FileNewsJudgmentStore(
                new FileNewsJudgmentStoreOptions { RootDirectory = Path.Combine(root, "news-risk") },
                NullLogger<FileNewsJudgmentStore>.Instance);
            var signalStore = new FileSignalStore(
                new FileSignalStoreOptions { RootDirectory = Path.Combine(root, "signals") },
                NullLogger<FileSignalStore>.Instance);

            var typings = await typingStore.GetAllAsync(ct);
            var judgments = await judgmentStore.GetAllAsync(ct);

            // ---- 1. every typed fact, classified ---------------------------------------------------
            var factsById = new Dictionary<Guid, NewsTypingValidatedFact>();
            var byBasis = new SortedDictionary<NewsFactComparisonBasis, int>();
            var samples = new Dictionary<NewsFactComparisonBasis, List<NewsTypingValidatedFact>>();
            var notQuantifiedWithFigure = new List<NewsTypingValidatedFact>();
            var totalFacts = 0;
            foreach (var typing in typings.OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.TypingId))
            {
                foreach (var fact in typing.Facts)
                {
                    totalFacts++;
                    factsById.TryAdd(fact.FactId, fact);
                    var basis = StatementComparisonClassifier.Classify(fact.Statement, fact.EventTypes);
                    byBasis[basis] = byBasis.GetValueOrDefault(basis) + 1;

                    // The inspection sample spec 214 §3 asks for when a bound is breached — and useful
                    // when it is not: the FIRST N of each class in store order, so a reviewer can see what
                    // the tables did rather than trust the shares. A NotQuantified statement that carries
                    // a figure is the one most worth a look (a metric noun the table lacks, or a figure
                    // the classifier rightly refuses to read as a level).
                    if (!samples.TryGetValue(basis, out var list))
                    {
                        samples[basis] = list = [];
                    }

                    if (list.Count < SampleSize)
                    {
                        list.Add(fact);
                    }

                    if (basis == NewsFactComparisonBasis.NotQuantified
                        && fact.Statement.Any(char.IsAsciiDigit)
                        && notQuantifiedWithFigure.Count < SampleSize)
                    {
                        notQuantifiedWithFigure.Add(fact);
                    }
                }
            }

            // ---- 2. directional judgments, by would-be trajectory basis ---------------------------
            var directional = judgments
                .Where(j => j.Status == NewsJudgmentStatus.Judged
                    && j.BusinessTrajectory is NewsJudgmentTrajectory.Improving
                        or NewsJudgmentTrajectory.Deteriorating)
                .OrderBy(j => j.CreatedAtUtc)
                .ThenBy(j => j.JudgmentId)
                .ToList();
            var judged = judgments.Count(j => j.Status == NewsJudgmentStatus.Judged);

            var levelOnly = new List<NewsJudgmentRecord>();
            var supported = 0;
            var noCitations = 0;
            var unresolvedCitationJudgments = 0;
            foreach (var judgment in directional)
            {
                if (judgment.TrajectoryFactIds is not { Count: > 0 } cited)
                {
                    noCitations++; // a v1 record — the field was never recorded
                    continue;
                }

                var bases = new List<NewsFactComparisonBasis>(cited.Count);
                var unresolved = false;
                foreach (var factId in cited)
                {
                    if (!factsById.TryGetValue(factId, out var fact))
                    {
                        unresolved = true;
                        break;
                    }

                    bases.Add(StatementComparisonClassifier.Classify(fact.Statement, fact.EventTypes));
                }

                if (unresolved)
                {
                    unresolvedCitationJudgments++;
                    continue;
                }

                if (bases.Any(b => b is NewsFactComparisonBasis.StatedComparison or NewsFactComparisonBasis.Event))
                {
                    supported++;
                }
                else
                {
                    levelOnly.Add(judgment);
                }
            }

            // ---- 3. of the would-be LevelOnly set, how many hold a materialized v2 (or v1) signal --
            var levelOnlyWithV2 = 0;
            var levelOnlyWithV1 = 0;
            foreach (var judgment in levelOnly)
            {
                if (await signalStore.GetByIdAsync(
                    NewsJudgmentSignalMaterializer.RetiredV2SignalIdFor(judgment.JudgmentId), ct) is not null)
                {
                    levelOnlyWithV2++;
                }

                if (await signalStore.GetByIdAsync(
                    NewsJudgmentSignalMaterializer.RetiredV1SignalIdFor(judgment.JudgmentId), ct) is not null)
                {
                    levelOnlyWithV1++;
                }
            }

            // ---- 4. the AGX 2026-09-07 judgment, fact by fact --------------------------------------
            var argan = judgments.FirstOrDefault(j => j.JudgmentId == ArganJudgmentId);

            // ---- render --------------------------------------------------------------------------
            var report = new StringBuilder();
            report.AppendLine("## Spec 214 §3 — live comparison-basis distribution (comparison-basis-v1)");
            report.AppendLine();
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Data root: `{root}` · typing records {typings.Count} · typed facts {totalFacts} "
                    + $"(distinct fact ids {factsById.Count}) · judgment records {judgments.Count} · Judged "
                    + $"{judged} · directional {directional.Count}"));
            report.AppendLine();
            report.AppendLine("| typed facts by ComparisonBasis | count | share |");
            report.AppendLine("| --- | ---: | ---: |");
            foreach (var basis in Enum.GetValues<NewsFactComparisonBasis>())
            {
                var count = byBasis.GetValueOrDefault(basis);
                report.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {basis} | {count} | {Share(count, totalFacts)} |"));
            }

            var quantified = byBasis.GetValueOrDefault(NewsFactComparisonBasis.StatedComparison)
                + byBasis.GetValueOrDefault(NewsFactComparisonBasis.LevelOnly)
                + byBasis.GetValueOrDefault(NewsFactComparisonBasis.Event);
            report.AppendLine();
            report.AppendLine("| directional judgments (Judged, Improving/Deteriorating) | count | share of directional |");
            report.AppendLine("| --- | ---: | ---: |");
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| would-be Supported (≥ 1 cited StatedComparison or Event) | {supported} | {Share(supported, directional.Count)} |"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| would-be LevelOnly (ALL cited facts LevelOnly/NotQuantified) | {levelOnly.Count} | {Share(levelOnly.Count, directional.Count)} |"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| no TrajectoryFactIds recorded (pre-187 v1 records) | {noCitations} | {Share(noCitations, directional.Count)} |"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| a cited fact not found in the typing store | {unresolvedCitationJudgments} | {Share(unresolvedCitationJudgments, directional.Count)} |"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| of the would-be LevelOnly set: holding a materialized news-judgment-signal-v2 signal | {levelOnlyWithV2} | {Share(levelOnlyWithV2, levelOnly.Count)} |"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| of the would-be LevelOnly set: holding a materialized news-judgment-signal-v1 signal | {levelOnlyWithV1} | {Share(levelOnlyWithV1, levelOnly.Count)} |"));

            report.AppendLine();
            report.AppendLine("### Would-be LevelOnly judgments (persisted verbatim; would mint nothing under v3)");
            report.AppendLine();
            report.AppendLine("| created (UTC) | ticker | trajectory | judgment id | cited facts |");
            report.AppendLine("| --- | --- | --- | --- | ---: |");
            foreach (var judgment in levelOnly)
            {
                report.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {judgment.CreatedAtUtc:yyyy-MM-dd} | {judgment.Ticker ?? judgment.CompanyName} | "
                        + $"{judgment.BusinessTrajectory} | `{judgment.JudgmentId:D}` | "
                        + $"{judgment.TrajectoryFactIds!.Count} |"));
            }

            report.AppendLine();
            report.AppendLine("### Inspection sample — the first statements of each class, in store order");
            report.AppendLine();
            foreach (var basis in Enum.GetValues<NewsFactComparisonBasis>())
            {
                report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"**{basis}**"));
                report.AppendLine();
                foreach (var fact in samples.GetValueOrDefault(basis) ?? [])
                {
                    report.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"- [{string.Join(", ", fact.EventTypes)}] {fact.Statement}"));
                }

                report.AppendLine();
            }

            report.AppendLine("**NotQuantified statements that carry a digit** (a metric noun the table lacks, or a figure rightly not read as a level):");
            report.AppendLine();
            foreach (var fact in notQuantifiedWithFigure)
            {
                report.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"- [{string.Join(", ", fact.EventTypes)}] {fact.Statement}"));
            }

            report.AppendLine();
            report.AppendLine("### Sanity bounds (spec 214 §3 — investigate if breached, never tune to fit)");
            report.AppendLine();
            var levelShareOfQuantified = quantified == 0
                ? 0.0
                : (double)byBasis.GetValueOrDefault(NewsFactComparisonBasis.LevelOnly) / quantified;
            var comparisonShareOfQuantified = quantified == 0
                ? 0.0
                : (double)byBasis.GetValueOrDefault(NewsFactComparisonBasis.StatedComparison) / quantified;
            var levelOnlyJudgmentShare = directional.Count == 0
                ? 0.0
                : (double)levelOnly.Count / directional.Count;
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- LevelOnly share of quantified facts (StatedComparison + LevelOnly + Event = {quantified}): "
                    + $"{levelShareOfQuantified:P1} — bound > 30% {(levelShareOfQuantified > 0.30 ? "BREACHED" : "holds")}"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- StatedComparison share of quantified facts: {comparisonShareOfQuantified:P1} — bound < 5% "
                    + $"{(comparisonShareOfQuantified < 0.05 ? "BREACHED" : "holds")}"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- would-be LevelOnly judgment share of directional: {levelOnlyJudgmentShare:P1} — bound above the "
                    + $"crude {CrudeLevelOnlyJudgmentShareUpperBound:P1} "
                    + $"{(levelOnlyJudgmentShare > CrudeLevelOnlyJudgmentShareUpperBound ? "BREACHED" : "holds")}"));

            report.AppendLine();
            report.AppendLine("### AGX 2026-09-07 judgment `928eb9f8-380b-7838-e80d-ad9283cbf066`, cited facts classified");
            report.AppendLine();
            if (argan is null)
            {
                report.AppendLine("Not present in this store.");
            }
            else
            {
                report.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Status {argan.Status} · trajectory {argan.BusinessTrajectory} · schema {argan.SchemaVersion} · "
                        + $"persisted trajectoryBasis {(argan.TrajectoryBasis is { } b ? b.ToString() : "null (pre-214)")}"));
                report.AppendLine();
                report.AppendLine("| cited fact id | event types | statement | ComparisonBasis |");
                report.AppendLine("| --- | --- | --- | --- |");
                var arganBases = new List<NewsFactComparisonBasis>();
                foreach (var factId in argan.TrajectoryFactIds ?? [])
                {
                    if (!factsById.TryGetValue(factId, out var fact))
                    {
                        report.AppendLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"| `{factId:D}` | (not in typing store) | | |"));
                        continue;
                    }

                    var basis = StatementComparisonClassifier.Classify(fact.Statement, fact.EventTypes);
                    arganBases.Add(basis);
                    report.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"| `{factId:D}` | {string.Join(", ", fact.EventTypes)} | "
                            + $"{fact.Statement.Replace("|", "\\|", StringComparison.Ordinal)} | {basis} |"));
                }

                var arganWouldBe = arganBases.Any(
                    b => b is NewsFactComparisonBasis.StatedComparison or NewsFactComparisonBasis.Event)
                    ? "Supported"
                    : "LevelOnly";
                var arganHasV2 = await signalStore.GetByIdAsync(
                    NewsJudgmentSignalMaterializer.RetiredV2SignalIdFor(argan.JudgmentId), ct) is not null;
                report.AppendLine();
                report.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Would-be trajectory basis: {arganWouldBe} · v2 signal on disk: {(arganHasV2 ? "yes" : "no")}"));
            }

            output.WriteLine(report.ToString());

            // The measurement is the deliverable; these guard only that it MEASURED something, so an empty
            // or misconfigured root can never be reported as a result.
            Assert.True(totalFacts > 0, "no typed facts were read — check the data root");
            Assert.True(judgments.Count > 0, "no judgment records were read — check the data root");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static string Share(int count, int total) =>
        total == 0 ? "n/a" : ((double)count / total).ToString("P1", CultureInfo.InvariantCulture);
}

/// <summary>Skips the spec-214 §3 live measurement with a named reason unless its data-root variable points at a directory.</summary>
public sealed class ComparisonBasisLiveFactAttribute : FactAttribute
{
    public ComparisonBasisLiveFactAttribute()
    {
        if (ComparisonBasisLiveMeasurementTests.DataRoot() is null)
        {
            Skip = ComparisonBasisLiveMeasurementTests.SkipReason;
        }
    }
}
