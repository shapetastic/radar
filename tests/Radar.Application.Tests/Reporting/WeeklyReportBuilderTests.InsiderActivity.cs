using Radar.Application.Collectors;
using Radar.Application.Reporting;
using Radar.Domain.Evidence;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 209 — the builder assembles <see cref="WeeklyReportEntry.InsiderActivity"/> from the DISTINCT Form 4
/// evidence behind a snapshot's links, inside the snapshot's exact window, loading each distinct evidence id
/// exactly once for both the evidence-ref block and the aggregate.
/// </summary>
public sealed partial class WeeklyReportBuilderTests
{
    private static readonly DateTimeOffset InsiderWindowStart = new(2026, 7, 5, 21, 44, 52, TimeSpan.Zero);
    private static readonly DateTimeOffset InsiderWindowEnd = new(2026, 9, 3, 21, 44, 52, TimeSpan.Zero);

    private static string Form4MetadataJson(string? token, string filingDate, string? netValue = null)
    {
        var pairs = new List<string>
        {
            "\"quality\":\"High\"", "\"form\":\"4\"", $"\"filingDate\":\"{filingDate}\"",
        };
        if (token is not null)
        {
            pairs.Add($"\"insiderClassificationReason\":\"{token}\"");
        }

        if (netValue is not null)
        {
            pairs.Add($"\"insiderNetValue\":\"{netValue}\"");
        }

        return "{\"metadata\":{" + string.Join(",", pairs) + "},\"companyHints\":[\"NWPX\"]}";
    }

    private static async Task<Guid> SeedInsiderSnapshotAsync(Harness h, Guid companyId, Guid snapshotId)
    {
        await h.Companies.AddAsync(
            new CompanyBuilder().WithId(companyId).WithName("Northwest Pipe").WithTicker("NWPX").Build(),
            default);
        var snapshot = new ScoreSnapshotBuilder()
            .WithId(snapshotId)
            .WithCompanyId(companyId)
            .WithOpportunityScore(60)
            .WithWindow(InsiderWindowStart, InsiderWindowEnd)
            .WithCreatedAtUtc(InPeriod)
            .Build();
        await h.Scores.AddSnapshotAsync(snapshot, default);
        return snapshotId;
    }

    // Seeds one Form 4 evidence item (published inside the window unless told otherwise) linked to the
    // snapshot through the given signal id. Returns the evidence id so a test can link it again.
    private static async Task<Guid> SeedForm4LinkAsync(
        Harness h,
        Guid snapshotId,
        Guid signalId,
        string? token,
        string filingDate,
        string? netValue = null,
        DateTimeOffset? publishedAt = null,
        Guid? existingEvidenceId = null)
    {
        var evidenceId = existingEvidenceId ?? Guid.NewGuid();
        if (existingEvidenceId is null)
        {
            var evidence = new EvidenceBuilder()
                .WithId(evidenceId)
                .WithSourceType(EvidenceSourceType.Filing)
                .WithSourceName("SEC EDGAR Form 4")
                .WithTitle("Form 4 insider filing: routine")
                .WithContentHash($"hash-{evidenceId}")
                .WithPublishedAtUtc(publishedAt ?? InsiderWindowStart.AddDays(20))
                .WithMetadataJson(Form4MetadataJson(token, filingDate, netValue))
                .Build();
            Assert.True(await h.Evidence.AddIfNewAsync(evidence, default));
        }

        await h.Scores.AddEvidenceLinkAsync(
            new ScoreEvidenceLink(
                Id: Guid.NewGuid(),
                ScoreSnapshotId: snapshotId,
                SignalId: signalId,
                EvidenceId: evidenceId,
                ContributionReason: "InsiderBuying (Neutral), strength 3, novelty 4",
                ContributionWeight: 3),
            default);
        return evidenceId;
    }

    [Fact]
    public async Task Entry_CarriesInsiderActivity_BuiltFromLinkedForm4Evidence_InsideTheWindow()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = await SeedInsiderSnapshotAsync(h, companyId, Guid.NewGuid());

        // The NWPX stream: eleven plan filings on the audit's dates, each behind its own signal.
        string[] dates =
        [
            "2026-08-05", "2026-08-11", "2026-08-11", "2026-08-18", "2026-08-18", "2026-08-25",
            "2026-08-25", "2026-08-28", "2026-09-02", "2026-09-02", "2026-09-03",
        ];
        foreach (var date in dates)
        {
            var signalId = Guid.NewGuid();
            await h.Signals.AddAsync(new SignalBuilder()
                .WithId(signalId)
                .WithCompanyId(companyId)
                .WithType(SignalType.InsiderBuying)
                .WithDirection(SignalDirection.Neutral)
                .WithReason("Insider stock transaction (routine)")
                .Build(), default);
            await SeedForm4LinkAsync(h, snapshotId, signalId, InsiderActivityMetadata.Plan10b51, date);
        }

