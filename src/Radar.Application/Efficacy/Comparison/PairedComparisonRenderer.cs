using System.Globalization;
using System.Text;

using Radar.Application.Efficacy.Claims;
using Radar.Application.Efficacy.Statistics;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// Pure, deterministic rendering of a <see cref="PairedStrategyComparison"/> plus its composite
/// <see cref="Ad15ClaimVerdict"/> as CSV (machine) + markdown (human), and the per-block CSV. Culture-
/// invariant, fixed precision, <c>\n</c> line endings, no embedded wall-clock — identical input yields
/// byte-identical output (AD-3), matching <see cref="StrategyLeaderboardRenderer"/>'s conventions.
/// <para>
/// <b>The limitation is rendered BESIDE every interval, never only in a footnote:</b> purging removes the
/// known mechanical forward-window overlap but cannot prove independence or stationarity across market
/// regimes, so the interval is conditional on that predeclared model.
/// </para>
/// <para>
/// <b>The claim gate is COMPOSITE (spec 170).</b> The "adding value" sentence is emitted ONLY for a
/// qualifying composite verdict — never for a price-side pass alone. A price-gate pass with an unmet AD-16
/// prerequisite states the price result, names the missing prerequisite, and says plainly that no claim is
/// licensed. When the prerequisite is met by a <c>Miss</c>, the Miss is stated in the same block, BEFORE the
/// licence sentence.
/// </para>
/// <para>
/// <b>Framing (AD-9).</b> The gate outcome is a statement about Radar's own scoring — whether the
/// predeclared primary adds value RELATIVE TO the predeclared baselines under AD-15's amended gate — never a
/// recommendation, a projection, or advice about any company, security or action.
/// </para>
/// </summary>
public sealed class PairedComparisonRenderer
{
    /// <summary>The one-line framing sentence both artifacts carry verbatim.</summary>
    public const string Framing =
        "Research statistic: whether the predeclared primary strategy's scores tracked the shared outcome more "
            + "closely than each predeclared baseline's, on identical companies, dates and outcomes. "
            + "Not a recommendation, not a projection, not financial advice. Radar reports; a human decides.";

    /// <summary>
    /// The model limitation rendered beside every interval — inline, so the interval can never be quoted
    /// without it.
    /// </summary>
    public const string IntervalLimitation =
        "conditional on the predeclared model: purging removes the known mechanical forward-window overlap "
            + "but cannot prove independence or stationarity across market regimes; ties make the "
            + "order-statistic interval conservative";

    // The pre-170 column set with `qualifiesUnderAd15` renamed to `satisfiesPriceGate` and `gateReasons`
    // carrying the COMPOSITE verdict's reasons (price texts unchanged, prerequisite code appended when
    // unmet), followed by the spec-170 additive columns. `observationsWithoutAsOfInstant` counts
    // OBSERVATIONS; `mismatchedAsOfInstantKeys` counts KEYS — the units differ deliberately and the names
    // carry them.
    private const string CsvHeader =
        "status,primaryStrategy,primaryPredeclared,firstEligibleAsOf,armsConsidered,baselinesCompared,"
            + "baseline,jointObservations,jointCompanies,jointDates,candidateDates,droppedDates,"
            + "developmentDates,inconsistentOutcomeObservations,purgedBlocks,medianPairedDelta,"
            + "intervalLower95,intervalUpper95,intervalCoverage,intervalReason,signTestP,signTestEffectiveN,"
            + "signTestZeroDeltasDropped,baselineClears,satisfiesPriceGate,gateReasons,"
            + "qualifiesUnderAd15,ad16ScreenOutcome,"
            + "eligibleJointObservations,eligibleJointCompanies,eligibleJointDates,"
            + "observationsWithoutAsOfInstant,mismatchedAsOfInstantKeys";

    private const string BlocksCsvHeader = "baseline,blockDate,companies,primaryRho,baselineRho,pairedDelta";

