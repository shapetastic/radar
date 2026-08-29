using Radar.Application.News;

namespace Radar.Application.NewsTyping;

/// <summary>
/// Per-company per-cohort typing coverage (spec 185 §5, split by spec 189 §2): whether every in-window
/// supplied-text observation for a company has a completed typing in one extractor cohort. The zero value is
/// DELIBERATELY the degraded state (the spec-182 convention): a consumer that never receives a computed value
/// must read "failed", never "complete". A deferred article (the spec-181 <c>MaxNewTypingsPerRun</c> cap) is
/// an untyped fact source, so "found no challenge" over a backlogged company is a weaker statement — and
/// says so.
/// <para>
/// <b>The computation precedence (spec 189 §2) is total and conservative</b>, and the generator applies it in
/// this order: (1) any in-window EXHAUSTED observation ⇒ <see cref="RetryExhausted"/>; (2) otherwise any
/// failure/refusal/unpersisted outcome in this pass ⇒ <see cref="RetryableFailure"/>; (3) otherwise any
/// eligible untyped observation ⇒ <see cref="Backlog"/>; (4) otherwise <see cref="Complete"/>. One
/// observation may remain in the artifact's <c>UntypedRemaining</c> population while ALSO explaining a
/// company-level <see cref="RetryableFailure"/>: the former is the disjoint population partition ("work still
/// eligible"), the latter is current-pass provenance ("why this company's read degraded today").
/// </para>
/// <para>
/// <b>Ordinals are frozen.</b> The two spec-189 values are APPENDED, so <c>Failed = 0</c>,
/// <c>Backlog = 1</c> and <c>Complete = 2</c> keep their numeric values. Nothing depends on the numbers —
/// every persisted record carries the string TOKEN (the shared file-store JSON options use
/// <c>JsonStringEnumConverter(allowIntegerValues: false)</c>, so an integer is REJECTED on read) — but the
/// ordinals stay put so the zero value remains the degraded one.
/// </para>
/// </summary>
public enum NewsTypingCompleteness
{
    /// <summary>
    /// LEGACY / UNCLASSIFIED degraded coverage, and the defensive zero value. Before spec 189 this single
    /// token meant BOTH "an attempt failed this pass" and "an observation exhausted its attempts", which are
    /// different facts with different remedies — so it is still READABLE (existing records hydrate unchanged
    /// and are never rewritten, AD-8) but is NEVER newly computed by the generator, which always knows which
    /// of <see cref="RetryableFailure"/> / <see cref="RetryExhausted"/> occurred.
    /// </summary>
    Failed = 0,

    /// <summary>At least one in-window observation for the company remains untyped (deferred by the per-run cap).</summary>
    Backlog,

    /// <summary>Every in-window supplied-text observation for the company has a completed typing in this cohort.</summary>
    Complete,

    /// <summary>
    /// Spec 189 §2: at least one of this company's observations had a provider/parse/validation failure, a
    /// refused attempt reservation or an outcome write that never persisted IN THIS PASS, and NO in-window
    /// observation has exhausted its attempt budget. The failed observation stays eligible, so a later run
    /// can still type it — this is a degraded read TODAY, not a permanent hole.
    /// <para>
    /// <b>SPEC 194 §3 — the failure set is WINDOW-SCOPED, matching exhaustion.</b> The computed value is
    /// <c>RetryableFailure</c> only when an IN-WINDOW <c>(ObservationId, PayloadHash)</c> is in this pass's
    /// retryable-failure set. Spec 189 deliberately kept the pre-189 pass-wide scope — degrading is the safe
    /// direction — and recorded the resulting asymmetry (a failure on a LEGACY-BACKLOG observation degraded
    /// an otherwise-complete in-window company, and an out-of-window observation spending its final attempt
    /// read retryable rather than exhausted) as a token-only limitation. It stopped being token-only when
    /// spec 191 made completeness a SCORING input: <c>NewsTrajectorySignalRules.StrengthFor</c> pays a
    /// complete-typing bonus, so a false non-<see cref="Complete"/> value silently costs a strength point on
    /// the judgment-derived signal. Completeness is a claim about THE WINDOW, so it is now derived from the
    /// window alone, through the same exhaustion-excluding predicate the decomposition artifact's per-company
    /// row uses — the token and the rendered row cannot disagree.
    /// </para>
    /// <para>
    /// An out-of-window backlog failure is NOT lost: it stays fully visible in the pass-wide reader summary
    /// and the lane accounting in the decomposition artifact, where it is a statement about the pass rather
    /// than about a company's window coverage. Exhaustion has been window-scoped since spec 186 §2, so the
    /// two now share one scope and the spec-189 asymmetry is gone rather than merely documented.
    /// </para>
    /// </summary>
    RetryableFailure,

