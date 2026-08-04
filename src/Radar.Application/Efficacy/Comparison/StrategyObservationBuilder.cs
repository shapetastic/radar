namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// One usable (company, as-of) observation of a strategy: its opportunity score and the forward return it is
/// judged against, PLUS the observed entry/exit bar dates of that return's price window.
/// <para>
/// The entry/exit dates were always computed (<see cref="ForwardReturnResult"/> carries them) but the
/// marginal harness's private observation dropped them; spec 155's paired path needs them to prove that no
/// admitted block's OBSERVED price interval overlaps the next admitted block's, so they ride on the shared
/// observation instead of being recomputed.
/// </para>
/// </summary>
public readonly record struct StrategyObservation(
    DateOnly AsOf,
    Guid CompanyId,
    double Score,
    double ForwardReturn,
    DateOnly EntryDate,
    DateOnly ExitDate);

/// <summary>
/// One usable observation keyed on the EXACT scoring instant (spec 170): the same facts as
/// <see cref="StrategyObservation"/> plus the snapshot's <c>WindowEndUtc</c>. Consumed only by the paired
/// comparison's claim path; the marginal leaderboard keeps the date-deduplicated projection.
/// <para>
/// <see cref="AsOf"/> is derived from the instant (<c>DateOnly.FromDateTime(instant.UtcDateTime)</c>) — the
/// calendar date is used ONLY for block grouping, purging and the boundary comparison; the intersection
/// itself is exact.
/// </para>
/// </summary>
public readonly record struct StrategyInstantObservation(
    DateTimeOffset AsOfInstantUtc,
    DateOnly AsOf,
    Guid CompanyId,
    double Score,
    double ForwardReturn,
    DateOnly EntryDate,
    DateOnly ExitDate);

/// <summary>
/// One strategy's whole usable observation set, with the two per-company-day exclusion tallies (spec 152's
/// split: "no price at all" vs "some price but not the horizon").
/// <para>
/// Since spec 170 it carries TWO projections of the SAME single read: the date-deduplicated
/// <see cref="Usable"/> list (byte-for-byte the pre-170 behaviour, consumed by the marginal leaderboard) and
/// the exact-instant <see cref="UsableByInstant"/> list (consumed by the paired comparison), plus the count
/// of usable company-days that carried NO instant and are therefore excluded from the claim path — fail
/// closed, never date-paired as a fallback.
/// </para>
/// </summary>
public sealed record StrategyObservationSet(
    string StrategyName,
    IReadOnlyList<StrategyObservation> Usable,
    int WithoutForwardPrice,
    int PartialWindow)
{
    /// <summary>
    /// The exact-instant projection: de-duplicated on <c>(CompanyId, AsOfInstantUtc)</c>, last occurrence
    /// wins (the same rule as <see cref="Usable"/> — nothing throws on a duplicate), deterministic order
    /// (as-of date, company id, instant).
    /// </summary>
    public IReadOnlyList<StrategyInstantObservation> UsableByInstant { get; init; } = [];

    /// <summary>
    /// Usable (forward-defined) company-days whose every occurrence lacked an <c>AsOfInstantUtc</c>. De-duped
    /// on the same <c>(CompanyId, AsOf)</c> key as every other tally, and — the established convention — a
    /// key some occurrence DID cover with an instant is not counted: that key entered the claim path.
    /// </summary>
    public int WithoutAsOfInstant { get; init; }
}

