using System.Text.Json;

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
    private static readonly JsonSerializerOptions MetadataJsonOptions = new();

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

        var metadataJson = JsonSerializer.Serialize(
            new { metadata = collected.Metadata, companyHints = collected.CompanyHints },
            MetadataJsonOptions);

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
    /// Maps a declared evidence quality string to <see cref="EvidenceQuality"/>. Accepts only a
    /// defined enum name (case-insensitive); rejects digit-only input (which
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would otherwise accept). Missing,
    /// blank, or unparseable values default to <see cref="EvidenceQuality.Unknown"/> (skip-don't-throw).
    /// </summary>
    private EvidenceQuality ParseQuality(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().All(char.IsDigit))
        {
            _logger.LogDebug(
                "Evidence declared quality '{Quality}' is missing, blank, or digit-only; defaulting to Unknown.",
                value);
            return EvidenceQuality.Unknown;
        }

        if (Enum.TryParse<EvidenceQuality>(value, ignoreCase: true, out var q) && Enum.IsDefined(q))
        {
            return q;
        }

        _logger.LogDebug(
            "Evidence declared quality '{Quality}' is not a recognized EvidenceQuality; defaulting to Unknown.",
            value);
        return EvidenceQuality.Unknown;
    }
}
