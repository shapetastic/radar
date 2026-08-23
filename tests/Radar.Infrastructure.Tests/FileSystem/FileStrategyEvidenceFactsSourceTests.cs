using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Lifecycle;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// Spec 184 §1 — the facts reader over the ALREADY-persisted efficacy artifacts. Missing/wrong-schema
/// artifacts degrade to "unavailable" (never throw, never hide an arm); readable ones surface the ranked
/// numbers, drop reasons and the paired gate context by HEADER NAME, not column position.
/// </summary>
public sealed class FileStrategyEvidenceFactsSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"radar-facts-{Guid.NewGuid():N}");

    public FileStrategyEvidenceFactsSourceTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private FileStrategyEvidenceFactsSource Source() => new(
        new FileStrategyEvidenceFactsSourceOptions(_dir),
        NullLogger<FileStrategyEvidenceFactsSource>.Instance);

    // The REAL spec-183 v2 header (byte-for-byte the renderer's CsvHeader), so a renderer column change
    // that would break this reader breaks this test.
    private const string LeaderboardHeader =
        "schemaVersion,status,rank,strategy,strategiesCompared,strategiesConsidered,"
            + "inSampleRhoExcessVsUniverseV1,inSampleLower95,inSampleUpper95,"
            + "inSampleObservations,inSampleCompanies,inSampleDates,"
            + "outOfSampleRhoExcessVsUniverseV1,outOfSampleLower95,outOfSampleUpper95,outOfSampleObservations,"
            + "outOfSampleCompanies,outOfSampleDates,observationsWithoutForwardPrice,"
            + "observationsWithPartialWindow,observationsBenchmarkUnavailable,"
            + "observationsNotInBenchmarkUniverse,benchmarkUniverseVersion,benchmarkUniverseContentHash,"
            + "dropReason,metricReason";

    private const string PairedHeader =
        "status,primaryStrategy,primaryPredeclared,firstEligibleAsOf,armsConsidered,baselinesCompared,"
            + "baseline,jointObservations,jointCompanies,jointDates,candidateDates,droppedDates,"
            + "developmentDates,inconsistentOutcomeObservations,purgedBlocks,medianPairedDelta,"
            + "intervalLower95,intervalUpper95,intervalCoverage,intervalReason,signTestP,signTestEffectiveN,"
            + "signTestZeroDeltasDropped,baselineClears,satisfiesPriceGate,gateReasons,"
            + "qualifiesUnderAd15,ad16ScreenOutcome,"
            + "eligibleJointObservations,eligibleJointCompanies,eligibleJointDates,"
            + "observationsWithoutAsOfInstant,mismatchedAsOfInstantKeys";

    private async Task WriteAsync(string fileName, params string[] lines) =>
        await File.WriteAllLinesAsync(Path.Combine(_dir, fileName), lines);

    [Fact]
    public async Task MissingArtifacts_ReportUnavailable_NeverThrow()
    {
        var facts = await Source().ReadAsync(default);

        Assert.False(facts.LeaderboardAvailable);
        Assert.Empty(facts.Leaderboard);
        Assert.False(facts.PairedAvailable);
        Assert.Null(facts.Paired);
    }

    [Fact]
    public async Task Leaderboard_RankedAndDroppedRows_ParseByHeaderName()
    {
        await WriteAsync(
            FileStrategyEvidenceFactsSource.LeaderboardFileName,
            LeaderboardHeader,
            "strategy-leaderboard-v2,ranked,1,default,1,10,0.1000,-0.0500,0.2500,120,40,3,"
                + "-0.0500,-0.3000,0.2000,72,36,2,4,9,0,1,benchmark-universe-v1,abc123,,defined",
            "strategy-leaderboard-v2,dropped,,\"filings, led\",1,10,,,,12,,,,,,3,,,,,,,"
                + "benchmark-universe-v1,abc123,insufficient-out-of-sample-observations,too-few-observations");

        var facts = await Source().ReadAsync(default);

        Assert.True(facts.LeaderboardAvailable);
        Assert.Equal(2, facts.Leaderboard.Count);

        var ranked = facts.Leaderboard[0];
        Assert.Equal("default", ranked.StrategyName);
        Assert.True(ranked.Ranked);
        Assert.Equal(1, ranked.Numbers!.Rank);
        Assert.Equal(-0.05, ranked.Numbers.OutOfSampleRho, precision: 10);
        Assert.Equal(-0.30, ranked.Numbers.Lower95, precision: 10);
        Assert.Equal(0.20, ranked.Numbers.Upper95, precision: 10);
        Assert.Equal(72, ranked.Numbers.Observations);
        Assert.True(ranked.Numbers.CiSpansZero);

        var dropped = facts.Leaderboard[1];
        Assert.Equal("filings, led", dropped.StrategyName); // quoted CSV field round-trips
        Assert.False(dropped.Ranked);
        Assert.Equal("insufficient-out-of-sample-observations", dropped.DropReason);
    }

    [Fact]
    public async Task Leaderboard_UnknownSchema_DegradesToUnavailable()
    {
        await WriteAsync(
            FileStrategyEvidenceFactsSource.LeaderboardFileName,
            LeaderboardHeader,
            "strategy-leaderboard-v3,ranked,1,default,1,1,,,,,,,,,,,,,,,,,v1,h,,defined");

        var facts = await Source().ReadAsync(default);

        Assert.False(facts.LeaderboardAvailable);
        Assert.Empty(facts.Leaderboard);
    }

    [Fact]
    public async Task Paired_GateContext_ParsesFromTheFirstDataRow()
    {
        await WriteAsync(
            FileStrategyEvidenceFactsSource.PairedComparisonFileName,
            PairedHeader,
            "baseline,disclosure-led-v11,true,2026-09-29,10,3,baseline-earnings-only,100,40,3,5,2,1,0,4,"
                + "0.0500,,,,insufficient-purged-blocks,,0,0,false,false,"
                + "\"no-precommitted-evaluation-boundary; baseline 'baseline-earnings-only': insufficient-purged-blocks (admitted 4, need at least 6 at 95%)\","
                + "false,pending,0,0,0,0,0");

        var facts = await Source().ReadAsync(default);

        Assert.True(facts.PairedAvailable);
        var paired = facts.Paired!;
        Assert.Equal("disclosure-led-v11", paired.PrimaryStrategyName);
        Assert.True(paired.PrimaryPredeclared);
        Assert.True(paired.BoundaryDeclared);
        Assert.False(paired.Qualifies);
        Assert.Contains("insufficient-purged-blocks", paired.GateReasons);
    }

    [Fact]
    public async Task Paired_HeaderMissingGateColumns_DegradesToUnavailable()
    {
        await WriteAsync(
            FileStrategyEvidenceFactsSource.PairedComparisonFileName,
            "status,primaryStrategy",
            "baseline,disclosure-led-v11");

        var facts = await Source().ReadAsync(default);

        Assert.False(facts.PairedAvailable);
    }
}
