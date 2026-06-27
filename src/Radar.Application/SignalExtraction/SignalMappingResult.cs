using Radar.Domain.Signals;

namespace Radar.Application.SignalExtraction;

public sealed record SignalMappingResult(Signal? Signal, IReadOnlyList<string> Errors)
{
    public bool IsValid => Signal is not null;
}
