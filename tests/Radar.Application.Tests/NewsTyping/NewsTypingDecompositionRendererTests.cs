using Radar.Application.News;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsTyping;

/// <summary>
/// Spec 181 §5: the decomposition artifact carries the caveat VERBATIM, renders every reader × capture-mode
/// cohort separately (never merged), shows the family count beside the raw count, and marks incompleteness
/// explicitly — never silently partial.
/// </summary>
public sealed class NewsTypingDecompositionRendererTests
{
    private static NewsTypingDecompositionCohort Cohort(
        string reader,
        string modelId,
        NewsObservationCaptureMode mode,
        int typed,
        int untyped,
        int familyCount,
        int retryExhausted,
        params NewsTypingDecompositionTypeRow[] types) => new(
        ReaderName: reader,
        Provider: "openai",
        ModelId: modelId,
        CohortKey: NewsTypingContract.CohortKey("openai", modelId),
        CaptureMode: mode,
        ObservationsTyped: typed,
        ObservationsInsufficientContent: 0,
        UntypedRemaining: untyped,
        FamilyCount: familyCount,
        Types: types,
        RetryExhausted: retryExhausted);

    private static NewsTypingDecompositionDocument Document(
        params NewsTypingDecompositionCompany[] companies) => new(
        SchemaVersion: NewsTypingDecompositionDocument.CurrentSchemaVersion,
        RunId: new Guid("cccccccc-0000-0000-0000-000000000001"),
        WindowStartUtc: NewsTypingTestData.AsOf.AddDays(-30),
        WindowEndUtc: NewsTypingTestData.AsOf,
        Caveat: NewsTypingDecompositionDocument.Caveat181,
        Readers: ["a (openai:model-a)", "b (openai:model-b)"],
        CaptureProvenThisRun: true,
        Companies: companies,
        ObservationsWithoutCompany: 0,
        GeneratedAtUtc: NewsTypingTestData.AsOf);

    [Fact]
    public void Caveat_RendersVerbatim()
    {
        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(Document());

        Assert.Contains(
            "Event typing describes what coverage was about. It is not a sentiment, a risk assessment or a "
                + "score input, and a type distribution is not a recommendation.",
            markdown);
        // And the constant itself is the spec's exact sentence — pinned so an edit is a conscious act.
        Assert.Equal(
            "Event typing describes what coverage was about. It is not a sentiment, a risk assessment or a "
                + "score input, and a type distribution is not a recommendation.",
            NewsTypingDecompositionDocument.Caveat181);
    }

    [Fact]
    public void Cohorts_RenderSideBySide_NeverMerged()
    {
        var company = new NewsTypingDecompositionCompany(
            CompanyId: Guid.NewGuid(),
            Ticker: "EOSE",
            ObservationsInWindow: 24,
            Incomplete: false,
            IncompleteReasons: [],
            Cohorts:
            [
                Cohort(
                    "a", "model-a", NewsObservationCaptureMode.ProspectiveRss, 14, 0, 3, 0,
                    new NewsTypingDecompositionTypeRow(NewsEventType.FinancingOrDilution, 14, 6, 2)),
                Cohort(
                    "b", "model-b", NewsObservationCaptureMode.ProspectiveRss, 10, 0, 2, 0,
                    new NewsTypingDecompositionTypeRow(NewsEventType.FinancingOrDilution, 10, 5, 1)),
            ]);

        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(Document(company));

        Assert.Contains("### Reader a (openai:model-a) — ProspectiveRss", markdown);
        Assert.Contains("### Reader b (openai:model-b) — ProspectiveRss", markdown);
        // Both cohorts' own counts survive — 14 and 10 are never summed into a merged 24 row.
        Assert.Contains("| FinancingOrDilution | 14 | 6 | 2 |", markdown);
        Assert.Contains("| FinancingOrDilution | 10 | 5 | 1 |", markdown);
        Assert.DoesNotContain("| FinancingOrDilution | 24", markdown);
    }

    [Fact]
    public void FamilyCount_RendersBesideTheRawCount()
    {
        var company = new NewsTypingDecompositionCompany(
            CompanyId: Guid.NewGuid(),
            Ticker: "TST",
            ObservationsInWindow: 40,
            Incomplete: false,
            IncompleteReasons: [],
            Cohorts:
            [
                Cohort(
                    "a", "model-a", NewsObservationCaptureMode.ProspectiveRss, 40, 0, 1, 0,
                    new NewsTypingDecompositionTypeRow(NewsEventType.FinancingOrDilution, 40, 12, 1)),
            ]);

        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(Document(company));

        // The 40-syndicated-copies shape: raw 40, families 1 — both visible in one row.
        Assert.Contains("| FinancingOrDilution | 40 | 12 | 1 |", markdown);
        Assert.Contains("same-event families 1", markdown);
    }

    [Fact]
    public void IncompleteCompany_IsMarkedLoudly_WithItsReasons()
    {
        var company = new NewsTypingDecompositionCompany(
            CompanyId: Guid.NewGuid(),
            Ticker: "TST",
            ObservationsInWindow: 5,
            Incomplete: true,
            IncompleteReasons: ["typing backlog: 3 observation(s) untyped for a (ProspectiveRss)"],
            Cohorts: [Cohort("a", "model-a", NewsObservationCaptureMode.ProspectiveRss, 2, 3, 0, 0)]);

        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(Document(company));

        Assert.Contains("**INCOMPLETE**", markdown);
        Assert.Contains("typing backlog: 3 observation(s) untyped", markdown);
    }

    [Fact]
    public void UnprovenCapture_IsStated_NeverSilentlyPartial()
    {
        var document = Document() with { CaptureProvenThisRun = null };

        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(document);

        Assert.Contains("Capture this run: unknown", markdown);
    }
}
