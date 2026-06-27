namespace Radar.Application.SignalExtraction;

using Radar.Domain.Signals;

internal sealed record KeywordSignalRule(
    string Phrase,
    SignalType Type,
    SignalDirection Direction,
    int Strength,
    int Novelty,
    decimal Confidence);
