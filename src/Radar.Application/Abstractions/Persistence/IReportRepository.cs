using Radar.Domain.Reports;

namespace Radar.Application.Abstractions.Persistence;

public interface IReportRepository
{
    Task AddAsync(RadarReport report, IReadOnlyList<RadarReportItem> items, CancellationToken ct);
    Task<RadarReport?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<RadarReportItem>> GetItemsAsync(Guid reportId, CancellationToken ct);
}
