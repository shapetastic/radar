using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Efficacy.DenominatorAudit;
using Radar.Application.Efficacy.EvidenceConfidence;
using Radar.Application.EntityResolution;
using Radar.Application.Scoring;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Scoring;
using Radar.Infrastructure.Persistence.InMemory;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// Spec 225 §2a — the READ-ONLY live measurement of <c>EvidenceConfidenceScore</c> over the live universe:
/// every company is RE-SCORED through the REAL <see cref="ScoringEngine"/> (the spec-224 harness's own
/// composition — 60-day window, <c>default</c>, default weights, <c>radar-formula-v8</c>) into an in-memory
/// score repository, and the SAME <see cref="EvidenceConfidenceDistributionReporter"/> the Worker runs then
/// reads those snapshots + links back through a small in-memory adapter over that repository. Nothing is
/// persisted under the data root: the durable stores are hydrated READ-ONLY (spec 142), evidence writes throw,
/// scores never leave the process, and the artifacts go to the system temp directory only.
/// <para>
/// <b>What this is and is not.</b> A re-score at ONE as-of instant (the latest <c>windowEndUtc</c> under
/// <c>scores/</c>, resolved by the shared <see cref="InsiderCollapseCounterfactualTests.ResolveAsOfAsync"/>),
/// NOT a persisted run's snapshots; the numbers are what the production path produces from the store as it
/// is. The decomposition is the production body (<see cref="ScoreSignalMath.EvidenceConfidenceDecomposition"/>)
/// — a second copy of the formula in a harness would measure itself.
/// </para>
/// <para>
/// <b>ENV-GATED</b> on <c>RADAR_EVIDENCE_CONFIDENCE_DATA_ROOT</c>, skipped with a NAMED reason otherwise.
/// NO network. Deterministic (AD-3) given a data root.
/// </para>
/// </summary>
public sealed class EvidenceConfidenceDistributionTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_EVIDENCE_CONFIDENCE_DATA_ROOT";

    internal const string SkipReason =
        "Spec 225 §2a read-only EvidenceConfidence measurement: set " + DataRootVariable
            + " to a Radar data root (companies.json, signals/, evidence/raw/, scores/) to run it.";

    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    /// <summary>
    /// The artifact stem, suffixed with the PROCESS ID so two concurrent runs on one machine cannot overwrite each
    /// other's outputs. Only the file NAME varies; the report CONTENT stays deterministic. Because the path is not
    /// fixed, the resolved paths are written to the test output (before and after the report).
    /// </summary>
    private static readonly string OutputStem =
        Path.Combine(
            Path.GetTempPath(),
            "radar-spec-225-evidence-confidence-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    [EvidenceConfidenceDistributionFact]
    public async Task ReadOnlyMeasurement_OverTheLiveUniverse_ThroughTheProductionPath()
    {
        var root = DataRoot()!;
        var ct = CancellationToken.None;

        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            await using var provider = AttentionPolicyCounterfactualTests.BuildReadOnlyProvider(root);
            var seeded = await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);
            var companyRepository = provider.GetRequiredService<ICompanyRepository>();
            var companies = (await companyRepository.GetAllAsync(ct)).OrderBy(c => c.Id).ToList();

            var signals = provider.GetRequiredService<ISignalRepository>();
            var evidence = new ReadOnlyEvidenceRepository(provider.GetRequiredService<IEvidenceRepository>());
            var windowReads = new MemoizingSignalWindowReads(provider.GetRequiredService<ISignalFileStore>());

            var asOf = await InsiderCollapseCounterfactualTests.ResolveAsOfAsync(root, signals, companies, ct);

            // The REAL engine, the spec-224 harness's composition, into an in-memory repository this harness owns.
            var scores = new InMemoryScoreRepository();
            var snapshots = await InsiderCollapseCounterfactualTests.ScoreAllAsync(
                provider, companies, signals, evidence, windowReads, asOf.Instant, ct, scores);

            var reporter = new EvidenceConfidenceDistributionReporter(
                ScoringStrategySet.SingleDefault(new ScoringWeights()),
                new SingleStoreSelector(new InMemoryScoreSnapshotLinkStore(scores), $"read-only re-score at {asOf.Instant:O} ({asOf.Source})"),
                companyRepository,
                signals,
                evidence,
                NullLogger<EvidenceConfidenceDistributionReporter>.Instance);
            var report = await reporter.BuildAsync(ct);

            var renderer = new EvidenceConfidenceDistributionRenderer();
            var markdown = renderer.RenderMarkdown(report) + WorkedCases(report);
            File.WriteAllText(OutputStem + ".md", markdown, Encoding.UTF8);
            File.WriteAllText(OutputStem + ".json", renderer.RenderJson(report), Encoding.UTF8);
            File.WriteAllText(OutputStem + ".csv", renderer.RenderCsv(report), Encoding.UTF8);

            output.WriteLine(
                $"Seeder reported {seeded}; {companies.Count} companies; {snapshots.Count} re-scored at {asOf.Instant:O} ({asOf.Source}).");
            output.WriteLine($"Artifacts: {OutputStem}.md, {OutputStem}.json, {OutputStem}.csv");
            output.WriteLine(markdown);
            output.WriteLine($"(written to {OutputStem}.md / .json / .csv)");

            // The measurement is the deliverable; these guard only that it measured SOMETHING and counted everything.
            Assert.NotEmpty(companies);
            Assert.Equal(companies.Count, snapshots.Count);
            Assert.True(report.StrategyConfigured);
            Assert.True(report.Counts.CompaniesIncluded > 0, "no company was included in the measurement");
            Assert.True(report.Counts.Reconciles, "the counted axes do not reconcile to the seeded universe");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>
    /// The tickers the spec-225 follow-up named from the first live read (2026-09-13): the largest fallers and
    /// risers under the held-EvidenceConfidence counterfactual. Harness-only — the production artifact lists its
    /// movers generically (§4a); this section exists so the named cases are legible with their best-confidence
    /// signal beside them, whatever the store now says.
    /// </summary>
    private static readonly string[] NamedFallers = ["STRL", "CVLT", "POWL", "PLMR", "AGYS", "UFPT"];

    private static readonly string[] NamedRisers = ["GHM", "PLUS", "THRM", "BKE", "EPM"];

    /// <summary>Spec 218's worked example: POWL's 8-K dated 2026-08-03 (items 2.02, 8.01), read Positive at 0.95.</summary>
    private const string Spec218PowlFilingDate = "2026-08-03";

    internal static string WorkedCases(EvidenceConfidenceDistributionReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Worked cases (harness-only: the movers named by the spec-225 follow-up)");
        sb.AppendLine();
        sb.AppendLine("Each row is read from this run's report rows — nothing here is typed in. A ticker absent from the "
            + "universe or unranked is stated as such.");
        sb.AppendLine();
        AppendNamed(sb, report, "Named as FALLING when EvidenceConfidence is held at the median", NamedFallers);
        AppendNamed(sb, report, "Named as RISING when EvidenceConfidence is held at the median", NamedRisers);

        var powl = report.Rows.FirstOrDefault(r => r.Ticker == "POWL");
        sb.Append("**POWL and spec 218.** ");
        if (powl?.BestConfidenceEvidenceTitle is not { } title)
        {
            sb.AppendLine("POWL has no best-confidence signal recorded in this run, so the spec-218 link cannot be checked.");
        }
        else if (title.Contains(Spec218PowlFilingDate, StringComparison.Ordinal))
        {
            sb.AppendLine("POWL's best confidence (" + powl.BestConfidence?.ToString("0.###", CultureInfo.InvariantCulture)
                + ", " + powl.BestConfidenceSignalType + " " + powl.BestConfidenceSignalDirection + ", strength "
                + powl.BestConfidenceSignalStrength + ", producer " + powl.BestConfidenceProducer + ") is set by `" + title
                + "` — an earnings 8-K dated " + Spec218PowlFilingDate + ", the date of spec 218's worked example "
                + "(docs/218-directional-filing-read-live-distribution.md), which records that filing read Positive at "
                + "0.95 while fourteen Neutral news signals beside it included a headline reporting an earnings miss. "
                + "The match is by filing date and items 2.02/8.01 (spec 218 quoted the title without 9.01); it is not "
                + "verified by accession here. That read's own "
                + "confidence is the bestConfidence term of POWL's EvidenceConfidence (Δrank "
                + (powl.RankDelta?.ToString("+0;-0;0", CultureInfo.InvariantCulture) ?? "(not recorded)")
                + " when it is held at the median); whether that read was RIGHT is not measured here.");
        }
        else
        {
            sb.AppendLine("POWL's best confidence is set by `" + title + "`, which is NOT the " + Spec218PowlFilingDate
                + " 8-K of spec 218's worked example; the spec-218 link does not hold in this run.");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static void AppendNamed(
        StringBuilder sb, EvidenceConfidenceDistributionReport report, string title, IReadOnlyList<string> tickers)
    {
        sb.AppendLine("**" + title + "**");
        sb.AppendLine();
        sb.AppendLine(EvidenceConfidenceDistributionRenderer.MoverHeader);
        foreach (var ticker in tickers)
        {
            var row = report.Rows.FirstOrDefault(r => r.Ticker == ticker);
            sb.AppendLine(row is null
                ? "| " + ticker + " | (not in the universe) | | | | | | | | | |"
                : EvidenceConfidenceDistributionRenderer.MoverCells(row));
        }

        sb.AppendLine();
    }

    /// <summary>Hands the reporter exactly one store for whichever strategy it asks about.</summary>
    private sealed class SingleStoreSelector(IScoreSnapshotFileStore store, string description) : IStrategyScoreSnapshotStoreSelector
    {
        public IScoreSnapshotFileStore ForStrategy(ScoringStrategyDefinition strategy) => store;

        public string SeriesDescription => description;
    }

    /// <summary>
    /// A read-only adapter presenting this harness's in-memory score repository through the file store's
    /// dual read seams, so the production reporter reads the re-scored snapshots WITH their links exactly as
    /// it would read the persisted series. Every member is implemented; the write throws.
    /// </summary>
    private sealed class InMemoryScoreSnapshotLinkStore(IScoreRepository scores) : IScoreSnapshotFileStore, IScoreSnapshotLinkReader
    {
        public Task<DurableWriteResult> WriteAsync(
            CompanyScoreSnapshot snapshot, IReadOnlyList<ScoreEvidenceLink> links, CancellationToken ct) =>
            throw new InvalidOperationException("The spec-225 harness is read-only and must never write a snapshot.");

        public async Task<CompanyScoreSnapshot?> ReadLatestBeforeAsync(
            Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct) =>
            (await scores.GetSnapshotsForCompanyAsync(companyId, ct))
                .Where(s => s.CreatedAtUtc < beforeUtc)
                .OrderByDescending(s => s.CreatedAtUtc)
                .ThenByDescending(s => s.Id)
                .FirstOrDefault();

        public Task<IReadOnlyList<CompanyScoreSnapshot>> ReadAllForCompanyAsync(Guid companyId, CancellationToken ct) =>
            scores.GetSnapshotsForCompanyAsync(companyId, ct);

        public async Task<IReadOnlyList<ScoreSnapshotWithLinks>> ReadAllWithLinksForCompanyAsync(
            Guid companyId, CancellationToken ct)
        {
            var result = new List<ScoreSnapshotWithLinks>();
            foreach (var snapshot in await scores.GetSnapshotsForCompanyAsync(companyId, ct))
            {
                result.Add(new ScoreSnapshotWithLinks(snapshot, await scores.GetLinksForSnapshotAsync(snapshot.Id, ct)));
            }

            return result;
        }
    }
}

/// <summary>
/// Runs the spec-225 measurement only when a live data root is supplied, and SKIPS WITH A NAMED REASON
/// otherwise — never silently.
/// </summary>
public sealed class EvidenceConfidenceDistributionFactAttribute : FactAttribute
{
    public EvidenceConfidenceDistributionFactAttribute()
    {
        if (EvidenceConfidenceDistributionTests.DataRoot() is null)
        {
            Skip = EvidenceConfidenceDistributionTests.SkipReason;
        }
    }
}
