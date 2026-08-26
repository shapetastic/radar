namespace Radar.Domain.Signals;

public sealed record Signal(
    Guid Id,
    Guid EvidenceId,
    Guid? CompanyId,
    string CompanyMention,
    SignalType Type,
    SignalDirection Direction,
    int Strength,
    int Novelty,
    decimal Confidence,
    string SupportingExcerpt,
    string Reason,
    SignalReviewStatus ReviewStatus,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset CreatedAtUtc,
    // Spec 191: the optional provenance envelope, mirroring EvidenceItem.MetadataJson exactly (same shared
    // composer/reader, same "one envelope definition" rule). TRAILING and NULLABLE by design — `null` means
    // NOT RECORDED, never an empty bag, so every construction site that predates spec 191 stays
    // source-compatible and every persisted signal without it hydrates honestly. Today the ONE producer is
    // the directional news read, which records its judgment id, cohort key and matched observation id so a
    // score can be walked back to the article Radar actually read.
    string? MetadataJson = null);
