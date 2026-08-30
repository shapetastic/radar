using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// Spec 203 §4: the RAW store reads one company's scoring needs at one as-of instant, materialised ONCE so N
/// strategy engines can score from the same inputs instead of each re-reading the same three stores.
/// Produced by <see cref="IScoringEngine.ReadCompanyAsync"/> and consumed by
/// <see cref="IScoringEngine.ScoreCompanyAsync(CompanyScoringReads, CancellationToken)"/>.
/// <para>
/// ONLY the reads are shared. Everything strategy-dependent — the per-strategy <c>SignalTypes</c> filter,
/// the legacy neutralization, the two supersedes, the media collapse and the formula — stays INSIDE each
/// engine, so a strategy's output is byte-identical to what it produced when it read the stores itself.
/// </para>
/// </summary>
/// <param name="CompanyId">The company the reads were taken for.</param>
/// <param name="WindowEndUtc">The as-of instant (the current window's inclusive end and the known-at bound).</param>
/// <param name="Window">
/// The window length the reads were sliced with. An engine REFUSES reads taken for a different window
/// (<see cref="ScoringOptions.Window"/>), because the previous/velocity window they carry would be the wrong
/// one and the score would be silently wrong. Every strategy engine shares one <see cref="ScoringOptions"/>
/// instance (<see cref="ScoringStrategyFactory"/>), so in a composed run this is always satisfied.
/// </param>
/// <param name="AllSignals">
/// The company's accrued signals exactly as <c>ISignalRepository.GetByCompanyAsync</c> returned them
/// (cross-run collapsed, deterministically ordered); the engine applies the window/known-at/Approved/type
/// predicates itself.
/// </param>
/// <param name="PreviousWindowSignals">
/// The activity-only previous/velocity window exactly as <c>ISignalFileStore.ReadApprovedInWindowAsync</c>
/// returned it for <c>(windowEnd − 2·Window, windowEnd − Window]</c> known as of <paramref name="WindowEndUtc"/>.
/// </param>
/// <param name="EvidenceById">
/// The evidence behind every signal that passes the window + known-at + Approved predicates — a SUPERSET of
/// what any strategy's type filter selects. A signal whose evidence could not be resolved is ABSENT from
/// this dictionary (never a null entry), so the engine's existing dropped-signal accounting runs identically.
/// </param>
public sealed record CompanyScoringReads(
    Guid CompanyId,
    DateTimeOffset WindowEndUtc,
    TimeSpan Window,
    IReadOnlyList<Signal> AllSignals,
    IReadOnlyList<Signal> PreviousWindowSignals,
    IReadOnlyDictionary<Guid, EvidenceItem> EvidenceById);
