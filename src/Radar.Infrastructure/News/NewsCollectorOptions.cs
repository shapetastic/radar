using Radar.Application.Scoring;

namespace Radar.Infrastructure.News;

/// <summary>
/// Reader-relevant options for the Google News RSS third-party market-attention source — Radar's alternative
/// to GDELT that is NOT per-IP throttled (keyless, no User-Agent required). Only the knobs THIS reader seam
/// needs live here: <see cref="MaxRecordsPerCompany"/> caps how many parsed items each company contributes,
/// and <see cref="EnglishOnly"/> is the default for whether coverage is restricted to English/US (spec 81
/// maps it onto each per-request <see cref="NewsSearchQuery.EnglishOnly"/>, which the reader honors by
/// appending the en-US locale params). The endpoint URL itself is owned solely by the reader
/// (<c>HttpNewsSearchReader</c>) — it is intentionally NOT duplicated here. Collector-level pacing now lives
/// here as <see cref="InterRequestDelay"/> (the <c>newssearch</c> collector is strictly sequential and paces
/// requests with it); the client-side title relevance filter and the <c>Radar:News</c> worker options bind
/// through to these fields. There is deliberately NO 429-retry knob: Google News RSS has no retry (the
/// reader returns <c>RateLimited</c> immediately).
/// <para>
/// <b>Spec 198 corrected this summary's other half.</b> It previously also claimed there is no
/// recency/timespan knob because "the endpoint exposes no recency parameter". That was FALSE, and the
/// undocumented operator was verified against the live endpoint on 2026-08-29 for the phrase
/// <c>Caterpillar Inc</c>: unfiltered
/// (<c>q=Caterpillar%20Inc&amp;hl=en-US&amp;gl=US&amp;ceid=US:en</c>) returned <b>100</b> items with the
/// oldest dated <c>Wed, 24 Jun 2026</c>, while the same query carrying <c>when:7d</c>
/// (<c>q=Caterpillar%20Inc%20when%3A7d&amp;…</c>) returned <b>66</b> items with the oldest dated
/// <c>Sun, 23 Aug 2026</c>. So <c>when:{n}d</c> demonstrably BOUNDS the response and does not silently
/// degrade to unfiltered. <see cref="RecencyWindowDays"/> is that knob; the failure posture if the operator
/// is ever withdrawn is "no improvement" (more, older items, handled exactly as today), never "no results".
/// </para>
/// </summary>
public sealed class NewsCollectorOptions
{
    /// <summary>
    /// Maximum parsed articles to collect per company per run (default 25). The reader clamps to a sane
    /// range. This is Radar's OWN effective/local retention limit — reaching it means Radar stopped
    /// retaining, never that the provider had no more to give (spec 190).
    /// </summary>
    public int MaxRecordsPerCompany { get; init; } = 25;

    /// <summary>Whether to restrict coverage to English/US (default true); the reader appends the en-US locale params to the request when set.</summary>
    public bool EnglishOnly { get; init; } = true;

    /// <summary>
    /// The recency window in days appended to the search phrase as a <c>when:{n}d</c> term (spec 198 §1).
    /// <c>0</c> disables the filter and reproduces the pre-198 unfiltered URL BYTE-FOR-BYTE; a negative
    /// value fails registration naming <c>Radar:News:RecencyWindowDays</c>. The default is
    /// <see cref="NewsQueryScoringIdentity.DefaultRecencyWindowDays"/> — THE one definition, shared with the
    /// Worker's own default and with the hashed scoring identity, so the value a run sends and the value the
    /// fingerprint records cannot drift.
    /// <para>
    /// <b>It is a hashed <c>ScoringConfigVersion</c> input</b> (via <c>NewsQueryScoringIdentity</c>): the
    /// query decides which evidence exists, so changing it changes <c>AttentionReach</c>,
    /// <c>OpportunityScore</c> and every rank. A company's FIRST collection is exempt and stays unfiltered
    /// (spec 198 §2) so seeding still acquires back history.
    /// </para>
    /// </summary>
    public int RecencyWindowDays { get; init; } = NewsQueryScoringIdentity.DefaultRecencyWindowDays;

    /// <summary>
    /// The pause between successive per-company requests (default 1s). The collector is strictly sequential,
    /// so this paces successive reads politely. Unlike GDELT, Google News RSS is NOT per-IP throttled (spec-80
    /// verified: back-to-back keyless requests succeed), so only a small polite pace is needed. Registration
    /// fails fast when negative.
    /// </summary>
    public TimeSpan InterRequestDelay { get; init; } = TimeSpan.FromSeconds(1);
}
