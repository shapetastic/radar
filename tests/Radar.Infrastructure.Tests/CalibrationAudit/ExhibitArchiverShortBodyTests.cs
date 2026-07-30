using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Radar.CalibrationAudit;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 163 fix 5: a fetched body below the 200-trimmed-char tripwire is a typed FAILURE
/// (<c>failed:short-body</c>), never a warned success — Phase B must not consume a degenerate fetch. The
/// row carries EMPTY hashes (the existing failure semantics, so <c>NeedsFetch</c> re-attempts it next
/// run), the two exhibit files still land on disk as evidence of what came back, and the console's
/// <c>failed &gt; 0 ⇒ exit 1</c> tally counts it like any other failure (it keys off <c>IsSuccess</c>).
/// </summary>
public sealed class ExhibitArchiverShortBodyTests : IDisposable
{
    private readonly string _root;

    public ExhibitArchiverShortBodyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "radar-cal-shortbody-" + Guid.NewGuid().ToString("N"));
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

    private sealed class StubReader(SecEarningsReleaseReadResult result) : ISecEarningsReleaseReader
    {
        public Task<SecEarningsReleaseReadResult> ReadAsync(string cik, string accession, CancellationToken ct) =>
            Task.FromResult(result);
    }

    [Fact]
    public async Task ShortFetchedBody_IsTypedFailure_WithEmptyHashes_AndRefetchesNextRun()
    {
        const string accession = "0000018230-25-000013";
        var shortText = new string('x', ExhibitArchiver.ShortBodyTripwireLength - 1);
        var archiver = new ExhibitArchiver(
            new StubReader(SecEarningsReleaseReadResult.Success(shortText, "EX-99.1", "ex991.htm")),
            maxInputLength: 12000,
            NullLogger.Instance);

        var row = await archiver.FetchAsync(
            _root, accession, "cat", "18230", new FakeTimeProvider(), CancellationToken.None);

        Assert.Equal("failed:short-body", row.Outcome);
        Assert.False(row.IsSuccess);
        Assert.Equal(string.Empty, row.FullTextSha256);
        Assert.Equal(string.Empty, row.ModelInputSha256);
        Assert.False(row.Truncated);

        // The files stay on disk as evidence of what came back.
        var fullPath = ExhibitArchiver.FullTextPath(_root, "cat", accession);
        var modelInputPath = ExhibitArchiver.ModelInputPath(_root, "cat", accession);
        Assert.Equal(shortText, File.ReadAllText(fullPath));
        Assert.Equal(shortText, File.ReadAllText(modelInputPath));

        // And the failure row re-attempts on the next run (the empty hash marks it non-successful).
        Assert.True(ExhibitArchiver.NeedsFetch(row, fullPath, modelInputPath, 12000, out var reason));
        Assert.Contains("failed:short-body", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortBodyOverInputCap_RecordsTruncatedFlag_OnTheFailureRow()
    {
        // A long, mostly-whitespace body: raw length exceeds the input cap (so the stored model-input
        // file IS truncated) while the trimmed length sits below the tripwire. The failure row must
        // record the truncation that actually happened to the archived artifact, not hard-code false.
        const string accession = "0000018230-25-000013";
        const int maxInputLength = 12000;
        var whitespaceHeavyText = new string(' ', maxInputLength + 999) + "x";
        var archiver = new ExhibitArchiver(
            new StubReader(SecEarningsReleaseReadResult.Success(whitespaceHeavyText, "EX-99.1", "ex991.htm")),
            maxInputLength,
            NullLogger.Instance);

        var row = await archiver.FetchAsync(
            _root, accession, "cat", "18230", new FakeTimeProvider(), CancellationToken.None);

        Assert.Equal("failed:short-body", row.Outcome);
        Assert.True(row.Truncated);
        Assert.Equal(maxInputLength, row.ModelInputLength);
        var modelInputPath = ExhibitArchiver.ModelInputPath(_root, "cat", accession);
        Assert.Equal(maxInputLength, File.ReadAllText(modelInputPath).Length);
    }

    [Fact]
    public async Task PlausibleFetchedBody_StaysSuccess_AndItsRowVerifiesAsNoOp()
    {
        const string accession = "0000018230-25-000013";
        var text = new string('x', 9000);
        var archiver = new ExhibitArchiver(
            new StubReader(SecEarningsReleaseReadResult.Success(text, "EX-99.1", "ex991.htm")),
            maxInputLength: 12000,
            NullLogger.Instance);

        var row = await archiver.FetchAsync(
            _root, accession, "cat", "18230", new FakeTimeProvider(), CancellationToken.None);

        Assert.True(row.IsSuccess);

        // The freshly-fetched row + files verify against each other: the very next run is a no-op skip
        // (the spec-163 idempotence criterion for the operator's post-merge rerun).
        var fullPath = ExhibitArchiver.FullTextPath(_root, "cat", accession);
        var modelInputPath = ExhibitArchiver.ModelInputPath(_root, "cat", accession);
        Assert.False(ExhibitArchiver.NeedsFetch(row, fullPath, modelInputPath, 12000, out _));
    }
}
