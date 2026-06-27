using Radar.Domain.Reports;

namespace Radar.Application.Abstractions.Persistence;

public interface IReportRepository
{
    /// <remarks>
    /// Upsert by Id (last-write-wins). The relational implementation must preserve these
    /// semantics; do not silently switch evidence to upsert or these to insert-only.
    /// </remarks>
    Task AddAsync(RadarReport report, IReadOnlyList<RadarReportItem> items, CancellationToken ct);
    Task<RadarReport?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<RadarReportItem>> GetItemsAsync(Guid reportId, CancellationToken ct);
}
