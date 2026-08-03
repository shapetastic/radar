namespace Radar.Application.Efficacy.Statistics;

/// <summary>
/// Exact binomial tail arithmetic at p = 0.5 — the ONE shared primitive under both the order-statistic
/// median interval and the sign-test diagnostic (spec 155).
/// <para>
/// <b>Outcome-agnostic on purpose.</b> This namespace deliberately imports nothing from
/// <c>Radar.Application.Prices</c> or <c>Radar.Application.Efficacy.Comparison</c>, so the AD-16 attention
/// evaluator can reuse the same interval and sign-test machinery over its publisher-count outcome without
/// dragging the price harness in.
/// </para>
/// <para>
/// <b>Exact and deterministic (AD-3).</b> Terms are accumulated by the Pascal-row recurrence
/// <c>C(n,i) = C(n,i−1) · (n−i+1)/i</c>, each term carrying the <c>2⁻ⁿ</c> scale from the start so no
/// intermediate value ever exceeds 1 — no factorials, no <c>Math.Gamma</c>, no overflow for any block count
/// this project can produce. Two runs over identical inputs are bit-identical.
/// </para>
/// </summary>
public static class ExactBinomial
{
    /// <summary>
    /// The largest trial count this helper accepts. Far beyond any purged block count Radar can accrue (a
    /// block costs a full forward horizon of calendar time), and comfortably inside the range where the
    /// leading <c>2⁻ⁿ</c> term is a normal double — the bound exists so a nonsense input fails loudly instead
    /// of silently degrading precision.
    /// </summary>
    public const int MaxTrials = 1000;

    /// <summary>
    /// <c>P(X ≤ successes)</c> for <c>X ~ Binomial(trials, 0.5)</c>, exactly:
    /// <c>Σ_{i=0}^{successes} C(trials, i) / 2^trials</c>.
    /// </summary>
    /// <param name="successes">The inclusive upper bound of the tail; must be in <c>[0, trials]</c>.</param>
    /// <param name="trials">The number of trials; must be in <c>[1, MaxTrials]</c>.</param>
    public static double CdfAtHalf(int successes, int trials)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(trials, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trials, MaxTrials);
        ArgumentOutOfRangeException.ThrowIfNegative(successes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(successes, trials);

        // term_i = C(trials, i) · 2^−trials, built up from term_0 = 2^−trials. Each multiplication stays a
        // ratio of small integers times an already-scaled term, so precision is preserved for the small n
        // (tens of blocks) this is used at.
        var term = Math.Pow(0.5, trials);
        var sum = term;
        for (var i = 1; i <= successes; i++)
        {
            term = term * (trials - i + 1) / i;
            sum += term;
        }

        // Guard the ≤ 1 contract against terminal-digit rounding; the exact sums used here are all < 1
        // unless successes == trials, where the true value is exactly 1.
        return Math.Min(sum, 1.0);
    }
}
