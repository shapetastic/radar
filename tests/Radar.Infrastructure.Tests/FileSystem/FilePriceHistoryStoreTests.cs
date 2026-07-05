using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Prices;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FilePriceHistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset RetrievedAt = new(2026, 6, 8, 12, 0, 0, TimeSpan.Zero);

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

    private static PriceBar Bar(int year, int month, int day, decimal close) =>
        new(
            Date: new DateOnly(year, month, day),
            Open: close - 1m,
            High: close + 2m,
            Low: close - 2m,
            Close: close,
            AdjClose: close - 0.25m,
            Volume: 1_000_000L);

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RoundTripsAllFields()
    {
        var history = new PriceHistory(
            Ticker: "MRCY",
            Source: "yahoo-chart-v8",
            RetrievedAtUtc: RetrievedAt,
            Bars: [Bar(2026, 6, 5, 124.25m), Bar(2026, 6, 8, 126.21m)]);

        var store = CreateStore();
        var path = await store.WriteAsync(history, CancellationToken.None);

        Assert.Equal(Path.Combine(_tempDir, "mrcy.json"), path);
        Assert.True(File.Exists(path), $"Expected file at {path}.");

        var read = await store.ReadAsync("MRCY", CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(history.Ticker, read!.Ticker);
        Assert.Equal(history.Source, read.Source);
        Assert.Equal(2, read.Bars.Count);
        // Ascending by Date.
        Assert.Equal(new DateOnly(2026, 6, 5), read.Bars[0].Date);
        Assert.Equal(new DateOnly(2026, 6, 8), read.Bars[1].Date);
        // Every PriceBar field round-trips.
        var b = read.Bars[0];
        Assert.Equal(123.25m, b.Open);
        Assert.Equal(126.25m, b.High);
        Assert.Equal(122.25m, b.Low);
        Assert.Equal(124.25m, b.Close);
        Assert.Equal(124.00m, b.AdjClose);
        Assert.Equal(1_000_000L, b.Volume);
    }

    [Fact]
    public async Task WriteAsync_OverlappingHistory_MergesAndDedupesByDate_LastWriteWins()
    {
        var store = CreateStore();

        // First write: two bars, June 5 and June 8.
        await store.WriteAsync(
            new PriceHistory("MRCY", "yahoo-chart-v8", RetrievedAt, [Bar(2026, 6, 5, 124.25m), Bar(2026, 6, 8, 126.21m)]),
            CancellationToken.None);

        // Second write: a CORRECTED June 8 bar (new close) plus a new June 9 bar.
        await store.WriteAsync(
            new PriceHistory("MRCY", "yahoo-chart-v8", RetrievedAt.AddDays(1), [Bar(2026, 6, 8, 130.00m), Bar(2026, 6, 9, 131.50m)]),
            CancellationToken.None);

        var read = await store.ReadAsync("MRCY", CancellationToken.None);

        Assert.NotNull(read);
        // Exactly one bar per date: June 5, 8, 9 — no duplicate June 8.
        Assert.Equal(3, read!.Bars.Count);
        Assert.Equal(
            [new DateOnly(2026, 6, 5), new DateOnly(2026, 6, 8), new DateOnly(2026, 6, 9)],
            read.Bars.Select(b => b.Date).ToArray());
        // The June 8 bar is the REPLACEMENT (last-write-wins per date).
        Assert.Equal(130.00m, read.Bars.Single(b => b.Date == new DateOnly(2026, 6, 8)).Close);
    }

    [Fact]
    public async Task WriteAsync_IoFailure_ReturnsAttemptedPathWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var store = CreateStore(rootAsFile);

        var path = await store.WriteAsync(
            new PriceHistory("MRCY", "yahoo-chart-v8", RetrievedAt, [Bar(2026, 6, 5, 124.25m)]),
            CancellationToken.None);

        Assert.Equal(Path.Combine(rootAsFile, "mrcy.json"), path);
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsNull()
    {
        var store = CreateStore();

        var read = await store.ReadAsync("NONE", CancellationToken.None);

        Assert.Null(read);
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
    public async Task WriteAsync_BlankTicker_SkipsWithoutThrowing_ReturnsEmptyPath()
    {
        var store = CreateStore();

        var path = await store.WriteAsync(
            new PriceHistory("   ", "yahoo-chart-v8", RetrievedAt, [Bar(2026, 6, 5, 124.25m)]),
            CancellationToken.None);

        Assert.Equal(string.Empty, path);
        Assert.Empty(Directory.EnumerateFiles(_tempDir));
    }
}
