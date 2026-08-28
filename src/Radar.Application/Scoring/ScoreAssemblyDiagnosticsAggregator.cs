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

    private long _unresolvedSignalIncidences;
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
    }

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
