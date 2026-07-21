namespace Radar.Infrastructure.Filings;

/// <summary>
/// Tunables for <see cref="DirectionalFilingSignalSource"/>: the confidence gate below which the AI read
/// produces no directional signal, the per-run cost cap, and the in-range Strength/Novelty constants each
/// emitted signal carries (they clear the deterministic reviewer floors). Validated at registration by
/// <c>AddDirectionalFilingSignals</c>.
/// </summary>
public sealed class DirectionalFilingSignalOptions
{
    /// <summary>Gate: an AI confidence below this yields no directional signal. In [0,1]. Default 0.6.</summary>
    public decimal MinConfidence { get; init; } = 0.6m;

    /// <summary>Cost cap: analyze at most this many filings per run. Must be &gt; 0. Default 5.</summary>
    public int MaxFilingsPerRun { get; init; } = 5;

    /// <summary>
    /// Signal strength constant (in-range [1,10]; clears the reviewer MinMaterialStrength floor). Default 8
    /// (spec 112): a confident, full-text directional earnings read is materially stronger than a generic
    /// keyword match, so it deliberately EXCEEDS the keyword extractor maximum of 6 and can lift Opportunity
    /// over the Investigate gate on a corroborated trajectory. A per-signal magnitude folded into the scoring
    /// fingerprint (spec 106), so tuning it re-stamps <c>ScoringConfigVersion</c> automatically. It applies
    /// symmetrically to Improving→Positive and Deteriorating→Negative reads (same field), so a confident
    /// guidance cut bites as hard as a raise lifts.
    /// </summary>
    public int Strength { get; init; } = 8;

    /// <summary>Signal novelty constant (in-range; clears the reviewer MinNovelty floor). Default 6.</summary>
    public int Novelty { get; init; } = 6;

    /// <summary>
    /// Per-run 429 circuit breaker: after this many CONSECUTIVE rate-limited (HTTP 429) earnings reads in a run,
    /// stop attempting the remaining filings this run (SEC's www.sec.gov host appears blocked). A success or a
    /// cache hit resets the count; a non-429 failure does not trip it. Default 2. Set to 0 to disable the breaker
    /// (unbounded — the pre-spec-107 behaviour), used for parity testing. Must not be negative.
    /// </summary>
    public int MaxConsecutiveRateLimited { get; init; } = 2;

    /// <summary>
    /// The earnings-read model identity in <c>provider:model</c> form (e.g.
    /// <c>openai:deepseek-ai/DeepSeek-V4-Flash</c>) — the SAME string the spec-118 analyzed-filing cache is
    /// scoped by, supplied by the Worker's composition root.
    /// <para>
    /// A <b>scoring-fingerprint input</b> (spec 119): the reading model changes signal <b>DIRECTION</b>, not just
    /// throughput — the 2026-07-21 A/B had llama3.1 read EOSE as <c>Improving 0.90</c> where DeepSeek-V4-Flash
    /// read the same release as <c>Mixed 0.85</c>. Leaving it out would let two runs with materially different
    /// directional signal sets share one <c>ScoringConfigVersion</c>, breaking the spec-69/95 comparability
    /// invariant and drawing the efficacy line as continuous across a real change. It is therefore folded into
    /// the descriptor <b>by value</b> (like the spec-95 collector set and the spec-96 insider tiers) — swapping
    /// the model re-stamps the fingerprint automatically, with no <c>_formula.Version</c> / <c>RuleSetVersion</c>
    /// bump.
    /// </para>
    /// <para>
    /// Default blank: the pre-spec-119 callers that do not supply it hash as "model not declared" rather than
    /// failing registration (it is a provenance/comparability label, never a behaviour switch — a blank identity
    /// changes nothing the source DOES). Only the AI-ON fingerprint is affected at all, because the descriptor is
    /// only folded in when this source is registered.
    /// </para>
    /// </summary>
    public string ModelIdentity { get; init; } = string.Empty;
}
