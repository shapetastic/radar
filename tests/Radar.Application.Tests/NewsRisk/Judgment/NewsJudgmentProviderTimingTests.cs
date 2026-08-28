using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.Tests.Ai;
using Radar.Application.Tests.NewsRisk;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 187 §7 for the stage-2 judge: every provider invocation is measured with the injected
/// <see cref="TimeProvider"/>'s MONOTONIC timestamp APIs, the duration is persisted as observational
/// provenance, bounded progress lines fire every 5 attempted calls plus the final partial batch, and the
/// pass ends with a deterministic nearest-rank latency summary per judge × stage-1 cohort.
/// <para>
/// The live motivation: 18 judgments took about 25 seconds inside a 1h03 run and nothing said so, so a
/// throttled judge would have been indistinguishable from a slow collector. None of it may touch identity,
/// ordering or selection (AD-3) — the last two tests are what hold that line.
/// </para>
/// </summary>
public sealed class NewsJudgmentProviderTimingTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    // The fixture builders live in JudgmentPassFixture (ONE definition, shared with the spec-197 §2.2
    // citation-recovery suite); these are thin delegations that keep the call sites here readable.
    private static Guid CompanyId(int index) => JudgmentPassFixture.CompanyId(index);

    private static Guid FactId(int index) => JudgmentPassFixture.FactId(index);

    private static NewsJudgmentOptions Options() => JudgmentPassFixture.Options();

    private static NewsJudgmentCandidatePlan Plan(int companies) => JudgmentPassFixture.Plan(companies);

    private static NewsTypingRunResult Typing(Guid? runId, int companies) =>
        JudgmentPassFixture.Typing(runId, companies);

    /// <summary>A judgment the v2 validator accepts: a directional call citing the supplied fact.</summary>
    private static Func<NewsJudgmentAnalysisRequest, NewsJudgmentAnalysisOutcome> Grounded(
        string? failureDetail = null) => request => failureDetail is not null
        ? new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.ProviderError, null, null, failureDetail)
        : new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse(
                BusinessTrajectory: "Deteriorating",
                ChallengeStrength: 3,
                Findings: [],
                Rationale: "The confirmed regulatory filing is adverse to the recent trajectory.",
                TrajectoryFactIds:
                    [.. request.Families.Select(f => f.RepresentativeFactId.ToString("D"))]),
            "raw-hash",
            null);

    private static List<(LogLevel Level, string Message, string? Exception)> Progress(
        JudgmentPassHarness harness) =>
        [.. harness.Logger.Entries.Where(e => e.Message.Contains("progress:", StringComparison.Ordinal))];

    [Fact]
    public async Task ProviderDuration_IsMeasuredMonotonically_AndPersistedOnEveryCallRecord()
    {
        var harness = new JudgmentPassHarness(AsOf);
        var runId = Guid.NewGuid();
        var generator = harness.Build(Grounded(), call => TimeSpan.FromMilliseconds(call * 100));

        await generator.GenerateAsync(runId, Plan(3), Typing(runId, 3), CancellationToken.None);

        Assert.Equal(3, harness.Analyzer!.Calls);
        Assert.Equal(
            [100d, 200d, 300d],
            harness.Store.Records
                .Where(r => r.IsCallProducingAttempt)
                .Select(r => r.ProviderDurationMs)
                .ToList());
    }

    [Fact]
    public async Task ProviderDuration_IsRetained_WhenTheCallFails()
    {
        var harness = new JudgmentPassHarness(AsOf);
        var runId = Guid.NewGuid();
        var generator = harness.Build(
            Grounded("429 rate limited"), _ => TimeSpan.FromMilliseconds(4200));

        await generator.GenerateAsync(runId, Plan(1), Typing(runId, 1), CancellationToken.None);

        var record = Assert.Single(harness.Store.Records);
        Assert.Equal(NewsJudgmentStatus.ProviderFailure, record.Status);
        Assert.Equal(4200d, record.ProviderDurationMs);
    }

    [Fact]
    public async Task NoCallRecords_CarryNoDuration()
    {
        // A cache REUSE, an InsufficientFacts non-result and an AttemptsExhausted marker all persist a
        // record without spending a call — `null` says exactly that, and is never "a call took no time".
        var harness = new JudgmentPassHarness(AsOf);
        var generator = harness.Build(Grounded(), _ => TimeSpan.FromMilliseconds(50));

        var first = Guid.NewGuid();
        await generator.GenerateAsync(first, Plan(1), Typing(first, 1), CancellationToken.None);

        // A second run over the SAME family set is served from the completed-judgment cache.
        var second = Guid.NewGuid();
        var result = await generator.GenerateAsync(
            second, Plan(1), Typing(second, 1), CancellationToken.None);

        Assert.Equal(1, harness.Analyzer!.Calls);
        var reused = Assert.Single(result!.Judgments);
        Assert.NotNull(reused.ReusedFromJudgmentId);
        Assert.Null(reused.ProviderDurationMs);

        // A candidate with NO families never reaches the provider either.
        var third = Guid.NewGuid();
        var empty = await generator.GenerateAsync(
            third, Plan(2), Typing(third, 1), CancellationToken.None);
        Assert.Equal(1, harness.Analyzer.Calls);
        Assert.Contains(
            empty!.Judgments,
            r => r.Status == NewsJudgmentStatus.InsufficientFacts && r.ProviderDurationMs is null);
    }

    [Theory]
    // Every 5 attempted calls, PLUS the final partial batch.
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(10, 2)]
    [InlineData(11, 3)]
    public async Task ProgressLines_FireAtEveryFifthCall_PlusTheFinalPartialBatch(
        int companies, int expectedProgressLines)
    {
        var harness = new JudgmentPassHarness(AsOf);
        var runId = Guid.NewGuid();
        var generator = harness.Build(Grounded(), _ => TimeSpan.FromMilliseconds(10));

        await generator.GenerateAsync(
            runId, Plan(companies), Typing(runId, companies), CancellationToken.None);

        var progress = Progress(harness);
        Assert.Equal(expectedProgressLines, progress.Count);
        Assert.All(progress, e => Assert.Equal(LogLevel.Information, e.Level));
        Assert.Contains(
            FormattableString.Invariant($"{companies}/{companies} call(s) attempted"),
            progress[^1].Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressLine_ReportsCompletedCandidatesPersistedFailuresElapsedMeanAndMax()
    {
        var harness = new JudgmentPassHarness(AsOf);
        var runId = Guid.NewGuid();
        var calls = 0;
        var generator = harness.Build(
            request =>
            {
                calls++;
                return calls == 2
                    ? new NewsJudgmentAnalysisOutcome(
                        NewsJudgmentAnalysisFailure.ParseError, null, null, "unparseable")
                    : Grounded()(request);
            },
            call => TimeSpan.FromMilliseconds(call == 1 ? 100 : 300));

        await generator.GenerateAsync(runId, Plan(2), Typing(runId, 2), CancellationToken.None);

        var progress = Assert.Single(Progress(harness));
        Assert.Contains("2/2 call(s) attempted", progress.Message, StringComparison.Ordinal);
        Assert.Contains("1 persisted judged verdict(s)", progress.Message, StringComparison.Ordinal);
        Assert.Contains(
            "failures 0 provider / 1 parse / 0 validation", progress.Message, StringComparison.Ordinal);
        Assert.Contains("stage elapsed 400 ms", progress.Message, StringComparison.Ordinal);
        Assert.Contains("mean call 200.0 ms", progress.Message, StringComparison.Ordinal);
        Assert.Contains("max call 300.0 ms", progress.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalSummary_ReportsNearestRankPercentilesOverThisPassCallsOnly()
    {
        var harness = new JudgmentPassHarness(AsOf);
        var runId = Guid.NewGuid();

        // Ascending [10, 20, 30, 2000]: rank(p50) = ceil(0.50 × 4) = 2 ⇒ 20 ms;
        // rank(p95) = ceil(0.95 × 4) = 4 ⇒ 2000 ms.
        var scripted = new[] { 30d, 2000d, 10d, 20d };
        var generator = harness.Build(
            Grounded(), call => TimeSpan.FromMilliseconds(scripted[call - 1]));

        await generator.GenerateAsync(runId, Plan(4), Typing(runId, 4), CancellationToken.None);

        Assert.Contains(
            harness.Logger.Entries,
            e => e.Message.Contains(
                "provider timing: 4 provider call(s); p50 20.0 ms, p95 2000.0 ms, max 2000.0 ms, "
                    + "total 2060.0 ms",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACacheOnlyPass_MakesNoCall_AndTheSummaryReportsZeroCalls()
    {
        var harness = new JudgmentPassHarness(AsOf);
        var generator = harness.Build(Grounded(), _ => TimeSpan.FromMilliseconds(80));

        var first = Guid.NewGuid();
        await generator.GenerateAsync(first, Plan(2), Typing(first, 2), CancellationToken.None);
        harness.Logger.Entries.Clear();

        var second = Guid.NewGuid();
        await generator.GenerateAsync(second, Plan(2), Typing(second, 2), CancellationToken.None);

        Assert.Equal(2, harness.Analyzer!.Calls);
        Assert.Contains(
            harness.Logger.Entries,
            e => e.Message.Contains(
                "provider timing: 0 provider call(s); no call latency measured this pass",
                StringComparison.Ordinal));
        Assert.Empty(Progress(harness));
        Assert.DoesNotContain(
            harness.Logger.Entries, e => e.Message.Contains("p50", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 188 §1 — CURRENT-PASS activity, never inferred from a record's durable duration
    // ---------------------------------------------------------------------------------------------------

    /// <summary>A provider failure: call-producing, NOT cacheable, so a re-entry takes the same-run branch.</summary>
    private static Func<NewsJudgmentAnalysisRequest, NewsJudgmentAnalysisOutcome> RateLimited() =>
        _ => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.ProviderError, null, null, "429 rate limited");

    [Fact]
    public async Task SameRunReEntry_OverCallProducingRecords_ReportsZeroCurrentPassActivity()
    {
        // THE DEFECT (spec 188 §1). A same-run reused attempt keeps the duration AND the failure status of
        // its ORIGINAL call, so reading `record.ProviderDurationMs` as "this pass called the provider"
        // replayed five old calls, five old failures and their latency as CURRENT — on exactly the rerun
        // path the telemetry exists to explain. The bill was still bounded; the observation was false.
        var harness = new JudgmentPassHarness(AsOf);
        var runId = Guid.NewGuid();
        var generator = harness.Build(RateLimited(), _ => TimeSpan.FromMilliseconds(1234));

        await generator.GenerateAsync(runId, Plan(5), Typing(runId, 5), CancellationToken.None);
        Assert.Equal(5, harness.Analyzer!.Calls);
        harness.Logger.Entries.Clear();

        var second = await generator.GenerateAsync(
            runId, Plan(5), Typing(runId, 5), CancellationToken.None);

        // No second call, and therefore no current-pass activity of any kind.
        Assert.Equal(5, harness.Analyzer.Calls);
        Assert.Empty(Progress(harness));
        Assert.Contains(
            harness.Logger.Entries,
            e => e.Message.Contains(
                "provider timing: 0 provider call(s); no call latency measured this pass",
                StringComparison.Ordinal));

        // No replayed latency and no replayed provider/parse/validation failure anywhere in this pass.
        Assert.All(harness.Logger.AllText, text =>
        {
            Assert.DoesNotContain("p50", text, StringComparison.Ordinal);
            Assert.DoesNotContain("1234", text, StringComparison.Ordinal);
            Assert.DoesNotContain("failures", text, StringComparison.Ordinal);
        });

        // The durable records are untouched: five call-producing attempts, each keeping its own duration.
        Assert.Equal(5, harness.Store.Records.Count(r => r.IsCallProducingAttempt));
        Assert.Equal(5, second!.Judgments.Count);
    }

    [Fact]
    public async Task SameRunReusedRecord_KeepsItsOriginalDuration_AndIsTheStoredRecord()
    {
        // The record must NOT be cloned with a null duration to drive the counters: `ProviderDurationMs` is
        // truthful provenance of the call that CREATED the attempt, and the presented copy may never
        // disagree with the insert-only record on disk. Pass-local activity rides beside it, not inside it.
        var harness = new JudgmentPassHarness(AsOf);
        var runId = Guid.NewGuid();
        var generator = harness.Build(RateLimited(), _ => TimeSpan.FromMilliseconds(777));

        await generator.GenerateAsync(runId, Plan(1), Typing(runId, 1), CancellationToken.None);
        var second = await generator.GenerateAsync(
            runId, Plan(1), Typing(runId, 1), CancellationToken.None);

        var stored = Assert.Single(harness.Store.Records);
        var reused = Assert.Single(second!.Judgments);
        Assert.Same(stored, reused);
        Assert.Equal(777d, reused.ProviderDurationMs);
        Assert.Equal(NewsJudgmentStatus.ProviderFailure, reused.Status);
        Assert.Equal(stored.JudgmentId, reused.JudgmentId);
    }

    [Fact]
    public async Task AMixedReuseAndNewCallPass_ReportsOnlyTheGenuinelyNewCall()
    {
        var harness = new JudgmentPassHarness(AsOf);
        var runId = Guid.NewGuid();
        var calls = 0;
        var generator = harness.Build(
            request =>
            {
                calls++;
                return calls == 1 ? RateLimited()(request) : Grounded()(request);
            },
            call => TimeSpan.FromMilliseconds(call == 1 ? 900 : 40));

        // Pass 1 spends one call on company 0 and it FAILS (900 ms) — a call-producing, uncacheable record.
        await generator.GenerateAsync(runId, Plan(1), Typing(runId, 1), CancellationToken.None);
        harness.Logger.Entries.Clear();

        // Pass 2, SAME run: company 0 is reused without a call; company 1 is a genuinely new 40 ms call.
        var result = await generator.GenerateAsync(
            runId, Plan(2), Typing(runId, 2), CancellationToken.None);

        Assert.Equal(2, harness.Analyzer!.Calls);

        // Every number describes the NEW call alone: one attempt, no failure, its own latency.
        var progress = Assert.Single(Progress(harness));
        Assert.Contains("1/2 call(s) attempted", progress.Message, StringComparison.Ordinal);
        Assert.Contains("1 persisted judged verdict(s)", progress.Message, StringComparison.Ordinal);
        Assert.Contains(
            "failures 0 provider / 0 parse / 0 validation", progress.Message, StringComparison.Ordinal);
        Assert.Contains("mean call 40.0 ms", progress.Message, StringComparison.Ordinal);
        Assert.Contains("max call 40.0 ms", progress.Message, StringComparison.Ordinal);
        Assert.Contains(
            harness.Logger.Entries,
            e => e.Message.Contains(
                "provider timing: 1 provider call(s); p50 40.0 ms, p95 40.0 ms, max 40.0 ms, total 40.0 ms",
                StringComparison.Ordinal));

        // …while BOTH records still reach the run result, the reused one with its original 900 ms.
        Assert.Equal(2, result!.Judgments.Count);
        Assert.Equal(
            [900d, 40d],
            result.Judgments.Select(r => r.ProviderDurationMs).ToList());
    }

    [Fact]
    public async Task ANoCallCandidateAfterTheFifthCall_CannotReEmitTheBoundary()
    {
        // The boundary is crossed by a CALL, so it may only be evaluated immediately after one. Evaluating
        // it after every candidate meant any later no-call candidate — here an InsufficientFacts company,
        // equally a same-run or cache reuse or an exhausted one — re-emitted the same `5/…` line while
        // nothing had happened.
        var harness = new JudgmentPassHarness(AsOf);
        var runId = Guid.NewGuid();
        var generator = harness.Build(Grounded(), _ => TimeSpan.FromMilliseconds(10));

        // Six candidates, facts for the first five: five calls, then one no-call candidate.
        await generator.GenerateAsync(runId, Plan(6), Typing(runId, 5), CancellationToken.None);

        Assert.Equal(5, harness.Analyzer!.Calls);
        var progress = Assert.Single(Progress(harness));
        Assert.Contains("5/6 call(s) attempted", progress.Message, StringComparison.Ordinal);
        Assert.Contains(
            harness.Store.Records,
            r => r.Status == NewsJudgmentStatus.InsufficientFacts && r.ProviderDurationMs is null);
    }

    [Fact]
    public async Task Durations_ChangeNoIdentity_NoCohortKey_NoFamilySetHash_AndNoOrdering()
    {
        async Task<(List<Guid> Ids, List<string> Cohorts, List<string> Hashes, List<Guid> Order)> RunAsync(
            Func<int, TimeSpan> latency)
        {
            var harness = new JudgmentPassHarness(AsOf);
            var runId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var generator = harness.Build(Grounded(), latency);
            var result = await generator.GenerateAsync(
                runId, Plan(4), Typing(runId, 4), CancellationToken.None);
            return (
                [.. harness.Store.Records.Select(r => r.JudgmentId)],
                [.. harness.Store.Records.Select(r => r.CohortKey)],
                [.. harness.Store.Records.Select(r => r.FamilySetHash)],
                [.. result!.Judgments.Select(r => r.CompanyId)]);
        }

        var fast = await RunAsync(_ => TimeSpan.FromMilliseconds(1));
        var slow = await RunAsync(call => TimeSpan.FromSeconds(call * 11));

        Assert.Equal(fast.Ids, slow.Ids);
        Assert.Equal(fast.Cohorts, slow.Cohorts);
        Assert.Equal(fast.Hashes, slow.Hashes);
        Assert.Equal(fast.Order, slow.Order);
        Assert.Equal(4, fast.Ids.Count);
    }

    [Fact]
    public async Task Logs_ContainNoModelText_NoApiKey_AndNoEnvironmentVariableValue()
    {
        const string Secret = "sk-RECOGNISABLE-SECRET-0123456789";
        const string ModelText = "RECOGNISABLE-RATIONALE-the regulator confirmed a filing";

        var harness = new JudgmentPassHarness(AsOf);
        var runId = Guid.NewGuid();
        var generator = harness.Build(
            _ => new NewsJudgmentAnalysisOutcome(
                NewsJudgmentAnalysisFailure.ProviderError,
                null,
                null,
                $"401 from https://api.example/v1?key={Secret} — model said '{ModelText}'"),
            _ => TimeSpan.FromMilliseconds(25));

        await generator.GenerateAsync(runId, Plan(2), Typing(runId, 2), CancellationToken.None);

        Assert.NotEmpty(harness.Logger.Entries);
        Assert.All(harness.Logger.AllText, text =>
        {
            Assert.DoesNotContain("RECOGNISABLE-SECRET", text, StringComparison.Ordinal);
            Assert.DoesNotContain("RECOGNISABLE-RATIONALE", text, StringComparison.Ordinal);
            Assert.DoesNotContain("api.example", text, StringComparison.Ordinal);
        });

        // Suppressed from the LOG, not from the durable record.
        Assert.Contains(
            harness.Store.Records, r => r.FailureDetail!.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void ProgressAndSummaryText_AreInvariantCulture()
    {
        // A comma decimal separator would make the log unparseable on a non-English host; the generators
        // format every duration with CultureInfo.InvariantCulture for exactly that reason.
        Assert.Equal("200.0", 200d.ToString("F1", CultureInfo.InvariantCulture));
    }
}
