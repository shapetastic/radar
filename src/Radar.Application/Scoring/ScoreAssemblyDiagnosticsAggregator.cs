using Microsoft.Extensions.Logging;

namespace Radar.Application.Scoring;

/// <summary>
/// THE one definition of how <see cref="ScoreAssemblyDiagnostics"/> is pooled and rendered for an operator
/// (spec 197 §3). Both production callers of <see cref="IScoringEngine.ScoreCompanyAsync"/> — the forward /
/// standalone <c>ScoringPass</c> and the read-only <c>ReplayRunner</c> — route through this type rather than
/// keeping two copies of the arithmetic and the wording, so moving the Warning out of the shared engine
/// cannot make one caller quieter or more optimistic than the other.
/// <para>
/// <b>THE POPULATION IS LABELLED HONESTLY, AND THAT IS THE LOAD-BEARING PART.</b> One signal is evaluated
/// once per strategy, so a count summed across engines is a count of signal-evaluation INCIDENCES, not of
/// globally distinct signals — reporting it as the latter would over-state the problem by the strategy count.
/// Every rendered line therefore carries its incidence total beside the number of affected strategy-company
/// evaluations, the number of DISTINCT companies and the number of DISTINCT strategies (and, for replay, the
/// number of distinct as-of instants). For the same reason the per-evaluation distinct-evidence counts are
/// rendered as an explicit "sum of per-evaluation distinct-evidence-id counts" and never as a global
/// distinct-evidence total: this type never sees the ids, only the per-evaluation cardinalities.
/// </para>
/// <para>
/// <b>The two categories are separate lines, and their axes are never pooled</b> (see
/// <see cref="ScoreAssemblyDiagnostics"/>): current window vs previous/velocity window, and accrued spec-191
/// residue vs malformed judgment-signal envelope. A malformed envelope means a CURRENT writer is producing
/// unverifiable provenance and must never disappear inside the expected legacy residue.
/// </para>
/// <para>
/// AT MOST ONE WARNING PER CATEGORY PER OPERATION. An operation with nothing to report logs nothing at all,
/// so the healthy path's log is byte-identical to a run in which these transforms never fired.
/// </para>
/// <para>
/// <b>Spec 224 adds a THIRD category, insider owner resolution, at Information</b>, carrying two axes that
/// are stated separately and never pooled: directional insider filings whose reporting owner could not be
/// resolved and so were passed through unbucketed by <c>InsiderActivityCollapse</c>, and filings that WERE
/// bucketed on an owner name recovered from the evidence title (accrued pre-224 evidence lacks the owner
/// metadata; <c>InsiderActivityMetadata.TryRead</c> falls back to the collector's fixed title shape). The line
/// is emitted when EITHER axis is non-zero. Information rather than Warning because, until pre-224 evidence
/// ages out of the window, a non-zero title-derived count is the expected state, not a fault; it should then
/// fall to zero, and a title-derived count that persists past one full window means the collector stopped
/// writing the owner keys. The fallback resolves every accrued shape, so a non-zero UNRESOLVED count is the
/// unexpected axis (an unknown title shape, the anonymous placeholder, an ambiguous name, or a non-Form 4
/// envelope) and the line's wording says so.
/// </para>
/// <para>
/// <b>Spec 226 adds a FOURTH category, at Information</b>: companies under a recognised pending acquisition for
/// which <c>CorporateActionSupersede</c> rewrote nothing in either window, named by id, with the "filing had
/// in-window signals but none rewritable" sub-case named separately. An enabled rule that did no work says so.
/// </para>
/// </summary>
/// <remarks>
/// Not thread-safe: both callers drive their scoring loops serially, and the aggregate must be deterministic
/// (AD-3). It carries no scoring meaning whatsoever — it is a reporting projection of transient state, hashed
/// into nothing and persisted nowhere.
/// </remarks>
public sealed class ScoreAssemblyDiagnosticsAggregator
{
    private readonly string _operation;
    private readonly bool _reportAsOfAxis;

    private readonly CategoryTally _unresolvedEvidence = new();
    private readonly CategoryTally _neutralization = new();
    private readonly CategoryTally _insiderOwnerResolution = new();
    private readonly CategoryTally _acquisitionSupersedeIdle = new();
    private readonly HashSet<Guid> _acquisitionFilingNotRewritableCompanies = [];

