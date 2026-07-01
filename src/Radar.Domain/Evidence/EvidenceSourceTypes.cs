namespace Radar.Domain.Evidence;

/// <summary>
/// Static classification helpers over <see cref="EvidenceSourceType"/>. Kept in the Domain so both
/// scoring and any future consumer share one definition of what counts as third-party market attention.
/// </summary>
public static class EvidenceSourceTypes
{
    /// <summary>
    /// True when the source type represents <b>third-party market attention</b> (someone other than the
    /// company drawing attention to it) rather than the company's own disclosure. The market-attention
    /// set is an explicit whitelist: <see cref="EvidenceSourceType.NewsArticle"/>,
    /// <see cref="EvidenceSourceType.SocialMedia"/>, and <see cref="EvidenceSourceType.ConferenceMention"/>.
    /// Everything else (own press releases, filings, RSS feeds, blogs, transcripts, contracts, job
    /// postings, patents, regulatory/insider disclosures, local files, manual entries) is first-party
    /// and contributes nothing to measured attention.
    /// <para>
    /// The <c>radar-formula</c> spec also lists <c>MediaAttention</c> as third-party, but that is a
    /// <see cref="Signals.SignalType"/>, <b>not</b> an <see cref="EvidenceSourceType"/> — its
    /// contribution to attention reach is captured separately by the media-signal term in the formula.
    /// </para>
    /// Written as an explicit whitelist so first-party stays the safe default as the append-only
    /// <see cref="EvidenceSourceType"/> enum grows.
    /// </summary>
    public static bool IsThirdPartyAttentionSource(EvidenceSourceType sourceType) => sourceType switch
    {
        EvidenceSourceType.NewsArticle => true,
        EvidenceSourceType.SocialMedia => true,
        EvidenceSourceType.ConferenceMention => true,
        _ => false,
    };
}
