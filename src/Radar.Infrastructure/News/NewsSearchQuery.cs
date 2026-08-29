namespace Radar.Infrastructure.News;

/// <summary>
/// A typed request spec 81's collector hands the reader for one company: the precise
/// <see cref="QueryPhrase"/> (the exact company name/phrase, URL-encoded into the Google News RSS
/// <c>q=</c> parameter), the page <see cref="MaxRecords"/> (clamped by the reader, applied by taking the first
/// N parsed items — Google News RSS has no <c>maxrecords</c> parameter), and <see cref="EnglishOnly"/> (when
/// set, the reader appends the <c>hl=en-US&amp;gl=US&amp;ceid=US:en</c> locale params to pin English/US
/// coverage; when clear, it omits them so Google News applies its default locale). Kept minimal and
/// reader-relevant; collector-level pacing/sequencing lands in spec 81.
/// <para>
/// <b>Spec 198 §1</b> adds <see cref="RecencyWindowDays"/> as a TRAILING component: when positive the
/// reader appends a <c>when:{n}d</c> term to the phrase before URL-encoding it, bounding the response to
/// the last n days; when <c>0</c> (the record default, and what every pre-198 construction expresses) the
/// request URL is byte-identical to the pre-198 one. The collector decides the value PER FEED — a company
/// whose first collection this is stays unfiltered (§2) — so this is a per-request value rather than a copy
/// of the configured option.
/// </para>
/// </summary>
internal sealed record NewsSearchQuery(
    string QueryPhrase,
    int MaxRecords,
    bool EnglishOnly,
    int RecencyWindowDays = 0);