    public string RenderCsv(PairedStrategyComparison result, Ad15ClaimVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireConsistent(result, verdict);

        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append('\n');

        if (result.Baselines.Count == 0)
        {
            AppendContext(sb, result, status: "no-baselines");
            sb.Append(',');                                   // baseline: there is none
            AppendJoint(sb, result);
            // purgedBlocks .. baselineClears: empty — with no baseline there is no headline to misread.
            sb.Append(",,,,,,,,,,");
            AppendGate(sb, result, verdict);
            sb.Append('\n');
            return sb.ToString();
        }

        foreach (var baseline in result.Baselines)
        {
            AppendContext(sb, result, status: "baseline");
            sb.Append(CsvField.Escape(baseline.BaselineName)).Append(',');
            AppendJoint(sb, result);

            sb.Append(Int(baseline.AdmittedDeltas.Count)).Append(',');
            sb.Append(baseline.MedianDelta is { } m ? Delta(m) : string.Empty).Append(',');
            sb.Append(baseline.Interval.IsDefined ? Delta(baseline.Interval.Lower) : string.Empty).Append(',');
            sb.Append(baseline.Interval.IsDefined ? Delta(baseline.Interval.Upper) : string.Empty).Append(',');
            sb.Append(baseline.Interval.IsDefined ? Coverage(baseline.Interval.AchievedCoverage) : string.Empty)
                .Append(',');
            sb.Append(IntervalReasonToken(baseline.Interval.Reason)).Append(',');
            sb.Append(baseline.SignTest.IsDefined ? Delta(baseline.SignTest.PValue) : string.Empty).Append(',');
            sb.Append(Int(baseline.SignTest.EffectiveN)).Append(',');
            sb.Append(Int(baseline.SignTest.ZeroDeltasDropped)).Append(',');
            sb.Append(Bool(baseline.ClearsGate)).Append(',');
            AppendGate(sb, result, verdict);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The spec-170 per-block artifact (<c>strategy-paired-comparison-blocks.csv</c>): one row per
    /// (baseline, admitted block) carrying the block's as-of date, its joint company N and both arms' ρs
    /// beside the paired delta. A SEPARATE file, deliberately — the summary CSV is one homogeneous row per
    /// baseline and a <c>recordType</c> discriminator would break that assumption for every existing reader.
    /// </summary>
    public string RenderBlocksCsv(PairedStrategyComparison result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.Append(BlocksCsvHeader).Append('\n');

        var candidatesByDate = result.CandidateDates.ToDictionary(d => d.Date);
        foreach (var baseline in result.Baselines)
        {
            foreach (var delta in baseline.AdmittedDeltas)
            {
                var candidate = candidatesByDate[delta.Date];
                var baselineRho = candidate.Baselines.Single(x =>
                    string.Equals(x.BaselineName, baseline.BaselineName, StringComparison.Ordinal));

                sb.Append(CsvField.Escape(baseline.BaselineName)).Append(',');
                sb.Append(Date(delta.Date)).Append(',');
                sb.Append(Int(candidate.Companies)).Append(',');
                sb.Append(Delta(candidate.PrimaryRho)).Append(',');
                sb.Append(Delta(baselineRho.Rho)).Append(',');
                sb.Append(Delta(delta.Delta)).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static void AppendContext(StringBuilder sb, PairedStrategyComparison result, string status)
    {
        sb.Append(status).Append(',');
        sb.Append(CsvField.Escape(result.PrimaryStrategyName)).Append(',');
        sb.Append(Bool(result.PrimaryWasPredeclared)).Append(',');
        sb.Append(result.FirstEligibleAsOf is { } b ? Date(b) : string.Empty).Append(',');
        sb.Append(Int(result.ArmsConsidered)).Append(',');
        sb.Append(Int(result.Baselines.Count)).Append(',');
    }

    private static void AppendJoint(StringBuilder sb, PairedStrategyComparison result)
    {
        sb.Append(Int(result.JointSupport.Observations)).Append(',');
        sb.Append(Int(result.JointSupport.DistinctCompanies)).Append(',');
        sb.Append(Int(result.JointSupport.DistinctAsOfDates)).Append(',');
        sb.Append(Int(result.CandidateDates.Count)).Append(',');
        sb.Append(Int(result.DroppedDates.Count)).Append(',');
        sb.Append(Int(result.DevelopmentDateCount)).Append(',');
        sb.Append(Int(result.InconsistentOutcomeObservationsDropped)).Append(',');
    }

    private static void AppendGate(
        StringBuilder sb, PairedStrategyComparison result, Ad15ClaimVerdict verdict)
    {
        sb.Append(Bool(result.SatisfiesPriceGate)).Append(',');
        sb.Append(CsvField.Escape(string.Join("; ", verdict.Reasons.Select(r => r.Render())))).Append(',');
        sb.Append(Bool(verdict.Qualifies)).Append(',');
        sb.Append(Ad15ClaimGate.OutcomeToken(verdict.Prerequisite.Outcome)).Append(',');
        sb.Append(Int(result.EligibleJointSupport.Observations)).Append(',');
        sb.Append(Int(result.EligibleJointSupport.DistinctCompanies)).Append(',');
        sb.Append(Int(result.EligibleJointSupport.DistinctAsOfDates)).Append(',');
        sb.Append(Int(result.ObservationsWithoutAsOfInstant)).Append(',');
        sb.Append(Int(result.ObservationsWithMismatchedAsOfInstant));
    }

    public string RenderMarkdown(PairedStrategyComparison result, Ad15ClaimVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireConsistent(result, verdict);

        var o = result.Options;
        var sb = new StringBuilder();

        sb.Append("# Strategy vs strategy — paired, purged comparison (spec 155)\n\n");
        sb.Append(Framing).Append("\n\n");

        var exploratory = !result.PrimaryWasPredeclared || result.FirstEligibleAsOf is null;
        if (exploratory)
        {
            sb.Append("**Status: EXPLORATORY — no AD-15 claim is expressible from this artifact.**\n\n");
        }
        else
        {
            sb.Append("**Status: claim path evaluated against the precommitted boundary.**\n\n");
        }

        sb.Append("## Predeclaration\n\n");
        if (result.PrimaryWasPredeclared)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- Predeclared primary composite: **{Md(result.PrimaryStrategyName)}** (Radar:Efficacy:Comparison:PairedPrimaryStrategy).\n");
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"- **No primary was predeclared.** Radar:Efficacy:Comparison:PairedPrimaryStrategy is empty, so this run pairs the pipeline's primary strategy '{Md(result.PrimaryStrategyName)}' for information only. Only an arm named primary BEFORE its outcomes exist may use the AD-15 gate.\n");
        }

        if (result.FirstEligibleAsOf is { } boundary)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- Precommitted first eligible as-of date: **{Date(boundary)}** (immutable by convention — moving it after outcomes exist invalidates the claim family). Candidate dates before it are development data and never enter the claim interval; {result.DevelopmentDateCount} such date(s) here.\n");
        }
        else
        {
            sb.Append("- **No precommitted evaluation boundary** (no-precommitted-evaluation-boundary): Radar:Efficacy:Comparison:PairedFirstEligibleAsOfUtc is empty. A claim needs a fixed forward boundary recorded before its outcomes exist — the moving 70/30 split of the marginal leaderboard is descriptive and is not one. Everything below is exploratory.\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"- Arms considered: {result.ArmsConsidered}; baselines compared: {result.Baselines.Count}");
        if (result.BaselineNames.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", result.BaselineNames.Select(Md))).Append(')');
        }

        sb.Append(". Clearing every FIXED baseline is an intersection-union claim and needs no Bonferroni correction; picking the best of several composite arms after seeing results would — only the predeclared primary may use this gate, and every other arm stays exploratory.\n\n");

        sb.Append("## How to read this\n\n");
        sb.Append("- One delta per date per baseline: on each joint date, every arm's cross-sectional Spearman rho is computed against the SAME outcome ranks over the SAME companies, and the paired difference is rho_primary − rho_baseline. Companies are never pooled across dates.\n");
        sb.Append("- Arms are intersected on the EXACT scoring instant (the snapshot's WindowEndUtc), not the calendar date: after a partial rerun, two arms' same-day snapshots can represent different knowledge cutoffs, and pairing those would attribute to strategy difference what is actually a difference in what each arm could see. The calendar date is used only for block grouping, purging and the boundary. An observation with no recorded instant is excluded from the claim path (counted below), never date-paired as a fallback.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Purge: candidate dates are admitted greedily in ascending order, earliest first, keeping only dates whose nominal outcome window (D, D+{o.Comparison.ForwardHorizonDays}] does not overlap the last admitted window — so admitted dates are at least {o.Comparison.ForwardHorizonDays} calendar days apart. A skipped date is counted as overlapping-outcome-window, never silently discarded. There is no search over weekday, phase or offset.\n");
        sb.Append("- Daily candidate dates are NOT independent — adjacent dates reuse most of the same forward path. Only the purged subset enters the interval, and even those blocks are independent only under the predeclared model stated beside each interval.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Each date needs at least {o.MinimumCompaniesPerDate} joint companies; per-date rho is a coefficient, not a claim.\n");
        sb.Append("- The marginal leaderboard (strategy-leaderboard.md) remains available and is DESCRIPTIVE: it answers whether a strategy tracked its outcome at all, not whether it beat a comparator. This paired, purged comparison is the only result that can support the amended AD-15 claim.\n\n");

        sb.Append("## Support (a materially smaller intersection is a result, not a log message)\n\n");
        sb.Append("| strategy | marginal observations (companies × dates) | without forward price | partial window |\n");
        sb.Append("| --- | --- | ---: | ---: |\n");
        foreach (var marginal in result.MarginalSupports)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {Md(marginal.StrategyName)} | {SupportCell(marginal.Support)} | {marginal.ObservationsWithoutForwardPrice} | {marginal.ObservationsWithPartialWindow} |\n");
        }

        sb.Append('\n');

        if (result.PairwiseSupports.Count > 0)
        {
            sb.Append("Pairwise primary∩baseline intersections (diagnostic only — the claim path uses the joint intersection exclusively):\n\n");
            sb.Append("| baseline | pairwise observations (companies × dates) |\n");
            sb.Append("| --- | --- |\n");
            foreach (var pairwise in result.PairwiseSupports)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| {Md(pairwise.BaselineName)} | {SupportCell(pairwise.Support)} |\n");
            }

            sb.Append('\n');
        }

        sb.Append(CultureInfo.InvariantCulture, $"Joint intersection across the primary and every baseline (ALL history — this figure describes the dataset, never the claim): **{SupportCell(result.JointSupport)}**. Observations dropped because the forward outcome disagreed across arms: {result.InconsistentOutcomeObservationsDropped}.\n\n");

        if (result.FirstEligibleAsOf is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Eligible joint support (the CLAIM's support: joint observations on as-of dates at or after the precommitted boundary): **{SupportCell(result.EligibleJointSupport)}**.\n\n");
        }
        else
        {
            sb.Append("Eligible joint support (the CLAIM's support): **empty** — no boundary is precommitted, so no observation is claim-eligible; the all-history joint figure above is never a substitute for it.\n\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"Exact-instant pairing exclusions: {result.ObservationsWithoutAsOfInstant} observation(s) (unit: de-duped company-day observations) excluded from the claim path because the snapshot recorded no as-of instant — such points fail closed and are never date-paired; {result.ObservationsWithMismatchedAsOfInstant} (company, calendar-date) key(s) (unit: keys, not observations) present in two or more arms with differing as-of instants and therefore not paired.\n\n");

        sb.Append(CultureInfo.InvariantCulture, $"## Candidate dates ({result.CandidateDates.Count} usable, {result.DroppedDates.Count} dropped)\n\n");
        if (result.DroppedDates.Count > 0)
        {
            sb.Append("| date | reason | baseline |\n");
            sb.Append("| --- | --- | --- |\n");
            foreach (var dropped in result.DroppedDates)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| {Date(dropped.Date)} | {DropReasonToken(dropped.Reason)} | {(dropped.BaselineName is { } bn ? Md(bn) : "—")} |\n");
            }

            sb.Append('\n');
        }

        var candidatesByDate = result.CandidateDates.ToDictionary(d => d.Date);

        sb.Append(CultureInfo.InvariantCulture, $"## Purged blocks ({result.AdmittedBlocks.Count} admitted)\n\n");
        if (result.AdmittedBlocks.Count == 0)
        {
            sb.Append("_No block was admitted — there is nothing to infer from, and nothing is being claimed._\n\n");
        }
        else
        {
            sb.Append("| block date | companies | observed entry | observed exit |\n");
            sb.Append("| --- | ---: | --- | --- |\n");
            foreach (var block in result.AdmittedBlocks)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| {Date(block.Date)} | {candidatesByDate[block.Date].Companies} | {Date(block.ObservedEntry)} | {Date(block.ObservedExit)} |\n");
            }

            sb.Append("\nConsecutive admitted blocks' OBSERVED price intervals are verified non-overlapping (the harness throws otherwise), not merely their nominal windows. The companies column is each block's joint cross-section — the N its rho was computed over.\n\n");
        }

        sb.Append("## Paired results per baseline\n\n");
        if (result.Baselines.Count == 0)
        {
            sb.Append("_No baseline strategy is configured (no-baselines): there is nothing to pair against, so no comparison exists and nothing is being claimed. Configure the spec-154 `baseline-` control arms to make this comparison meaningful._\n\n");
        }

        foreach (var baseline in result.Baselines)
        {
            sb.Append(CultureInfo.InvariantCulture, $"### {Md(baseline.BaselineName)}\n\n");
            if (baseline.Interval.IsDefined)
            {
                sb.Append(CultureInfo.InvariantCulture, $"Purged median paired delta **{Delta(baseline.MedianDelta!.Value)}** over {baseline.AdmittedDeltas.Count} admitted block(s); exact two-sided 95% order-statistic interval **{Delta(baseline.Interval.Lower)} to {Delta(baseline.Interval.Upper)}** (order statistics k={baseline.Interval.LowerOrderStatistic}, achieved coverage {Coverage(baseline.Interval.AchievedCoverage)}; {IntervalLimitation}).\n\n");
            }
            else
            {
                sb.Append(CultureInfo.InvariantCulture, $"insufficient-purged-blocks: {baseline.AdmittedDeltas.Count} admitted block(s), but a finite exact two-sided 95% order-statistic interval needs at least 6. Confidence is not relaxed and no interval is published ({IntervalLimitation}).\n\n");
            }

            if (baseline.SignTest.IsDefined)
            {
                sb.Append(CultureInfo.InvariantCulture, $"Sign test (diagnostic only — never a substitute for the interval gate): p = {Delta(baseline.SignTest.PValue)} over {baseline.SignTest.EffectiveN} non-zero delta(s) ({baseline.SignTest.PositiveDeltas} positive, {baseline.SignTest.NegativeDeltas} negative); {baseline.SignTest.ZeroDeltasDropped} exact-zero delta(s) excluded from its effective N only — the interval keeps every delta.\n\n");
            }
            else if (baseline.AdmittedDeltas.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"Sign test (diagnostic only): undefined — every one of the {baseline.AdmittedDeltas.Count} admitted delta(s) is exactly zero.\n\n");
            }

            if (baseline.AdmittedDeltas.Count > 0)
            {
                sb.Append("| block date | companies | primary rho | baseline rho | paired delta |\n");
                sb.Append("| --- | ---: | ---: | ---: | ---: |\n");
                foreach (var delta in baseline.AdmittedDeltas)
                {
                    var candidate = candidatesByDate[delta.Date];
                    var baselineRho = candidate.Baselines.Single(x =>
                        string.Equals(x.BaselineName, baseline.BaselineName, StringComparison.Ordinal));
                    sb.Append(CultureInfo.InvariantCulture, $"| {Date(delta.Date)} | {candidate.Companies} | {Delta(candidate.PrimaryRho)} | {Delta(baselineRho.Rho)} | {Delta(delta.Delta)} |\n");
                }

                sb.Append('\n');
            }
        }

        AppendGateMarkdown(sb, result, verdict);

        return sb.ToString();
    }

    /// <summary>
    /// The composite AD-15 gate block. The order of its parts is load-bearing: the two halves are always
    /// stated first, an attention <c>Miss</c> that satisfies the prerequisite is stated BEFORE the licence
    /// sentence, and the licence sentence exists only under a qualifying COMPOSITE verdict.
    /// </summary>
    private static void AppendGateMarkdown(
        StringBuilder sb, PairedStrategyComparison result, Ad15ClaimVerdict verdict)
    {
        sb.Append("## AD-15 gate (composite: price result + AD-16 attention prerequisite)\n\n");
        sb.Append("AD-15's gate is COMPOSITE: the paired price result in this artifact is one half; the other is that AD-16's precommitted attention-arrival screen has actually been CALCULATED (calculated, not passed — a Miss satisfies the prerequisite and is stated here when it does). A price-side pass alone licenses no claim.\n\n");

        sb.Append(CultureInfo.InvariantCulture, $"- Price half — satisfies the price gate: **{YesNo(result.SatisfiesPriceGate)}**.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- AD-16 attention prerequisite: {PrerequisiteSentence(verdict.Prerequisite.Outcome)}.\n\n");

        if (verdict.Qualifies)
        {
            if (verdict.Prerequisite.Outcome == Ad16ScreenOutcome.Miss)
            {
                // BEFORE the licence sentence, in the same block — a reader must never see the positive
                // price verdict without the attention outcome beside it (spec 170 §1.2).
                sb.Append("**AD-16's precommitted attention-arrival screen returned `Miss` at its declared horizon.** The prerequisite requires the screen to be calculated, not passed, so the price-side licence below stands — but the Miss stands beside it and travels with any quotation of it.\n\n");
            }

            sb.Append(CultureInfo.InvariantCulture, $"**Qualifies under AD-15's amended gate: yes.** Against every predeclared baseline on the joint out-of-sample support, the purged median paired difference is positive and the exact 95% interval's lower bound is strictly greater than zero, and AD-16's precommitted attention-arrival screen has been calculated ({Ad15ClaimGate.OutcomeToken(verdict.Prerequisite.Outcome)}). The predeclared primary '{Md(result.PrimaryStrategyName)}' may therefore be described as adding value relative to these baselines under AD-15's gate — a statement about Radar's scoring, never about any company, security or action, and never a basis for one.\n");
        }
        else if (result.SatisfiesPriceGate)
        {
            sb.Append("**Satisfies the price half of AD-15's gate: yes — and still NO claim is licensed.** The AD-16 attention prerequisite is unmet, and the gate is composite. Reasons:\n\n");
            foreach (var reason in verdict.Reasons)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- {Md(reason.Render())}\n");
            }
        }
        else
        {
            sb.Append("**Qualifies under AD-15's amended gate: no.** Reasons:\n\n");
            foreach (var reason in verdict.Reasons)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- {Md(reason.Render())}\n");
            }
        }
    }

