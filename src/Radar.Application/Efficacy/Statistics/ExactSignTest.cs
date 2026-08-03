namespace Radar.Application.Efficacy.Statistics;

/// <summary>Why the sign-test diagnostic does not exist for a block set.</summary>
public enum SignTestUndefinedReason
{
    /// <summary>It exists; not a degeneracy.</summary>
    None = 0,

    /// <summary>Every delta is exactly zero, so the effective N is zero and no tail exists to double.</summary>
    NoNonZeroDeltas,
}

/// <summary>
/// The exact two-sided sign test over one baseline's purged deltas — a DIAGNOSTIC only, never a substitute
/// for the order-statistic interval gate.
/// <para>
/// Exact zeros are dropped from THIS diagnostic's effective N only (the standard sign-test convention: a zero
/// carries no sign information), and the exclusion is reported rather than silent — the median interval keeps
/// every delta, zeros included, so the two statistics deliberately answer over slightly different N when zeros
/// exist.
/// </para>
/// </summary>
/// <param name="PValue">The exact two-sided p-value (see <see cref="ExactSignTest.Compute"/> for the convention).</param>
/// <param name="EffectiveN">Non-zero deltas only — the N the p-value was computed over.</param>
/// <param name="ZeroDeltasDropped">How many exact zeros were excluded from <see cref="EffectiveN"/>.</param>
public sealed record SignTestResult(
    bool IsDefined,
    double PValue,
    int EffectiveN,
    int PositiveDeltas,
    int NegativeDeltas,
    int ZeroDeltasDropped,
    SignTestUndefinedReason Reason)
{
    public static SignTestResult Undefined(int zeroDeltasDropped, SignTestUndefinedReason reason) =>
        new(
            IsDefined: false,
            PValue: 0.0,
            EffectiveN: 0,
            PositiveDeltas: 0,
            NegativeDeltas: 0,
            ZeroDeltasDropped: zeroDeltasDropped,
            Reason: reason);
}

/// <summary>
/// The exact two-sided sign test at p = 0.5 (spec 155).
/// <para>
/// <b>Convention, stated so the number is verifiable:</b> the reported p-value is the DOUBLED SMALLER EXACT
/// TAIL, capped at 1 — <c>p = min(1, 2 · BinomialCdf(min(pos, neg); pos + neg, 0.5))</c>. This is the
/// standard exact two-sided convention for a symmetric null; it is conservative at the midpoint (a perfectly
/// balanced split reports p = 1 exactly).
/// </para>
/// <para>
/// Deterministic (AD-3) and outcome-agnostic: bare doubles in, an exact tail out.
/// </para>
/// </summary>
public static class ExactSignTest
{
    public static SignTestResult Compute(IReadOnlyList<double> deltas)
    {
        ArgumentNullException.ThrowIfNull(deltas);

        var positive = 0;
        var negative = 0;
        var zero = 0;
        foreach (var delta in deltas)
        {
            if (delta > 0.0)
            {
                positive++;
            }
            else if (delta < 0.0)
            {
                negative++;
            }
            else
            {
                // Exact zero (a genuinely tied pair of per-date rhos). NaN cannot occur here — deltas are
                // differences of defined Spearman coefficients — and would land in this branch's else-chain
                // by comparison semantics, so counting it as zero-signed is the conservative fallback.
                zero++;
            }
        }

        var effectiveN = positive + negative;
        if (effectiveN == 0)
        {
            return SignTestResult.Undefined(zero, SignTestUndefinedReason.NoNonZeroDeltas);
        }

        var smallerTail = ExactBinomial.CdfAtHalf(Math.Min(positive, negative), effectiveN);
        var pValue = Math.Min(1.0, 2.0 * smallerTail);

        return new SignTestResult(
            IsDefined: true,
            PValue: pValue,
            EffectiveN: effectiveN,
            PositiveDeltas: positive,
            NegativeDeltas: negative,
            ZeroDeltasDropped: zero,
            Reason: SignTestUndefinedReason.None);
    }
}
