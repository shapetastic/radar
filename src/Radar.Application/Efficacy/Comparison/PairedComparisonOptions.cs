namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// The resolved, already-validated knobs of the spec-155 paired, purged strategy comparison. Like
/// <see cref="StrategyComparisonOptions"/> these are plain resolved values: <c>IConfiguration</c> never
/// reaches <c>Radar.Application</c>, so the composition root binds <c>Radar:Efficacy:Comparison:Paired*</c>
/// and hands the result in.
/// </summary>
public sealed class PairedComparisonOptions
{
    /// <summary>
    /// The floor for the per-date cross-section: a cross-sectional Spearman ρ needs at least two companies to
    /// rank at all. Config may — and by default does — demand more, because a "cross-section" of two
    /// companies is a claim about very little.
    /// </summary>
    public const int MinimumCompaniesFloor = 2;

    public PairedComparisonOptions(
        string? configuredPrimaryStrategyName,
        DateOnly? firstEligibleAsOf,
        int minimumCompaniesPerDate,
        StrategyComparisonOptions comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        if (minimumCompaniesPerDate < MinimumCompaniesFloor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumCompaniesPerDate),
                minimumCompaniesPerDate,
                $"Radar:Efficacy:Comparison:PairedMinimumCompaniesPerDate must be at least {MinimumCompaniesFloor}: "
                    + "a per-date cross-sectional Spearman rho needs at least two companies to rank, and the "
                    + "config exists to demand MORE than the mathematical floor, never less.");
        }

        ConfiguredPrimaryStrategyName = configuredPrimaryStrategyName?.Trim() ?? string.Empty;
        FirstEligibleAsOf = firstEligibleAsOf;
        MinimumCompaniesPerDate = minimumCompaniesPerDate;
        Comparison = comparison;
    }

    /// <summary>
    /// The predeclared primary composite (from <c>Radar:Efficacy:Comparison:PairedPrimaryStrategy</c>),
    /// trimmed. EMPTY means no primary was predeclared: the comparison still runs against the pipeline's
    /// primary strategy, but the result is EXPLORATORY and the AD-15 gate can never pass — only the arm named
    /// primary BEFORE its outcomes exist may use the gate.
    /// </summary>
    public string ConfiguredPrimaryStrategyName { get; }

    /// <summary>
    /// The immutable claim boundary (from <c>Radar:Efficacy:Comparison:PairedFirstEligibleAsOfUtc</c>):
    /// only candidate dates at or after it may enter the claim interval; earlier dates are development data.
    /// <c>null</c> means no boundary was precommitted — the result is
    /// <c>NoPrecommittedEvaluationBoundary</c> and exploratory.
    /// <para>
    /// <b>IMMUTABLE BY CONVENTION</b> (the same rule spec 141 applies to a strategy's identity): the boundary
    /// must be recorded BEFORE its outcomes exist, and moving it afterwards invalidates the whole claim
    /// family — a boundary tuned after seeing deltas is the unfalsifiability failure AD-16's pre-commitment
    /// clause names. Neither this type nor any evaluator may DERIVE a boundary from observed data; the config
    /// hands it in, and its absence means no claim.
    /// </para>
    /// </summary>
    public DateOnly? FirstEligibleAsOf { get; }

    /// <summary>
    /// The minimum joint companies a candidate date needs for its cross-sectional ρs to be computed; a date
    /// below it is dropped as <c>TooFewCompanies</c>, named and counted.
    /// </summary>
    public int MinimumCompaniesPerDate { get; }

    /// <summary>
    /// The shared spec-140 knobs the paired path reuses — <see cref="StrategyComparisonOptions.ForwardHorizonDays"/>
    /// and <see cref="StrategyComparisonOptions.ExitToleranceDays"/> — held whole rather than copied field by
    /// field, so their validation (and its measured rationale) lives in exactly one place.
    /// </summary>
    public StrategyComparisonOptions Comparison { get; }
}
