using Radar.Application.Collectors;
using Radar.Application.Reporting;
using Radar.Domain.Evidence;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 209 — the pure bucket arithmetic behind the weekly report's insider-activity line. Buckets mirror
/// the persisted classification taxonomy exactly; a null value is "not captured", never 0; the mixed
/// bucket has no value at all; the plan span is stated only over the whole dated set; and anything outside
/// the snapshot's <c>(start, end]</c> window is COUNTED, never silently dropped.
/// </summary>
public sealed class InsiderActivitySummaryTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 7, 5, 21, 44, 52, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 9, 3, 21, 44, 52, TimeSpan.Zero);

    private static EvidenceItem Form4(
        string? token,
        string? filingDate = "2026-08-11",
        string? netValue = null,
        DateTimeOffset? publishedAt = null,
        Guid? id = null)
    {
        var pairs = new List<string> { "\"form\":\"4\"" };
        if (filingDate is not null)
        {
            pairs.Add($"\"filingDate\":\"{filingDate}\"");
        }

        if (token is not null)
        {
            pairs.Add($"\"insiderClassificationReason\":\"{token}\"");
        }

        if (netValue is not null)
        {
            pairs.Add($"\"insiderNetValue\":\"{netValue}\"");
        }

        var evidenceId = id ?? Guid.NewGuid();
        return new EvidenceBuilder()
            .WithId(evidenceId)
            .WithContentHash($"hash-{evidenceId}")
            .WithSourceType(EvidenceSourceType.Filing)
            .WithPublishedAtUtc(publishedAt ?? WindowStart.AddDays(20))
            .WithCollectedAtUtc(publishedAt ?? WindowStart.AddDays(20))
            .WithMetadataJson("{\"metadata\":{" + string.Join(",", pairs) + "},\"companyHints\":[]}")
            .Build();
    }

    private static InsiderActivitySummary? Summarise(
        IEnumerable<EvidenceItem> evidence, ICollection<string>? unrecognised = null) =>
        InsiderActivitySummary.From(evidence, WindowStart, WindowEnd, unrecognised);

    [Fact]
    public void NoEvidence_ReturnsNull()
    {
        Assert.Null(Summarise([]));
    }

    [Fact]
    public void NoForm4Evidence_ReturnsNull_NotAZeroSummary()
    {
        var press = new EvidenceBuilder().WithContentHash("h1").Build();
        var eightK = new EvidenceBuilder()
            .WithContentHash("h2")
            .WithMetadataJson("{\"metadata\":{\"form\":\"8-K\",\"filingDate\":\"2026-08-05\"},\"companyHints\":[]}")
            .Build();

        Assert.Null(Summarise([press, eightK]));
    }

    [Fact]
    public void NwpxShape_ElevenPlanFilings_CountElevenSpanTwentyNineNoValues()
    {
        // The audit's NWPX filing dates (docs/cohorts/insider-flow-audit-2026-09.md): 08-05, 08-11 ×2,
        // 08-18 ×2, 08-25 ×2, 08-28, 09-02 ×2, 09-03 — eleven plan filings, (09-03 − 08-05) = 29 days.
        string[] dates =
        [
            "2026-08-05", "2026-08-11", "2026-08-11", "2026-08-18", "2026-08-18", "2026-08-25",
            "2026-08-25", "2026-08-28", "2026-09-02", "2026-09-02", "2026-09-03",
        ];
        var evidence = dates.Select(d => Form4(InsiderActivityMetadata.Plan10b51, d)).ToList();

        var summary = Summarise(evidence);

        Assert.NotNull(summary);
        Assert.Equal(11, summary.FilingCount);
        Assert.Equal(11, summary.PlannedDispositionCount);
        Assert.Equal(new DateOnly(2026, 8, 5), summary.PlannedDispositionFirstFilingDate);
        Assert.Equal(new DateOnly(2026, 9, 3), summary.PlannedDispositionLastFilingDate);
        Assert.Equal(29, summary.PlannedDispositionSpanDays);
        Assert.Equal(0, summary.PlannedDispositionUndatedCount);
        Assert.Equal(0, summary.DiscretionaryPurchaseCount);
        Assert.Null(summary.DiscretionaryPurchaseValue);
        Assert.Equal(0, summary.DiscretionarySaleCount);
        Assert.Null(summary.DiscretionarySaleValue);
        Assert.Equal(0, summary.MixedCount);
        Assert.Equal(0, summary.NoDiscretionaryTransactionsCount);
        Assert.Equal(0, summary.UnknownClassificationCount);
        Assert.Equal(0, summary.UnrecognisedClassificationCount);
        Assert.Equal(0, summary.OutsideWindowCount);
    }

    [Fact]
    public void SameEvidenceLinkedTwice_CountsOnce()
    {
        var id = Guid.NewGuid();
        var once = Form4(InsiderActivityMetadata.DiscretionarySale, netValue: "1000", id: id);
        var again = Form4(InsiderActivityMetadata.DiscretionarySale, netValue: "1000", id: id);

        var summary = Summarise([once, again]);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.FilingCount);
        Assert.Equal(1, summary.DiscretionarySaleCount);
        Assert.Equal(1000m, summary.DiscretionarySaleValue);
    }

    [Fact]
    public void Window_IsExclusiveStartInclusiveEnd_AndOutsideItemsAreCounted()
    {
        var atStart = Form4(InsiderActivityMetadata.Plan10b51, publishedAt: WindowStart);
        var justAfterStart = Form4(InsiderActivityMetadata.Plan10b51, publishedAt: WindowStart.AddTicks(1));
        var atEnd = Form4(InsiderActivityMetadata.Plan10b51, publishedAt: WindowEnd);
        var justAfterEnd = Form4(InsiderActivityMetadata.Plan10b51, publishedAt: WindowEnd.AddTicks(1));
        var longBefore = Form4(InsiderActivityMetadata.DiscretionarySale, netValue: "5000",
            publishedAt: WindowStart.AddDays(-30));

        var summary = Summarise([atStart, justAfterStart, atEnd, justAfterEnd, longBefore]);

        Assert.NotNull(summary);
        Assert.Equal(2, summary.FilingCount);
        Assert.Equal(2, summary.PlannedDispositionCount);
        Assert.Equal(3, summary.OutsideWindowCount);
        // The out-of-window sale contributes to NO in-window bucket.
        Assert.Equal(0, summary.DiscretionarySaleCount);
        Assert.Null(summary.DiscretionarySaleValue);
    }

    [Fact]
    public void Window_UsesCollectedAtWhenPublishedAtIsNull()
    {
        var evidence = new EvidenceBuilder()
            .WithContentHash("h-collected")
            .WithPublishedAtUtc(null)
            .WithCollectedAtUtc(WindowEnd.AddDays(1))
            .WithMetadataJson(
                "{\"metadata\":{\"form\":\"4\",\"insiderClassificationReason\":\"plan-10b5-1\"},\"companyHints\":[]}")
            .Build();

        var summary = Summarise([evidence]);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.FilingCount);
        Assert.Equal(1, summary.OutsideWindowCount);
    }

    [Fact]
    public void PurchaseAndSaleValues_AreSummedFromCapturedValuesOnly_AndUncapturedAreCounted()
    {
        var evidence = new[]
        {
            Form4(InsiderActivityMetadata.DiscretionaryBuy, netValue: "50000"),
            Form4(InsiderActivityMetadata.DiscretionaryBuy, netValue: "25000.5"),
            Form4(InsiderActivityMetadata.DiscretionaryBuy), // legacy: value never persisted
            Form4(InsiderActivityMetadata.DiscretionarySale, netValue: "3313222"),
            Form4(InsiderActivityMetadata.DiscretionarySale, netValue: "garbage"),
        };

        var summary = Summarise(evidence);

        Assert.NotNull(summary);
        Assert.Equal(5, summary.FilingCount);
        Assert.Equal(3, summary.DiscretionaryPurchaseCount);
        Assert.Equal(75000.5m, summary.DiscretionaryPurchaseValue);
        Assert.Equal(1, summary.DiscretionaryPurchaseValueNotCapturedCount);
        Assert.Equal(2, summary.DiscretionarySaleCount);
        Assert.Equal(3313222m, summary.DiscretionarySaleValue);
        Assert.Equal(1, summary.DiscretionarySaleValueNotCapturedCount);
    }

    [Fact]
    public void PurchaseValue_IsNullWhenNoneCaptured_NeverZero()
    {
        var summary = Summarise([
            Form4(InsiderActivityMetadata.DiscretionaryBuy),
            Form4(InsiderActivityMetadata.DiscretionaryBuy),
        ]);

        Assert.NotNull(summary);
        Assert.Equal(2, summary.DiscretionaryPurchaseCount);
        Assert.Null(summary.DiscretionaryPurchaseValue);
        Assert.Equal(2, summary.DiscretionaryPurchaseValueNotCapturedCount);
    }

    [Fact]
    public void MixedFilings_AreCounted_AndTheirValueIsNeverTotalled()
    {
        // The persisted magnitude of a mixed filing is Math.Max(purchase, sale) — neither net nor total.
        var summary = Summarise([
            Form4(InsiderActivityMetadata.MixedBuySell, netValue: "900000"),
            Form4(InsiderActivityMetadata.MixedBuySell, netValue: "100"),
            Form4(InsiderActivityMetadata.DiscretionaryBuy, netValue: "10"),
        ]);

        Assert.NotNull(summary);
        Assert.Equal(2, summary.MixedCount);
        Assert.Equal(10m, summary.DiscretionaryPurchaseValue);
        Assert.Null(summary.DiscretionarySaleValue);
        // Structural guarantee: the record exposes no mixed-value member to total into.
        Assert.DoesNotContain(
            typeof(InsiderActivitySummary).GetProperties(),
            p => p.Name.StartsWith("Mixed", StringComparison.Ordinal) && p.PropertyType != typeof(int));
    }

    [Fact]
    public void NoDiscretionary_Unknown_AndUnrecognised_AreEachCounted()
    {
        var unrecognised = new List<string>();
        var summary = Summarise(
        [
            Form4(InsiderActivityMetadata.NoDiscretionaryTransactions),
            Form4(token: null), // legacy, pre-spec-156: no token at all
            Form4("future-token"),
            Form4("future-token"),
            Form4("other-token"),
        ], unrecognised);

        Assert.NotNull(summary);
        Assert.Equal(5, summary.FilingCount);
        Assert.Equal(1, summary.NoDiscretionaryTransactionsCount);
        Assert.Equal(1, summary.UnknownClassificationCount);
        Assert.Equal(3, summary.UnrecognisedClassificationCount);
        Assert.Equal(["future-token", "future-token", "other-token"], unrecognised);
    }

    [Fact]
    public void PlanSpan_IsNullForASinglePlanFiling()
    {
        var summary = Summarise([Form4(InsiderActivityMetadata.Plan10b51, "2026-08-05")]);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.PlannedDispositionCount);
        Assert.Equal(new DateOnly(2026, 8, 5), summary.PlannedDispositionFirstFilingDate);
        Assert.Equal(new DateOnly(2026, 8, 5), summary.PlannedDispositionLastFilingDate);
        Assert.Null(summary.PlannedDispositionSpanDays);
    }

    [Fact]
    public void PlanSpan_IsNullWhenAnyPlanFilingIsUndated_AndTheUndatedAreCounted()
    {
        var summary = Summarise([
            Form4(InsiderActivityMetadata.Plan10b51, "2026-08-05"),
            Form4(InsiderActivityMetadata.Plan10b51, "2026-09-03"),
            Form4(InsiderActivityMetadata.Plan10b51, filingDate: null),
        ]);

        Assert.NotNull(summary);
        Assert.Equal(3, summary.PlannedDispositionCount);
        Assert.Equal(1, summary.PlannedDispositionUndatedCount);
        Assert.Null(summary.PlannedDispositionSpanDays);
        // The dated bounds are still reported — they are real — only the span is withheld.
        Assert.Equal(new DateOnly(2026, 8, 5), summary.PlannedDispositionFirstFilingDate);
        Assert.Equal(new DateOnly(2026, 9, 3), summary.PlannedDispositionLastFilingDate);
    }

    [Fact]
    public void PlanSpan_IsZeroForTwoSameDayFilings()
    {
        var summary = Summarise([
            Form4(InsiderActivityMetadata.Plan10b51, "2026-08-11"),
            Form4(InsiderActivityMetadata.Plan10b51, "2026-08-11"),
        ]);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.PlannedDispositionSpanDays);
    }

    [Fact]
    public void PlanSpan_IsOrderIndependent()
    {
        var ascending = Summarise([
            Form4(InsiderActivityMetadata.Plan10b51, "2026-08-05"),
            Form4(InsiderActivityMetadata.Plan10b51, "2026-08-18"),
            Form4(InsiderActivityMetadata.Plan10b51, "2026-09-03"),
        ]);
        var shuffled = Summarise([
            Form4(InsiderActivityMetadata.Plan10b51, "2026-09-03"),
            Form4(InsiderActivityMetadata.Plan10b51, "2026-08-05"),
            Form4(InsiderActivityMetadata.Plan10b51, "2026-08-18"),
        ]);

        Assert.Equal(29, ascending!.PlannedDispositionSpanDays);
        Assert.Equal(ascending, shuffled);
    }

    [Fact]
    public void AllForm4OutsideTheWindow_IsASummaryWithZeroFilingsAndTheOutsideCount_NotNull()
    {
        var summary = Summarise([
            Form4(InsiderActivityMetadata.Plan10b51, publishedAt: WindowStart.AddDays(-1)),
        ]);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.FilingCount);
        Assert.Equal(1, summary.OutsideWindowCount);
    }
}
