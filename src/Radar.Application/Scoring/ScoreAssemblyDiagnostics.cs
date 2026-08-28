namespace Radar.Application.Scoring;

/// <summary>
/// What ONE strategy-company scoring evaluation had to discard or correct while assembling its input
/// (spec 197 §3). TRANSIENT ORCHESTRATION STATE: it is never persisted, never a wire contract, never a cache
/// or cohort key, never an identity input and hashed into nothing. It exists so the fact travels to the
/// caller that can see the whole operation instead of being logged N × M times by an engine that can only
/// see one cell of the grid.
/// <para>
/// <b>Why it exists.</b> <c>ScoringEngine</c> is ONE STRATEGY, so its "one Warning per company"
/// (spec 145 for unresolved evidence, spec 194 §1.4 for the neutralized accrued directions) is really one
/// Warning per strategy × company. The live baseline run
/// <c>0b48b865-76b8-4485-996c-9b9139b694aa</c> emitted ~462 Warnings — 397 unresolved-evidence lines and 63
/// neutralization lines — burying the two genuine RSS transport failures an operator actually needed to see.
/// Nothing is silenced: every count the engine used to log still reaches the operator, aggregated by
/// <see cref="ScoreAssemblyDiagnosticsAggregator"/> at the pass boundary ("nothing may be discarded without
/// being counted").
/// </para>
/// <para>
/// <b>The axes are kept separate deliberately.</b> Current window and previous/velocity window are different
/// populations (only the current window builds contributions and evidence links — AD-6), and
/// accrued-legacy is different from malformed-envelope: the first is the KNOWN, expected spec-191 residue
/// ageing out of the window, the second means a CURRENT writer is producing provenance that cannot be
/// verified. Pooling them would let the urgent fact disappear inside the expected one.
/// </para>
/// </summary>
/// <param name="UnresolvedEvidenceSignalCount">
/// Signals dropped by THIS evaluation because <c>EvidenceId</c> resolved to no evidence item.
/// </param>
/// <param name="UnresolvedEvidenceDistinctEvidenceCount">
/// Distinct evidence ids behind those dropped signals, within THIS evaluation. Summing this across
/// evaluations yields a sum of per-evaluation counts, never a globally distinct total.
/// </param>
/// <param name="CurrentWindowLegacyInheritanceNeutralized">
/// Current-window accrued spec-191 inherited news directions scored as Neutral media attention.
/// </param>
/// <param name="CurrentWindowMalformedEnvelopeNeutralized">
/// Current-window judgment-signal envelopes whose grounding could not be verified, scored as Neutral.
/// </param>
/// <param name="PreviousWindowLegacyInheritanceNeutralized">
/// The same accrued-legacy axis over the previous/velocity window (activity-only; no links, by design).
/// </param>
/// <param name="PreviousWindowMalformedEnvelopeNeutralized">
/// The same malformed-envelope axis over the previous/velocity window.
/// </param>
public sealed record ScoreAssemblyDiagnostics(
    int UnresolvedEvidenceSignalCount,
    int UnresolvedEvidenceDistinctEvidenceCount,
    int CurrentWindowLegacyInheritanceNeutralized,
    int CurrentWindowMalformedEnvelopeNeutralized,
    int PreviousWindowLegacyInheritanceNeutralized,
    int PreviousWindowMalformedEnvelopeNeutralized)
{
    /// <summary>The healthy evaluation: nothing dropped, nothing neutralized.</summary>
    public static ScoreAssemblyDiagnostics None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>True when this evaluation dropped at least one signal for unresolvable evidence.</summary>
    public bool HasUnresolvedEvidence => UnresolvedEvidenceSignalCount > 0;

    /// <summary>True when this evaluation neutralized at least one direction, in EITHER window.</summary>
    public bool HasNeutralization =>
        CurrentWindowLegacyInheritanceNeutralized > 0
        || CurrentWindowMalformedEnvelopeNeutralized > 0
        || PreviousWindowLegacyInheritanceNeutralized > 0
        || PreviousWindowMalformedEnvelopeNeutralized > 0;

    /// <summary>True when this evaluation has anything at all to report.</summary>
    public bool HasAny => HasUnresolvedEvidence || HasNeutralization;
}
