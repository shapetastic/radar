namespace Radar.Application.Efficacy.Attention;

/// <summary>
/// The values AD-16 PRECOMMITTED, held as code constants rather than configuration (spec 169).
/// <para>
/// <b>This is the whole point of a pre-commitment.</b> A declared horizon, minimum N or failure threshold that
/// an operator can tune between runs is not declared at all — it is a knob that can be turned until the answer
/// improves, which is exactly the unfalsifiability failure AD-16's anti-unfalsifiability clause exists to
/// prevent. Changing any value here is an AMENDMENT to AD-16, recorded with its reason, that invalidates
/// comparisons across the change.
/// </para>
/// </summary>
public static class AttentionArrivalScreen
{
    /// <summary>
    /// The first eligible primary-screen as-of date, pinned by AD-16's 2026-08-03 amendment: the first whole
    /// UTC calendar day unambiguously past 60 days from the first post-spec-160 baseline run
    /// (<c>PipelineRunRecord 7f28ca48-5cb3-4646-8d57-56baf1e482e1</c>, <c>2026-07-30T08:07:19.5804397Z</c>,
    /// whose 60-day seam falls DURING 2026-09-28). Whole-day rather than instant so eligibility never depends
    /// on the intraday drift of a once-daily job.
    /// </summary>
    public static DateOnly FirstEligibleAsOfDateUtc { get; } = new(2026, 9, 29);

    /// <summary>AD-16 §3: h = 21 calendar days, with NO exit tolerance (an attention window that ends early is simply missing possible events).</summary>
    public static TimeSpan Horizon { get; } = TimeSpan.FromDays(21);

    /// <summary>AD-16 §7: at least 20 eligible companies per as-of date, counted AFTER the cohort exclusion.</summary>
    public const int MinimumCompaniesPerDate = 20;

    /// <summary>AD-16 §7: the median δ screen requires at least 20 eligible dates; below it the status is <c>Pending</c>.</summary>
    public const int MinimumEligibleDates = 20;

    /// <summary>
    /// The maximum tolerated interval between consecutive complete coverage checkpoints (and between an
    /// interval endpoint and its nearest checkpoint). 36 hours accommodates ordinary drift in a once-daily job
    /// without treating a MISSED day as covered. It is a collection-cadence rule, not a shortened outcome:
    /// evidence is still counted through the exact endpoint, and there is no price-style exit tolerance.
    /// </summary>
    public static TimeSpan MaximumCheckpointGap { get; } = TimeSpan.FromHours(36);

    /// <summary>AD-16 §7: the primary arm.</summary>
    public const string PrimaryStrategyName = "disclosure-led-v11";

    /// <summary>AD-16 §7: the matched-budget formula control, reported as a diagnostic and never screened on.</summary>
    public const string ControlStrategyName = "disclosure-led-v10-control";

    /// <summary>
    /// The fixed <c>baseline-*</c> arms whose ρ is retained for spec 155's later joint-support gate. They are
    /// reported only; they cannot alter AD-16's status here.
    /// </summary>
    public static IReadOnlyList<string> BaselineStrategyNames { get; } =
    [
        "baseline-earnings-only",
        "baseline-activity-only",
        "baseline-media-only",
    ];
}

/// <summary>
/// The ONLY part of the attention-arrival evaluation that is composition-dependent rather than precommitted:
/// which COLLECTOR names produce third-party <c>MediaAttention</c>, and which of those supplies the spec-169
/// per-company coverage contract.
/// <para>
/// It exists because collector names are Infrastructure facts (<c>RadarCollectorNames</c>) and
/// <c>Radar.Application</c> must not reference Infrastructure or <c>IConfiguration</c>. The composition root
/// resolves them and hands them across already validated — the same config→Application boundary
/// <c>StrategyComparisonOptions</c> and <c>ReplayPlan</c> use.
/// </para>
/// </summary>
public sealed class AttentionArrivalOptions
{
    public AttentionArrivalOptions(
        string attentionCollector, IReadOnlyList<string> thirdPartyAttentionCollectors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attentionCollector);
        ArgumentNullException.ThrowIfNull(thirdPartyAttentionCollectors);

        AttentionCollector = attentionCollector;
        ThirdPartyAttentionCollectors =
            [.. thirdPartyAttentionCollectors.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)];

        if (!ThirdPartyAttentionCollectors.Contains(attentionCollector, StringComparer.Ordinal))
        {
            // A supported collector missing from the capability set would make the "is another third-party
            // attention producer enabled?" guard nonsense — it would fire on the supported collector itself.
            throw new ArgumentException(
                $"The supported attention collector '{attentionCollector}' must also appear in the "
                    + "third-party attention collector set.",
                nameof(thirdPartyAttentionCollectors));
        }
    }

    /// <summary>
    /// The ONE collector whose coverage the evaluator can prove — <c>newssearch</c> in the live profile. Its
    /// per-company coverage rows are what turn a publisher count of zero into a VALID zero rather than an
    /// unobserved window.
    /// </summary>
    public string AttentionCollector { get; }

    /// <summary>
    /// Every collector capable of producing third-party <c>MediaAttention</c> evidence, ordinally ordered. If
    /// an ENABLED collector in this set is not <see cref="AttentionCollector"/>, the whole evaluation is
    /// Unavailable under <c>UnsupportedAttentionCollector</c>: its signals would enter an outcome whose
    /// coverage cannot be proved, and silently mixing them in is the failure AD-16 §5 forbids.
    /// </summary>
    public IReadOnlyList<string> ThirdPartyAttentionCollectors { get; }
}
