using Radar.Application.Collectors;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// Structural guardrail for the spec-107 analysis-result cache (AD-14 analogue): the cache and the directional
/// source must never become a scoring/fingerprint input. The cache is NOT an <see cref="IEvidenceCollector"/>
/// (so it cannot be swept into the collector <c>IEnumerable</c> the fingerprint descriptor reads), and neither
/// the source nor the cache references any scoring-engine/fingerprint type. A source scan keeps a future edit
/// from silently letting the cache flow back into scoring. (Consuming <c>EvidenceItem</c> is legitimate — the
/// source reads evidence — so it is deliberately NOT forbidden here.)
/// </summary>
public sealed class AnalyzedFilingCacheReadOnlyGuardrailTests
{
    private static readonly string[] ForbiddenTypeReferences =
    [
        "IScoringEngine",
        "ScoringEngine",
        "ScoringConfigFingerprint",
    ];

    private static readonly string[] GuardedSourceFiles =
    [
        Path.Combine("src", "Radar.Infrastructure", "Filings", "DirectionalFilingSignalSource.cs"),
        Path.Combine("src", "Radar.Infrastructure", "Filings", "FileAnalyzedFilingCache.cs"),
        Path.Combine("src", "Radar.Application", "Filings", "IAnalyzedFilingCache.cs"),
    ];

    [Fact]
    public void FileAnalyzedFilingCache_IsNotAnEvidenceCollector()
    {
        Assert.False(typeof(IEvidenceCollector).IsAssignableFrom(typeof(FileAnalyzedFilingCache)));
    }

    [Fact]
    public void CacheAndSource_ReferenceNoScoringWriteType()
    {
        var repoRoot = LocateRepoRoot();
        foreach (var relative in GuardedSourceFiles)
        {
            var path = Path.Combine(repoRoot, relative);
            Assert.True(File.Exists(path), $"Expected guarded source file at {path}.");
            var text = File.ReadAllText(path);
            foreach (var forbidden in ForbiddenTypeReferences)
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.Ordinal),
                    $"{Path.GetFileName(path)} references forbidden type '{forbidden}' — the analysis-result cache "
                        + "must stay operational/reference data, never a scoring/fingerprint input (spec 107, AD-14).");
            }
        }
    }

    private static string LocateRepoRoot()
    {
        // Walk up from the test assembly's base directory to the repo root (the folder holding Radar.sln).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
