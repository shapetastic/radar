namespace Radar.Application.Lifecycle;

/// <summary>The CLOSED evidence-status vocabulary (spec 184 §1). Descriptive only — never a verdict.</summary>
public enum StrategyEvidenceStatusKind
{
    /// <summary>No ranked outcome sample yet (or the evidence artifacts could not be read).</summary>
    Accruing = 0,

    /// <summary>The spec-140/183 leaderboard ranked this arm — always rendered WITH its numbers.</summary>
    Ranked = 1,

    /// <summary>The arm is under the precommitted AD-15 composite gate, which has not yet evaluated.</summary>
    GatePending = 2,

    /// <summary>The AD-15 composite gate evaluated and passed for this arm.</summary>
    GatePassed = 3,

    /// <summary>The AD-15 composite gate evaluated ON ITS MERITS and failed for this arm.</summary>
    GateFailed = 4,
}

/// <summary>
/// The leaderboard's descriptive numbers for a ranked arm (spec 140/183: out-of-sample Spearman rho of
/// opportunity score vs EXCESS forward return, with its Fisher-z 95% interval). Carried so
/// <see cref="StrategyEvidenceStatusKind.Ranked"/> can NEVER render without them, and so a gate status can
/// still show the descriptive numbers beside it (descriptive and confirmatory facts are orthogonal).
/// </summary>
public sealed record RankedEvidence(
    int Rank,
    double OutOfSampleRho,
    double Lower95,
    double Upper95,
    int Observations)
{
    /// <summary>
    /// True when the interval contains zero — rendered as the SENTENCE "no evidence of discrimination
    /// yet", never converted into a pass/fail verdict ahead of the precommitted gates.
    /// </summary>
    public bool CiSpansZero => Lower95 <= 0.0 && Upper95 >= 0.0;
}

/// <summary>
/// One strategy's computed evidence status (spec 184 §1): derived mechanically each run from artifacts that
/// already exist (the spec-140/183 leaderboard and the spec-155/170 paired AD-15 composite gate outputs).
/// Factory-only construction enforces the invariants structurally: <c>Ranked</c> requires its numbers, and
/// "evidence unavailable" is only ever an <c>Accruing</c> display — an unreadable artifact degrades the
/// display, it never hides the arm and never invents a gate state.
/// </summary>
public sealed record StrategyEvidenceStatus
{
    private StrategyEvidenceStatus(
        StrategyEvidenceStatusKind kind, bool evidenceUnavailable, RankedEvidence? ranked, string? detail)
    {
        Kind = kind;
        EvidenceUnavailable = evidenceUnavailable;
        Ranked = ranked;
        Detail = detail;
    }

    public StrategyEvidenceStatusKind Kind { get; }

    /// <summary>True when the artifacts could not be read: renders "Accruing (evidence unavailable)".</summary>
    public bool EvidenceUnavailable { get; }

    /// <summary>
    /// The descriptive leaderboard numbers. Non-null by construction when <see cref="Kind"/> is
    /// <see cref="StrategyEvidenceStatusKind.Ranked"/>; optionally present beside a gate status.
    /// </summary>
    public RankedEvidence? Ranked { get; }

    /// <summary>Free-form machine detail (e.g. the leaderboard drop reason or the gate reasons).</summary>
    public string? Detail { get; }

    public static StrategyEvidenceStatus Accruing(string? detail = null) =>
        new(StrategyEvidenceStatusKind.Accruing, evidenceUnavailable: false, ranked: null, detail);

    public static StrategyEvidenceStatus AccruingEvidenceUnavailable(string? detail = null) =>
        new(StrategyEvidenceStatusKind.Accruing, evidenceUnavailable: true, ranked: null, detail);

    public static StrategyEvidenceStatus RankedStatus(RankedEvidence ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked); // Ranked NEVER renders without its numbers (spec 184 §1)
        return new(StrategyEvidenceStatusKind.Ranked, evidenceUnavailable: false, ranked, detail: null);
    }

    public static StrategyEvidenceStatus GatePending(RankedEvidence? ranked, string? detail) =>
        new(StrategyEvidenceStatusKind.GatePending, evidenceUnavailable: false, ranked, detail);

    public static StrategyEvidenceStatus GatePassed(RankedEvidence? ranked, string? detail) =>
        new(StrategyEvidenceStatusKind.GatePassed, evidenceUnavailable: false, ranked, detail);

    public static StrategyEvidenceStatus GateFailed(RankedEvidence? ranked, string? detail) =>
        new(StrategyEvidenceStatusKind.GateFailed, evidenceUnavailable: false, ranked, detail);
}
