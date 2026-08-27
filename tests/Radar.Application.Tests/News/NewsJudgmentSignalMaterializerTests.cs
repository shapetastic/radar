using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.Filings;
using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Application.Tests.News;

/// <summary>
/// SPEC 194 §1.2 — the judgment-derived news signal. These are the §4 regressions: the Monday/Tuesday
/// failure shape spec 191 actually produced, the full provenance walk, the fail-closed eligibility rules,
/// idempotency, and the honest knowledge-time that keeps replay from seeing a signal Radar did not have.
/// <para>
/// Every fixture is CONSTRUCTED — no test reads mutable live data.
/// </para>
/// </summary>
public sealed class NewsJudgmentSignalMaterializerTests
{
    private const string MondayHeadline = "Acme reports a widening quarterly loss";
    private const string MondayBody =
        "Acme said quarterly revenue fell 18% and the company widened its full-year loss guidance.";
    private const string MondayCitation = "quarterly revenue fell 18%";

    private const string TuesdayHeadline = "Acme opens a new distribution centre";
    private const string TuesdayBody =
        "Acme opened a new distribution centre, adding capacity ahead of the holiday season.";

    // ---------------------------------------------------------------------------------------------
    // §4.1 — the Monday/Tuesday failure shape.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task MondaysCitedDeterioration_MaterializesOneNegativeSignalAnchoredToMondaysEvidence_AndTuesdaysArticleStaysNeutral()
    {
        // THE SHAPE SPEC 191 GOT WRONG. Monday's article caused a Deteriorating judgment. Tuesday a new,
        // positive-looking article arrives. Under spec 191 the extractor asked "does this company have a
        // judgment?" while extracting TUESDAY's article and stamped Monday's negative verdict on it — one
        // judged call multiplied by the company's news volume.
        //
        // MUTATION PROOF (run, not asserted): reverting to company-only inheritance — i.e. anchoring the
        // materialized signal on the company's LATEST news evidence instead of on the evidence the
        // judgment cited — makes the first block red, because the signal's EvidenceId becomes Tuesday's.
        // Changing `anchor` in NewsJudgmentSignalMaterializer.MaterializeOneAsync to select from the whole
        // evidence set rather than from `evidenceIds` reproduces exactly that failure.
        var scenario = Scenario.Build();
        var materializer = scenario.Materializer();

        var summary = await materializer.MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(1, summary.Eligible);
        Assert.Equal(1, summary.Materialized);

        var stored = Assert.Single(scenario.FileStore.Writes);
        Assert.Equal(SignalType.MediaAttention, stored.Signal.Type);
        Assert.Equal(SignalDirection.Negative, stored.Signal.Direction);

        // Anchored to MONDAY's evidence — the article the judgment actually cited.
        Assert.Equal(scenario.MondayEvidence.Id, stored.Signal.EvidenceId);
        Assert.NotEqual(scenario.TuesdayEvidence.Id, stored.Signal.EvidenceId);

        // TUESDAY's newly collected article, extracted the ordinary way, is Neutral and carries no
        // judgment provenance of any kind. The extractor has no news-read dependency at all since §1.1.
        var extractor = new KeywordSignalExtractor(
            NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights());
        var extracted = await extractor.ExtractAsync(scenario.TuesdayEvidence, CancellationToken.None);
        var tuesdaySignal = Assert.Single(extracted.Signals);

        Assert.Equal(nameof(SignalType.MediaAttention), tuesdaySignal.SignalType);
        Assert.Equal(nameof(SignalDirection.Neutral), tuesdaySignal.Direction);
        Assert.Null(tuesdaySignal.MetadataJson);
        Assert.DoesNotContain(
            scenario.JudgmentId.ToString("D"), tuesdaySignal.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // §4.2 — the whole provenance chain.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryMetadataId_WalksSignalToJudgmentToFactToObservationToEvidence()
    {
        var scenario = Scenario.Build();

        await scenario.Materializer().MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        var signal = Assert.Single(scenario.FileStore.Writes).Signal;

        Assert.True(EvidenceMetadata.TryRead(signal.MetadataJson, out var metadata, out var hints));
        Assert.Empty(hints);

        // The versioned marker the §1.4 admission transform reads to leave this signal alone.
        Assert.Equal(
            NewsDirectionalSignalMetadata.JudgmentSignalVersionValue,
            metadata[NewsDirectionalSignalMetadata.JudgmentSignalVersionKey]);

        // signal → judgment.
        Assert.Equal(
            scenario.JudgmentId.ToString("D"),
            metadata[NewsDirectionalSignalMetadata.JudgmentIdKey]);
        Assert.Equal(
            scenario.CohortKey, metadata[NewsDirectionalSignalMetadata.JudgmentCohortKeyKey]);
        Assert.Equal("deteriorating", metadata[NewsDirectionalSignalMetadata.TrajectoryKey]);

        // judgment → fact.
        var factIds = NewsDirectionalSignalMetadata.ParseGuidList(
            metadata[NewsDirectionalSignalMetadata.TrajectoryFactIdsKey]);
        Assert.Equal([scenario.FactId], factIds);
        var factRef = scenario.Typing.Cohorts[0].FactsById[factIds[0]];

        // fact → observation.
        var observationIds = NewsDirectionalSignalMetadata.ParseGuidList(
            metadata[NewsDirectionalSignalMetadata.SourceObservationIdsKey]);
        Assert.Equal([scenario.MondayObservation.ObservationId], observationIds);
        Assert.Equal(observationIds[0], factRef.ObservationId);

        // observation → evidence, and the anchor is one of them.
        var evidenceIds = NewsDirectionalSignalMetadata.ParseGuidList(
            metadata[NewsDirectionalSignalMetadata.CitedEvidenceIdsKey]);
        Assert.Equal([scenario.MondayEvidence.Id], evidenceIds);
        Assert.Contains(signal.EvidenceId, evidenceIds);

        // The signal carries the judgment's own company and a citation verbatim from the anchor evidence.
        Assert.Equal(scenario.CompanyId, signal.CompanyId);
        Assert.Equal("Acme Corporation", signal.CompanyMention);
        Assert.Equal(MondayCitation, signal.SupportingExcerpt);
        Assert.True(ExtractedSignalMapper.IsExcerptSupportedByEvidence(
            scenario.MondayEvidence, signal.SupportingExcerpt));

        // ObservedAtUtc is the ANCHOR EVIDENCE's real publication instant, not the judgment's clock.
        Assert.Equal(scenario.MondayEvidence.PublishedAtUtc, signal.ObservedAtUtc);

        // Spec 191's declared magnitudes, retained: Novelty 4, Confidence 0.5, base strength 4 with no
        // findings and incomplete typing.
        Assert.Equal(4, signal.Novelty);
        Assert.Equal(0.5m, signal.Confidence);
        Assert.Equal(4, signal.Strength);

        // House output-language rule.
        Assert.False(AdviceLanguageGuard.ContainsAdviceLanguage(signal.Reason));
    }

    [Fact]
    public async Task StrengthScalesWithFindingsAndCompleteTyping()
    {
        var scenario = Scenario.Build(findings: 5, typingCompleteness: NewsTypingCompleteness.Complete);

        await scenario.Materializer().MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        // 4 base + min(5, 3) findings + 1 complete-typing bonus.
        Assert.Equal(8, Assert.Single(scenario.FileStore.Writes).Signal.Strength);
    }

    // ---------------------------------------------------------------------------------------------
    // §4.3 — every ineligible shape creates no directional signal and names its reason.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(NewsJudgmentTrajectory.Mixed)]
    [InlineData(NewsJudgmentTrajectory.Unknown)]
    public async Task NonDirectionalTrajectory_MaterializesNothing_AndIsNamed(
        NewsJudgmentTrajectory trajectory)
    {
        var scenario = Scenario.Build(trajectory: trajectory);

        var summary = await scenario.Materializer().MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(0, summary.Eligible);
        Assert.Equal(0, summary.Materialized);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.NonDirectionalTrajectory));
        Assert.Empty(scenario.FileStore.Writes);
    }

