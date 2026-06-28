using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Evidence;
using Radar.Domain.Evidence;

namespace Radar.Application.Collectors;

/// <summary>
/// Pure, deterministic Application service — the single place a raw <see cref="CollectedEvidence"/>
/// becomes an immutable domain <see cref="EvidenceItem"/>. Centralises normalization
/// (<see cref="IEvidenceNormalizer"/>), content hashing, quality parsing (AD-7),
/// <see cref="EvidenceSourceType"/> resolution, and hint/metadata serialization. <c>Id</c> uses
/// <see cref="Guid.NewGuid"/>; <c>CollectedAt</c> comes from the <see cref="CollectedEvidence"/>
/// (the collector already stamped the run instant), so no <see cref="TimeProvider"/> is needed.
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

        var sourceType = ResolveSourceType(collected.SourceType);

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
    /// Resolves a collector's canonical source-type token to an <see cref="EvidenceSourceType"/>
    /// via a documented, case-insensitive table. Unknown tokens default to
    /// <see cref="EvidenceSourceType.Manual"/> (skip-don't-throw, logged at Debug).
    /// </summary>
    private EvidenceSourceType ResolveSourceType(string? sourceType)
    {
        switch (sourceType?.Trim().ToLowerInvariant())
        {
            case "local_file":
            case "localfile":
                return EvidenceSourceType.LocalFile;
            case "press_release":
            case "pressrelease":
                return EvidenceSourceType.PressRelease;
            case "rss":
            case "rss_feed":
                return EvidenceSourceType.RssFeed;
            case "news":
            case "news_article":
                return EvidenceSourceType.NewsArticle;
            default:
                _logger.LogDebug(
                    "Collected evidence source-type '{SourceType}' is not recognized; defaulting to Manual.",
                    sourceType);
                return EvidenceSourceType.Manual;
        }
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
