namespace Radar.Infrastructure.Gdelt;

/// <summary>
/// A single parsed news article from a GDELT DOC 2.0 <c>ArtList</c> response (one record per
/// <c>articles[]</c> row). Raw metadata only — the collector synthesizes evidence Title/RawText from these
/// real fields and never fabricates article body text (the DOC <c>ArtList</c> mode returns no body).
/// <see cref="Url"/> is the stable landing page used for provenance and within-feed dedupe.
/// <see cref="SeenDate"/> is the instant GDELT first saw the article (parsed from the exact
/// <c>yyyyMMddTHHmmssZ</c> form, UTC); it is <see langword="null"/> when absent/unparseable, in which case
/// the collector still stamps <c>CollectedAt</c>. GDELT inserts spaces around punctuation in
/// <see cref="Title"/> (e.g. <c>"Inc . ( MRCY )"</c>) — that is cosmetic and kept as-is; the collector's
/// relevance filter whitespace-normalises before matching so a spaced ticker still matches.
/// </summary>
internal sealed record GdeltArticleItem(
    string Url,
    string Title,
    string Domain,
    DateTimeOffset? SeenDate,
    string Language,
    string SourceCountry);
