namespace Radar.Application.Reporting;

using Radar.Domain.Signals;

/// <summary>One contributing signal behind a company entry (provenance for the "why noticed" block).</summary>
public sealed record ReportSignalRef(
    Guid SignalId,
    SignalType Type,
    SignalDirection Direction,
    string Reason);
