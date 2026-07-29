using Radar.CalibrationAudit;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 162: SHA-256(accession) lowercase hex is the study's ONE deterministic ordering key (worksheet
/// order, fetch order, Phase-B batching, and — reimplemented in PowerShell inside
/// <c>analyze-labels.ps1</c> — the calibration probability sample). The vectors here are pinned externally
/// (computed outside this codebase) so the C# and PowerShell implementations can both be checked against
/// the same constants.
/// </summary>
public sealed class AccessionHashTests
{
    [Theory]
    [InlineData(
        "0000018230-25-000013",
        "0cf0e439bd77959a44a89495a1d8092bcf83d09c1d2b5575e9bf5ae974e49ad4")]
    [InlineData(
        "0001628280-26-048253",
        "549cf25060c55a3099ef6a9eb9b1cf7d9047f27302456c5bc90e0d296ef2d092")]
    public void HexOf_MatchesExternallyPinnedVectors(string accession, string expected)
        => Assert.Equal(expected, AccessionHash.HexOf(accession));

    [Fact]
    public void HexOf_IsLowercase_And64Chars()
    {
        var hex = AccessionHash.HexOf("0000018230-25-000013");
        Assert.Equal(64, hex.Length);
        Assert.Equal(hex.ToLowerInvariant(), hex);
    }
}
