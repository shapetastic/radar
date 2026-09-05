namespace Radar.Application.Reporting;

using Radar.Application.Collectors;
using Radar.Domain.Evidence;

/// <summary>
/// Spec 209: the structured, report-side aggregate of the SEC Form 4 evidence behind ONE score snapshot,
/// inside that snapshot's exact scoring window. Buckets mirror the PERSISTED classification taxonomy
/// (<see cref="InsiderActivityMetadata"/>) exactly — nothing is inferred beyond what the store carries, and
/// every "not captured" is counted rather than defaulted to zero.
/// <para>
/// <b>Numerically inert.</b> This is a presentation aggregate only: it is no signal, no scoring input, no
/// weight, is never persisted, and is no fingerprint input (<c>ScoringConfigVersion</c> is untouched). It
/// exists so a reader can see "11 planned-disposition filings across 29 days" without opening eleven filings.
/// </para>
/// <para>
/// <b>What the store cannot say.</b> A 10b5-1 plan filing retains only its plan marker — no transaction
/// value (the reader forces every plan transaction Neutral before reading codes/shares/prices), so the plan
/// bucket carries a count and a date span only. A <c>mixed-buy-sell</c> filing's persisted magnitude is
/// <c>Math.Max(purchaseValue, saleValue)</c> — neither a net nor a total — so the mixed bucket is a COUNT
/// with deliberately NO value member; it must never be summed into either value total.
/// </para>
/// </summary>
/// <param name="FilingCount">Distinct Form 4 evidence items inside the window (every in-window bucket below
/// partitions this count).</param>
/// <param name="PlannedDispositionCount"><c>plan-10b5-1</c> filings.</param>
/// <param name="PlannedDispositionFirstFilingDate">Earliest dated plan filing, or <c>null</c> when none
/// carried a date.</param>
/// <param name="PlannedDispositionLastFilingDate">Latest dated plan filing, or <c>null</c> when none carried
/// a date.</param>
/// <param name="PlannedDispositionSpanDays">Elapsed days from the first to the last plan filing date
/// (<c>(last - first).Days</c>); <c>null</c> when fewer than two plan filings OR any plan filing lacks a
/// parseable filing date (a span over a partial set would be a fabricated number).</param>
/// <param name="PlannedDispositionUndatedCount">Plan filings whose filing date was absent/unparseable.</param>
/// <param name="DiscretionaryPurchaseCount"><c>discretionary-buy</c> filings.</param>
/// <param name="DiscretionaryPurchaseValue">Sum of the captured values of <c>discretionary-buy</c> filings;
/// <c>null</c> when NO such filing carried a value (never 0).</param>
/// <param name="DiscretionaryPurchaseValueNotCapturedCount"><c>discretionary-buy</c> filings with no captured
/// value.</param>
/// <param name="DiscretionarySaleCount"><c>discretionary-sale</c> filings.</param>
/// <param name="DiscretionarySaleValue">Sum of the captured values of <c>discretionary-sale</c> filings;
/// <c>null</c> when NO such filing carried a value (never 0).</param>
/// <param name="DiscretionarySaleValueNotCapturedCount"><c>discretionary-sale</c> filings with no captured
/// value.</param>
/// <param name="MixedCount"><c>mixed-buy-sell</c> filings — count only; split and total were not
/// captured.</param>
/// <param name="NoDiscretionaryTransactionsCount"><c>no-discretionary-transactions</c> filings.</param>
/// <param name="UnknownClassificationCount">Filings with no classification token (legacy, pre-spec-156
/// evidence).</param>
/// <param name="UnrecognisedClassificationCount">Filings carrying a token outside the closed set — counted,
/// never silently dropped.</param>
/// <param name="OutsideWindowCount">Linked Form 4 evidence whose instant
/// (<c>PublishedAtUtc ?? CollectedAtUtc</c>) fell outside the snapshot's
/// <c>(WindowStartUtc, WindowEndUtc]</c> window. Excluded from every other bucket, but counted so nothing
/// is discarded invisibly.</param>
public sealed record InsiderActivitySummary(
    int FilingCount,
    int PlannedDispositionCount,
    DateOnly? PlannedDispositionFirstFilingDate,
    DateOnly? PlannedDispositionLastFilingDate,
    int? PlannedDispositionSpanDays,
    int PlannedDispositionUndatedCount,
    int DiscretionaryPurchaseCount,
    decimal? DiscretionaryPurchaseValue,
    int DiscretionaryPurchaseValueNotCapturedCount,
    int DiscretionarySaleCount,
    decimal? DiscretionarySaleValue,
    int DiscretionarySaleValueNotCapturedCount,
    int MixedCount,
    int NoDiscretionaryTransactionsCount,
    int UnknownClassificationCount,
    int UnrecognisedClassificationCount,
    int OutsideWindowCount)
{
    /// <summary>
    /// Pure, deterministic assembly from the DISTINCT (by <see cref="EvidenceItem.Id"/>) evidence behind a
    /// snapshot. Non-Form-4 items are ignored (they are not insider activity). Returns <c>null</c> when no
    /// Form 4 evidence is present at all — in or out of the window — so a renderer prints nothing rather
    /// than a fabricated "0 filings". The window is exclusive-start, inclusive-end:
    /// <c>windowStartUtc &lt; instant &lt;= windowEndUtc</c>, the scoring-window convention.
    /// </summary>
    /// <param name="unrecognisedTokens">Receives every distinct stored token outside the closed set, so the
    /// caller can log ONE aggregated warning naming them; may be <c>null</c>.</param>
    public static InsiderActivitySummary? From(
        IEnumerable<EvidenceItem> evidence,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        ICollection<string>? unrecognisedTokens = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var seen = new HashSet<Guid>();
        var any = false;
        int filings = 0, plans = 0, planUndated = 0;
        DateOnly? planFirst = null, planLast = null;
        int purchases = 0, purchasesNoValue = 0;
        decimal? purchaseValue = null;
        int sales = 0, salesNoValue = 0;
        decimal? saleValue = null;
        int mixed = 0, noDiscretionary = 0, unknown = 0, unrecognised = 0, outsideWindow = 0;

        foreach (var item in evidence)
        {
            if (!seen.Add(item.Id))
            {
                continue; // the same evidence linked through several signals counts once
            }

            var read = InsiderActivityMetadata.TryRead(item);
            if (read is null)
            {
                continue; // not a Form 4 — not insider activity, not part of this summary
            }

            any = true;

            var instant = item.PublishedAtUtc ?? item.CollectedAtUtc;
            if (instant <= windowStartUtc || instant > windowEndUtc)
            {
                outsideWindow++;
                continue;
            }

            filings++;
            switch (read.ClassificationReason)
            {
                case null:
                    unknown++;
                    break;

                case InsiderActivityMetadata.Plan10b51:
                    plans++;
                    if (read.FilingDate is { } planDate)
                    {
                        planFirst = planFirst is null || planDate < planFirst ? planDate : planFirst;
                        planLast = planLast is null || planDate > planLast ? planDate : planLast;
                    }
                    else
                    {
                        planUndated++;
                    }
                    break;

                case InsiderActivityMetadata.DiscretionaryBuy:
                    purchases++;
                    if (read.NetValue is { } pv)
                    {
                        purchaseValue = (purchaseValue ?? 0m) + pv;
                    }
                    else
                    {
                        purchasesNoValue++;
                    }
                    break;

                case InsiderActivityMetadata.DiscretionarySale:
                    sales++;
                    if (read.NetValue is { } sv)
                    {
                        saleValue = (saleValue ?? 0m) + sv;
                    }
                    else
                    {
                        salesNoValue++;
                    }
                    break;

                case InsiderActivityMetadata.MixedBuySell:
                    // Count only: the persisted value is Math.Max(purchase, sale) and is never totalled.
                    mixed++;
                    break;

                case InsiderActivityMetadata.NoDiscretionaryTransactions:
                    noDiscretionary++;
                    break;

                default:
                    unrecognised++;
                    unrecognisedTokens?.Add(read.ClassificationReason);
                    break;
            }
        }

        if (!any)
        {
            return null;
        }

        // A span is stated only when it is computed over the WHOLE plan set: two or more filings, all dated.
        int? spanDays = plans >= 2 && planUndated == 0 && planFirst is { } first && planLast is { } last
            ? last.DayNumber - first.DayNumber
            : null;

        return new InsiderActivitySummary(
            FilingCount: filings,
            PlannedDispositionCount: plans,
            PlannedDispositionFirstFilingDate: planFirst,
            PlannedDispositionLastFilingDate: planLast,
            PlannedDispositionSpanDays: spanDays,
            PlannedDispositionUndatedCount: planUndated,
            DiscretionaryPurchaseCount: purchases,
            DiscretionaryPurchaseValue: purchaseValue,
            DiscretionaryPurchaseValueNotCapturedCount: purchasesNoValue,
            DiscretionarySaleCount: sales,
            DiscretionarySaleValue: saleValue,
            DiscretionarySaleValueNotCapturedCount: salesNoValue,
            MixedCount: mixed,
            NoDiscretionaryTransactionsCount: noDiscretionary,
            UnknownClassificationCount: unknown,
            UnrecognisedClassificationCount: unrecognised,
            OutsideWindowCount: outsideWindow);
    }
}
