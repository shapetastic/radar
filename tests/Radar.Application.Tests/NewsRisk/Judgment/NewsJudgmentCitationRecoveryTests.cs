using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// SPEC 197 §2 — the shared, fail-closed citation resolver and the measurement it produces.
/// <para>
/// The motivating live failure (baseline run <c>0b48b865-76b8-4485-996c-9b9139b694aa</c>, 2026-08-27): five
/// of nineteen judgments came back <c>ValidationFailed</c>, each with a SINGLE drop reason of the form
/// <c>trajectory-fact-not-supplied: 'f160ab52' is not a supplied representative fact id</c> — an
/// EIGHT-CHARACTER PREFIX of a real supplied fact id (IOSP <c>f160ab52</c>, LBRT <c>2f4bd2fd</c>, CAT
/// <c>97c73714</c>, CASS <c>11e52ee0</c>, WDFC <c>252d42b5</c>). Their grounded findings were then never
/// examined. Across those five rationales all 44 distinct 8+-hex tokens expanded to exactly ONE supplied
/// representative fact id against supplied sets of 24-35 families — 0 unmatched, 0 ambiguous — so the
/// recovery grammar is deterministic on the live data rather than speculative.
/// </para>
/// <para>
/// These fixtures are CONSTRUCTED. No live artifact is read, copied or regenerated.
/// </para>
/// </summary>
public sealed class NewsJudgmentCitationRecoveryTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The CASS-shaped id: canonical N rendering <c>11e52ee0111141118111111111111111</c>.</summary>
    private static readonly Guid PrimaryFactId = Guid.Parse("11e52ee0-1111-4111-8111-111111111111");

    /// <summary>A second, unrelated supplied fact — nothing about it prefixes the primary one.</summary>
    private static readonly Guid SecondFactId = Guid.Parse("2f4bd2fd-2222-4222-8222-222222222222");

    /// <summary>Two facts sharing the first EIGHT hexadecimal characters: the ambiguity fixture.</summary>
    private static readonly Guid AmbiguousFactIdA = Guid.Parse("abcdef01-1111-4111-8111-111111111111");

    private static readonly Guid AmbiguousFactIdB = Guid.Parse("abcdef01-2222-4222-8222-222222222222");

    /// <summary>An interior fragment of <see cref="PrimaryFactId"/>'s N rendering, never a prefix.</summary>
    private const string InteriorFragment = "41118111";

    private static NewsJudgmentInputFamily Business(Guid factId) => NewsJudgmentTestData.Family(
        factId: factId, assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);

    private static IReadOnlyList<NewsJudgmentInputFamily> Supplied() =>
        [Business(PrimaryFactId), Business(SecondFactId)];

    private static IReadOnlyList<NewsJudgmentInputFamily> SuppliedWithAmbiguity() =>
    [
        Business(PrimaryFactId),
        Business(SecondFactId),
        Business(AmbiguousFactIdA),
        Business(AmbiguousFactIdB),
    ];

    private static NewsJudgmentModelResponse Response(
        IReadOnlyList<string> trajectoryFactIds,
        IReadOnlyList<NewsJudgmentModelFinding>? findings = null) => new(
        BusinessTrajectory: "Deteriorating",
        ChallengeStrength: findings is { Count: > 0 } ? 60 : null,
        Findings: findings,
        Rationale: "The confirmed filing is adverse to the recent trajectory.",
        TrajectoryFactIds: trajectoryFactIds);

    private static NewsJudgmentModelFinding Finding(params string[] rawFactIds) => new(
        Category: "RegulatoryOrLegalSetback",
        Severity: "High",
        Confidence: 0.8,
        FactIds: rawFactIds,
        AttributionCaveat: null);

    private static string ReasonFor(NewsJudgmentValidationResult result) =>
        Assert.Single(result.FindingDropReasons);

    // ── §5.2 item 8 — a complete GUID is unchanged; a unique eight-character prefix expands everywhere ──

    [Fact]
    public void ACompleteSuppliedGuid_ValidatesUnchanged_AndRecordsAMeasuredZeroExpansions()
    {
        var response = Response(
            [PrimaryFactId.ToString("D")], [Finding(SecondFactId.ToString("D"))]);

        var result = NewsJudgmentValidator.Validate(response, Supplied());

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal([PrimaryFactId], result.TrajectoryFactIds);
        Assert.Equal([SecondFactId], Assert.Single(result.Findings).FactIds);

        // MEASURED zero: a response WAS examined and every accepted citation was already complete. This is
        // a different fact from `null` ("no validated response was examined under this contract").
        Assert.Equal(0, result.FactIdPrefixExpansionCount);
    }

    [Fact]
    public void AUniqueEightCharacterPrefix_ExpandsInBothTrajectoryAndFindingCitations()
    {
        // The live CASS/LBRT/IOSP shape, in both citation lists at once.
        var response = Response(["11e52ee0"], [Finding("2f4bd2fd")]);

        var result = NewsJudgmentValidator.Validate(response, Supplied());

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        // The FULL GUID is what is persisted — never the shorthand the model sent.
        Assert.Equal([PrimaryFactId], result.TrajectoryFactIds);
        Assert.Equal([SecondFactId], Assert.Single(result.Findings).FactIds);
        // Two RAW CITATION OCCURRENCES were expanded: one trajectory, one finding.
        Assert.Equal(2, result.FactIdPrefixExpansionCount);
    }

    [Fact]
    public void PrefixExpansion_IsCaseInsensitive_AndAcceptsLongerUniquePrefixes()
    {
        var response = Response(["11E52EE011114111"]);

        var result = NewsJudgmentValidator.Validate(response, Supplied());

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal([PrimaryFactId], result.TrajectoryFactIds);
        Assert.Equal(1, result.FactIdPrefixExpansionCount);
    }

    // ── §5.2 item 9 — every other shorthand fails, each by its OWN name ────────────────────────────────

    [Fact]
    public void AnAmbiguousPrefix_FailsClosed_AndIsNeverResolvedToTheFirstCollision()
    {
        // MUTATION PROOF: delete the "exactly one match" rule (return the first match instead) and this
        // test turns red — the response would be Judged, citing AmbiguousFactIdA, which Radar cannot know.
        var result = NewsJudgmentValidator.Validate(
            Response(["abcdef01"]), SuppliedWithAmbiguity());

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains("trajectory-fact-id-prefix-ambiguous", ReasonFor(result), StringComparison.Ordinal);
        Assert.Empty(result.TrajectoryFactIds);
        Assert.Equal(0, result.FactIdPrefixExpansionCount);
    }

    [Fact]
    public void APrefixShorterThanEightCharacters_FailsClosed_EvenWhenItWouldMatchUniquely()
    {
        // MUTATION PROOF: remove the 8-character floor and this test turns red — "11e52ee" is a UNIQUE
        // prefix of the supplied set, so without the floor it would expand. A short match in a small
        // supplied set is coincidence; a larger set would silently start selecting the wrong fact.
        var result = NewsJudgmentValidator.Validate(Response(["11e52ee"]), Supplied());

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains("trajectory-fact-id-prefix-too-short", ReasonFor(result), StringComparison.Ordinal);
        Assert.Equal(0, result.FactIdPrefixExpansionCount);
    }

    [Fact]
    public void ANonPrefixSubstring_FailsClosed_ASuffixOrInteriorFragmentIsNeverExpanded()
    {
        // `41118111` occurs INSIDE 11e52ee0111141118111111111111111 but does not start it.
        var result = NewsJudgmentValidator.Validate(Response([InteriorFragment]), Supplied());

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains("trajectory-fact-id-prefix-unmatched", ReasonFor(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ANonHexadecimalToken_FailsClosed_AsMalformed()
    {
        var result = NewsJudgmentValidator.Validate(Response(["the-first-fact"]), Supplied());

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains("trajectory-fact-id-malformed", ReasonFor(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCompleteGuid_StillFailsAsNotSupplied_TheSuppliedSetRuleIsNotRelaxed()
    {
        var result = NewsJudgmentValidator.Validate(
            Response([Guid.Parse("99999999-9999-4999-8999-999999999999").ToString("D")]), Supplied());

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains("trajectory-fact-not-supplied", ReasonFor(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AFullGuidAndItsOwnPrefix_AreOneCitationCitedTwice_AndFailAsDuplicate()
    {
        // MUTATION PROOF: check distinctness BEFORE expansion (i.e. on the raw tokens) and this test turns
        // red — the two spellings would read as two independent citations of one fact.
        var result = NewsJudgmentValidator.Validate(
            Response([PrimaryFactId.ToString("D"), "11e52ee0"]), Supplied());

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains("trajectory-fact-duplicate", ReasonFor(result), StringComparison.Ordinal);
        // The expansion HAPPENED before the duplicate was detected, and is still counted (spec 197 §2.2:
        // "including expansions observed before a different validation error failed the response").
        Assert.Equal(1, result.FactIdPrefixExpansionCount);
    }

    [Fact]
    public void EveryFailureClass_CarriesItsOwnDistinctNamedReason()
    {
        var reasons = new[]
        {
            ReasonFor(NewsJudgmentValidator.Validate(
                Response([Guid.Parse("99999999-9999-4999-8999-999999999999").ToString("D")]), Supplied())),
            ReasonFor(NewsJudgmentValidator.Validate(Response(["11e52ee"]), Supplied())),
            ReasonFor(NewsJudgmentValidator.Validate(Response([InteriorFragment]), Supplied())),
            ReasonFor(NewsJudgmentValidator.Validate(
                Response(["abcdef01"]), SuppliedWithAmbiguity())),
            ReasonFor(NewsJudgmentValidator.Validate(Response(["not-an-id"]), Supplied())),
        };

        // Five failure classes, five DISTINCT reason strings: "the model invented an id", "it shortened one
        // past uniqueness" and "it sent something that is not an id" are different facts about the provider.
        Assert.Equal(5, reasons.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AFindingsCitationFailure_DropsThatFindingAlone_WithItsOwnNamedReason()
    {
        var response = Response(
            [PrimaryFactId.ToString("D")],
            [Finding("abcdef01"), Finding(SecondFactId.ToString("D"))]);

        var result = NewsJudgmentValidator.Validate(response, SuppliedWithAmbiguity());

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal([SecondFactId], Assert.Single(result.Findings).FactIds);
        Assert.Contains(
            result.FindingDropReasons,
            r => r.Contains("finding[0] cited-fact-id-prefix-ambiguous", StringComparison.Ordinal));
    }

    // ── The live CASS regression fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// CASS on baseline run <c>0b48b865-…</c>: a rationale-bearing response whose trajectory cited
    /// <c>11e52ee0</c> against a THIRTY-FIVE-family supplied set. Under prompt/schema v2 it was
    /// <c>ValidationFailed</c> with zero accepted findings, because <c>Guid.TryParse</c> rejects an
    /// eight-character token outright (asserted below, which is exactly WHY v2 failed it). Under v3 it
    /// expands to the single supplied fact it prefixes and is accepted.
    /// </summary>
    [Fact]
    public void TheLiveCassShape_FailedUnderV2_AndIsAcceptedUnderV3()
    {
        Assert.False(Guid.TryParse("11e52ee0", out _));

        var supplied = new List<NewsJudgmentInputFamily> { Business(PrimaryFactId) };
        for (var i = 1; i < 35; i++)
        {
            supplied.Add(Business(Guid.Parse(
                FormattableString.Invariant($"{i:x8}-0000-4000-8000-000000000000"))));
        }

        Assert.Equal(35, supplied.Count);
        Assert.Single(
            supplied,
            f => f.RepresentativeFactId
                .ToString("N")
                .StartsWith("11e52ee0", StringComparison.OrdinalIgnoreCase));

        var result = NewsJudgmentValidator.Validate(Response(["11e52ee0"]), supplied);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsJudgmentTrajectory.Deteriorating, result.BusinessTrajectory);
        Assert.Equal([PrimaryFactId], result.TrajectoryFactIds);
        Assert.Equal(1, result.FactIdPrefixExpansionCount);
    }

    // ── The resolver itself, so each failure class is pinned at its own boundary ───────────────────────

    [Theory]
    [InlineData("11e52ee", NewsJudgmentCitationFailure.PrefixTooShort)]
    [InlineData("1", NewsJudgmentCitationFailure.PrefixTooShort)]
    [InlineData(InteriorFragment, NewsJudgmentCitationFailure.PrefixUnmatched)]
    [InlineData("deadbeef", NewsJudgmentCitationFailure.PrefixUnmatched)]
    [InlineData("zzzzzzzz", NewsJudgmentCitationFailure.Malformed)]
    [InlineData("11e52ee0-1111", NewsJudgmentCitationFailure.Malformed)]
    [InlineData("", NewsJudgmentCitationFailure.Malformed)]
    [InlineData(null, NewsJudgmentCitationFailure.Malformed)]
    [InlineData(
        "11e52ee0111141118111111111111111ff", NewsJudgmentCitationFailure.Malformed)]
    public void Resolver_NamesEachFailureClass(string? token, NewsJudgmentCitationFailure expected)
    {
        var resolver = new NewsJudgmentCitationResolver([PrimaryFactId, SecondFactId]);

        var resolution = resolver.Resolve(token);

        Assert.False(resolution.Resolved);
        Assert.Equal(expected, resolution.Failure);
        Assert.Equal(Guid.Empty, resolution.FactId);
        Assert.False(resolution.Expanded);
        Assert.Equal(0, resolver.ExpansionCount);
    }

    [Fact]
    public void Resolver_AcceptsEverySpellingOfASuppliedGuid_WithoutCountingAnExpansion()
    {
        var resolver = new NewsJudgmentCitationResolver([PrimaryFactId, SecondFactId]);

        foreach (var format in new[] { "D", "N", "B", "P" })
        {
            var resolution = resolver.Resolve(PrimaryFactId.ToString(format).ToUpperInvariant());
            Assert.True(resolution.Resolved);
            Assert.Equal(PrimaryFactId, resolution.FactId);
            Assert.False(resolution.Expanded);
        }

        // The complete 32-character N rendering parses as a GUID, which is why the prefix window stops at
        // 31 — it is never treated as "a prefix that happens to be the whole thing".
        Assert.Equal(0, resolver.ExpansionCount);
    }

    [Fact]
    public void Resolver_NeverConsultsAnythingOutsideTheSuppliedSet()
    {
        // A prefix of a real fact that this company's judge was NOT handed resolves to nothing: recovery is
        // scoped to the supplied set, never to the global fact store.
        var resolver = new NewsJudgmentCitationResolver([SecondFactId]);

        var resolution = resolver.Resolve("11e52ee0");

        Assert.False(resolution.Resolved);
        Assert.Equal(NewsJudgmentCitationFailure.PrefixUnmatched, resolution.Failure);
    }

    [Fact]
    public void Resolver_CountsOccurrences_NotDistinctIds()
    {
        var resolver = new NewsJudgmentCitationResolver([PrimaryFactId, SecondFactId]);

        Assert.True(resolver.Resolve("11e52ee0").Expanded);
        Assert.True(resolver.Resolve("11e52ee0").Expanded);
        Assert.True(resolver.Resolve("2f4bd2fd").Expanded);

        Assert.Equal(3, resolver.ExpansionCount);
    }

    [Fact]
    public void ResolutionInvariant_ResolvedIffNoNamedFailure()
    {
        var resolver = new NewsJudgmentCitationResolver([PrimaryFactId]);

        var accepted = resolver.Resolve("11e52ee0");
        Assert.True(accepted.Resolved);
        Assert.Null(accepted.Failure);
        Assert.Equal(string.Empty, accepted.ReasonCode);
        Assert.Equal(string.Empty, accepted.ReasonDetail);

        var rejected = resolver.Resolve("zzzzzzzz");
        Assert.False(rejected.Resolved);
        Assert.NotNull(rejected.Failure);
        Assert.NotEmpty(rejected.ReasonCode);
        Assert.NotEmpty(rejected.ReasonDetail);
    }

    // ── §5.2 item 10 — the contract fork, and what does NOT fork it ────────────────────────────────────

    [Fact]
    public void PromptAndSchemaV3_ForkTheStage2CohortKey_WhileAReaderDisplayNameStillDoesNot()
    {
        const string Stage1 = "openai:extractor|p|s|news-event-taxonomy-v1";
        var current = NewsJudgmentContract.CohortKey("openai", "judge-model", Stage1);

        Assert.Contains("news-judgment-prompt-v3", current, StringComparison.Ordinal);
        Assert.Contains("news-judgment-schema-v3", current, StringComparison.Ordinal);
        Assert.DoesNotContain("news-judgment-prompt-v2", current, StringComparison.Ordinal);
        Assert.DoesNotContain("news-judgment-schema-v2", current, StringComparison.Ordinal);

        // The spec-179 rule, unchanged: a reader's display NAME is provenance, never cohort identity.
        Assert.Equal(
            new NewsJudgmentReaderIdentity("judge-a", "openai", "judge-model").CohortKeyFor(Stage1),
            new NewsJudgmentReaderIdentity("renamed", "openai", "judge-model").CohortKeyFor(Stage1));
    }

    [Fact]
    public async Task TheV3Fork_EarnsAFreshAttemptBudget_WhileTheCurrentCohortsBudgetStillBinds()
    {
        // A real pass, to obtain the production cohort key and family-set hash for the seeded records.
        var probeRunId = Guid.NewGuid();
        var probe = new JudgmentPassHarness(AsOf);
        await probe.Build(Grounded()).GenerateAsync(
            probeRunId,
            JudgmentPassFixture.Plan(1),
            JudgmentPassFixture.Typing(probeRunId, 1),
            CancellationToken.None);
        var template = Assert.Single(probe.Store.Records);

        // Three accrued call-producing attempts under the RETIRED v2 cohort key: the v3 contract can accept
        // citations v2 rejected, so those attempts must not spend the new contract's budget.
        var retired = template.CohortKey
            .Replace("news-judgment-prompt-v3", "news-judgment-prompt-v2", StringComparison.Ordinal)
            .Replace("news-judgment-schema-v3", "news-judgment-schema-v2", StringComparison.Ordinal);
        Assert.NotEqual(template.CohortKey, retired);

        Assert.Equal(1, await CallsAfterSeeding(template, retired));
        Assert.Equal(0, await CallsAfterSeeding(template, template.CohortKey));
    }

    /// <summary>
    /// Seeds three spent attempts under <paramref name="cohortKey"/> and returns how many provider calls a
    /// fresh pass then makes for the same company and family set.
    /// </summary>
    private static async Task<int> CallsAfterSeeding(NewsJudgmentRecord template, string cohortKey)
    {
        var store = new InsertOnlyStore();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var seedRun = Guid.NewGuid();
            await store.WriteAsync(
                template with
                {
                    CohortKey = cohortKey,
                    RunId = seedRun,
                    JudgmentId = NewsJudgmentRecord.IdentityFor(
                        cohortKey, template.CompanyId, template.FamilySetHash, seedRun),
                    Status = NewsJudgmentStatus.ValidationFailed,
                },
                CancellationToken.None);
        }

        var runId = Guid.NewGuid();
        var harness = new JudgmentPassHarness(AsOf, store);
        await harness.Build(Grounded()).GenerateAsync(
            runId,
            JudgmentPassFixture.Plan(1),
            JudgmentPassFixture.Typing(runId, 1),
            CancellationToken.None);

        return harness.Analyzer!.Calls;
    }

    // ── §5.2 item 11 — the current-pass diagnostic, and the three persisted states ─────────────────────

    /// <summary>A judge that shortens every FactId to its first eight hex characters (the live shape).</summary>
    private static Func<NewsJudgmentAnalysisRequest, NewsJudgmentAnalysisOutcome> Shortened() =>
        request => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse(
                BusinessTrajectory: "Deteriorating",
                ChallengeStrength: null,
                Findings: [],
                Rationale: "The confirmed regulatory filing is adverse to the recent trajectory.",
                TrajectoryFactIds:
                [
                    .. request.Families.Select(f => f.RepresentativeFactId.ToString("N")[..8]),
                ]),
            "raw-hash",
            null);

    /// <summary>A judge that quotes every supplied FactId in full.</summary>
    private static Func<NewsJudgmentAnalysisRequest, NewsJudgmentAnalysisOutcome> Grounded() =>
        request => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse(
                BusinessTrajectory: "Deteriorating",
                ChallengeStrength: null,
                Findings: [],
                Rationale: "The confirmed regulatory filing is adverse to the recent trajectory.",
                TrajectoryFactIds:
                    [.. request.Families.Select(f => f.RepresentativeFactId.ToString("D"))]),
            "raw-hash",
            null);

    private static Func<NewsJudgmentAnalysisRequest, NewsJudgmentAnalysisOutcome> ProviderDown() =>
        _ => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.ProviderError, null, null, "429 rate limited");

    private static List<string> ExpansionLines(JudgmentPassHarness harness) =>
    [
        .. harness.Logger.Entries
            .Where(e => e.Message.Contains(
                "deterministically expanded", StringComparison.Ordinal))
            .Select(e => e.Message),
    ];

    [Fact]
    public async Task ACalledPass_RecordsTheExpansions_AndReportsThemOncePerCohort()
    {
        var runId = Guid.NewGuid();
        var harness = new JudgmentPassHarness(AsOf);

        await harness.Build(Shortened()).GenerateAsync(
            runId,
            JudgmentPassFixture.Plan(3),
            JudgmentPassFixture.Typing(runId, 3),
            CancellationToken.None);

        Assert.Equal(3, harness.Analyzer!.Calls);
        Assert.All(
            harness.Store.Records,
            r =>
            {
                Assert.Equal(NewsJudgmentStatus.Judged, r.Status);
                Assert.Equal(1, r.FactIdPrefixExpansionCount);
            });

        // ONE aggregated line for the cohort (the spec-145 precedent), never one per judgment.
        var line = Assert.Single(ExpansionLines(harness));
        Assert.Contains("3 FactId citation(s) across 3 judgment(s)", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidatedResponseWithNoShorthand_RecordsAMeasuredZero_AndSaysNothing()
    {
        var runId = Guid.NewGuid();
        var harness = new JudgmentPassHarness(AsOf);

        await harness.Build(Grounded()).GenerateAsync(
            runId,
            JudgmentPassFixture.Plan(1),
            JudgmentPassFixture.Typing(runId, 1),
            CancellationToken.None);

        Assert.Equal(0, Assert.Single(harness.Store.Records).FactIdPrefixExpansionCount);
        // Silence here MEANS measured zero: the durable record carries the 0, so nothing is lost by not
        // logging a cohort that expanded nothing.
        Assert.Empty(ExpansionLines(harness));
    }

    [Fact]
    public async Task AnAttemptThatNeverProducedAValidatedResponse_RecordsNull_NeverAFabricatedZero()
    {
        var runId = Guid.NewGuid();
        var harness = new JudgmentPassHarness(AsOf);

        await harness.Build(ProviderDown()).GenerateAsync(
            runId,
            JudgmentPassFixture.Plan(1),
            JudgmentPassFixture.Typing(runId, 1),
            CancellationToken.None);

        var record = Assert.Single(harness.Store.Records);
        Assert.Equal(NewsJudgmentStatus.ProviderFailure, record.Status);
        Assert.Null(record.FactIdPrefixExpansionCount);
        Assert.Empty(ExpansionLines(harness));
    }

    [Fact]
    public async Task SameRunReEntry_KeepsTheDurableCount_AndReportsNoNewCurrentPassNormalization()
    {
        var runId = Guid.NewGuid();
        var first = new JudgmentPassHarness(AsOf);
        await first.Build(Shortened()).GenerateAsync(
            runId,
            JudgmentPassFixture.Plan(1),
            JudgmentPassFixture.Typing(runId, 1),
            CancellationToken.None);
        Assert.Single(ExpansionLines(first));

        // The SAME run, re-entered over the SAME store: spec 187 §1 same-run idempotency reuses the record.
        var second = new JudgmentPassHarness(AsOf, first.Store);
        await second.Build(Shortened()).GenerateAsync(
            runId,
            JudgmentPassFixture.Plan(1),
            JudgmentPassFixture.Typing(runId, 1),
            CancellationToken.None);

        Assert.Equal(0, second.Analyzer!.Calls);
        // Spec 188 §1: no call this pass ⇒ no current-pass normalization reported…
        Assert.Empty(ExpansionLines(second));
        // …while the durable record keeps its ORIGINAL count, which is truthful provenance of the call that
        // produced it.
        Assert.Equal(1, Assert.Single(first.Store.Records).FactIdPrefixExpansionCount);
    }

    [Fact]
    public async Task ACacheReuseInALaterRun_CarriesTheOriginalCount_AndReportsNoNewNormalization()
    {
        var firstRun = Guid.NewGuid();
        var first = new JudgmentPassHarness(AsOf);
        await first.Build(Shortened()).GenerateAsync(
            firstRun,
            JudgmentPassFixture.Plan(1),
            JudgmentPassFixture.Typing(firstRun, 1),
            CancellationToken.None);

        var laterRun = Guid.NewGuid();
        var later = new JudgmentPassHarness(AsOf, first.Store);
        var result = await later.Build(Shortened()).GenerateAsync(
            laterRun,
            JudgmentPassFixture.Plan(1),
            JudgmentPassFixture.Typing(laterRun, 1),
            CancellationToken.None);

        Assert.Equal(0, later.Analyzer!.Calls);
        Assert.Empty(ExpansionLines(later));

        var reused = Assert.Single(result!.Judgments);
        Assert.NotNull(reused.ReusedFromJudgmentId);
        // The replayed verdict carries its ORIGINAL durable count — never "not recorded" beside the very
        // citations it carries forward.
        Assert.Equal(1, reused.FactIdPrefixExpansionCount);
    }
}
