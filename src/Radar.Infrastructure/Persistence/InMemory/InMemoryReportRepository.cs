using System.Collections.Concurrent;
using Radar.Application.Abstractions.Persistence;
using Radar.Domain.Reports;

namespace Radar.Infrastructure.Persistence.InMemory;

public sealed class InMemoryReportRepository : IReportRepository
{
    private readonly ConcurrentDictionary<Guid, RadarReport> _reports = new();
    private readonly ConcurrentDictionary<Guid, RadarReportItem> _items = new();

    public Task AddAsync(RadarReport report, IReadOnlyList<RadarReportItem> items, CancellationToken ct)
    {
        _reports[report.Id] = report;
        foreach (var item in items)
        {
            _items[item.Id] = item;
        }

        return Task.CompletedTask;
    }

    public Task<RadarReport?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        _reports.TryGetValue(id, out var report);
        return Task.FromResult(report);
    }

    public Task<IReadOnlyList<RadarReportItem>> GetItemsAsync(Guid reportId, CancellationToken ct)
    {
        // Order by Rank (with Id as a stable tiebreaker) so report output is
        // deterministic and consumers do not need to re-sort.
        IReadOnlyList<RadarReportItem> result = _items.Values
            .Where(i => i.ReportId == reportId)
            .OrderBy(i => i.Rank)
            .ThenBy(i => i.Id)
            .ToList();
        return Task.FromResult(result);
    }
}
