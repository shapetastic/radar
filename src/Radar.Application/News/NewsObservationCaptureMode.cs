namespace Radar.Application.News;

/// <summary>
/// How a <see cref="NewsObservationRecord"/> was captured (spec 177). The three modes are deliberately
/// distinguishable in every downstream query, because they carry DIFFERENT epistemic weight: only
/// <see cref="ProspectiveRss"/> proves what Radar actually observed at the time; the other two are honest
/// reconstructions that must never be mistaken for point-in-time knowledge.
/// </summary>
public enum NewsObservationCaptureMode
{
    /// <summary>
    /// Captured live from the Google News RSS read during a collection pass — the headline, description and
    /// provenance exactly as the provider supplied them, at the instant Radar retrieved them. This is the
    /// only mode a later backtest may treat as point-in-time knowledge.
    /// </summary>
    ProspectiveRss = 0,

    /// <summary>
    /// Migrated from accrued raw <c>NewsArticle</c> evidence collected BEFORE spec 177 existed: headline,
    /// publisher, landing URL and the original collection instant were genuinely persisted then, so
    /// <c>FirstObservedAtUtc</c> honestly carries the original <c>CollectedAtUtc</c> — but the RSS
    /// description was discarded at the time and is <c>null</c> forever. Headline-only knowledge.
    /// </summary>
    LegacyHeadlineOnly,

    /// <summary>
    /// Produced by the explicit retrospective URL fetch: a saved landing URL re-visited through the safe
    /// content reader long after publication. <c>RetrievedAtUtc</c>/<c>FirstObservedAtUtc</c> are the ACTUAL
    /// fetch instant — never the publication or original collection time — because whatever the URL returns
    /// months later cannot establish what was knowable historically. Useful for prompt development and
    /// source-availability measurement only.
    /// </summary>
    RetrospectiveUrlFetch,
}
