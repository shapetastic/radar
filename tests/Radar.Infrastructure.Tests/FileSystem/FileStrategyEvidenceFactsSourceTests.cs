using Microsoft.Extensions.Logging;
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
            + "observationsWithoutAsOfInstant,mismatchedAsOfInstantKeys,gateVerdictId";

    /// <summary>The pre-186 header, byte-for-byte — the artifact a deployment already has on disk.</summary>
    private const string PreSpec186PairedHeader =
        "status,primaryStrategy,primaryPredeclared,firstEligibleAsOf,armsConsidered,baselinesCompared,"
            + "baseline,jointObservations,jointCompanies,jointDates,candidateDates,droppedDates,"
            + "developmentDates,inconsistentOutcomeObservations,purgedBlocks,medianPairedDelta,"
            + "intervalLower95,intervalUpper95,intervalCoverage,intervalReason,signTestP,signTestEffectiveN,"
            + "signTestZeroDeltasDropped,baselineClears,satisfiesPriceGate,gateReasons,"
            + "qualifiesUnderAd15,ad16ScreenOutcome,"
            + "eligibleJointObservations,eligibleJointCompanies,eligibleJointDates,"
            + "observationsWithoutAsOfInstant,mismatchedAsOfInstantKeys";

    private const string VerdictId = "6e5480aeb82d39b899c5b67b7c35469d1c852421a8306a11b269bd4d10c52944";

    /// <summary>One paired data row; a null verdict id renders the pre-186 shape.</summary>
    private static string PairedRow(string? verdictId) =>
        "baseline,disclosure-led-v11,true,2026-09-29,10,3,baseline-earnings-only,100,40,3,5,2,1,0,4,"
            + "0.0500,,,,insufficient-purged-blocks,,0,0,false,false,"
            + "\"no-precommitted-evaluation-boundary; baseline 'baseline-earnings-only': insufficient-purged-blocks (admitted 4, need at least 6 at 95%)\","
            + "false,pending,0,0,0,0,0"
            + (verdictId is null ? string.Empty : "," + verdictId);

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
            PairedRow(VerdictId));

        var facts = await Source().ReadAsync(default);

        Assert.True(facts.PairedAvailable);
        var paired = facts.Paired!;
        Assert.Equal("disclosure-led-v11", paired.PrimaryStrategyName);
        Assert.True(paired.PrimaryPredeclared);
        Assert.True(paired.BoundaryDeclared);
        Assert.False(paired.Qualifies);
        Assert.Contains("insufficient-purged-blocks", paired.GateReasons);
        Assert.Equal(VerdictId, paired.GateVerdictId);
    }

    // ---- spec 186 §3: the semantic verdict identity replaces the artifact's filesystem mtime -----------

    [Fact]
    public async Task Paired_GateVerdictId_SurvivesAnIdenticalRewriteAndACopyRestore()
    {
        // The defect this closes: the "verdict instant" used to be File.GetLastWriteTimeUtc, so the daily
        // efficacy re-write (and a copy/restore, and a different machine) silently expired a valid
        // override. Identical CONTENT must yield an identical verdict identity, whatever the file's mtime.
        await WriteAsync(
            FileStrategyEvidenceFactsSource.PairedComparisonFileName, PairedHeader, PairedRow(VerdictId));
        var first = (await Source().ReadAsync(default)).Paired!;

        var path = Path.Combine(_dir, FileStrategyEvidenceFactsSource.PairedComparisonFileName);
        var content = await File.ReadAllTextAsync(path);

        // (a) an identical rewrite, with a deliberately advanced write time
        await File.WriteAllTextAsync(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(7));
        var afterRewrite = (await Source().ReadAsync(default)).Paired!;

        // (b) a copy/restore, which also resets the mtime
        var copy = Path.Combine(_dir, "copy.csv");
        File.Copy(path, copy, overwrite: true);
        File.Delete(path);
        File.Copy(copy, path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        var afterRestore = (await Source().ReadAsync(default)).Paired!;

        Assert.Equal(VerdictId, first.GateVerdictId);
        Assert.Equal(first.GateVerdictId, afterRewrite.GateVerdictId);
        Assert.Equal(first.GateVerdictId, afterRestore.GateVerdictId);
    }

    [Fact]
    public async Task Paired_PreSpec186Artifact_WarnsOnce_AndReportsNoVerdictIdentity()
    {
        // No gateVerdictId column ⇒ the verdict identity is UNKNOWN. Nothing is fabricated (AD-8): the fact
        // carries an empty id, which can never match an override, so the gate default wins. It self-heals
        // on the next efficacy run, and the warning says exactly that.
        await WriteAsync(
            FileStrategyEvidenceFactsSource.PairedComparisonFileName,
            PreSpec186PairedHeader,
            PairedRow(verdictId: null));

        var logger = new CapturingLogger<FileStrategyEvidenceFactsSource>();
        var source = new FileStrategyEvidenceFactsSource(
            new FileStrategyEvidenceFactsSourceOptions(_dir), logger);

        var facts = await source.ReadAsync(default);

        Assert.True(facts.PairedAvailable);                       // the arm is never hidden
        Assert.Equal(string.Empty, facts.Paired!.GateVerdictId);
        Assert.Equal("disclosure-led-v11", facts.Paired.PrimaryStrategyName);

        var warnings = logger.Entries
            .Where(e => e.Level == LogLevel.Warning
                && e.Message.Contains("gateVerdictId", StringComparison.Ordinal))
            .ToList();
        Assert.Single(warnings);
        Assert.Contains("re-run efficacy", warnings[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paired_TheAdditiveColumn_DoesNotShiftAnyByNameReader()
    {
        // The additive column must be invisible to every by-header-name reader: parsing the pre-186 and the
        // post-186 artifact must produce the SAME gate context apart from the identity itself.
        await WriteAsync(
            FileStrategyEvidenceFactsSource.PairedComparisonFileName,
            PreSpec186PairedHeader,
            PairedRow(verdictId: null));
        var before = (await Source().ReadAsync(default)).Paired!;

        await WriteAsync(
            FileStrategyEvidenceFactsSource.PairedComparisonFileName, PairedHeader, PairedRow(VerdictId));
        var after = (await Source().ReadAsync(default)).Paired!;

        Assert.Equal(before with { GateVerdictId = VerdictId }, after);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
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
