using System.Globalization;

namespace Radar.Application.Efficacy.Claims;

/// <summary>
/// The CLOSED code vocabulary of <see cref="Ad15GateReason"/> (spec 170). The pre-170 price reasons were not a
/// closed set — two of them interpolated baseline names and one composed a count — so the closure lives here:
/// the CODE is closed and the variable parts ride in their own fields.
/// </summary>
public static class Ad15GateReasonCodes
{
    /// <summary>
    /// The version of the gate-rule / reason-code VOCABULARY (spec 186 §3). It is an input to the semantic
    /// gate-verdict identity (<c>GateVerdictIdentity</c>): the same evidence judged under a different rule
    /// vocabulary is a DIFFERENT verdict, and an operating-call override bound to the old one must not
    /// silently keep applying. Bump it whenever a code is added/removed/re-meant or the merit split below
    /// changes.
    /// </summary>
    public const string VocabularyVersion = "ad15-gate-reasons-v1";

    // ---- price-side codes (spec 155, migrated verbatim) ------------------------------------------------
    public const string NoPredeclaredPrimary = "no-predeclared-primary-strategy";
    public const string NoPrecommittedBoundary = "no-precommitted-evaluation-boundary";
    public const string NoBaselines = "no-baselines";
    public const string EmptyIntersection = "empty-intersection";
    public const string NoEligibleBlocks = "no-eligible-blocks";
    public const string InsufficientPurgedBlocks = "insufficient-purged-blocks";
    public const string MedianPairedDeltaNotPositive = "median-paired-delta-not-positive";
    public const string IntervalLowerBoundNotPositive = "interval-lower-bound-not-positive";

    // ---- AD-16 attention-prerequisite codes (spec 170) -------------------------------------------------
    public const string Ad16ScreenNotCalculated = "ad16-screen-not-calculated";
    public const string Ad16ScreenUnavailable = "ad16-screen-unavailable";
    public const string Ad16ScreenPending = "ad16-screen-pending";
    public const string Ad16ScreenInvalid = "ad16-screen-invalid";

    /// <summary>Every valid code, in a fixed declaration order. The constructor validates against this set.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        NoPredeclaredPrimary,
        NoPrecommittedBoundary,
        NoBaselines,
        EmptyIntersection,
        NoEligibleBlocks,
        InsufficientPurgedBlocks,
        MedianPairedDeltaNotPositive,
        IntervalLowerBoundNotPositive,
        Ad16ScreenNotCalculated,
        Ad16ScreenUnavailable,
        Ad16ScreenPending,
        Ad16ScreenInvalid,
    ];

    /// <summary>
    /// The codes that mean the gate evaluated ON ITS MERITS and came out negative — i.e. a real VERDICT of
    /// failure. THE one definition (spec 186 §3): both <c>StrategyEvidenceStatusCalculator</c> (which sees
    /// the rendered reason text off the artifact) and <c>GateVerdictIdentity</c> (which sees the structured
    /// reasons) read this list, so "is there a verdict?" cannot be answered two different ways.
    /// </summary>
    public static IReadOnlyList<string> MeritFailureCodes { get; } =
    [
        MedianPairedDeltaNotPositive,
        IntervalLowerBoundNotPositive,
    ];

    /// <summary>
    /// Every other code: the gate could not (yet) evaluate — accrual, missing predeclaration, or the AD-16
    /// prerequisite. Those are PENDING, never failed: "not enough data yet" must never read as a negative
    /// result. Kept as an explicit list (not "All minus merit") so the split is legible where it is used.
    /// </summary>
    public static IReadOnlyList<string> NonMeritCodes { get; } =
    [
        NoPredeclaredPrimary,
        NoPrecommittedBoundary,
        NoBaselines,
        EmptyIntersection,
        NoEligibleBlocks,
        InsufficientPurgedBlocks,
        Ad16ScreenNotCalculated,
        Ad16ScreenUnavailable,
        Ad16ScreenPending,
        Ad16ScreenInvalid,
    ];
}

/// <summary>
/// One STRUCTURED reason the AD-15 gate (price side or attention prerequisite) did not pass:
/// a closed machine-readable <see cref="Code"/>, plus the variable parts — the baseline the reason is about,
/// and free-form detail — in their own fields, never interpolated into the code (spec 170 §1.2).
/// <para>
/// <see cref="Render"/> reproduces the pre-170 human-readable text for the migrated price reasons EXACTLY
/// (<c>baseline 'x': median-paired-delta-not-positive</c>,
/// <c>baseline 'x': insufficient-purged-blocks (admitted 4, need at least 6 at 95%)</c>, bare codes for the
/// context reasons), so the artifact's rendered output does not regress while the record becomes parseable.
/// </para>
/// </summary>
public sealed record Ad15GateReason
{
    public Ad15GateReason(string code, string? baselineName = null, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (!Ad15GateReasonCodes.All.Contains(code, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{code}' is not a valid AD-15 gate-reason code. The vocabulary is CLOSED — variable parts "
                    + "(baseline names, counts) belong in BaselineName/Detail, never in the code. Valid codes: "
                    + string.Join(", ", Ad15GateReasonCodes.All)
                    + ".",
                nameof(code));
        }

        Code = code;
        BaselineName = baselineName;
        Detail = detail;
    }

    /// <summary>The closed machine-readable code (one of <see cref="Ad15GateReasonCodes.All"/>).</summary>
    public string Code { get; }

    /// <summary>The baseline the reason is about, for the per-baseline price reasons; otherwise null.</summary>
    public string? BaselineName { get; }

    /// <summary>Free-form human detail (e.g. the admitted-block count); otherwise null.</summary>
    public string? Detail { get; }

    /// <summary>
    /// The human-readable text, culture-invariant and byte-stable (AD-3). Preserves the pre-170 rendering of
    /// every migrated price reason exactly; the new prerequisite reasons render as
    /// <c>code (detail)</c>.
    /// </summary>
    public string Render()
    {
        if (BaselineName is { } baseline)
        {
            return Detail is { } detail
                ? string.Create(CultureInfo.InvariantCulture, $"baseline '{baseline}': {Code} ({detail})")
                : string.Create(CultureInfo.InvariantCulture, $"baseline '{baseline}': {Code}");
        }

        return Detail is { } bareDetail
            ? string.Create(CultureInfo.InvariantCulture, $"{Code} ({bareDetail})")
            : Code;
    }
}