    /// <summary>One restrained sentence per prerequisite outcome; total, and every unmet state names its code.</summary>
    private static string PrerequisiteSentence(Ad16ScreenOutcome outcome) => outcome switch
    {
        Ad16ScreenOutcome.NotCalculated =>
            "**not calculated** (ad16-screen-not-calculated) — no attention-arrival screen result was supplied to this comparison (the generator is disabled or was not run), so the prerequisite is unmet",
        Ad16ScreenOutcome.Unavailable =>
            "**unavailable** (ad16-screen-unavailable) — the screen could not be evaluated (a configuration failure, per AD-16), so the prerequisite is unmet",
        Ad16ScreenOutcome.Pending =>
            "**pending** (ad16-screen-pending) — the screen ran but its minimum eligible dates have not accrued, so it is not yet calculated and the prerequisite is unmet",
        Ad16ScreenOutcome.Miss =>
            "**calculated — Miss** at the declared horizon. The prerequisite is met (AD-15 requires the screen to be calculated, not passed); the Miss is stated beside any licensed claim",
        Ad16ScreenOutcome.ClearsNecessaryScreen =>
            "**calculated — clears its necessary screen** (a necessary screen only, never proof of efficacy). The prerequisite is met",
        _ =>
            "**invalid** (ad16-screen-invalid) — the screen result could not be interpreted, so the prerequisite is unmet",
    };

