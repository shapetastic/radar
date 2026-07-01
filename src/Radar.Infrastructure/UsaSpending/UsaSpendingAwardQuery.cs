namespace Radar.Infrastructure.UsaSpending;

/// <summary>
/// A typed request the collector hands the reader for one recipient: the fuzzy
/// <see cref="SearchText"/> (<c>recipient_search_text</c>), the recent-activity time window
/// (<see cref="StartDate"/>/<see cref="EndDate"/> as <c>YYYY-MM-DD</c> strings, start clamped no earlier
/// than the API's <c>2007-10-01</c> floor), the mutually-exclusive award-type group
/// (<see cref="AwardTypeCodes"/>, default the contracts group A/B/C/D), and the page
/// <see cref="Limit"/> (max 100). The reader serializes this into the fixed request body shape.
/// </summary>
internal sealed record UsaSpendingAwardQuery(
    string SearchText,
    string StartDate,
    string EndDate,
    IReadOnlyList<string> AwardTypeCodes,
    int Limit);
