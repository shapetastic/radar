using Radar.CalibrationAudit;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 162: the exhibit manifest is the re-runnability key — an accession with a successful row, both
/// exhibit files on disk and a plausibly-long stored body is SKIPPED (no SEC request); a failed row, a
/// missing file or a suspiciously short body (&lt; the 200-char tripwire mirroring the production spec-114
/// <c>MinPlausibleBodyLength</c>) forces a refetch. The manifest round-trips through the audit's own CSV
/// and is written in SHA-256(accession) hash order (deterministic).
/// </summary>
public sealed class ExhibitManifestTests : IDisposable
{
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

    private static ExhibitManifestRow SuccessRow(string accession, int fullTextLength = 9000) => new(
        accession, "cat", "18230", "ex991.htm", "EX-99.1",
        "https://www.sec.gov/Archives/edgar/data/18230/000001823025000013/ex991.htm",
        FullTextSha256: "aa", FullTextLength: fullTextLength,
        ModelInputSha256: "bb", ModelInputLength: Math.Min(fullTextLength, 12000),
        Truncated: fullTextLength > 12000, MaxInputLength: 12000,
        Outcome: "success", FetchedAtUtc: "2026-07-29T00:00:00.0000000Z");

    private (string FullPath, string ModelInputPath) WriteExhibits(string accession, string? fullText = null)
    {
        var fullPath = ExhibitArchiver.FullTextPath(_root, "cat", accession);
        var modelInputPath = ExhibitArchiver.ModelInputPath(_root, "cat", accession);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modelInputPath)!);
        File.WriteAllText(fullPath, fullText ?? new string('x', 9000));
        File.WriteAllText(modelInputPath, fullText ?? new string('x', 9000));
        return (fullPath, modelInputPath);
    }

    [Fact]
    public void CompleteRow_WithFilesOnDisk_IsSkipped()
    {
        const string accession = "0000018230-25-000013";
        var (fullPath, modelInputPath) = WriteExhibits(accession);

        Assert.False(ExhibitArchiver.NeedsFetch(SuccessRow(accession), fullPath, modelInputPath, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(199)] // One below the tripwire.
    public void SuspiciouslyShortStoredBody_ForcesRefetch(int storedBodyLength)
    {
        const string accession = "0000018230-25-000013";
        var (fullPath, modelInputPath) = WriteExhibits(accession, new string('x', storedBodyLength));

        // The manifest row claims a plausible length — the STORED FILE is what decides.
        Assert.True(ExhibitArchiver.NeedsFetch(
            SuccessRow(accession), fullPath, modelInputPath, out var reason));
        Assert.Contains("suspiciously short", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MostlyWhitespaceStoredBody_ForcesRefetch_EvenWhenUntrimmedLengthIsLong()
    {
        const string accession = "0000018230-25-000013";
        var (fullPath, modelInputPath) = WriteExhibits(accession, new string(' ', 9000) + "shell");

        Assert.True(ExhibitArchiver.NeedsFetch(
            SuccessRow(accession), fullPath, modelInputPath, out var reason));
        Assert.Contains("suspiciously short", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingManifestRow_FailedRow_OrMissingFile_EachForceAFetch()
    {
        const string accession = "0000018230-25-000013";
        var (fullPath, modelInputPath) = WriteExhibits(accession);

        Assert.True(ExhibitArchiver.NeedsFetch(null, fullPath, modelInputPath, out _));

        var failedRow = SuccessRow(accession) with { Outcome = "failed:RateLimited", FullTextSha256 = "" };
        Assert.True(ExhibitArchiver.NeedsFetch(failedRow, fullPath, modelInputPath, out _));

        File.Delete(modelInputPath);
        Assert.True(ExhibitArchiver.NeedsFetch(SuccessRow(accession), fullPath, modelInputPath, out var reason));
        Assert.Contains("missing", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_RoundTrips_AndIsWrittenInAccessionHashOrder()
    {
        var rows = new[]
        {
            SuccessRow("0000018230-25-000013"),
            SuccessRow("0001628280-26-048253") with { Truncated = true, FullTextLength = 15000 },
            SuccessRow("0001654954-26-006655") with { Outcome = "failed:Forbidden", FullTextSha256 = "" },
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
