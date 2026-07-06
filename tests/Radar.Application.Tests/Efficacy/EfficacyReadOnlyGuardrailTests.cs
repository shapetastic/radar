namespace Radar.Application.Tests.Efficacy;

/// <summary>
/// Structural guardrail for the AD-14 read-only boundary (spec 101, test 13): the efficacy subsystem
/// (<c>Radar.Application/Efficacy/*</c>) must depend on NO evidence/signal/scoring <b>write</b> type. It reads
/// only <c>IScoreSnapshotFileStore</c> / <c>IPriceHistoryStore</c> / <c>ICompanyRepository</c> and writes only
/// <c>IEfficacyArtifactStore</c>. A source scan keeps a future edit from silently letting price/score data flow
/// back into scoring.
/// </summary>
public sealed class EfficacyReadOnlyGuardrailTests
{
    // Write/compute types the efficacy layer must never reference (reading persisted score history via
    // IScoreSnapshotFileStore is allowed; these are the evidence/signal/scoring WRITE + compute seams).
    // NOTE: deliberately NOT including "IRadarPipeline" — the efficacy layer legitimately DOCUMENTS that it runs
    // OUTSIDE IRadarPipeline (that prose is the boundary, not a dependency). These are the actual write/compute
    // seams that must never be referenced.
    private static readonly string[] ForbiddenTypeReferences =
    [
        "EvidenceItem",
        "CollectedEvidence",
        "ISignalExtractor",
        "IScoringEngine",
        "ScoringEngine",
        "ScoringConfigFingerprint",
        "ISignalRepository",
        "IEvidenceRepository",
        "IEvidenceCollector",
    ];

    [Fact]
    public void EfficacySources_ReferenceNoEvidenceSignalOrScoringWriteType()
    {
        var efficacyDir = LocateEfficacySourceDirectory();
        // AllDirectories: the guardrail must keep covering efficacy code if it later grows subfolders.
        var files = Directory.GetFiles(efficacyDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in ForbiddenTypeReferences)
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} references forbidden type '{forbidden}' — the efficacy layer must "
                        + "stay read-only over score history + price (AD-14 read side).");
            }
        }
    }

    private static string LocateEfficacySourceDirectory()
    {
        // Walk up from the test assembly's base directory to the repo root (the folder holding Radar.sln).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var efficacyDir = Path.Combine(dir!.FullName, "src", "Radar.Application", "Efficacy");
        Assert.True(Directory.Exists(efficacyDir), $"Expected efficacy source directory at {efficacyDir}.");
        return efficacyDir;
    }
}
