namespace Radar.Application.Lifecycle;

/// <summary>
/// The raw FACTS the evidence-status computation consumes (spec 184 §1), read off artifacts that already
/// exist: the spec-140/183 leaderboard (<c>data/efficacy/strategy-leaderboard.csv</c>) and the spec-155/170
/// paired comparison with its AD-15 composite gate (<c>data/efficacy/strategy-paired-comparison.csv</c>).
/// Deliberately a dumb transport — the mapping onto <see cref="StrategyEvidenceStatus"/> lives in the pure
/// <see cref="StrategyEvidenceStatusCalculator"/> so it is testable without a file system.
/// <para>
/// Availability is per artifact and honest: an absent or unreadable artifact sets its flag false, which the
/// calculator degrades to "Accruing (evidence unavailable)" — the display degrades, the arm is never hidden.
/// </para>
/// </summary>
public sealed record EfficacyEvidenceFacts(
    bool LeaderboardAvailable,
    IReadOnlyList<LeaderboardStrategyFact> Leaderboard,
    bool PairedAvailable,
    PairedGateFact? Paired)
{
    /// <summary>Both artifacts unavailable — the all-unavailable degenerate case.</summary>
    public static EfficacyEvidenceFacts Unavailable { get; } = new(
        LeaderboardAvailable: false,
        Leaderboard: [],
        PairedAvailable: false,
        Paired: null);
}

/// <summary>
/// One strategy's row from the leaderboard artifact. <paramref name="Ranked"/> rows carry the out-of-sample
/// numbers; dropped rows carry the machine drop-reason token instead.
/// </summary>
public sealed record LeaderboardStrategyFact(
    string StrategyName,
    bool Ranked,
    RankedEvidence? Numbers,
    string? DropReason);

/// <summary>
/// The paired artifact's gate context: WHICH arm is under confirmatory test (the predeclared paired
/// primary), whether a precommitted boundary exists, the composite verdict, and the rendered gate-reason
/// text (the closed spec-170 codes appear verbatim inside it).
/// <para>
/// <paramref name="GateVerdictId"/> is the artifact's own SEMANTIC verdict identity (spec 186 §3,
/// <c>GateVerdictIdentity</c>) — the value a human operating call must bind to in order to override the
/// verdict. EMPTY means "no verdict identity is available": either the artifact expresses no verdict, or it
/// predates spec 186 and carries no <c>gateVerdictId</c> column at all. Both fail CLOSED — an empty id can
/// never match an override, so the gate default wins.
/// </para>
/// <para>
/// It deliberately REPLACES the pre-186 <c>ArtifactWrittenAtUtc</c> (the file's mtime): the efficacy
/// artifacts are rewritten every run, so an mtime-anchored override silently expired after one run, a
/// copy/restore had the same effect, and the answer was machine-dependent. No verdict consumer reads
/// filesystem metadata or compares timestamps any more.
/// </para>
/// </summary>
public sealed record PairedGateFact(
    string PrimaryStrategyName,
    bool PrimaryPredeclared,
    bool BoundaryDeclared,
    bool Qualifies,
    string GateReasons,
    string GateVerdictId);

/// <summary>
/// Reads the persisted efficacy artifacts into <see cref="EfficacyEvidenceFacts"/>. NEVER throws for a
/// missing/unreadable artifact — it degrades the availability flags instead (spec 184 §1: unreadable
/// evidence degrades the display, it never hides the arm and never fails the run).
/// </summary>
public interface IStrategyEvidenceFactsSource
{
    Task<EfficacyEvidenceFacts> ReadAsync(CancellationToken ct);
}

/// <summary>
/// The inert default: no artifacts readable. Every strategy then reads "Accruing (evidence unavailable)"
/// in a multi-strategy report, and nothing at all in a single-strategy one.
/// </summary>
public sealed class UnavailableStrategyEvidenceFactsSource : IStrategyEvidenceFactsSource
{
    public static UnavailableStrategyEvidenceFactsSource Instance { get; } = new();

    private UnavailableStrategyEvidenceFactsSource()
    {
    }

    public Task<EfficacyEvidenceFacts> ReadAsync(CancellationToken ct) =>
        Task.FromResult(EfficacyEvidenceFacts.Unavailable);
}
