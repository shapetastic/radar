namespace Radar.Infrastructure.Sources;

/// <summary>
/// Internal JSON DTO for the company seed file: a root document with a <c>companies</c> array. All
/// members are nullable so that malformed or incomplete entries can be detected and skipped rather
/// than throwing.
/// </summary>
internal sealed record LocalFileCompanySeedDocument(
    IReadOnlyList<LocalFileCompanySeedEntry>? Companies);

/// <summary>
/// Internal JSON DTO for a single seed company plus its optional aliases. All members are nullable so
/// that entries missing a stable <c>id</c> or a <c>name</c> can be skipped (never fabricated).
/// </summary>
internal sealed record LocalFileCompanySeedEntry(
    string? Id,
    string? Name,
    string? LegalName,
    string? Ticker,
    string? Exchange,
    string? CountryCode,
    string? Sector,
    string? Industry,
    IReadOnlyList<string?>? Aliases,
    IReadOnlyList<LocalFileSourceFeed?>? SourceFeeds,
    IReadOnlyList<string?>? Themes);

/// <summary>
/// Internal JSON DTO for a single configured source feed on a seed company. All members are nullable
/// so that a feed missing its required <c>url</c> can be skipped (never fabricated).
/// </summary>
internal sealed record LocalFileSourceFeed(
    string? Type,
    string? Name,
    string? Url);
