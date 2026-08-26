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

    private static NewsTypingDecompositionCompany Company(
        params NewsTypingDecompositionCohort[] cohorts) => new(
        CompanyId: new Guid("aaaaaaaa-0000-0000-0000-000000000009"),
        Ticker: "TST",
        ObservationsInWindow: 4,
        Incomplete: false,
        IncompleteReasons: [],
        Cohorts: cohorts);

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

    /// <summary>
    /// Spec 187 §2: the first-attempt lane split renders BESIDE the existing counters, so "the leaders we
    /// were about to judge were typed first" is a visible number rather than a claim.
    /// </summary>
    [Fact]
    public void LaneSplit_RendersBesideTheExistingCounters_WhenThePassSelectedWork()
    {
        var cohort = Cohort("a", "model-a", NewsObservationCaptureMode.ProspectiveRss, 4, 0, 2, 0) with
        {
            CandidatePrioritySelected = 3,
            GeneralSelected = 1,
            ProviderCallsAttempted = 4,
        };

        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(
            Document(Company(cohort)));

        Assert.Contains(
            "same-event families 2 · selected this pass: 0 retry, 3 judgment-candidate priority, 1 general "
                + "(4 provider call(s) made)",
            markdown);
    }

    /// <summary>
    /// Spec 189 §3: the RETRY lane is the third selection column, and what the pass actually SPENT renders
    /// beside what it selected. The live 2026-08-24 pass allocated 100 candidate + 99 general + 1 retry
    /// against a 200-call budget, and without a retry column that reads as an unused slot.
    /// </summary>
    [Fact]
    public void RetryLaneAndProviderCalls_RenderBesideTheSelectionCounts()
    {
        var cohort = Cohort("a", "model-a", NewsObservationCaptureMode.ProspectiveRss, 4, 0, 2, 0) with
        {
            RetrySelected = 1,
            CandidatePrioritySelected = 3,
            GeneralSelected = 1,
            // Deliberately BELOW the five selections: one reservation was refused, so a selection was never
            // spent. Equating the two numbers is exactly the error the separate column exists to prevent.
            ProviderCallsAttempted = 4,
        };

        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(Document(Company(cohort)));

        Assert.Contains(
            "selected this pass: 1 retry, 3 judgment-candidate priority, 1 general (4 provider call(s) "
                + "made)",
            markdown);
    }

    /// <summary>
    /// Spec 189 §3: a retryable failure is NAMED separately from backlog and from exhaustion, and only when
    /// it happened — so an untouched company row is unchanged.
    /// </summary>
    [Fact]
    public void RetryableFailures_RenderSeparatelyFromBacklogAndExhaustion()
    {
        var cohort = Cohort("a", "model-a", NewsObservationCaptureMode.ProspectiveRss, 4, 2, 2, 0) with
        {
            RetryableFailuresThisRun = 1,
        };

        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(Document(Company(cohort)));

        Assert.Contains("untyped remaining 2", markdown);
        Assert.Contains("retryable failures this run 1", markdown);
        Assert.DoesNotContain("retries exhausted", markdown);

        // And it is absent when it did not happen.
        Assert.DoesNotContain(
            "retryable failures this run",
            NewsTypingDecompositionRenderer.RenderMarkdown(Document(Company(
                Cohort("a", "model-a", NewsObservationCaptureMode.ProspectiveRss, 4, 2, 2, 0)))));
    }

    /// <summary>
    /// Spec 189 §3: capture INFLOW renders beside the pass, because "252 captured against a 200-call budget"
    /// is the fact the capacity decision turns on. An unresolvable batch renders "not recorded" — never a
    /// guessed number.
    /// </summary>
    [Fact]
    public void CaptureInflow_Renders_AndFailsClosedWhenTheBatchIsUnresolvable()
    {
        var withBatch = Document() with
        {
            NewsObservationBatchId = new Guid("bbbbbbbb-0000-0000-0000-000000000001"),
            ObservationsCapturedThisRun = 252,
        };

        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(withBatch);
        Assert.Contains(
            "Observation capture this run: batch `bbbbbbbb-0000-0000-0000-000000000001` · new observations "
                + "252",
            markdown);

        var unresolvable = NewsTypingDecompositionRenderer.RenderMarkdown(Document());
        Assert.Contains(
            "Observation capture this run: batch `(none)` · new observations not recorded", unresolvable);
    }

    /// <summary>
    /// Spec 189 §3: the AUTHORITATIVE pass-wide reader summary renders as its own table, and the note under
    /// it STATES that pass-wide totals and the window's company rows may legitimately differ — it never
    /// silently claims they are equal.
    /// </summary>
    [Fact]
    public void PassWideReaderSummary_RendersAsTheAuthoritativeBudgetView_WithItsHonestyNote()
    {
        var document = Document() with
        {
            ReaderSummaries =
            [
                new NewsTypingDecompositionReaderSummary(
                    ReaderName: "a",
                    Provider: "openai",
                    ModelId: "model-a",
                    CohortKey: NewsTypingContract.CohortKey("openai", "model-a"),
                    RetrySelected: 1,
                    CandidatePrioritySelected: 150,
                    GeneralSelected: 199,
                    ProviderCallsAttempted: 350,
                    CompletedOutcomesPersisted: 345,
                    ProviderFailures: 0,
                    ParseFailures: 0,
                    ValidationFailures: 5,
                    ReservationsRefused: 0,
                    OutcomeWritesFailed: 0,
                    RetryExhausted: 0,
                    ReservedWithoutOutcome: 0,
                    UntypedRemaining: 2_017),
            ],
        };

        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(document);

        Assert.Contains("### Typing pass totals (pass-wide, authoritative for the call budget)", markdown);
        Assert.Contains("| a (openai:model-a) | 1 | 150 | 199 | 350 | 345 | 0 | 0 | 5 | 0 | 0 | 0 | 0 "
            + "| 2017 |", markdown);
        Assert.Contains("may legitimately differ", markdown);
    }

    /// <summary>
    /// A document carrying NO reader summaries (a re-rendered pre-189 artifact) omits the table entirely
    /// rather than rendering a row of zeroes — an absent measurement must never look like a measured zero.
    /// </summary>
    [Fact]
    public void PassWideReaderSummary_IsOmittedEntirely_WhenNoneWasRecorded()
    {
        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(Document());

        Assert.DoesNotContain("Typing pass totals", markdown);
    }

    /// <summary>
    /// A company whose work was all deferred (or already complete) renders EXACTLY as it did before spec
    /// 187 §2 — the lane split appears only when the pass actually selected something, so it reads as the
    /// exception it is rather than as a column of zeros on every row.
    /// </summary>
    [Fact]
    public void LaneSplit_IsAbsent_WhenThePassSelectedNothingForThatCompany()
    {
        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(Document(Company(
            Cohort("a", "model-a", NewsObservationCaptureMode.ProspectiveRss, 4, 0, 2, 0))));

        Assert.DoesNotContain("selected this pass", markdown);
    }

    /// <summary>
    /// A re-rendered PRE-v4 artifact recorded spec 187 §2's two lanes but not spec 189 §3's retry lane or
    /// call count, which deserialize as 0. The row must not claim "0 retry … (0 provider call(s) made)" —
    /// a defaulted zero is not a measured zero (spec 187 §7's rule), so the unrecorded numbers are NAMED.
    /// </summary>
    [Fact]
    public void PreV4Document_NamesTheUnrecordedRetryLaneAndCallCount_RatherThanRenderingAMeasuredZero()
    {
        var cohort = Cohort("a", "model-a", NewsObservationCaptureMode.ProspectiveRss, 4, 0, 2, 0) with
        {
            CandidatePrioritySelected = 3,
            GeneralSelected = 1,
        };

        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(
            Document(Company(cohort)) with { SchemaVersion = "news-typing-decomposition-v3" });

        Assert.Contains(
            "selected this pass: 3 judgment-candidate priority, 1 general (retry lane and provider calls "
                + "not recorded in news-typing-decomposition-v3)",
            markdown);
        Assert.DoesNotContain("0 retry", markdown);
        Assert.DoesNotContain("provider call(s) made", markdown);
        // The capture-inflow line is v4-only too: "(none)" would claim the run genuinely had no batch.
        Assert.Contains(
            "Observation capture this run: not recorded (schema news-typing-decomposition-v3)", markdown);
        Assert.DoesNotContain("batch `(none)`", markdown);
    }

    /// <summary>
    /// The gate is on the KNOWN pre-v4 tags, never on "equals the current tag" — a future schema must keep
    /// rendering a real measurement rather than silently reporting it as unrecorded.
    /// </summary>
    [Fact]
    public void UnrecognisedSchemaVersion_StillRendersTheRecordedDiagnostics()
    {
        var cohort = Cohort("a", "model-a", NewsObservationCaptureMode.ProspectiveRss, 4, 0, 2, 0) with
        {
            RetrySelected = 1,
            CandidatePrioritySelected = 3,
            GeneralSelected = 1,
            ProviderCallsAttempted = 4,
        };

        var markdown = NewsTypingDecompositionRenderer.RenderMarkdown(
            Document(Company(cohort)) with { SchemaVersion = "news-typing-decomposition-v5" });

        Assert.Contains(
            "selected this pass: 1 retry, 3 judgment-candidate priority, 1 general (4 provider call(s) "
                + "made)",
            markdown);
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
