namespace Radar.Application.Collectors;

/// <summary>
/// Whether this process re-derives collector attribution for evidence that predates spec 146's recording
/// (spec 151). Registered as a singleton by the composition root; the <c>TryAddSingleton</c> default means an
/// unaware composition keeps the pre-151 behaviour exactly.
/// <para>
/// It exists as an OPTIONS type rather than a constructor flag because two unrelated types must agree on the
/// same answer: the resolver that performs the inference, and <c>SignalSourceDescriptor</c>, which records
/// that it was performed on every snapshot's <c>CollectionProvenance</c>. Reading one setting in one place
/// keeps the behaviour and its recorded provenance from drifting apart.
/// </para>
/// </summary>
public sealed class CollectorAttributionOptions
{
    /// <summary>
    /// <c>Radar:Scoring:InferLegacyCollectorAttribution</c>. <b>Default <c>false</c></b>, and that default is
    /// load-bearing: with inference off the attribution seam resolves exactly what spec 146 recorded, so
    /// scoring output is byte-identical to pre-151, <c>replay ⊆ forward</c> is untouched, and no
    /// already-produced score can move.
    /// <para>
    /// ⚠ <b>This must never become a silent fallback (spec 151 §4).</b> It exists to recover attribution that
    /// was deterministic at collection time and simply was not persisted — a bounded, historical gap. Forward
    /// collection records the real collector, and if it ever stops doing so that is a DEFECT which must
    /// surface as unattributed evidence, not be papered over by an inference that quietly looks correct.
    /// Enabling this permanently would convert a loud failure into a plausible-looking guess. It is therefore
    /// opt-in, marked on every snapshot it touches (<c>attribution=inferred-legacy;</c>), and marked again per
    /// signal in the v9 channel breakdown, so no artifact built on it can hide its provenance.
    /// </para>
    /// </summary>
    public bool InferLegacyAttribution { get; init; }
}
