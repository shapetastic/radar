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
    /// <para>
    /// Spec 147 added a second, orthogonal fact to it: <c>collectors=&lt;csv&gt;;</c> is what this run is
    /// CONFIGURED with, and a trailing <c>collection=none-this-pass;</c> marks a pass that ran no collector
    /// (spec 144's standalone <c>score</c>). The two used to be conflated onto one empty CSV, so a score pass
    /// recorded "no collectors" over evidence seven collectors had genuinely gathered. A pass that did
    /// collect renders exactly the pre-147 string, byte for byte.
    /// </para>
    /// </summary>
    string CollectionProvenance();

    /// <summary>
    /// The same enabled-collector set as <see cref="CollectionProvenance"/>, but as the ordered distinct
    /// NAMES rather than an escaped descriptor string (spec 146) — so a
    /// <c>radar-formula-v9</c> collector channel can record which of its declared collectors actually RAN,
    /// as opposed to ran-and-found-nothing.
    /// <para>
    /// It must be the SAME projection the descriptor is built from, never a second independently-resolved
    /// answer: "what the snapshot says was collected" and "what the channel provenance says ran" cannot be
    /// allowed to disagree. Like <see cref="CollectionProvenance"/> it is recorded provenance and is hashed
    /// into <b>nothing</b>.
    /// </para>
    /// <para>
    /// <b>What "did not run" actually means (spec 147, §4 — stated plainly because it is weaker than it
    /// looks).</b> <c>ScoringStrategyFactory</c> validates every v9 channel collector against THIS list at
    /// startup and refuses to build any engine if one is missing, and <c>ScoringEngine</c> then hands the very
    /// same list to the formula as <c>ScoringInput.EnabledCollectors</c>. So in any composed run that started
    /// successfully, <c>ChannelBreakdown.CollectorsNotRun</c> is <b>structurally empty</b> — in EVERY mode,
    /// not just <c>score</c> — and a channel scoring 0 always means "this window holds no signals whose
    /// evidence that collector retrieved". It is NOT an outage signal, and it never was: a collector that was
    /// registered and then failed every fetch is indistinguishable here from one that found nothing. Since
    /// spec 147 the vocabulary is the CONFIGURED set in every mode, so a <c>score</c> pass no longer inverts
    /// this (before it, every declared collector read as "did not run", which was the exact opposite of the
    /// truth). Collection HEALTH lives in the collection summary and the run record, not here.
    /// </para>
    /// </summary>
    IReadOnlyList<string> EnabledCollectors();
}
