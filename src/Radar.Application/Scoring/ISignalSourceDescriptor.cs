namespace Radar.Application.Scoring;

/// <summary>
/// Canonical, deterministic descriptor of the run's SIGNAL-PRODUCTION surface, split (spec 141) into the
/// TWO facts it used to weld together:
/// <list type="bullet">
/// <item><description>
/// <see cref="CanonicalDescriptor"/> — <b>strategy identity</b>: the deterministic extractor's rule-set
/// identity plus, when the opt-in AI directional-filing path is registered, that source's per-signal
/// magnitudes. This is the part folded into the <c>ScoringConfigVersion</c> content fingerprint (AD-10 as
/// amended by spec 141).
/// </description></item>
/// <item><description>
/// <see cref="CollectionProvenance"/> — <b>collection provenance</b>: the enabled evidence-collector set.
/// Recorded verbatim on every snapshot (<c>CompanyScoreSnapshot.CollectionProvenance</c>) and hashed into
/// <b>nothing</b>.
/// </description></item>
/// </list>
/// <para>
/// WHY THE SPLIT (spec 141). "What was collected on this run" and "what hypothesis produced this score" are
/// different facts with different lifetimes. Welding them into one hash meant enabling an eighth collector
/// re-stamped every strategy's identity — including strategies that consume none of its signal types and
/// whose scores were bit-for-bit identical — fragmenting the score series for no behavioural reason (17
/// distinct stamps over 851 live snapshots, the largest cohort ≈ 3 runs). The collector set is now
/// <i>recorded</i> alongside the score instead of <i>inside</i> its identity; nothing becomes unknowable,
/// because per-signal and per-evidence source attribution already names the collector behind each item
/// (AD-3 / the provenance invariant).
/// </para>
/// <para>
/// The <c>ai=</c> segment deliberately stays on the IDENTITY side. It is not a collector set: it carries the
/// AI directional-filing read's per-signal strength/novelty/min-confidence/model magnitudes, and the model
/// changes signal DIRECTION (spec 119) — two runs on different models produce genuinely different scores and
/// must never share a <c>ScoringConfigVersion</c>.
/// </para>
/// <para>
/// Both members are deterministic: stable ordering, culture-invariant, no clock/IO/randomness (AD-3).
/// </para>
/// </summary>
public interface ISignalSourceDescriptor
{
    /// <summary>
    /// STRATEGY IDENTITY — folded into the <c>ScoringConfigVersion</c> fingerprint. Contains the extractor
    /// rule-set identity and the optional AI directional-filing magnitudes; it does <b>NOT</b> contain the
    /// enabled-collector set (spec 141).
    /// </summary>
    string CanonicalDescriptor();

    /// <summary>
    /// COLLECTION PROVENANCE — the enabled-collector descriptor, recorded on every snapshot and hashed into
    /// nothing. Enabling or disabling a collector changes this value and <b>only</b> this value.
    /// </summary>
    string CollectionProvenance();
}