    [Theory]
    [InlineData(NewsJudgmentStatus.ValidationFailed)]
    [InlineData(NewsJudgmentStatus.ProviderFailure)]
    [InlineData(NewsJudgmentStatus.ParseFailure)]
    [InlineData(NewsJudgmentStatus.InsufficientFacts)]
    [InlineData(NewsJudgmentStatus.AttemptsExhausted)]
    public async Task NonJudgedStatus_MaterializesNothing_AndIsNamed(NewsJudgmentStatus status)
    {
        var scenario = Scenario.Build(status: status);

        var summary = await scenario.Materializer().MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(0, summary.Eligible);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.NotJudged));
        Assert.Empty(scenario.FileStore.Writes);
    }

    [Fact]
    public async Task NonPresentationCohort_MaterializesNothing_AndIsNamed()
    {
        // A real, complete, directional judgment — from a cohort that was NOT designated. Only the
        // prospectively designated cohort may contribute a direction, so the display and the score cannot
        // come from different models.
        var scenario = Scenario.Build(cohortKey: "some-other-judge|prompt|schema|stage1=x|families=y");

        var summary = await scenario.Materializer().MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(0, summary.Eligible);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.NotPresentationCohort));
        Assert.Empty(scenario.FileStore.Writes);
    }

    [Fact]
    public async Task PresentationCohortUnresolvable_MaterializesNothing_AndIsCountedOncePerPass()
    {
        var scenario = Scenario.Build();
        var materializer = scenario.Materializer(
            options: MaterializerFixture.Options(presentationExtractor: "a-reader-that-did-not-run"));

        var summary = await materializer.MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(0, summary.Eligible);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.PresentationCohortUnresolved));
        Assert.Empty(scenario.FileStore.Writes);
    }

    [Fact]
    public async Task MissingTrajectoryFactIds_MaterializeNothing_AndAreNamed()
    {
        // A news-judgment-v1 record: the field did not exist, so `null` means NOT RECORDED — which is
        // precisely why it cannot ground a direction.
        var scenario = Scenario.Build(omitTrajectoryFactIds: true);

        var summary = await scenario.Materializer().MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(0, summary.Eligible);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.NoTrajectoryFactIds));
    }

    [Fact]
    public async Task PartiallyResolvableCitationSet_MaterializesNothing_AndIsNamed()
    {
        // ALL-OR-NOTHING: one cited fact resolves, one does not. Scoring the resolvable half would rest a
        // company-level verdict on a subset of the evidence that produced it, invisibly.
        var scenario = Scenario.Build();
        var unknownFactId = Guid.NewGuid();
        var record = scenario.Record with
        {
            TrajectoryFactIds = [scenario.FactId, unknownFactId],
        };

        var summary = await scenario.Materializer().MaterializeAsync(
            MaterializerFixture.RunResult(record), scenario.Typing, CancellationToken.None);

        Assert.Equal(1, summary.Eligible);
        Assert.Equal(0, summary.Materialized);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.UnresolvedFact));
        Assert.Empty(scenario.FileStore.Writes);
    }

    [Fact]
    public async Task CitedObservationThatDoesNotJoin_MaterializesNothing_AndIsNamed()
    {
        // The fact resolves, but its observation's headline matches no news evidence — the join's
        // fail-closed no-match rule, surfaced by name.
        var scenario = Scenario.Build(observationHeadline: "A headline no evidence carries");

        var summary = await scenario.Materializer().MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(1, summary.Eligible);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.UnresolvedObservation));
        Assert.Empty(scenario.FileStore.Writes);
    }

    [Fact]
    public async Task CitedFactBelongingToAnotherCompany_MaterializesNothing_AndIsNamed()
    {
        var scenario = Scenario.Build(factCompanyId: Guid.NewGuid());

        var summary = await scenario.Materializer().MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(1, summary.Eligible);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.CompanyMismatch));
        Assert.Empty(scenario.FileStore.Writes);
    }

    [Fact]
    public async Task CitationNotFoundInTheAnchorEvidence_MaterializesNothing_AndIsNamed()
    {
        var scenario = Scenario.Build(citation: "a sentence that appears nowhere in the article");

        var summary = await scenario.Materializer().MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(1, summary.Eligible);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.ExcerptNotInEvidence));
        Assert.Empty(scenario.FileStore.Writes);
    }

    [Fact]
    public async Task NothingEligible_NeverReadsTheEvidenceStore()
    {
        // The cheap gates run first precisely so an ordinary all-Mixed run does not hydrate the whole
        // evidence store to discover it has nothing to do.
        var scenario = Scenario.Build(trajectory: NewsJudgmentTrajectory.Mixed);
        var materializer = scenario.Materializer(evidenceRepository: new ThrowingEvidenceRepository());

        var summary = await materializer.MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(0, summary.Eligible);
    }

    // ---------------------------------------------------------------------------------------------
    // §4.4 — idempotency.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RerunningTheSameJudgment_ReusesTheDeterministicId_ReviewsNothingAndWritesNothing()
    {
        var scenario = Scenario.Build();
        var reviewer = new CountingReviewer(new FixedClock(MaterializerFixture.Now));
        var materializer = scenario.Materializer(reviewer: reviewer);

        var first = await materializer.MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);
        var firstSignal = Assert.Single(scenario.FileStore.Writes).Signal;

        var second = await materializer.MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(1, first.Materialized);
        Assert.Equal(0, second.Materialized);
        Assert.Equal(1, second.AlreadyMaterialized);

        // No SECOND review record (a second immutable review for one signal is a provenance lie), and no
        // second write (the stored signal is the record of what Radar actually did).
        Assert.Equal(1, reviewer.Calls);
        Assert.Single(scenario.FileStore.Writes);

        // The id is a pure function of the judgment id.
        Assert.Equal(
            NewsJudgmentSignalMaterializer.SignalIdFor(scenario.JudgmentId), firstSignal.Id);
    }

    [Fact]
    public void SignalId_IsDeterministicAndJudgmentScoped()
    {
        var a = Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111");
        var b = Guid.Parse("bbbbbbbb-2222-4222-8222-222222222222");

        Assert.Equal(
            NewsJudgmentSignalMaterializer.SignalIdFor(a),
            NewsJudgmentSignalMaterializer.SignalIdFor(a));
        Assert.NotEqual(
            NewsJudgmentSignalMaterializer.SignalIdFor(a),
            NewsJudgmentSignalMaterializer.SignalIdFor(b));
    }

    // ---------------------------------------------------------------------------------------------
    // §4.5 — knowledge time is NOW, and replay cannot see it.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AReusedOldJudgmentMaterializedToday_IsCreatedToday_AndAReplayJustBeforeTodayCannotSeeIt()
    {
        // The judgment is from Monday and was reused; the signal is created NOW. Backdating it to the
        // judgment instant would let a spec-136 replay at an earlier as-of see a signal Radar did not have.
        var scenario = Scenario.Build();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var fileStore = new FileSignalStore(
                new FileSignalStoreOptions { RootDirectory = tempDir },
                NullLogger<FileSignalStore>.Instance);
            var materializer = scenario.Materializer(fileStore: fileStore);

            await materializer.MaterializeAsync(
                scenario.RunResult, scenario.Typing, CancellationToken.None);

            var signalId = NewsJudgmentSignalMaterializer.SignalIdFor(scenario.JudgmentId);
            var windowStart = MaterializerFixture.Monday.AddDays(-30);

            // Known-as-of NOW: the signal is visible.
            var visible = await fileStore.ReadApprovedInWindowAsync(
                scenario.CompanyId,
                windowStart,
                MaterializerFixture.Now,
                MaterializerFixture.Now,
                CancellationToken.None);
            Assert.Contains(visible, s => s.Id == signalId);
            Assert.Equal(MaterializerFixture.Now, visible.Single(s => s.Id == signalId).CreatedAtUtc);

            // Known-as-of one tick BEFORE now — the spec-136 `CreatedAtUtc <= knownAsOfUtc` predicate.
            // The judgment existed on Monday; the SIGNAL did not, so a replay must not see it.
            var replayed = await fileStore.ReadApprovedInWindowAsync(
                scenario.CompanyId,
                windowStart,
                MaterializerFixture.Now,
                MaterializerFixture.Now.AddTicks(-1),
                CancellationToken.None);
            Assert.DoesNotContain(replayed, s => s.Id == signalId);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Durable-write failure, per-company isolation, cancellation.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedDurableWrite_IsCounted_AndNotReportedAsMaterialized()
    {
        var scenario = Scenario.Build();
        scenario.FileStore.FailWrites = true;

        var summary = await scenario.Materializer().MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        Assert.Equal(1, summary.Eligible);
        Assert.Equal(0, summary.Materialized);
        Assert.Equal(1, summary.WriteFailed);

        // The write was ATTEMPTED (so the failure is diagnosable) and nothing was retried or queued.
        Assert.Single(scenario.FileStore.Writes);
    }

    [Fact]
    public async Task OneUnexpectedCompanyFailure_DoesNotStopTheRest()
    {
        var scenario = Scenario.Build(label: " (first)");
        var other = Scenario.Build(label: " (second)");

        // Both judgments in one pass, over the union of both scenarios' stores.
        var facts = new Dictionary<Guid, NewsTypingFactRef>
        {
            [scenario.FactId] = scenario.FactRef,
            [other.FactId] = other.FactRef,
        };
        var typing = MaterializerFixture.Typing(facts);
        var archive = new FakeObservationArchive(scenario.MondayObservation, other.MondayObservation);
        var evidence = new InMemoryEvidenceRepository();
        await evidence.AddIfNewAsync(scenario.MondayEvidence, CancellationToken.None);
        await evidence.AddIfNewAsync(other.MondayEvidence, CancellationToken.None);

        var fileStore = new RecordingSignalFileStore();
        var exploding = new ExplodingSignalRepository(
            NewsJudgmentSignalMaterializer.SignalIdFor(scenario.JudgmentId));
        var materializer = new NewsJudgmentSignalMaterializer(
            archive,
            evidence,
            exploding,
            new InMemorySignalReviewRepository(),
            fileStore,
            new DeterministicSignalReviewer(
                new FixedClock(MaterializerFixture.Now),
                NullLogger<DeterministicSignalReviewer>.Instance),
            MaterializerFixture.Options(),
            MaterializerFixture.Judges(),
            new FixedClock(MaterializerFixture.Now),
            NullLogger<NewsJudgmentSignalMaterializer>.Instance);

        var summary = await materializer.MaterializeAsync(
            MaterializerFixture.RunResult(scenario.Record, other.Record),
            typing,
            CancellationToken.None);

        Assert.Equal(2, summary.Eligible);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.UnexpectedFailure));
        Assert.Equal(1, summary.Materialized);
        Assert.Equal(other.MondayEvidence.Id, Assert.Single(fileStore.Writes).Signal.EvidenceId);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var scenario = Scenario.Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scenario.Materializer().MaterializeAsync(scenario.RunResult, scenario.Typing, cts.Token));
    }

    // ---------------------------------------------------------------------------------------------
    // Coherence with the §1.4 admission transform.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMaterializedSignalSatisfiesTheLegacyInheritanceTransformsValidV1Envelope()
    {
        // The §1.4 transform fails an unverifiable directional news signal CLOSED to Neutral. A signal this
        // class writes must pass its validity gate, or the correction would neutralize its own fix.
        var scenario = Scenario.Build();
        await scenario.Materializer().MaterializeAsync(
            scenario.RunResult, scenario.Typing, CancellationToken.None);

        var signal = Assert.Single(scenario.FileStore.Writes).Signal;
        var result = LegacyNewsInheritanceNeutralization.Apply(new List<Signal> { signal });

        Assert.Equal(0, result.TotalNeutralized);
        Assert.Same(signal, Assert.Single(result.Signals));
        Assert.Equal(SignalDirection.Negative, result.Signals[0].Direction);
    }

    // ---------------------------------------------------------------------------------------------
    // The summary's accounting invariants — pinned here because an invariant stated only in a doc
    // comment is how accounting quietly drifts.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSummaryAccounting_Reconciles_AcrossAMixedPass()
    {
        // One pass containing every shape at once: an eligible judgment that materializes, an eligible one
        // that fails a per-record provenance rule, and one of each per-record gate.
        var materializedScenario = Scenario.Build(label: " (materialized)");
        var mismatch = Scenario.Build(factCompanyId: Guid.NewGuid(), label: " (mismatch)");
        var notJudged = Scenario.Build(
            status: NewsJudgmentStatus.ValidationFailed, label: " (not judged)");
        var nonDirectional = Scenario.Build(
            trajectory: NewsJudgmentTrajectory.Unknown, label: " (unknown)");
        var otherCohort = Scenario.Build(
            cohortKey: "some-other-judge|prompt|schema|stage1=x|families=y", label: " (other cohort)");
        var noFactIds = Scenario.Build(omitTrajectoryFactIds: true, label: " (v1 record)");

        var facts = new Dictionary<Guid, NewsTypingFactRef>
        {
            [materializedScenario.FactId] = materializedScenario.FactRef,
            [mismatch.FactId] = mismatch.FactRef,
        };
        var archive = new FakeObservationArchive(
            materializedScenario.MondayObservation, mismatch.MondayObservation);
        var evidence = new InMemoryEvidenceRepository();
        await evidence.AddIfNewAsync(materializedScenario.MondayEvidence, CancellationToken.None);
        await evidence.AddIfNewAsync(mismatch.MondayEvidence, CancellationToken.None);

        var clock = new FixedClock(MaterializerFixture.Now);
        var materializer = new NewsJudgmentSignalMaterializer(
            archive,
            evidence,
            new InMemorySignalRepository(),
            new InMemorySignalReviewRepository(),
            new RecordingSignalFileStore(),
            new DeterministicSignalReviewer(clock, NullLogger<DeterministicSignalReviewer>.Instance),
            MaterializerFixture.Options(),
            MaterializerFixture.Judges(),
            clock,
            NullLogger<NewsJudgmentSignalMaterializer>.Instance);

        var summary = await materializer.MaterializeAsync(
            MaterializerFixture.RunResult(
                materializedScenario.Record,
                mismatch.Record,
                notJudged.Record,
                nonDirectional.Record,
                otherCohort.Record,
                noFactIds.Record),
            MaterializerFixture.Typing(facts),
            CancellationToken.None);

        Assert.Equal(6, summary.JudgmentsConsidered);
        Assert.Equal(2, summary.Eligible);
        Assert.Equal(1, summary.Materialized);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.CompanyMismatch));
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.NotJudged));
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.NonDirectionalTrajectory));
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.NotPresentationCohort));
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.NoTrajectoryFactIds));

        // Invariant 1 — holds on EVERY path: what happened to an eligible judgment sums to Eligible.
        Assert.Equal(summary.Eligible, EligibleOutcomes(summary));

        // Invariant 2 — holds whenever the presentation cohort RESOLVED: the per-record gates plus the
        // eligible judgments account for every judgment considered.
        Assert.Equal(summary.JudgmentsConsidered, PerRecordGates(summary) + summary.Eligible);
    }

    [Fact]
    public async Task PresentationCohortUnresolved_IsTheNamedExceptionToTheJudgmentsConsideredIdentity()
    {
        // The ONE documented exception. An unresolvable presentation cohort is a PASS-level fact, counted
        // exactly once rather than once per record, and the pass returns before any record is examined —
        // so the second invariant deliberately does not hold here, while the first still does.
        var scenario = Scenario.Build(label: " (a)");
        var second = Scenario.Build(label: " (b)");
        var materializer = scenario.Materializer(
            options: MaterializerFixture.Options(presentationExtractor: "a-reader-that-did-not-run"));

        var summary = await materializer.MaterializeAsync(
            MaterializerFixture.RunResult(scenario.Record, second.Record),
            scenario.Typing,
            CancellationToken.None);

        Assert.Equal(2, summary.JudgmentsConsidered);
        Assert.Equal(0, summary.Eligible);
        Assert.Equal(1, summary.SkipCount(NewsJudgmentSignalSkipReason.PresentationCohortUnresolved));

        // Invariant 1 still holds, trivially (0 = 0).
        Assert.Equal(summary.Eligible, EligibleOutcomes(summary));

        // Invariant 2 does NOT: no record reached a per-record gate, so the gates plus Eligible are 0
        // against 2 judgments considered. Asserted rather than merely noted, so the exception cannot be
        // "fixed" into a per-record count without this test saying so.
        Assert.Equal(0, PerRecordGates(summary) + summary.Eligible);
        Assert.NotEqual(summary.JudgmentsConsidered, PerRecordGates(summary) + summary.Eligible);
    }

    /// <summary>
    /// Everything that can happen to a judgment AFTER it passed the eligibility gates: the four outcome
    /// counters plus the per-record provenance skips.
    /// </summary>
    private static int EligibleOutcomes(NewsJudgmentSignalMaterializationSummary summary) =>
        summary.Materialized
        + summary.AlreadyMaterialized
        + summary.ValidationRejected
        + summary.WriteFailed
        + summary.SkipCount(NewsJudgmentSignalSkipReason.UnresolvedFact)
        + summary.SkipCount(NewsJudgmentSignalSkipReason.UnresolvedObservation)
        + summary.SkipCount(NewsJudgmentSignalSkipReason.CompanyMismatch)
        + summary.SkipCount(NewsJudgmentSignalSkipReason.ExcerptNotInEvidence)
        + summary.SkipCount(NewsJudgmentSignalSkipReason.UnexpectedFailure);

    /// <summary>The four gates evaluated once per RECORD, before eligibility is decided.</summary>
    private static int PerRecordGates(NewsJudgmentSignalMaterializationSummary summary) =>
        summary.SkipCount(NewsJudgmentSignalSkipReason.NotPresentationCohort)
        + summary.SkipCount(NewsJudgmentSignalSkipReason.NotJudged)
        + summary.SkipCount(NewsJudgmentSignalSkipReason.NonDirectionalTrajectory)
        + summary.SkipCount(NewsJudgmentSignalSkipReason.NoTrajectoryFactIds);

    /// <summary>
    /// One constructed end-to-end situation: a company, Monday's cited article, Tuesday's uncited article,
    /// the typed fact that names Monday's observation, and the judgment that cites that fact.
    /// </summary>
    private sealed record Scenario(
        Guid CompanyId,
        Guid JudgmentId,
        Guid FactId,
        string CohortKey,
        EvidenceItem MondayEvidence,
        EvidenceItem TuesdayEvidence,
        NewsObservationRecord MondayObservation,
        NewsTypingFactRef FactRef,
        NewsJudgmentRecord Record,
        NewsTypingRunResult Typing,
        NewsJudgmentRunResult RunResult,
        InMemoryEvidenceRepository Evidence,
        FakeObservationArchive Archive,
        RecordingSignalFileStore FileStore)
    {
        public static Scenario Build(
            NewsJudgmentTrajectory? trajectory = NewsJudgmentTrajectory.Deteriorating,
            NewsJudgmentStatus status = NewsJudgmentStatus.Judged,
            string? cohortKey = null,
            IReadOnlyList<Guid>? trajectoryFactIds = null,
            bool omitTrajectoryFactIds = false,
            Guid? factCompanyId = null,
            string? observationHeadline = null,
            string? citation = null,
            int findings = 0,
            NewsTypingCompleteness typingCompleteness = NewsTypingCompleteness.Backlog,
            // Distinguishes two scenarios built in ONE pass. The join is fail-closed on ambiguity, so two
            // companies sharing a normalized headline would legitimately resolve to nothing — real
            // behaviour, but not what a multi-company test is about.
            string label = "")
        {
            var companyId = Guid.NewGuid();
            var judgmentId = Guid.NewGuid();
            var factId = Guid.NewGuid();

            var mondayHeadline = MondayHeadline + label;
            var tuesdayHeadline = TuesdayHeadline + label;
            var mondayEvidence = MaterializerFixture.NewsEvidence(
                mondayHeadline, MondayBody, MaterializerFixture.Monday);
            var tuesdayEvidence = MaterializerFixture.NewsEvidence(
                tuesdayHeadline, TuesdayBody, MaterializerFixture.Tuesday);

            var observation = MaterializerFixture.Observation(
                companyId, observationHeadline ?? mondayHeadline, MaterializerFixture.Monday);
            var tuesdayObservation = MaterializerFixture.Observation(
                companyId, tuesdayHeadline, MaterializerFixture.Tuesday);

            var factRef = MaterializerFixture.FactRef(
                factId,
                factCompanyId ?? companyId,
                observation.ObservationId,
                "Acme reported an 18% revenue decline",
                citation ?? MondayCitation);

            var key = cohortKey ?? MaterializerFixture.PresentationCohortKey();
            var record = MaterializerFixture.Judgment(
                companyId,
                key,
                trajectory,
                trajectoryFactIds ?? (omitTrajectoryFactIds ? null : [factId]),
                status,
                judgmentId,
                findings,
                typingCompleteness);

            var evidence = new InMemoryEvidenceRepository();
            evidence.AddIfNewAsync(mondayEvidence, CancellationToken.None).GetAwaiter().GetResult();
            evidence.AddIfNewAsync(tuesdayEvidence, CancellationToken.None).GetAwaiter().GetResult();

            return new Scenario(
                companyId,
                judgmentId,
                factId,
                key,
                mondayEvidence,
                tuesdayEvidence,
                observation,
                factRef,
                record,
                MaterializerFixture.Typing(new Dictionary<Guid, NewsTypingFactRef> { [factId] = factRef }),
                MaterializerFixture.RunResult(record),
                evidence,
                new FakeObservationArchive(observation, tuesdayObservation),
                new RecordingSignalFileStore());
        }

        public NewsJudgmentSignalMaterializer Materializer(
            NewsJudgmentOptions? options = null,
            ISignalReviewer? reviewer = null,
            ISignalFileStore? fileStore = null,
            Abstractions.Persistence.IEvidenceRepository? evidenceRepository = null)
        {
            var clock = new FixedClock(MaterializerFixture.Now);
            return new NewsJudgmentSignalMaterializer(
                Archive,
                evidenceRepository ?? Evidence,
                new InMemorySignalRepository(),
                new InMemorySignalReviewRepository(),
                fileStore ?? FileStore,
                reviewer ?? new DeterministicSignalReviewer(
                    clock, NullLogger<DeterministicSignalReviewer>.Instance),
                options ?? MaterializerFixture.Options(),
                MaterializerFixture.Judges(),
                clock,
                NullLogger<NewsJudgmentSignalMaterializer>.Instance);
        }
    }
}
