using System.Security.Cryptography;
using System.Text;

using Radar.CalibrationAudit;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 162/163: the exhibit manifest is the re-runnability key, and <c>NeedsFetch</c> VERIFIES the stored
/// artifacts against it rather than trusting existence — an accession is skipped only when the row is
/// successful, both files exist, the stored body is plausibly long, the stored files' SHA-256 hashes and
/// the model-input char length match the manifest's recorded values, and the recorded MaxInputLength
/// equals the cap in force. A failed row, missing file, short body, tampered file, mis-sized model input
/// or changed input cap each force a refetch with a named reason. The fully-valid fixture asserting a skip
/// is the spec-163 idempotence guarantee: the operator's post-merge rerun over data/calibration-audit/
/// must be a 0-refetch no-op. The manifest round-trips through the audit's own CSV and is written in
/// SHA-256(accession) hash order (deterministic).
/// </summary>
public sealed class ExhibitManifestTests : IDisposable
{
    private const int Cap = 12000;
    private readonly string _root;

    public ExhibitManifestTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "radar-cal-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Writes both exhibit files exactly as <c>FetchAsync</c> does (UTF-8, no BOM) and returns a manifest
    /// row whose hashes/lengths are computed the same way <c>FetchAsync</c> computes them — a genuinely
    /// VALID fixture, not a claimed one.
    /// </summary>
    private (ExhibitManifestRow Row, string FullPath, string ModelInputPath) WriteValidFixture(
        string accession, string? fullText = null, int maxInputLength = Cap)
    {
        fullText ??= new string('x', 9000);
        var (modelInput, truncated) = ModelInputTruncation.Apply(fullText, maxInputLength);

        var fullPath = ExhibitArchiver.FullTextPath(_root, "cat", accession);
        var modelInputPath = ExhibitArchiver.ModelInputPath(_root, "cat", accession);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modelInputPath)!);
        File.WriteAllText(fullPath, fullText);
        File.WriteAllText(modelInputPath, modelInput);

        var row = new ExhibitManifestRow(
            accession, "cat", "18230", "ex991.htm", "EX-99.1",
            "https://www.sec.gov/Archives/edgar/data/18230/000001823025000013/ex991.htm",
            FullTextSha256: Sha256Hex(fullText), FullTextLength: fullText.Length,
            ModelInputSha256: Sha256Hex(modelInput), ModelInputLength: modelInput.Length,
            Truncated: truncated, MaxInputLength: maxInputLength,
            Outcome: "success", FetchedAtUtc: "2026-07-29T00:00:00.0000000Z");
        return (row, fullPath, modelInputPath);
    }

    [Fact]
    public void ValidRow_WithVerifiedFilesOnDisk_IsSkipped_TheIdempotenceNoOp()
    {
        var (row, fullPath, modelInputPath) = WriteValidFixture("0000018230-25-000013");

        Assert.False(ExhibitArchiver.NeedsFetch(row, fullPath, modelInputPath, Cap, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void ValidTruncatedRow_IsAlsoSkipped()
    {
        // A body longer than the cap: the model input is the leading substring; both hashes must verify.
        var (row, fullPath, modelInputPath) = WriteValidFixture(
            "0000018230-25-000013", new string('y', Cap + 500));

        Assert.True(row.Truncated);
        Assert.False(ExhibitArchiver.NeedsFetch(row, fullPath, modelInputPath, Cap, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(199)] // One below the tripwire.
    public void SuspiciouslyShortStoredBody_ForcesRefetch(int storedBodyLength)
    {
        var (row, fullPath, modelInputPath) = WriteValidFixture(
            "0000018230-25-000013", new string('x', storedBodyLength));

        // Even a hash-consistent row refetches when the STORED body is degenerate.
        Assert.True(ExhibitArchiver.NeedsFetch(row, fullPath, modelInputPath, Cap, out var reason));
        Assert.Contains("suspiciously short", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MostlyWhitespaceStoredBody_ForcesRefetch_EvenWhenUntrimmedLengthIsLong()
    {
        var (row, fullPath, modelInputPath) = WriteValidFixture(
            "0000018230-25-000013", new string(' ', 9000) + "shell");

        Assert.True(ExhibitArchiver.NeedsFetch(row, fullPath, modelInputPath, Cap, out var reason));
        Assert.Contains("suspiciously short", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TamperedFullTextFile_ForcesRefetch_WithHashMismatchReason()
    {
        var (row, fullPath, modelInputPath) = WriteValidFixture("0000018230-25-000013");
        File.WriteAllText(fullPath, new string('z', 9000)); // Same length, different bytes.

        Assert.True(ExhibitArchiver.NeedsFetch(row, fullPath, modelInputPath, Cap, out var reason));
        Assert.Contains("full-text hash", reason, StringComparison.Ordinal);
        Assert.Contains(row.FullTextSha256, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TamperedModelInputFile_ForcesRefetch_WithHashMismatchReason()
    {
        var (row, fullPath, modelInputPath) = WriteValidFixture("0000018230-25-000013");
        File.WriteAllText(modelInputPath, new string('z', row.ModelInputLength)); // Length intact, bytes not.

        Assert.True(ExhibitArchiver.NeedsFetch(row, fullPath, modelInputPath, Cap, out var reason));
        Assert.Contains("model-input hash", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelInputLengthMismatch_ForcesRefetch()
    {
        // File and hash intact; the manifest's recorded CHAR length disagrees — the length check must
        // trip on its own, not ride on the hash check.
        var (row, fullPath, modelInputPath) = WriteValidFixture("0000018230-25-000013");
        var lied = row with { ModelInputLength = row.ModelInputLength - 1 };

        Assert.True(ExhibitArchiver.NeedsFetch(lied, fullPath, modelInputPath, Cap, out var reason));
        Assert.Contains("model-input length", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedMaxInputLength_ForcesRefetch()
    {
        var (row, fullPath, modelInputPath) = WriteValidFixture("0000018230-25-000013");

        Assert.True(ExhibitArchiver.NeedsFetch(row, fullPath, modelInputPath, Cap + 1, out var reason));
        Assert.Contains("maxInputLength", reason, StringComparison.Ordinal);
        Assert.Contains((Cap + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingManifestRow_FailedRow_OrMissingFile_EachForceAFetch()
    {
        var (row, fullPath, modelInputPath) = WriteValidFixture("0000018230-25-000013");

        Assert.True(ExhibitArchiver.NeedsFetch(null, fullPath, modelInputPath, Cap, out _));

        var failedRow = row with { Outcome = "failed:RateLimited", FullTextSha256 = "" };
        Assert.True(ExhibitArchiver.NeedsFetch(failedRow, fullPath, modelInputPath, Cap, out _));

        File.Delete(modelInputPath);
        Assert.True(ExhibitArchiver.NeedsFetch(row, fullPath, modelInputPath, Cap, out var reason));
        Assert.Contains("missing", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_RoundTrips_AndIsWrittenInAccessionHashOrder()
    {
        var rows = new[]
        {
            WriteValidFixture("0000018230-25-000013").Row,
            WriteValidFixture("0001628280-26-048253", new string('y', 15000)).Row,
            WriteValidFixture("0001654954-26-006655").Row with { Outcome = "failed:Forbidden", FullTextSha256 = "" },
        };

        ExhibitArchiver.WriteManifest(_root, rows);
        var reloaded = ExhibitArchiver.LoadManifest(_root);

        Assert.Equal(3, reloaded.Count);
        foreach (var row in rows)
        {
            Assert.Equal(row, reloaded[row.Accession]);
        }

        // Written order = SHA-256(accession) hex ascending.
        var lines = File.ReadAllLines(ExhibitArchiver.ManifestPath(_root));
        var writtenAccessions = lines.Skip(1)
            .Where(static l => !string.IsNullOrWhiteSpace(l))
            .Select(static l => Csv.ParseLine(l)[0])
            .ToList();
        var expected = rows.Select(static r => r.Accession)
            .OrderBy(AccessionHash.HexOf, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, writtenAccessions);
    }
}