    private long _unresolvedSignalIncidences;
    private long _insiderOwnerUnresolvedIncidences;
    private long _insiderOwnerFromTitleIncidences;
    private long _unresolvedDistinctEvidencePerEvaluationSum;
    private long _currentLegacy;
    private long _currentMalformed;
    private long _previousLegacy;
    private long _previousMalformed;

    /// <param name="operation">
    /// What the aggregate is a statement ABOUT, rendered verbatim as the line's subject (for example
    /// <c>"Scoring pass"</c> or <c>"Replay 'label'"</c>). The caller owns this because only the caller knows
    /// the scope it just completed.
    /// </param>
    /// <param name="reportAsOfAxis">
    /// True when the operation spans several as-of instants (replay), so the distinct as-of count is a
    /// meaningful fourth axis. A forward pass scores at exactly one instant, where the axis would be a
    /// constant 1 and therefore noise.
    /// </param>
    public ScoreAssemblyDiagnosticsAggregator(string operation, bool reportAsOfAxis = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        _operation = operation;
        _reportAsOfAxis = reportAsOfAxis;
    }

    /// <summary>True when at least one recorded evaluation dropped a signal for unresolvable evidence.</summary>
    public bool HasUnresolvedEvidence => _unresolvedEvidence.Evaluations > 0;

    /// <summary>True when at least one recorded evaluation neutralized a direction.</summary>
    public bool HasNeutralization => _neutralization.Evaluations > 0;

    /// <summary>True when at least one recorded evaluation passed through an insider filing it could not bucket (spec 224).</summary>
    public bool HasInsiderOwnerUnresolved => _insiderOwnerUnresolvedIncidences > 0;

    /// <summary>True when at least one recorded evaluation bucketed an insider filing on a title-derived owner (spec 224 amendment).</summary>
    public bool HasInsiderOwnerFromTitle => _insiderOwnerFromTitleIncidences > 0;

    /// <summary>
    /// Records ONE strategy-company evaluation. A healthy evaluation contributes to no axis, so an operation
    /// over an unaffected store leaves the aggregate empty and silent.
    /// </summary>
    /// <param name="strategyName">
    /// The strategy's name, or null for the synthesised primary/legacy composition — normalised through
    /// <see cref="ScoreSeriesKey"/>'s rule so the distinct-strategy axis counts series, not spellings.
    /// </param>
    public void Record(
        string? strategyName, Guid companyId, DateTimeOffset asOfUtc, ScoreAssemblyDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var strategy = ScoreSeriesKey.For(strategyName);

        if (diagnostics.HasUnresolvedEvidence)
        {
            _unresolvedEvidence.Add(strategy, companyId, asOfUtc);
            _unresolvedSignalIncidences += diagnostics.UnresolvedEvidenceSignalCount;
            _unresolvedDistinctEvidencePerEvaluationSum +=
                diagnostics.UnresolvedEvidenceDistinctEvidenceCount;
        }

        if (diagnostics.HasNeutralization)
        {
            _neutralization.Add(strategy, companyId, asOfUtc);
            _currentLegacy += diagnostics.CurrentWindowLegacyInheritanceNeutralized;
            _currentMalformed += diagnostics.CurrentWindowMalformedEnvelopeNeutralized;
            _previousLegacy += diagnostics.PreviousWindowLegacyInheritanceNeutralized;
            _previousMalformed += diagnostics.PreviousWindowMalformedEnvelopeNeutralized;
        }

        if (diagnostics.HasInsiderOwnerUnresolved || diagnostics.HasInsiderOwnerFromTitle)
        {
            _insiderOwnerResolution.Add(strategy, companyId, asOfUtc);
            _insiderOwnerUnresolvedIncidences += diagnostics.CurrentWindowInsiderOwnerUnresolved;
            _insiderOwnerFromTitleIncidences += diagnostics.CurrentWindowInsiderOwnerFromTitle;
        }

        if (diagnostics.HasRecognisedAcquisitionNothingRewritten)
        {
            _acquisitionSupersedeIdle.Add(strategy, companyId, asOfUtc);
            if (diagnostics.RecognisedAcquisitionFilingHadNoRewritableSignal > 0)
            {
                _acquisitionFilingNotRewritableCompanies.Add(companyId);
            }
        }
    }