/// <summary>
/// THE observation builder — extracted from <see cref="StrategyComparisonHarness"/> (spec 155,
/// reuse-over-copy) so the marginal leaderboard and the paired comparison consume the SAME admission rules:
/// same forward-return computation, same last-occurrence-wins de-dup, same exclusion tallies, same
/// deterministic (as-of, company) ordering. Two copies would drift, and a drifted copy here would make the
/// paired deltas answer over a different observation set than the leaderboard beside them.
/// <para>
/// Projects one strategy's joined series into (as-of date, company, score, forward return) observations,
/// counting — never hiding — the ones for which no forward price pair exists.
/// </para>
/// <para>
/// <b>TWO projections from ONE read (spec 170).</b> The date-deduplicated projection keys on
/// <c>(CompanyId, DateOnly)</c> and is BYTE-FOR-BYTE the pre-170 behaviour — re-keying it on the instant
/// would make the marginal leaderboard count multiple same-day runs instead of collapsing them, silently
/// changing a descriptive artifact this slice promises not to touch. The exact-instant projection keys on
/// <c>(CompanyId, DateTimeOffset)</c> and feeds only the paired claim path. One traversal of the score/price
/// data feeds both; neither re-reads.
/// </para>
/// <para>
/// A company/as-of pair can appear more than once (two runs on the same day both stamp a snapshot). The
/// LAST occurrence in the store's deterministic ascending-by-CreatedAtUtc order wins — in BOTH projections,
/// and nothing throws on a duplicate (spec 170 §2.1 explicitly retains this rule; a throw would introduce a
/// new fatal condition over stores whose duplicate rate has not been measured).
/// </para>
/// <para>
/// <b>The unusable count is de-duped on the SAME key, for the same reason.</b> It is reported next to a
/// de-duped observation count, so counting raw points there would make the two sides of "how much of this
/// series was usable?" incommensurable — three snapshots of one company-day with no forward price would
/// read as three lost observations when the usable side would only ever have yielded one. It counts
/// company-days with NO usable observation: a key excluded here after any occurrence succeeded is not
/// lost coverage. Definedness cannot differ between occurrences of one key WITHIN one company's series —
/// the forward return is a function of that series' bars, the as-of date and the horizon alone — but a
/// strategy may legally carry the same company id in two series with different bars, and then it can, so
/// the exclusion is load-bearing rather than defensive.
/// </para>
/// <para>
/// <b>A partial forward window is its own tally, on the same key (spec 152).</b> An observation whose
/// latest bar falls more than the exit tolerance short of <c>D+h</c> is not a full-horizon return, so
/// it is excluded from the usable set — but it is NOT "no forward price", and
/// <c>WithoutForwardPrice</c> keeps its exact pre-152 definition (<c>NoForwardBar</c> /
/// <c>SingleForwardBar</c> / <c>NonPositiveEntryPrice</c>) so that column's meaning does not silently
/// change. Partials therefore get their own set, de-duped on the SAME (company, as-of) key and excluded
/// against the usable keys for the SAME reason: a key some occurrence DID cover to the horizon is not lost
/// coverage. The two sets are independent, and a key can appear in both only in the pathological case the
/// paragraph above describes — one company id carried in two series with different bars.
/// </para>
/// </summary>
public static class StrategyObservationBuilder
{
    public static StrategyObservationSet Build(
        StrategyScoreSeries strategy, int forwardHorizonDays, int exitToleranceDays)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        var byKey = new Dictionary<(Guid CompanyId, DateOnly AsOf), StrategyObservation>();
        var byInstant = new Dictionary<(Guid CompanyId, DateTimeOffset Instant), StrategyInstantObservation>();
        var withoutForwardPrice = new HashSet<(Guid CompanyId, DateOnly AsOf)>();
        var partialWindow = new HashSet<(Guid CompanyId, DateOnly AsOf)>();
        var withoutInstant = new HashSet<(Guid CompanyId, DateOnly AsOf)>();
        var instantCoveredKeys = new HashSet<(Guid CompanyId, DateOnly AsOf)>();

        foreach (var company in strategy.Companies)
        {
            foreach (var point in company.Points)
            {
                var asOf = point.AsOfDate ?? point.ScoreDate;
                var forward = ForwardReturn.TryCompute(
                    company.PriceBars, asOf, forwardHorizonDays, exitToleranceDays);
                if (!forward.IsDefined)
                {
                    var target = forward.Reason == ForwardReturnUnavailableReason.PartialWindow
                        ? partialWindow
                        : withoutForwardPrice;
                    target.Add((company.CompanyId, asOf));
                    continue;
                }

                byKey[(company.CompanyId, asOf)] = new StrategyObservation(
                    asOf,
                    company.CompanyId,
                    point.OpportunityScore,
                    forward.Value,
                    // Non-null whenever IsDefined — the forward-return contract, not an assumption.
                    forward.EntryDate!.Value,
                    forward.ExitDate!.Value);

                // Spec 170: the exact-instant projection, from the SAME forward-return computation. A point
                // without an instant cannot enter it — that is the fail-closed exclusion, counted below.
                if (point.AsOfInstantUtc is { } instant)
                {
                    byInstant[(company.CompanyId, instant)] = new StrategyInstantObservation(
                        instant,
                        // Block grouping, purging and the boundary comparison operate on the instant's own
                        // UTC calendar date (spec 170 §2.2); EfficacyDatasetBuilder derives AsOfDate from the
                        // same WindowEndUtc, so the two agree for every store-read point.
                        DateOnly.FromDateTime(instant.UtcDateTime),
                        company.CompanyId,
                        point.OpportunityScore,
                        forward.Value,
                        forward.EntryDate!.Value,
                        forward.ExitDate!.Value);
                    instantCoveredKeys.Add((company.CompanyId, asOf));
                }
                else
                {
                    withoutInstant.Add((company.CompanyId, asOf));
                }
            }
        }

        withoutForwardPrice.ExceptWith(byKey.Keys);
        partialWindow.ExceptWith(byKey.Keys);
        // The established rule, applied to the instant tally too: a key some occurrence DID cover (here,
        // with an instant) is not lost from the claim path.
        withoutInstant.ExceptWith(instantCoveredKeys);

        var usable = byKey.Values.ToList();
        usable.Sort(static (a, b) =>
        {
            var byDate = a.AsOf.CompareTo(b.AsOf);
            return byDate != 0 ? byDate : a.CompanyId.CompareTo(b.CompanyId);
        });

        var usableByInstant = byInstant.Values.ToList();
        usableByInstant.Sort(static (a, b) =>
        {
            var byDate = a.AsOf.CompareTo(b.AsOf);
            if (byDate != 0)
            {
                return byDate;
            }

            var byCompany = a.CompanyId.CompareTo(b.CompanyId);
            return byCompany != 0 ? byCompany : a.AsOfInstantUtc.CompareTo(b.AsOfInstantUtc);
        });

        return new StrategyObservationSet(
            strategy.StrategyName, usable, withoutForwardPrice.Count, partialWindow.Count)
        {
            UsableByInstant = usableByInstant,
            WithoutAsOfInstant = withoutInstant.Count,
        };
    }
}
