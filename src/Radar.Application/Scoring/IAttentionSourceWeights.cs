namespace Radar.Application.Scoring;

/// <summary>
/// Per-publisher attention-breadth weighting for the scoring formula's reach term. Injected into
/// <see cref="RadarScoreFormulaV8"/> so the curated "what counts as genuine market notice" policy lives as
/// Infrastructure config data (AD-5) while the formula stays a pure function of its input plus this immutable
/// lookup (AD-3).
/// <para>
/// <b>Spec 196 §3 made <see cref="Resolve"/> the one authoritative matching operation and demoted
/// <see cref="WeightFor"/> to a projection of it.</b> Once the unknown default became the <c>Mill</c> weight
/// (spec 196 §1) a bare <see cref="double"/> stopped being able to distinguish an explicitly-classified mill
/// from an unclassified publisher — so a diagnostic built on <see cref="WeightFor"/> would either duplicate
/// the matching rules (and drift) or silently report every mill as unclassified, which would make it lie
/// about the exact thing it exists to expose. <see cref="WeightFor"/> is a default interface member here so
/// no implementation can make the score and the diagnostic disagree: there is ONE matching implementation
/// and two consumers.
/// </para>
/// </summary>
public interface IAttentionSourceWeights
{
    /// <summary>
    /// Resolve a third-party publisher SourceName against the curated tier map. Returns the matched tier's
    /// name and weight with <see cref="AttentionSourceResolution.IsExplicitlyMapped"/> true, or the
    /// configured unknown default with it false. Case-insensitive; a blank/null name is unclassified and
    /// returns the unknown default. Deterministic and side-effect free (AD-3).
    /// </summary>
    AttentionSourceResolution Resolve(string? sourceName);

    /// <summary>
    /// The attention-breadth weight for a third-party publisher SourceName, in [0,1].
    /// 1.0 = genuine market notice (Reuters, Bloomberg, WSJ, CNBC, AP, industry trades);
    /// low = wire distribution / algorithmic content-mill / aggregator (PR Newswire, MarketBeat, Zacks, ...);
    /// an UNKNOWN publisher returns the configured default (non-zero) so real coverage is never
    /// silently zeroed. Case-insensitive; blank/null returns the unknown default.
    /// <para>
    /// A THIN PROJECTION of <see cref="Resolve"/> — never a second matching implementation.
    /// </para>
    /// </summary>
    double WeightFor(string? sourceName) => Resolve(sourceName).Weight;

    /// <summary>
    /// An ordered, culture-invariant serialization of the effective publisher→weight entries plus the
    /// unknown default, for provenance / scoring-config fingerprinting only (read-only, additive). The
    /// tier map affects Attention output, so it is folded into the <c>ScoringConfigVersion</c> content
    /// fingerprint (AD-10) — two runs with different tier maps must not be judged comparable. The
    /// descriptor MUST be deterministic (stable ordering, culture-invariant number formatting; AD-3).
    /// <para>
    /// It carries publisher KEYS and WEIGHTS only, deliberately NOT tier NAMES: renaming a tier without
    /// changing any weight or any membership produces byte-identical scoring, and a stamp that moved for
    /// that would re-stamp a whole series for a cosmetic edit (spec 141 — a fingerprint records identity,
    /// not vocabulary).
    /// </para>
    /// </summary>
    string CanonicalDescriptor();
}
