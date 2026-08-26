namespace Radar.Application.SignalExtraction;

/// <summary>
/// One extractor-produced candidate signal, pre-mapping. <see cref="MetadataJson"/> (spec 191) is TRAILING
/// and NULLABLE — <c>null</c> means NOT RECORDED, which is what every non-provenance-bearing signal carries
/// and what every pre-191 construction site still produces. It rides the shared evidence-metadata envelope
/// (<c>Radar.Application.Collectors.EvidenceMetadata</c>); see
/// <see cref="NewsDirectionalSignalMetadata"/> for the one producer that populates it today.
/// </summary>
public sealed record ExtractedSignal(
    string CompanyMention,
    string SignalType,
    string Direction,
    int Strength,
    int Novelty,
    decimal Confidence,
    string SupportingExcerpt,
    string Reason,
    string? MetadataJson = null);