        // One discretionary sale that fell BEFORE the window: counted as outside, never in a bucket.
        await SeedForm4LinkAsync(
            h, snapshotId, Guid.NewGuid(), InsiderActivityMetadata.DiscretionarySale, "2026-06-12",
            netValue: "3313222", publishedAt: InsiderWindowStart.AddDays(-10));
        // A non-Form-4 evidence link contributes nothing to the summary.
        var (_, _) = await SeedEvidenceLinkAsync(h, snapshotId);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var entry = Assert.Single(h.Renderer.LastModel!.Entries);
        var insider = Assert.IsType<InsiderActivitySummary>(entry.InsiderActivity);
        Assert.Equal(11, insider.FilingCount);
        Assert.Equal(11, insider.PlannedDispositionCount);
        Assert.Equal(29, insider.PlannedDispositionSpanDays);
        Assert.Null(insider.DiscretionarySaleValue);
        Assert.Equal(0, insider.DiscretionarySaleCount);
        Assert.Equal(1, insider.OutsideWindowCount);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains(
            "- Insider activity (Form 4, this window): 11 filings; 11 planned-disposition filings across 29 days; "
                + "transaction value not captured; 1 outside the window\n",
            markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Buying", markdown, StringComparison.Ordinal);
        Assert.Contains("InsiderActivity (Neutral): Insider stock transaction (routine)", markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entry_WithNoForm4Evidence_HasNullInsiderActivity_AndRendersNoInsiderLine()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70);
        await SeedEvidenceLinkAsync(h, snapshotId);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var entry = Assert.Single(h.Renderer.LastModel!.Entries);
        Assert.Null(entry.InsiderActivity);
        Assert.DoesNotContain("Insider activity", result.Report.MarkdownContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameEvidenceLinkedThroughTwoSignals_CountsOnce_AndIsLoadedOnce()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = await SeedInsiderSnapshotAsync(h, companyId, Guid.NewGuid());

        var evidenceId = await SeedForm4LinkAsync(
            h, snapshotId, Guid.NewGuid(), InsiderActivityMetadata.DiscretionaryBuy, "2026-08-11",
            netValue: "50000");
        await SeedForm4LinkAsync(
            h, snapshotId, Guid.NewGuid(), InsiderActivityMetadata.DiscretionaryBuy, "2026-08-11",
            netValue: "50000", existingEvidenceId: evidenceId);
        await SeedForm4LinkAsync(
            h, snapshotId, Guid.NewGuid(), InsiderActivityMetadata.MixedBuySell, "2026-08-12",
            netValue: "900000");

        await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var entry = Assert.Single(h.Renderer.LastModel!.Entries);
        var insider = Assert.IsType<InsiderActivitySummary>(entry.InsiderActivity);
        Assert.Equal(2, insider.FilingCount);
        Assert.Equal(1, insider.DiscretionaryPurchaseCount);
        Assert.Equal(50000m, insider.DiscretionaryPurchaseValue);
        Assert.Equal(1, insider.MixedCount);
        // The evidence-ref block still lists every LINK (three), while the aggregate counts distinct items.
        Assert.Equal(3, entry.Evidence.Count);
        // Two distinct evidence ids behind three links: exactly two repository loads, shared by both.
        Assert.Equal(2, h.CountingEvidence.GetByIdCallCount);
    }

    [Fact]
    public async Task MissingEvidence_StillRendersPlaceholder_AndIsAbsentFromTheSummary()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = await SeedInsiderSnapshotAsync(h, companyId, Guid.NewGuid());
        await SeedForm4LinkAsync(h, snapshotId, Guid.NewGuid(), InsiderActivityMetadata.Plan10b51, "2026-08-05");
        // A link whose evidence was never stored (id fabricated, nothing seeded).
        await h.Scores.AddEvidenceLinkAsync(
            new ScoreEvidenceLink(
                Guid.NewGuid(), snapshotId, Guid.NewGuid(), Guid.NewGuid(), "Contributed to the score.", 5),
            default);

        await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var entry = Assert.Single(h.Renderer.LastModel!.Entries);
        Assert.Contains(entry.Evidence, e => e.Title == "(evidence unavailable)");
        var insider = Assert.IsType<InsiderActivitySummary>(entry.InsiderActivity);
        Assert.Equal(1, insider.FilingCount);
        Assert.Equal(1, insider.PlannedDispositionCount);
    }
}
