using Radar.Domain.Companies;

namespace Radar.Application.Pipeline;

/// <summary>
/// Stage 6 as an independently invokable unit (spec 144): score a supplied company universe at a supplied
/// as-of instant, once PER CONFIGURED STRATEGY (spec 137). It depends on <b>no collector</b>, no extractor,
/// no resolver and no AI seam — that is what makes a standalone <c>score</c> pass provably free of collection
/// side effects rather than free of them by convention.
/// <para>
/// There is exactly ONE scoring code path. The combined run, the standalone <c>score</c> pass and the spec-139
/// replay all reach <c>ScoringEngine.ScoreCompanyAsync</c>; this type is the shared stage-6 loop for the first
/// two, and replay drives the same engines through its own read-only, replay-scoped harness. A second copy
/// would drift and silently invalidate <c>replay ⊆ forward</c>.
/// </para>
/// </summary>
public interface IScoringPass
{
    /// <summary>
    /// Scores <paramref name="companies"/> at <paramref name="asOfUtc"/> with every configured strategy.
    /// The caller supplies the universe: the combined run reuses the list the collection pass already loaded
    /// (one repository read per run), and a standalone score pass loads it itself.
    /// </summary>
    Task<ScoringPassResult> RunAsync(
        IReadOnlyList<Company> companies, DateTimeOffset asOfUtc, CancellationToken ct);
}

/// <summary>
/// What stage 6 did: how many companies the PRIMARY strategy scored (the run record's established meaning of
/// "companies scored"), the strategy names in run order, and which of them was primary.
/// </summary>
/// <param name="ScoreSnapshotsNotPersisted">
/// Spec 193 §1: how many score snapshots this pass computed but could NOT durably persist. Counted across
/// ALL strategies, not just the primary — deliberately, and unlike <paramref name="CompaniesScored"/>, whose
/// established meaning is the primary strategy's company count. The reason the two differ: "how many
/// companies were scored this run" is a statement about the series the report renders, whereas "what did
/// this run fail to write" is a statement about the DISK, and a non-primary strategy's lost snapshot is just
/// as lost. Trailing + defaulted to 0, the truthful value for a pass that persisted everything.
/// </param>
/// <param name="ScoringConfigsNotPersisted">
/// Spec 201 §1: how many per-strategy effective scoring-config files (content-addressed, insert-if-new)
/// this pass could NOT durably persist. A snapshot still carries its fingerprint stamp either way; this
/// counts the stamps whose dereference target never landed. Trailing + defaulted to 0, the truthful value
/// for a pass that persisted every config it attempted.
/// </param>
public sealed record ScoringPassResult(
    int CompaniesScored,
    IReadOnlyList<string> Strategies,
    string PrimaryStrategy,
    int ScoreSnapshotsNotPersisted = 0,
    int ScoringConfigsNotPersisted = 0);
