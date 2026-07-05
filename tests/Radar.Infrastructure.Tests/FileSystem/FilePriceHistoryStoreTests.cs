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
            // Best-effort cleanup.
        }
    }

    private FilePriceHistoryStore CreateStore(string? rootDirectory = null) =>
        new(
            new FilePriceHistoryStoreOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FilePriceHistoryStore>.Instance);

    private static PriceBar Bar(int day, decimal close) =>
        new(new DateOnly(2026, 6, day), close - 1m, close + 1m, close - 2m, close, close, 1000L * day);

    private static PriceHistory History(string ticker, params PriceBar[] bars) =>
        new(ticker, "yahoo-chart-v8", RetrievedAt, bars);

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RoundTripsAllFields()
    {
        var history = History("MRCY", Bar(6, 124.25m), Bar(9, 126.21m));

        var store = CreateStore();
        var path = await store.WriteAsync(history, CancellationToken.None);

        // File is at {root}/{lowercased ticker}.json.
        Assert.Equal(Path.Combine(_tempDir, "mrcy.json"), path);
        Assert.True(File.Exists(path));

        var read = await store.ReadAsync("MRCY", CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal("MRCY", read!.Ticker);
        Assert.Equal("yahoo-chart-v8", read.Source);
        Assert.Equal(2, read.Bars.Count);

        // Ascending by Date, every field round-trips.
        Assert.Equal(new DateOnly(2026, 6, 6), read.Bars[0].Date);
        Assert.Equal(124.25m, read.Bars[0].Close);
        Assert.Equal(123.25m, read.Bars[0].Open);
        Assert.Equal(125.25m, read.Bars[0].High);
        Assert.Equal(122.25m, read.Bars[0].Low);
        Assert.Equal(124.25m, read.Bars[0].AdjClose);
        Assert.Equal(6000L, read.Bars[0].Volume);
        Assert.Equal(new DateOnly(2026, 6, 9), read.Bars[1].Date);
    }

    [Fact]
    public async Task WriteAsync_MergesAndDedupesByDate_LastWriteWins()
    {
        var store = CreateStore();

        // First write: days 6, 7.
        await store.WriteAsync(History("MRCY", Bar(6, 100m), Bar(7, 101m)), CancellationToken.None);

        // Second write: an overlapping day 7 with a CORRECTED close + a new day 8.
        await store.WriteAsync(History("MRCY", Bar(7, 999m), Bar(8, 102m)), CancellationToken.None);

        var read = await store.ReadAsync("MRCY", CancellationToken.None);
        Assert.NotNull(read);

        // Union keyed by Date: exactly one bar per date (6, 7, 8), ascending.
        Assert.Equal(3, read!.Bars.Count);
        Assert.Equal(new DateOnly(2026, 6, 6), read.Bars[0].Date);
        Assert.Equal(new DateOnly(2026, 6, 7), read.Bars[1].Date);
        Assert.Equal(new DateOnly(2026, 6, 8), read.Bars[2].Date);

        // Day 7 is the last-written value (999), not the original 101.
        Assert.Equal(999m, read.Bars[1].Close);
    }

    [Fact]
    public async Task ReadAsync_NoFile_ReturnsNull()
    {
        var store = CreateStore();

        var read = await store.ReadAsync("NOPE", CancellationToken.None);

        Assert.Null(read);
    }

    [Fact]
    public async Task ReadAsync_MalformedFile_ReturnsNullWithoutThrowing()
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "bad.json"), "{ not valid");

        var read = await store.ReadAsync("BAD", CancellationToken.None);

        Assert.Null(read);
    }

    [Fact]
    public async Task WriteAsync_IoFailure_ReturnsAttemptedPathWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var store = CreateStore(rootAsFile);

        var path = await store.WriteAsync(History("MRCY", Bar(6, 100m)), CancellationToken.None);

        Assert.Equal(Path.Combine(rootAsFile, "mrcy.json"), path);
    }

    [Fact]
    public async Task WriteAsync_BlankTicker_SkipsWithoutWriting()
    {
        var store = CreateStore();

        var path = await store.WriteAsync(History("   ", Bar(6, 100m)), CancellationToken.None);

        Assert.Equal(string.Empty, path);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task WriteAsync_TickerWithInvalidChars_SkipsWithoutWriting()
    {
        var store = CreateStore();

        // A path-separator ticker must never escape the root.
        var path = await store.WriteAsync(History("../evil", Bar(6, 100m)), CancellationToken.None);

        Assert.Equal(string.Empty, path);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }
}
