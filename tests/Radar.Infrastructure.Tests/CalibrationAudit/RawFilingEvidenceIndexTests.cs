using Microsoft.Extensions.Logging.Abstractions;

using Radar.CalibrationAudit;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 162: CIK/ticker recovery from the persisted raw filing evidence (<c>data/evidence/raw/filing/**</c>,
/// singular). The accession comes from the persisted <c>metadata.accessionNumber</c> or the dashed
/// accession inside the index <c>sourceUrl</c>; the CIK from the EDGAR archive path; the ticker from
/// <c>companyHints</c>. Malformed files are skipped (logged), and an unindexed accession resolves false —
/// the console then LISTS it as unrecoverable rather than dropping it.
/// </summary>
public sealed class RawFilingEvidenceIndexTests : IDisposable
{
    private readonly string _root;

    public RawFilingEvidenceIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "radar-cal-rawidx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "2025", "04"));
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

    private void WriteRaw(string fileName, string json) =>
        File.WriteAllText(Path.Combine(_root, "2025", "04", fileName), json);

    private RawFilingEvidenceIndex Load() =>
        RawFilingEvidenceIndex.Load(_root, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public void AccessionFromMetadata_CikFromSourceUrl_TickerFromCompanyHints()
    {
        WriteRaw("a.json", """
            {
              "sourceType": "filing",
              "sourceName": "Caterpillar Inc. — SEC filings (EDGAR)",
              "sourceUrl": "https://www.sec.gov/Archives/edgar/data/18230/000001823025000013/0000018230-25-000013-index.htm",
              "companyHints": ["CAT"],
              "metadata": { "accessionNumber": "0000018230-25-000013" }
            }
            """);

        var index = Load();

        Assert.True(index.TryResolve("0000018230-25-000013", out var attribution));
        Assert.Equal("18230", attribution.Cik);
        Assert.Equal("CAT", attribution.Ticker);
        Assert.Equal("Caterpillar Inc. — SEC filings (EDGAR)", attribution.SourceName);
    }

    [Fact]
    public void AccessionRecoverable_FromSourceUrlAlone_WhenMetadataLacksIt()
    {
        WriteRaw("b.json", """
            {
              "sourceUrl": "https://www.sec.gov/Archives/edgar/data/98677/000114036106001750/0001140361-06-001750-index.htm",
              "companyHints": ["TR"]
            }
            """);

        var index = Load();

        Assert.True(index.TryResolve("0001140361-06-001750", out var attribution));
        Assert.Equal("98677", attribution.Cik);
        Assert.Equal("TR", attribution.Ticker);
    }

    [Fact]
    public void MalformedFile_IsSkipped_AndUnindexedAccessionResolvesFalse()
    {
        WriteRaw("broken.json", "{ nope");
        WriteRaw("ok.json", """
            {
              "sourceUrl": "https://www.sec.gov/Archives/edgar/data/18230/000001823025000013/0000018230-25-000013-index.htm",
              "companyHints": ["CAT"]
            }
            """);

        var index = Load();

        Assert.Equal(1, index.Count);
        Assert.False(index.TryResolve("9999999999-99-999999", out _));
    }

    [Fact]
    public void FirstFileInOrdinalPathOrder_Wins_Deterministically()
    {
        // Same accession in two files; "a.json" < "b.json" ordinal — the first must win on every run.
        WriteRaw("a.json", """
            {
              "sourceUrl": "https://www.sec.gov/Archives/edgar/data/18230/000001823025000013/0000018230-25-000013-index.htm",
              "companyHints": ["FIRST"]
            }
            """);
        WriteRaw("b.json", """
            {
              "sourceUrl": "https://www.sec.gov/Archives/edgar/data/18230/000001823025000013/0000018230-25-000013-index.htm",
              "companyHints": ["SECOND"]
            }
            """);

        var index = Load();

        Assert.True(index.TryResolve("0000018230-25-000013", out var attribution));
        Assert.Equal("FIRST", attribution.Ticker);
    }

    [Fact]
    public void MissingRoot_YieldsEmptyIndex_NeverThrows()
    {
        var index = RawFilingEvidenceIndex.Load(
            Path.Combine(_root, "does-not-exist"), NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, index.Count);
    }
}
