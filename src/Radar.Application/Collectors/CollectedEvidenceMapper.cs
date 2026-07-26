using Microsoft.Extensions.Logging;

using Radar.Application.Evidence;
using Radar.Domain.Evidence;

namespace Radar.Application.Collectors;

/// <summary>
/// Pure, deterministic Application service — the single place a raw <see cref="CollectedEvidence"/>
/// becomes an immutable domain <see cref="EvidenceItem"/>. Centralises normalization
/// (<see cref="IEvidenceNormalizer"/>), content hashing, quality parsing (AD-7), and hint/metadata
/// serialization. The collector-declared <see cref="EvidenceSourceType"/> is carried straight
/// through. <c>Id</c> uses <see cref="Guid.NewGuid"/>; <c>CollectedAt</c> comes from the
/// <see cref="CollectedEvidence"/> (the collector already stamped the run instant), so no
/// <see cref="TimeProvider"/> is needed. This mapper is the sole author of the
/// <c>{ "metadata": {...}, "companyHints": [...] }</c> envelope, which every consumer reads back through
/// <see cref="EvidenceMetadata"/> so author and readers stay adjacent.
/// </summary>
public sealed class CollectedEvidenceMapper
{
    private readonly IEvidenceNormalizer _normalizer;
    private readonly ILogger<CollectedEvidenceMapper> _logger;

    public CollectedEvidenceMapper(
        IEvidenceNormalizer normalizer,
        ILogger<CollectedEvidenceMapper> logger)
    {
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(logger);
        _normalizer = normalizer;
        _logger = logger;
    }

    public EvidenceItem ToEvidenceItem(CollectedEvidence collected)
    {
        ArgumentNullException.ThrowIfNull(collected);

        var normalized = _normalizer.Normalize(collected.Title, collected.RawText);

        var sourceType = collected.SourceType;

        var quality = ParseQuality(
            collected.Metadata.TryGetValue("quality", out var declaredQuality)
                ? declaredQuality
                : null);

        // The envelope is AUTHORED here and re-authored, byte-identically, by the durable raw-evidence
        // hydration path (spec 142). Both go through EvidenceMetadata.Compose so that identity holds by
        // construction rather than by two copies of the same serializer call happening to agree.
        var metadataJson = EvidenceMetadata.Compose(collected.Metadata, collected.CompanyHints);

        return new EvidenceItem(
            Id: Guid.NewGuid(),
            SourceType: sourceType,
            SourceName: collected.SourceName,
            SourceUrl: collected.SourceUrl,
            Title: collected.Title,
            Summary: null,
            RawText: normalized.NormalizedText,
            ContentHash: normalized.ContentHash,
            PublishedAtUtc: collected.PublishedAt?.ToUniversalTime(),
            CollectedAtUtc: collected.CollectedAt.ToUniversalTime(),
            Quality: quality,
            MetadataJson: metadataJson);
    }

    /// <summary>
    /// Applies the shared <see cref="EvidenceQualityParser"/> rule (the SINGLE definition of how a declared
    /// quality string becomes an <see cref="EvidenceQuality"/>, spec 142) and keeps this mapper's Debug
    /// logging of the two failure modes. The rule itself lives in the parser so the durable raw-evidence
    /// hydration path can recover a legacy file's quality using the EXACT rule that produced it live.
    /// </summary>
    private EvidenceQuality ParseQuality(string? value)
    {
        var quality = EvidenceQualityParser.Parse(value, out var status);

        switch (status)
        {
            case EvidenceQualityParseStatus.Missing:
                _logger.LogDebug(
                    "Evidence declared quality '{Quality}' is missing, blank, or digit-only; defaulting to Unknown.",
                    value);
                break;
            case EvidenceQualityParseStatus.Unrecognized:
                _logger.LogDebug(
                    "Evidence declared quality '{Quality}' is not a recognized EvidenceQuality; defaulting to Unknown.",
                    value);
                break;
        }

        return quality;
    }
}
