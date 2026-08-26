using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.SignalExtraction;

/// <summary>
/// One admitted directional read of a news article (spec 191 §2): the direction and strength the signal
/// layer should carry, plus the MANDATORY provenance triple that lets a score walk back
/// signal → judgment → cited facts → observation → article. A read whose provenance cannot be recorded is
/// never produced — see <see cref="INewsDirectionalReadSource"/>.
/// <para>
/// <b>This record references Domain and BCL types ONLY, deliberately.</b> It is reachable from
/// <c>Radar.Application.SignalExtraction</c>, which two reflection guards forbid from reaching
/// <c>Radar.Application.News</c> (spec 177's acquisition-only boundary) and the transitive closure of
/// <c>Radar.Application.NewsRisk*</c> (spec 179 §10). Spec 191 makes the news read a scoring input through
/// exactly ONE seam and does not weaken either guard: the judgment/observation types stay on the far side
/// of this interface, and the trajectory arrives as an already-rendered display TOKEN rather than as an
/// enum the extractor would have to reach into the judgment subsystem to name.
/// </para>
/// </summary>
/// <param name="Direction">The signal direction to emit — never <see cref="SignalDirection.Neutral"/>; a non-directional read produces no <see cref="NewsDirectionalRead"/> at all.</param>
/// <param name="Strength">The signal strength, already within the domain range 1–10.</param>
/// <param name="ObservationId">The matched point-in-time news observation (spec 177).</param>
/// <param name="JudgmentId">The admitted stage-2 judgment record (spec 185).</param>
/// <param name="JudgmentCohortKey">The judgment's stage-2 cohort key — the prospectively designated presentation cohort.</param>
/// <param name="TrajectoryToken">The judge's business-trajectory display token (<c>improving</c> / <c>deteriorating</c>).</param>
public sealed record NewsDirectionalRead(
    SignalDirection Direction,
    int Strength,
    Guid ObservationId,
    Guid JudgmentId,
    string JudgmentCohortKey,
    string TrajectoryToken);

/// <summary>
/// The ONE seam through which the deterministic signal extractor learns that Radar has actually READ a news
/// article (spec 191). It mirrors the SHAPE of <c>Radar.Application.Filings.IDirectionalFilingSignalSource</c>
/// — an optional, per-run-prepared, AI-derived input to the deterministic extractor — and is likewise OPT-IN:
/// a composition that does not register it leaves <see cref="KeywordSignalExtractor"/> byte-identical to its
/// pre-spec-191 behaviour.
/// <para>
/// ⚠ <b>The precedent is the SHAPE only, NOT the fingerprint contribution.</b> The filing seam additionally
/// carries <c>ScoringDescriptor()</c>, which <c>SignalSourceDescriptor</c> folds into
/// <c>ScoringConfigVersion</c> as the <c>ai=</c> segment — so enabling the AI filing path or changing its
/// reading model re-stamps automatically. <b>This seam has no such member and contributes to NO</b>
/// <b>fingerprint.</b> Two runs differing only in <c>Radar:NewsResearch:Judgment:Enabled</c>, in the judge
/// model, in the designated <c>PresentationCohort</c>, or in the strength constants of
/// <c>NewsTrajectorySignalRules</c> therefore produce materially different news signals and stamp the
/// IDENTICAL <c>ScoringConfigVersion</c>: <c>StrategyIdentityGuard</c> cannot see the difference and
/// <c>ScoreSeriesKey</c> pools both cohorts into one series. Spec 191 deliberately did not build the
/// descriptor (recorded in CLAUDE.md's spec-191 bullet under out-of-scope); folding it is its own spec and
/// would move all four pins again.
/// </para>
/// <para>
/// Implementations must return <c>null</c> for anything they cannot fully justify: unjoined evidence, a
/// company with no admitted judgment, a <c>Mixed</c>/<c>Unknown</c> trajectory, or a read whose provenance
/// triple cannot be recorded. <c>null</c> means "Radar has not read this article" and the extractor falls
/// back to today's Neutral <see cref="SignalType.MediaAttention"/> signal.
/// </para>
/// </summary>
public interface INewsDirectionalReadSource
{
    /// <summary>
    /// Prepares the source for ONE run, at that run's captured <paramref name="asOfUtc"/> — the same instant
    /// the pass stamps as every signal's <c>CreatedAtUtc</c>. This is the per-run preparation the filing
    /// seam's <c>ProduceAsync(candidates, asOfUtc, ct)</c> already establishes, and it exists for two
    /// reasons:
    /// <list type="bullet">
    /// <item><b>Point-in-time honesty comes from the RUN, not from a second clock read.</b> The admission
    /// predicate is <c>judgment.CreatedAtUtc &lt;= asOfUtc</c> against this exact instant (spec 136).</item>
    /// <item><b>A long-running process runs the pipeline MORE THAN ONCE.</b> With
    /// <c>Radar:RunOnce=false</c> the Worker loops on a <c>PeriodicTimer</c>, and each run's post-pipeline
    /// judgment step writes new judgments. A source that indexed once per instance would never see them.
    /// Implementations must therefore rebuild when <paramref name="asOfUtc"/> differs from the last
    /// prepared value, and be a cheap no-op when it matches (idempotent within one run).</item>
    /// </list>
    /// Never throws for a missing/unreadable store; caller cancellation propagates.
    /// </summary>
    Task PrepareAsync(DateTimeOffset asOfUtc, CancellationToken ct);

    /// <summary>
    /// The admitted directional read for one <see cref="EvidenceSourceType.NewsArticle"/> evidence item, or
    /// <c>null</c>. <b>FAILS CLOSED before any <see cref="PrepareAsync"/>:</b> an unprepared source returns
    /// <c>null</c> and never builds an index implicitly, because an implicit build would silently invent an
    /// as-of instant of its own — exactly the hindsight the run-scoped bound exists to prevent. Never throws
    /// for a missing/unreadable store; caller cancellation propagates.
    /// </summary>
    Task<NewsDirectionalRead?> TryReadAsync(EvidenceItem evidence, CancellationToken ct);
}