    /// <summary>True when at least one evaluation's company had a recognised acquisition the supersede rewrote nothing for (spec 226).</summary>
    public bool HasRecognisedAcquisitionNothingRewritten => _acquisitionSupersedeIdle.Evaluations > 0;

    /// <summary>
    /// Emits AT MOST ONE Warning per category. Call exactly once, at the end of the operation, after every
    /// evaluation has been recorded.
    /// </summary>
    public void LogAggregates(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (HasUnresolvedEvidence)
        {
            logger.LogWarning(
                "{Operation}: {DroppedSignalIncidences} signal-evaluation incidence(s) were dropped because "
                    + "their evidence could not be resolved, across {AffectedEvaluations} affected "
                    + "strategy-company evaluation(s), {DistinctCompanies} distinct company/companies and "
                    + "{DistinctStrategies} distinct strateg(ies){AsOfAxis}. These are signal-evaluation "
                    + "INCIDENCES, not globally distinct signals: every strategy re-evaluates the same "
                    + "signal, so one unresolvable signal counts once per strategy that scored it. The "
                    + "per-evaluation distinct-evidence-id counts SUM to "
                    + "{DistinctEvidencePerEvaluationSum}, which is a sum of per-evaluation counts and is "
                    + "NOT a globally distinct evidence total. An unresolvable evidence chain is a real "
                    + "provenance defect (spec 145 heals evidence identity forward only, so the accrued "
                    + "residue does not go away); per-signal and per-evaluation detail stay at Debug on the "
                    + "scoring engine.",
                _operation,
                _unresolvedSignalIncidences,
                _unresolvedEvidence.Evaluations,
                _unresolvedEvidence.Companies.Count,
                _unresolvedEvidence.Strategies.Count,
                AsOfAxis(_unresolvedEvidence),
                _unresolvedDistinctEvidencePerEvaluationSum);
        }

        if (HasNeutralization)
        {
            logger.LogWarning(
                "{Operation}: neutralized {CurrentLegacyIncidences} accrued spec-191 inherited news "
                    + "direction(s) and {CurrentMalformedIncidences} unverifiable judgment-signal "
                    + "envelope(s) in the current window (and {PreviousLegacyIncidences} / "
                    + "{PreviousMalformedIncidences} in the previous/velocity window), across "
                    + "{AffectedEvaluations} affected strategy-company evaluation(s), {DistinctCompanies} "
                    + "distinct company/companies and {DistinctStrategies} distinct strateg(ies){AsOfAxis}. "
                    + "All four counts are signal-evaluation INCIDENCES, not globally distinct signals: "
                    + "every strategy re-evaluates the same signal. Those signals are scored as Neutral "
                    + "media attention because their direction was never grounded in the matched article; "
                    + "they stay on disk unchanged (append-only) and each current-window suppression is "
                    + "named on that signal's contribution reason. A non-zero unverifiable-envelope count "
                    + "is a CURRENT writer producing provenance that cannot be verified — a different and "
                    + "more urgent fact than the expected spec-191 residue, which should fall to zero as "
                    + "the accrued cohort ages out of the window.",
                _operation,
                _currentLegacy,
                _currentMalformed,
                _previousLegacy,
                _previousMalformed,
                _neutralization.Evaluations,
                _neutralization.Companies.Count,
                _neutralization.Strategies.Count,
                AsOfAxis(_neutralization));
        }

        if (HasInsiderOwnerUnresolved || HasInsiderOwnerFromTitle)
        {
            logger.LogInformation(
                "{Operation}: {InsiderOwnerUnresolvedIncidences} directional insider filing-evaluation "
                    + "incidence(s) could not be bucketed by {CollapseVersion} because no reporting-owner "
                    + "identity could be resolved (an envelope that is not a readable Form 4; no owner "
                    + "metadata and a title in no known collector shape or naming only the anonymous "
                    + "placeholder; or a name-only filing whose name appears beside two or more CIKs), and "
                    + "{InsiderOwnerFromTitleIncidences} WERE bucketed on an owner name recovered from the "
                    + "evidence title because the evidence predates the owner metadata (structured metadata "
                    + "wins wherever present), across {AffectedEvaluations} affected strategy-company "
                    + "evaluation(s), {DistinctCompanies} distinct company/companies and {DistinctStrategies} "
                    + "distinct strateg(ies){AsOfAxis}. These are signal-evaluation INCIDENCES, not globally "
                    + "distinct filings: every strategy re-evaluates the same signal. Each unresolved filing "
                    + "was scored as its own signal exactly as before spec 224 (nothing dropped) — it simply "
                    + "could not be collapsed with other filings by the same insider. The title fallback "
                    + "resolves every shape the Form 4 collector has written, so the unresolved count is "
                    + "EXPECTED to be near zero; the title-derived count is EXPECTED to be non-zero until "
                    + "pre-224 evidence ages out of the window and should then fall to zero — one that "
                    + "persists past one full window means the collector has stopped writing the owner keys "
                    + "and is a defect.",
                _operation,
                _insiderOwnerUnresolvedIncidences,
                InsiderActivityCollapse.Version,
                _insiderOwnerFromTitleIncidences,
                _insiderOwnerResolution.Evaluations,
                _insiderOwnerResolution.Companies.Count,
                _insiderOwnerResolution.Strategies.Count,
                AsOfAxis(_insiderOwnerResolution));
        }

        // Spec 226: ONE line per operation naming every company (by id, ordered) whose recognised pending
        // acquisition the corporate-action supersede rewrote nothing for, in either window. Information: the
        // usual cause is that the recognised filing has aged out of the window (or a strategy's type filter
        // excludes it), which is expected; the companies whose filing HAD in-window signals with no rewritable
        // read are named separately because that is the case to read.
        if (HasRecognisedAcquisitionNothingRewritten)
        {
            logger.LogInformation(
                "{Operation}: {SupersedeVersion} rewrote NOTHING for {DistinctCompanies} company/companies under a "
                    + "recognised pending acquisition ({CompanyIds}), across {AffectedEvaluations} "
                    + "strategy-company evaluation(s) and {DistinctStrategies} distinct strateg(ies){AsOfAxis}: no "
                    + "StrategicPartnership or CorporateAction read of the recognised filing was in either scoring "
                    + "window. Expected once the filing ages out of the window or where a strategy's signal-type "
                    + "filter excludes it. Of these, {NotRewritableCompanies} company/companies ({NotRewritableIds}) "
                    + "had signals over the recognised filing in a window but none of a rewritable type — that "
                    + "sub-case is the one to read.",
                _operation,
                CorporateActionSupersede.Version,
                _acquisitionSupersedeIdle.Companies.Count,
                string.Join(", ", _acquisitionSupersedeIdle.Companies.Order()),
                _acquisitionSupersedeIdle.Evaluations,
                _acquisitionSupersedeIdle.Strategies.Count,
                AsOfAxis(_acquisitionSupersedeIdle),
                _acquisitionFilingNotRewritableCompanies.Count,
                _acquisitionFilingNotRewritableCompanies.Count == 0
                    ? "none"
                    : string.Join(", ", _acquisitionFilingNotRewritableCompanies.Order()));
        }
    }

    /// <summary>
    /// The optional fourth axis, rendered as a whole clause so a forward pass's line carries no vestigial
    /// "over 1 as-of instant(s)" and a replay's line can never omit it.
    /// </summary>
    private string AsOfAxis(CategoryTally tally) =>
        _reportAsOfAxis ? $" over {tally.AsOfInstants.Count} as-of instant(s)" : string.Empty;

    /// <summary>The incidence axes one category tracks. Sets, so a repeat never inflates a distinct count.</summary>
    private sealed class CategoryTally
    {
        public int Evaluations { get; private set; }

        public HashSet<Guid> Companies { get; } = [];

        public HashSet<string> Strategies { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<DateTimeOffset> AsOfInstants { get; } = [];

        public void Add(string strategy, Guid companyId, DateTimeOffset asOfUtc)
        {
            Evaluations++;
            Companies.Add(companyId);
            Strategies.Add(strategy);
            AsOfInstants.Add(asOfUtc);
        }
    }
}