    /// <summary>
    /// Spec 189 §2: at least one IN-WINDOW observation has spent all permitted attempts without a durable
    /// completed typing (spec 186 §2's bound, enforced by spec 187 §3's durable pre-call reservations). It is
    /// a PERMANENT hole for the current <c>(cohort, observation, payload)</c> — a later run will not drain it
    /// — so it takes precedence over every other state, <see cref="RetryableFailure"/> included.
    /// </summary>
    RetryExhausted,
}

/// <summary>
/// One validated fact joined back to its observation provenance — the lookup a downstream consumer (the
/// spec-185 judge) needs to resolve a family's <c>RepresentativeFactId</c> into the fact's typed content
/// without re-reading the durable typing store.
/// </summary>
public sealed record NewsTypingFactRef(
    NewsTypingValidatedFact Fact,
    Guid ObservationId,
    Guid? CompanyId,
    NewsObservationCaptureMode CaptureMode);

/// <summary>
/// One extractor cohort's view of one typing pass (spec 185 §5): the checkpoint families this pass built
/// (spec 186 §4's WINDOW PROJECTION — durable full-history ids, in-window representatives and metadata, so
/// every <c>RepresentativeFactId</c> resolves in <see cref="NewsTypingCohortRunResult.FactsById"/>),
/// the fact lookup behind them, the per-company typing-completeness map over the window, the stage-1
/// fact-drop count (the extraction side of the extraction-vs-judgment error split), and the spec-186 §2
/// count of observations whose typing attempts are EXHAUSTED (they left selection — visible, never silent).
/// That count is PASS-WIDE (window and backlog alike, since an exhausted backlog article is a permanent
/// cost fact), while the decomposition artifact reports its own per-company IN-WINDOW count and only an
/// in-window exhaustion degrades a company's completeness above.
/// <para>
/// <c>ReservedWithoutOutcome</c> (spec 187 §3) is the pass-wide count of this cohort's durable pre-call
/// attempt reservations holding no linked outcome record — a hosted call whose result was never persisted
/// (crash, cancellation, or an outcome write that returned <c>false</c>). It conservatively consumed an
/// attempt, so it is surfaced rather than silently folded into backlog: a reservation-or-outcome storage
/// failure must never read as ordinary deferred work.
/// </para>
/// <para>
/// <c>RetrySelected</c>, <c>CandidatePrioritySelected</c> and <c>GeneralSelected</c> (spec 187 §2, completed
/// by spec 189 §3) are this cohort's PASS-WIDE lane split: how many hosted calls went to the bounded FIFO
/// retry lane, how many went round-robin to the companies this run was about to judge, and how many flowed
/// back to the global window/backlog queue. The three lanes are disjoint, so no observation is counted
/// twice — and the retry count is reported explicitly because without it a 100 + 99 split against a 200-call
/// budget reads as an unused slot rather than as the one retry it actually was. Diagnostics only — nothing
/// here changes typing content, validation, cohort identity or fact-family membership.
/// </para>
/// Exposes the generator's existing in-memory join instead of adding a read seam to the write-only family
/// snapshot store.
/// </summary>
public sealed record NewsTypingCohortRunResult(
    NewsTypingReaderIdentity Reader,
    IReadOnlyList<FactFamilyRecord> Families,
    IReadOnlyDictionary<Guid, NewsTypingFactRef> FactsById,
    IReadOnlyDictionary<Guid, NewsTypingCompleteness> TypingCompletenessByCompany,
    int FactsDroppedInWindow,
    int RetryExhausted,
    int ReservedWithoutOutcome = 0,
    int CandidatePrioritySelected = 0,
    int GeneralSelected = 0,
    int RetrySelected = 0);

/// <summary>
/// The typed outcome of one typing pass (spec 185 §5), returned by <see cref="INewsTypingGenerator"/> so the
/// stage-2 judge can consume the SAME families/facts this pass checkpointed — never a re-read, never a
/// second family build. <c>null</c> from the generator means the pass failed or produced nothing consumable.
/// Carries the run's archive-capture provenance (fail-closed: <c>null</c> = unproven) so downstream
/// completeness dimensions come from the same evaluation this pass recorded.
/// </summary>
/// <param name="FamilySnapshotsNotPersisted">
/// Spec 202 §2: how many per-cohort fact-family checkpoint snapshots this pass could NOT durably persist
/// (the store's <c>WriteAsync</c> returned <c>false</c>). The in-memory families on each cohort are the
/// judge's input regardless — this counts what is missing from the ACCRUED store, not from this run.
/// Trailing + NULLABLE: <c>null</c> means no checkpoint write was attempted (no reader ran), never a
/// fabricated 0.
/// </param>
public sealed record NewsTypingRunResult(
    Guid? RunId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    Guid? NewsObservationBatchId,
    IReadOnlyList<NewsTypingCohortRunResult> Cohorts,
    int? FamilySnapshotsNotPersisted = null);
