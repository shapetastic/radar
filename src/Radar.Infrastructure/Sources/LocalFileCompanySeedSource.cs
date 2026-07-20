using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Radar.Application.EntityResolution;
using Radar.Domain.Companies;

namespace Radar.Infrastructure.Sources;

/// <summary>
/// Deterministic <see cref="ICompanySeedSource"/> that reads the seed watch-universe from a local
/// JSON file. Mirrors the local-file evidence collector: it never throws when the file is missing or
/// unreadable, skips entries lacking a stable <c>id</c> or a <c>name</c> (never hallucinating data),
/// and preserves file order. Company Ids come from the file and alias Ids are derived deterministically
/// so re-seeding upserts the same rows. The injected <see cref="TimeProvider"/> only stamps
/// timestamps; it never affects identity.
/// </summary>
public sealed class LocalFileCompanySeedSource : ICompanySeedSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly LocalFileCompanySeedOptions _options;
    private readonly ILogger<LocalFileCompanySeedSource> _logger;
    private readonly TimeProvider _timeProvider;

    public LocalFileCompanySeedSource(
        LocalFileCompanySeedOptions options,
        ILogger<LocalFileCompanySeedSource> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<CompanySeedData> GetSeedAsync(CancellationToken ct)
    {
        var filePath = _options.FilePath;

        if (!File.Exists(filePath))
        {
            _logger.LogWarning(
                "Company seed file '{FilePath}' does not exist; returning an empty seed.",
                filePath);
            return new CompanySeedData([], [], []);
        }

        LocalFileCompanySeedDocument? doc;
        try
        {
            var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            doc = JsonSerializer.Deserialize<LocalFileCompanySeedDocument>(text, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read or parse company seed file '{FilePath}'; returning an empty seed.",
                filePath);
            return new CompanySeedData([], [], []);
        }

        if (doc?.Companies is null)
        {
            _logger.LogWarning(
                "Company seed file '{FilePath}' contained no companies; returning an empty seed.",
                filePath);
            return new CompanySeedData([], [], []);
        }

        var now = _timeProvider.GetUtcNow();
        var companies = new List<Company>(doc.Companies.Count);
        var aliases = new List<CompanyAlias>();
        var feeds = new List<CompanySourceFeed>();

        foreach (var entry in doc.Companies)
        {
            if (entry is null)
            {
                continue;
            }

            if (!Guid.TryParse(entry.Id, out var companyId) || string.IsNullOrWhiteSpace(entry.Name))
            {
                _logger.LogWarning(
                    "Company seed entry with id '{Id}' and name '{Name}' is missing a parseable id or a name; skipping.",
                    entry.Id,
                    entry.Name);
                continue;
            }

            IReadOnlyList<string> themes = entry.Themes?
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!.Trim())
                .ToList()
                .AsReadOnly() ?? [];

            // Curated following tier (spec 117): case-insensitive parse; absent/blank silently defaults to
            // Small (no extra discount, the fail-safe), present-but-unrecognized ALSO defaults to Small but
            // warns naming the entry and the bad value (never throws, never hallucinates a tier). AD-14:
            // this is curated seed metadata, never derived from price/market cap.
            var followingTier = FollowingTier.Small;
            if (!string.IsNullOrWhiteSpace(entry.FollowingTier))
            {
                if (Enum.TryParse<FollowingTier>(entry.FollowingTier.Trim(), ignoreCase: true, out var parsed)
                    && Enum.IsDefined(parsed))
                {
                    followingTier = parsed;
                }
                else
                {
                    _logger.LogWarning(
                        "Company seed entry '{Name}' ({Id}) has an unrecognized followingTier '{FollowingTier}'; "
                            + "defaulting to Small.",
                        entry.Name,
                        entry.Id,
                        entry.FollowingTier);
                }
            }

            companies.Add(new Company(
                Id: companyId,
                Name: entry.Name,
                LegalName: entry.LegalName,
                Ticker: entry.Ticker,
                Exchange: entry.Exchange,
                CountryCode: entry.CountryCode,
                Sector: entry.Sector,
                Industry: entry.Industry,
                Status: CompanyStatus.Active,
                CreatedAtUtc: now,
                UpdatedAtUtc: now,
                Themes: themes,
                FollowingTier: followingTier));

            if (entry.Aliases is not null)
            {
                foreach (var aliasText in entry.Aliases)
                {
                    if (string.IsNullOrWhiteSpace(aliasText))
                    {
                        continue;
                    }

                    aliases.Add(new CompanyAlias(
                        Id: DeterministicGuid(companyId, "seed", aliasText),
                        CompanyId: companyId,
                        Alias: aliasText,
                        AliasType: "seed",
                        CreatedAtUtc: now));
                }
            }

            if (entry.SourceFeeds is null)
            {
                continue;
            }

            foreach (var feed in entry.SourceFeeds)
            {
                if (feed is null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(feed.Url))
                {
                    _logger.LogWarning(
                        "Source feed (name '{FeedName}', type '{FeedType}') for company {CompanyId} is missing a url; skipping.",
                        feed.Name,
                        feed.Type,
                        companyId);
                    continue;
                }

                var feedType = string.IsNullOrWhiteSpace(feed.Type) ? "rss" : feed.Type.Trim();
                var feedUrl = feed.Url.Trim();

                feeds.Add(new CompanySourceFeed(
                    Id: DeterministicGuid(companyId, "feed", $"{feedType}|{feedUrl}"),
                    CompanyId: companyId,
                    FeedType: feedType,
                    Name: feed.Name?.Trim() ?? string.Empty,
                    Url: feedUrl,
                    CreatedAtUtc: now));
            }
        }

        return new CompanySeedData(companies, aliases, feeds);
    }

    /// <summary>
    /// Derives a stable <see cref="Guid"/> for a seed child row (an alias keyed on its text, or a
    /// source feed keyed on its <c>type|url</c>) from its identifying tuple so that re-seeding upserts the same
    /// row rather than creating a new one. The Id is the MD5 hash of the canonical string
    /// <c>$"{companyId}|{kind}|{normalizedValue}"</c> (the value normalized by trim + lower-invariant)
    /// reinterpreted as a 16-byte Guid. MD5 is used purely as a fast non-cryptographic hash to obtain
    /// a deterministic 128-bit value, not for security.
    /// </summary>
    private static Guid DeterministicGuid(Guid companyId, string kind, string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        var canonical = $"{companyId}|{kind}|{normalized}";
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(bytes);
    }
}
