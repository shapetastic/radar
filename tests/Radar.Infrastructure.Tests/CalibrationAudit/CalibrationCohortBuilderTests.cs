using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Application.SignalExtraction;
using Radar.CalibrationAudit;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 162: the cohort builder reads the model-scoped cache through the PRODUCTION
/// <see cref="FileAnalyzedFilingCache"/>, excludes legacy root-level files with reason
/// <c>legacy-scope</c>, NAMES legacy/active outcome conflicts, reports (never drops) files the production
/// reader rejects, and orders rows by SHA-256(accession) hex ascending.
/// </summary>
public sealed class CalibrationCohortBuilderTests : IDisposable
{
    private const string Scope = "test-model-0123456789abcdef";

    private readonly string _root;
    private readonly FileAnalyzedFilingCache _scopedCache;
    private readonly FileAnalyzedFilingCache _legacyCache;

    public CalibrationCohortBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "radar-cal-cohort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, Scope));
        _scopedCache = new FileAnalyzedFilingCache(
            new FileAnalyzedFilingCacheOptions { RootDirectory = _root, ModelSegment = Scope },
            NullLogger<FileAnalyzedFilingCache>.Instance);
        _legacyCache = new FileAnalyzedFilingCache(
            new FileAnalyzedFilingCacheOptions { RootDirectory = _root },
            NullLogger<FileAnalyzedFilingCache>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static AnalyzedFilingRecord Directional(string accession) => new(
        accession,
        AnalyzedFilingOutcome.DirectionalSignalProduced,
        new ExtractedSignal("Test Co", "GuidanceChange", "Positive", 8, 6, 0.85m, "excerpt", "reason"),
        DateTimeOffset.UtcNow,
        AnalyzedFilingRecord.CurrentCacheVersion);

    private static AnalyzedFilingRecord NoSignal(string accession) => new(
        accession,
        AnalyzedFilingOutcome.NoDirectionalSignal,
        Signal: null,
        ObservedAtUtc: null,
        AnalyzedFilingRecord.CurrentCacheVersion);

    private Task<CalibrationCohort> BuildAsync() => CalibrationCohortBuilder.BuildAsync(
        _root, Scope, _scopedCache, _legacyCache, CancellationToken.None);

    [Fact]
    public async Task ScopedRows_AreRead_AndHashOrdered()
    {
        await _scopedCache.PutAsync(Directional("0000000001-25-000001"), CancellationToken.None);
        await _scopedCache.PutAsync(NoSignal("0000000002-25-000002"), CancellationToken.None);
        await _scopedCache.PutAsync(Directional("0000000003-25-000003"), CancellationToken.None);

        var cohort = await BuildAsync();

        Assert.Equal(3, cohort.Rows.Count);
        Assert.Empty(cohort.LegacyExclusions);
        Assert.Empty(cohort.UnreadableFiles);
        Assert.Equal(Scope, cohort.ScopeSegment);

        // Ordered by SHA-256(accession) hex ascending — the study's one deterministic ordering key.
        var expected = cohort.Rows.Select(r => r.AccessionSha256).OrderBy(h => h, StringComparer.Ordinal);
        Assert.Equal(expected, cohort.Rows.Select(r => r.AccessionSha256));
        Assert.All(cohort.Rows, r => Assert.Equal(AccessionHash.HexOf(r.Accession), r.AccessionSha256));
    }

    [Fact]
    public async Task LegacyRootFiles_AreExcluded_AndConflictsAreNamed()
    {
        // Active scope: no-signal. Legacy root: a STALE directional read of the same accession — the
        // spec-named conflict shape (two of the five live legacy files disagree with the active outcome).
        await _scopedCache.PutAsync(NoSignal("0000000001-25-000001"), CancellationToken.None);
        await _legacyCache.PutAsync(Directional("0000000001-25-000001"), CancellationToken.None);

        // A legacy file that AGREES with the active outcome — excluded, but no conflict.
        await _scopedCache.PutAsync(Directional("0000000002-25-000002"), CancellationToken.None);
        await _legacyCache.PutAsync(Directional("0000000002-25-000002"), CancellationToken.None);

        // A legacy file with no active counterpart at all.
        await _legacyCache.PutAsync(NoSignal("0000000003-25-000003"), CancellationToken.None);

        var cohort = await BuildAsync();

        Assert.Equal(2, cohort.Rows.Count); // Legacy files never enter the cohort.
        Assert.Equal(3, cohort.LegacyExclusions.Count);

        var conflict = Assert.Single(cohort.LegacyExclusions, e => e.OutcomeConflict);
        Assert.Equal("0000000001-25-000001", conflict.Accession);
        Assert.Equal("DirectionalSignalProduced", conflict.LegacyOutcome);
        Assert.Equal("NoDirectionalSignal", conflict.ActiveOutcome);

        var orphan = Assert.Single(cohort.LegacyExclusions, e => e.Accession == "0000000003-25-000003");
        Assert.Null(orphan.ActiveOutcome);
        Assert.False(orphan.OutcomeConflict);
    }

    [Fact]
    public async Task FileTheProductionReaderRejects_IsReported_NeverSilentlyDropped()
    {
        await _scopedCache.PutAsync(Directional("0000000001-25-000001"), CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(_root, Scope, "0000000002-25-000002.json"), "{ not valid json");

        var cohort = await BuildAsync();

        Assert.Single(cohort.Rows);
        var unreadable = Assert.Single(cohort.UnreadableFiles);
        Assert.Equal("0000000002-25-000002", unreadable.Accession);
        Assert.Equal("scoped", unreadable.Location);
    }

    [Fact]
    public async Task MissingScopedDirectory_FailsLoudly()
    {
        Directory.Delete(Path.Combine(_root, Scope), recursive: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(BuildAsync);
        Assert.Contains(Scope, ex.Message, StringComparison.Ordinal);
    }
}
