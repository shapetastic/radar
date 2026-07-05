using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Prices;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FilePriceHistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset RetrievedAt = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public FilePriceHistoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; ignore transient filesystem locks and permission errors.
        }
    }

    private FilePriceHistoryStore CreateStore(string? rootDirectory = null) =>
        new(
            new FilePriceHistoryStoreOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FilePriceHistoryStore>.Instance);

    private static PriceBar Bar(DateOnly date, decimal close) =>
        new(date, Open: close - 1m, High: close + 1m, Low: close - 2m, Close: close, AdjClose: close - 0.5m, Volume: 1000);

    private static PriceHistory HistoryWith(string ticker, params PriceBar[] bars) =>
        new(ticker, "yahoo-chart-v8", RetrievedAt, bars);

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RoundTripsAllFields()
    {
        var d0 = new DateOnly(2026, 6, 8);
        var d1 = new DateOnly(2026, 6, 9);
        var history = HistoryWith("MRCY", Bar(d0, 100.25m), Bar(d1, 101.50m));

        var store = CreateStore();
        var path = await store.WriteAsync(history, CancellationToken.None);

        Assert.Equal(Path.Combine(_tempDir, "mrcy.json"), path);
        Assert.True(File.Exists(path), $"Expected file at {path}.");

        var read = await store.ReadAsync("MRCY", CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal("MRCY", read!.Ticker);
        Assert.Equal("yahoo-chart-v8", read.Source);
        Assert.Equal(2, read.Bars.Count);

        // Ascending by Date.
        Assert.Equal(d0, read.Bars[0].Date);
        Assert.Equal(d1, read.Bars[1].Date);

        var b0 = read.Bars[0];
        Assert.Equal(99.25m, b0.Open);
        Assert.Equal(101.25m, b0.High);
        Assert.Equal(98.25m, b0.Low);
        Assert.Equal(100.25m, b0.Close);
        Assert.Equal(99.75m, b0.AdjClose);
        Assert.Equal(1000L, b0.Volume);
    }

    [Fact]
    public async Task WriteAsync_OverlappingHistory_MergesAndDedupesByDate_LastWriteWins()
    {
        var d0 = new DateOnly(2026, 6, 8);
        var d1 = new DateOnly(2026, 6, 9);
        var d2 = new DateOnly(2026, 6, 10);

        var store = CreateStore();
        await store.WriteAsync(HistoryWith("MRCY", Bar(d0, 100m), Bar(d1, 101m)), CancellationToken.None);

        // Second write overlaps d1 (with a corrected close) and adds d2.
        await store.WriteAsync(HistoryWith("MRCY", Bar(d1, 199m), Bar(d2, 102m)), CancellationToken.None);

        var read = await store.ReadAsync("MRCY", CancellationToken.None);
        Assert.NotNull(read);

        // Exactly one bar per date, ascending, no duplicates.
        Assert.Equal(3, read!.Bars.Count);
        Assert.Equal([d0, d1, d2], read.Bars.Select(b => b.Date).ToArray());

        // The overlapping d1 bar was REPLACED by the newer write (last-write-wins per date).
        Assert.Equal(199m, read.Bars.Single(b => b.Date == d1).Close);
        Assert.Equal(100m, read.Bars.Single(b => b.Date == d0).Close);
        Assert.Equal(102m, read.Bars.Single(b => b.Date == d2).Close);
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsNull()
    {
        var store = CreateStore();

        Assert.Null(await store.ReadAsync("NOPE", CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_MalformedFile_ReturnsNullWithoutThrowing()
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "mrcy.json"), "{ not valid");

        var read = await store.ReadAsync("MRCY", CancellationToken.None);

        Assert.Null(read);
    }

    [Fact]
    public async Task WriteAsync_IoFailure_ReturnsAttemptedPathWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException on write.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var store = CreateStore(rootAsFile);

        var path = await store.WriteAsync(
            HistoryWith("MRCY", Bar(new DateOnly(2026, 6, 8), 100m)), CancellationToken.None);

        Assert.Equal(Path.Combine(rootAsFile, "mrcy.json"), path);
    }

    [Fact]
    public async Task WriteAsync_BlankTicker_IsSkipped_NeverWritesOutsideRoot()
    {
        var store = CreateStore();

        var path = await store.WriteAsync(
            HistoryWith("   ", Bar(new DateOnly(2026, 6, 8), 100m)), CancellationToken.None);

        // No real file was written; the returned path stays under the root (never outside it).
        Assert.StartsWith(_tempDir, path, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }
}
