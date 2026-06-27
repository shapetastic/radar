using Radar.Domain.Reports;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Infrastructure.Tests.Persistence;

public class InMemoryReportRepositoryTests
{
    private static RadarReport MakeReport(Guid id)
        => new(
            Id: id,
            ReportType: "Daily",
            Title: "Daily Radar",
            PeriodStartUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PeriodEndUtc: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            MarkdownContent: "# Daily Radar",
            CreatedAtUtc: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

    private static RadarReportItem MakeItem(Guid reportId, int rank)
        => new(
            Id: Guid.NewGuid(),
            ReportId: reportId,
            CompanyId: Guid.NewGuid(),
            ScoreSnapshotId: Guid.NewGuid(),
            SuggestedAction: RadarReportAction.Investigate,
            Summary: $"Item rank {rank}",
            Rank: rank);

    [Fact]
    public async Task GetItemsAsync_ReturnsItemsOrderedByRank()
    {
        var repo = new InMemoryReportRepository();
        var reportId = Guid.NewGuid();
        var report = MakeReport(reportId);

        // Insert out of rank order to prove the repository sorts.
        var items = new[]
        {
            MakeItem(reportId, 3),
            MakeItem(reportId, 1),
            MakeItem(reportId, 2),
        };

        await repo.AddAsync(report, items, CancellationToken.None);

        var result = await repo.GetItemsAsync(reportId, CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, result.Select(i => i.Rank).ToArray());
    }

    [Fact]
    public async Task GetItemsAsync_OnlyReturnsItemsForRequestedReport()
    {
        var repo = new InMemoryReportRepository();
        var reportA = Guid.NewGuid();
        var reportB = Guid.NewGuid();

        await repo.AddAsync(MakeReport(reportA), new[] { MakeItem(reportA, 1) }, CancellationToken.None);
        await repo.AddAsync(MakeReport(reportB), new[] { MakeItem(reportB, 1) }, CancellationToken.None);

        var result = await repo.GetItemsAsync(reportA, CancellationToken.None);

        Assert.Single(result);
        Assert.All(result, i => Assert.Equal(reportA, i.ReportId));
    }

    [Fact]
    public async Task GetByIdAsync_Absent_ReturnsNull()
    {
        var repo = new InMemoryReportRepository();

        var result = await repo.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }
}
