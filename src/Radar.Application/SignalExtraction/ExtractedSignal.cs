namespace Radar.Application.SignalExtraction;

/// <summary>
/// One extractor-produced candidate signal, pre-mapping. <see cref="MetadataJson"/> (spec 191) is TRAILING
/// and NULLABLE — <c>null</c> means NOT RECORDED, which is what every non-provenance-bearing signal carries
/// and what every pre-191 construction site still produces. It rides the shared evidence-metadata envelope
/// (<c>Radar.Application.Collectors.EvidenceMetadata</c>); <see cref="NewsDirectionalSignalMetadata"/>
/// declares the provenance keys a news signal's envelope uses.
/// <para>
/// SPEC 194: <see cref="KeywordSignalExtractor"/> no longer populates this. Its spec-191 news producer took
/// a direction from a company judgment that had never read the article being extracted, and is retired;
/// direction now rides a separate judgment-derived signal materialized after the judgment exists.
/// </para>
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
