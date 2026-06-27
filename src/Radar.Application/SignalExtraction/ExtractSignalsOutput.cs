namespace Radar.Application.SignalExtraction;

public sealed record ExtractSignalsOutput(
    IReadOnlyList<ExtractedSignal> Signals,
    string OverallSummary);
