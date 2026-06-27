using Radar.Domain.Evidence;

namespace Radar.Application.SignalExtraction;

public interface ISignalExtractor
{
    Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence, CancellationToken ct);
}
