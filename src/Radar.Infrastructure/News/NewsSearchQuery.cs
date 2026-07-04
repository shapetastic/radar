namespace Radar.Infrastructure.News;

/// <summary>
/// A typed request spec 81's collector hands the reader for one company: the precise
/// <see cref="QueryPhrase"/> (the exact company name/phrase, URL-encoded into the Google News RSS
/// <c>q=</c> parameter), the page <see cref="MaxRecords"/> (clamped by the reader, applied by taking the first
/// N parsed items — Google News RSS has no <c>maxrecords</c> parameter), and <see cref="EnglishOnly"/> (when
/// set, the reader appends the <c>hl=en-US&amp;gl=US&amp;ceid=US:en</c> locale params to pin English/US
/// coverage; when clear, it omits them so Google News applies its default locale). Kept minimal and
/// reader-relevant; collector-level pacing/sequencing lands in spec 81.
/// </summary>
internal sealed record NewsSearchQuery(
    string QueryPhrase,
    int MaxRecords,
    bool EnglishOnly);
