using Radar.Application.Collectors;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.SignalExtraction;

namespace Radar.Application.Tests.News;

/// <summary>
/// SPEC 194 §1.2 — the versioned judgment-signal envelope (one writer, one reader, one delimiter) and the
/// SHARED presentation-cohort resolution the marker path and the scoring path both go through.
/// </summary>
public sealed class NewsJudgmentSignalMetadataTests
{
    private static readonly Guid JudgmentId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public void ComposeAndParse_RoundTripEveryIdList()
    {
        var factA = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
        var factB = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
        var observation = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
        var evidence = Guid.Parse("dddddddd-0000-4000-8000-000000000004");

        var json = NewsDirectionalSignalMetadata.ComposeJudgmentSignal(
            JudgmentId, "cohort-key", "deteriorating", [factA, factB], [observation], [evidence]);

        Assert.True(EvidenceMetadata.TryRead(json, out var metadata, out var hints));

        // A signal carries no collector company hints — the one artifact of having exactly ONE envelope
        // definition instead of two.
        Assert.Empty(hints);

        Assert.Equal(
            NewsDirectionalSignalMetadata.JudgmentSignalVersionValue,
            metadata[NewsDirectionalSignalMetadata.JudgmentSignalVersionKey]);
        Assert.Equal(
            JudgmentId.ToString("D"), metadata[NewsDirectionalSignalMetadata.JudgmentIdKey]);
        Assert.Equal("cohort-key", metadata[NewsDirectionalSignalMetadata.JudgmentCohortKeyKey]);

        // The trajectory rides the EXISTING newsBusinessTrajectory key — a deliberate deviation from the
        // spec's `newsTrajectory` prose, because the key was already declared and is already read by the
        // §1.4 admission transform. Two spellings would mean two readers.
        Assert.Equal("newsBusinessTrajectory", NewsDirectionalSignalMetadata.TrajectoryKey);
        Assert.Equal("deteriorating", metadata[NewsDirectionalSignalMetadata.TrajectoryKey]);

        Assert.Equal(
            [factA, factB],
            NewsDirectionalSignalMetadata.ParseGuidList(
                metadata[NewsDirectionalSignalMetadata.TrajectoryFactIdsKey]));
        Assert.Equal(
            [observation],
            NewsDirectionalSignalMetadata.ParseGuidList(
                metadata[NewsDirectionalSignalMetadata.SourceObservationIdsKey]));
        Assert.Equal(
            [evidence],
            NewsDirectionalSignalMetadata.ParseGuidList(
                metadata[NewsDirectionalSignalMetadata.CitedEvidenceIdsKey]));
    }

    [Fact]
    public void GuidLists_AreDistinctAndOrdinallyOrdered_RegardlessOfInputOrder()
    {
        var a = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var b = Guid.Parse("ffffffff-0000-4000-8000-000000000002");

        // Same set, different input order, one duplicate: one rendering (AD-3).
        Assert.Equal(
            NewsDirectionalSignalMetadata.ComposeGuidList([a, b]),
            NewsDirectionalSignalMetadata.ComposeGuidList([b, a, a]));
        Assert.Equal(
            a.ToString("D") + "," + b.ToString("D"),
            NewsDirectionalSignalMetadata.ComposeGuidList([b, a]));
        Assert.Equal(",", NewsDirectionalSignalMetadata.GuidListDelimiter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    public void ParseGuidList_DegradesToEmpty_NeverThrows(string? value) =>
        Assert.Empty(NewsDirectionalSignalMetadata.ParseGuidList(value));

    [Fact]
    public void ParseGuidList_DropsAnUnparseableEntryAndKeepsTheRest()
    {
        var id = Guid.Parse("22222222-2222-4222-8222-222222222222");

        // Dropped, never guessed at: a caller checking provenance completeness compares the parsed count
        // against what it expected rather than receiving an invented id.
        Assert.Equal([id], NewsDirectionalSignalMetadata.ParseGuidList($"{id:D},garbage"));
    }

    [Fact]
    public void PresentationCohort_ResolvesTheSameKeyTheMarkerPathUses()
    {
        // The reuse-over-copy claim, asserted: the composed key is exactly
        // judge.CohortKeyFor(extractorCohort.Reader.CohortKey) — the composition the leaders-marker
        // derivation performs. If the two ever diverged, Radar would SCORE a direction from one cohort
        // while DISPLAYING a marker from another.
        var typing = MaterializerFixture.Typing(new Dictionary<Guid, NewsTypingFactRef>());
        var judges = MaterializerFixture.Judges();

        var resolved = NewsJudgmentPresentationCohort.TryResolve(
            MaterializerFixture.Options(), judges, typing);

        Assert.NotNull(resolved);
        Assert.Same(judges.Readers[0], resolved!.Judge);
        Assert.Same(typing.Cohorts[0], resolved.ExtractorCohort);
        Assert.Equal(
            judges.Readers[0].Identity.CohortKeyFor(typing.Cohorts[0].Reader.CohortKey),
            resolved.CohortKey);
    }

    [Fact]
    public void PresentationCohort_MatchesNamesCaseInsensitively()
    {
        var typing = MaterializerFixture.Typing(
            new Dictionary<Guid, NewsTypingFactRef>(), extractorName: "Ambient");

        Assert.NotNull(NewsJudgmentPresentationCohort.TryResolve(
            MaterializerFixture.Options(presentationExtractor: "ambient"),
            MaterializerFixture.Judges("AMBIENT"),
            typing));
    }

    [Fact]
    public void PresentationCohort_FailsClosedWhenEitherHalfIsAbsent()
    {
        var typing = MaterializerFixture.Typing(new Dictionary<Guid, NewsTypingFactRef>());

        // An undesignated cohort is NEVER substituted — each caller states its own consequence instead.
        Assert.Null(NewsJudgmentPresentationCohort.TryResolve(
            MaterializerFixture.Options(presentationExtractor: "a-reader-that-did-not-run"),
            MaterializerFixture.Judges(),
            typing));
        Assert.Null(NewsJudgmentPresentationCohort.TryResolve(
            MaterializerFixture.Options(presentationJudge: "a-judge-that-did-not-run"),
            MaterializerFixture.Judges(),
            typing));
    }
}
