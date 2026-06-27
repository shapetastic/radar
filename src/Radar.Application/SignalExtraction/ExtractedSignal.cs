namespace Radar.Application.SignalExtraction;

public sealed record ExtractedSignal(
    string CompanyMention,
    string SignalType,
    string Direction,
    int Strength,
    int Novelty,
    decimal Confidence,
    string SupportingExcerpt,
    string Reason);