    /// <summary>
    /// The verdict must be THE verdict for this result — computed from its price gate, not another run's. A
    /// mismatch is a wiring defect and must not render as a claim.
    /// </summary>
    private static void RequireConsistent(PairedStrategyComparison result, Ad15ClaimVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.SatisfiesPriceGate != result.SatisfiesPriceGate)
        {
            throw new InvalidOperationException(
                "The AD-15 claim verdict disagrees with the paired result's price gate "
                    + $"(verdict {verdict.SatisfiesPriceGate}, result {result.SatisfiesPriceGate}) — the "
                    + "verdict must be computed from THIS result's price half via Ad15ClaimGate.Evaluate.");
        }
    }

    private static string SupportCell(PairedSupport support) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{support.Observations} ({support.DistinctCompanies} × {support.DistinctAsOfDates})");

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Delta(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>Five decimals so the exact n=6 coverage 0.96875 renders exactly rather than rounded.</summary>
    private static string Coverage(double value) => value.ToString("0.00000", CultureInfo.InvariantCulture);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>Stable machine tokens — the enum name is the contract, not a localized phrase.</summary>
    internal static string DropReasonToken(PairedDateDropReason reason) => reason switch
    {
        PairedDateDropReason.TooFewCompanies => "too-few-companies",
        PairedDateDropReason.ConstantPrimary => "constant-primary",
        PairedDateDropReason.ConstantOutcome => "constant-outcome",
        PairedDateDropReason.ConstantBaseline => "constant-baseline",
        PairedDateDropReason.OverlappingOutcomeWindow => "overlapping-outcome-window",
        _ => "unknown",
    };

    private static string IntervalReasonToken(MedianIntervalUndefinedReason reason) => reason switch
    {
        MedianIntervalUndefinedReason.None => "defined",
        MedianIntervalUndefinedReason.InsufficientPurgedBlocks => "insufficient-purged-blocks",
        _ => "unknown",
    };

    // Escape the markdown table's cell separator so an exotic strategy name cannot break the table (the
    // shared convention with StrategyLeaderboardRenderer).
    private static string Md(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
