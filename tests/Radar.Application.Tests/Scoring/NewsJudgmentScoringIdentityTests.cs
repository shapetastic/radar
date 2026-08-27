using System.Globalization;

using Radar.Application.Collectors;
using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Infrastructure.Attention;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// SPEC 194 §2 — the news-read scoring identity. Every assertion the section demands, at the level the
/// section states it: the identity itself (this file) and the composed Worker graph across run modes
/// (<c>Radar.Worker.Tests.NewsJudgmentScoringIdentityModeTests</c>).
/// <para>
/// The fingerprint is asserted through the real <see cref="ScoringConfigFingerprint.Compute"/> over a real
/// <see cref="SignalSourceDescriptor"/>, not over a hand-written descriptor string, so a test cannot pass by
/// agreeing with a literal that production does not emit.
/// </para>
/// </summary>
public sealed class NewsJudgmentScoringIdentityTests
{
    // The shipped constants, so a perturbation below is visibly ONE change away from production. They are
    // duplicated here on purpose: NewsTrajectorySignalRules' values reaching this file through
    // NewsJudgmentScoringIdentityFactory is what the "current" cases assert, and a fixture that reused the
    // same constants for its perturbation baseline could not tell the two apart.
    private const string CurrentMaterializerVersion = "news-judgment-signal-v1";
    private const int CurrentBaseStrength = 4;
    private const int CurrentMaxFindingContribution = 3;
    private const int CurrentCompleteTypingBonus = 1;
    private const int CurrentNovelty = 4;
    private const decimal CurrentConfidence = 0.5m;

    private static readonly string[] CurrentMapping =
        ["Unknown>none", "Improving>Positive", "Deteriorating>Negative", "Mixed>none"];

    private const string CohortA =
        "openai:model-a|news-judgment-prompt-v2|news-judgment-schema-v2|stage1=openai:x|families=fact-family-v2";

    private const string CohortB =
        "openai:model-b|news-judgment-prompt-v2|news-judgment-schema-v2|stage1=openai:x|families=fact-family-v2";

    private static NewsJudgmentScoringIdentity Enabled(
        string cohortKey = CohortA,
        string? materializerVersion = null,
        IReadOnlyList<string>? mapping = null,
        int? baseStrength = null,
        int? maxFindingContribution = null,
        int? completeTypingBonus = null,
        int? novelty = null,
        decimal? confidence = null) =>
        NewsJudgmentScoringIdentity.ForPresentationCohort(
            cohortKey,
            materializerVersion ?? CurrentMaterializerVersion,
            mapping ?? CurrentMapping,
            baseStrength ?? CurrentBaseStrength,
            maxFindingContribution ?? CurrentMaxFindingContribution,
            completeTypingBonus ?? CurrentCompleteTypingBonus,
            novelty ?? CurrentNovelty,
            confidence ?? CurrentConfidence);

    private static string FingerprintFor(NewsJudgmentScoringIdentity news) =>
        ScoringConfigFingerprint.Compute(
            "mvp-engine-v1",
            "radar-formula-v8",
            new ScoringWeights(),
            new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default).CanonicalDescriptor(),
            new SignalSourceDescriptor(
                EnabledCollectorVocabulary.FromNames(["sec-edgar"]),
                null,
                null,
                null,
                news).CanonicalDescriptor(),
            new InsiderMaterialityWeights().CanonicalDescriptor(),
            new MediaAttentionCollapse(new MediaCollapseOptions()).CanonicalDescriptor(),
            new ScoringOptions().Window);

    // ---- the five assertions spec 194 §2 demands (the fifth is in the Worker mode tests) ---------------

    [Fact]
    public void JudgmentOffVersusOn_ProducesADifferentScoringConfigVersion()
    {
        // THE headline. Before this slice, enabling the stage-2 judgment changed which durable signals could
        // exist — a validated judgment mints a directional MediaAttention signal (§1.2) that supersedes the
        // article's ordinary Neutral one (§1.3) — while stamping the IDENTICAL ScoringConfigVersion. So
        // StrategyIdentityGuard could not see the change and ScoreSeriesKey pooled both cohorts into one
        // series.
        Assert.NotEqual(
            FingerprintFor(NewsJudgmentScoringIdentity.Disabled),
            FingerprintFor(Enabled()));
    }

    [Fact]
    public void ChangingOnlyTheModelOrPresentationCohort_ProducesADifferentScoringConfigVersion()
    {
        // The cohort key carries provider + exact model id + judge prompt/schema + the whole stage-1
        // extractor cohort identity, so "DeepSeek today, some other model tomorrow" and "a different
        // designated presentation cohort" are the same assertion. This is the news analogue of spec 119's
        // earnings-read model fold: a reading model changes DIRECTION, so two runs on different models must
        // never share a stamp.
        Assert.NotEqual(FingerprintFor(Enabled(CohortA)), FingerprintFor(Enabled(CohortB)));
    }

    [Theory]
    [InlineData("base")]
    [InlineData("maxFinding")]
    [InlineData("completeBonus")]
    [InlineData("novelty")]
    [InlineData("confidence")]
    [InlineData("materializer")]
    [InlineData("mapping")]
    public void ChangingAStrengthConstantOrTheMapping_ProducesADifferentScoringConfigVersion(string knob)
    {
        // Through a CONSTRUCTED fixture, deliberately — spec 194 §2 requires the constants to move the
        // stamp WITHOUT becoming configurable. Making them config would invite tuning a signal magnitude at
        // runtime; making them unhashed is what this slice is fixing. A wide private factory is the third
        // option: production has exactly one caller supplying the shipped values.
        var perturbed = knob switch
        {
            "base" => Enabled(baseStrength: CurrentBaseStrength + 1),
            "maxFinding" => Enabled(maxFindingContribution: CurrentMaxFindingContribution + 1),
            "completeBonus" => Enabled(completeTypingBonus: CurrentCompleteTypingBonus + 1),
            "novelty" => Enabled(novelty: CurrentNovelty + 1),
            "confidence" => Enabled(confidence: CurrentConfidence + 0.1m),
            "materializer" => Enabled(materializerVersion: "news-judgment-signal-v2"),
            // Improving → Negative: the mapping ITSELF, not a magnitude. A silent inversion here would flip
            // the sign of every judgment-derived signal while leaving the stamp untouched.
            _ => Enabled(mapping: ["Unknown>none", "Improving>Negative", "Deteriorating>Positive", "Mixed>none"]),
        };

        Assert.NotEqual(FingerprintFor(Enabled()), FingerprintFor(perturbed));
    }

    [Fact]
    public void TheRuleVersions_AreRecordedInBothStates()
    {
        // §1.3's supersede and §1.4's legacy-inheritance neutralization act on signals ALREADY on disk, so
        // ScoringEngine applies them unconditionally — their rules change what a JUDGMENT-DISABLED run
        // scores too. That is why they are the one part of this segment rendered in both states.
        foreach (var identity in new[] { NewsJudgmentScoringIdentity.Disabled, Enabled() })
        {
            Assert.Contains(
                LegacyNewsInheritanceNeutralization.Version, identity.Segment, StringComparison.Ordinal);
            Assert.Contains(
                NewsJudgmentSignalSupersede.Version, identity.Segment, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSegment_IsStable_AndCultureInvariant()
    {
        // AD-3. The confidence constant is a decimal, so a comma-decimal locale would corrupt the segment
        // (and therefore every stamp) if it were formatted with the ambient culture.
        var invariant = Enabled().Segment;
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(invariant, Enabled().Segment);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(invariant, Enabled().Segment);
    }

    [Fact]
    public void TheSegment_EscapesTheCohortKeysOwnDelimiters_KeepingTheEncodingInjective()
    {
        // The cohort key legitimately contains ':', '|' and '=' — the same characters this segment uses for
        // its own field/list structure — so it goes through DescriptorEscaping.EscapeNested, not Escape.
        // Without the nested escape, a cohort key containing ':' could impersonate an extra field and two
        // different configurations could serialize identically. Widening the shared Escape instead would
        // move the AI-ON pin, whose descriptor legitimately contains ':'.
        var segment = Enabled("a:b|c=d").Segment;

        Assert.Contains("a%3Ab%7Cc%3Dd", segment, StringComparison.Ordinal);
        Assert.DoesNotContain("a:b", segment, StringComparison.Ordinal);

        // Injectivity, demonstrated rather than asserted in prose: two keys that would collide under a
        // naive concatenation stay distinct.
        Assert.NotEqual(Enabled("a:b").Segment, Enabled("a", materializerVersion: "b").Segment);
    }

    [Fact]
    public void ForPresentationCohort_RejectsABlankCohortKey()
    {
        // A blank key would render an EMPTY cohort field, making an enabled judgment with an unresolved
        // cohort indistinguishable from one designating a genuinely empty-named cohort — an identity that
        // cannot say what it identifies.
        Assert.Throws<ArgumentException>(() => Enabled("   "));
    }

    // ---- the FACTORY reads the shipped rules, rather than restating them -------------------------------

    [Fact]
    public void TheFactory_RendersTheShippedMappingAndConstants()
    {
        // Non-vacuity for every "current" case above: the production factory must produce exactly the
        // fixture this file calls current. If NewsTrajectorySignalRules' constants or its Improving/
        // Deteriorating mapping ever move without this file moving with them, this fails — which is the
        // point, because the stamp would move and the lineage would need a conscious update.
        Assert.Equal(CurrentMapping, NewsJudgmentScoringIdentityFactory.DirectionMappingTokens);
        Assert.Equal(
            Enabled(CohortA).Segment,
            NewsJudgmentScoringIdentityFactory.ForPresentationCohort(CohortA).Segment);
    }

    [Fact]
    public void TheFactory_MapsOnlyImprovingAndDeterioratingToADirection()
    {
        // Mixed and Unknown are honest NON-directions (spec 185): genuine both-ways evidence is not a
        // direction, and a judge that declined to call has not called. Asserted on the rendered mapping so
        // the identity provably encodes the rule rather than a summary of it.
        Assert.Contains("Improving>Positive", NewsJudgmentScoringIdentityFactory.DirectionMappingTokens);
        Assert.Contains("Deteriorating>Negative", NewsJudgmentScoringIdentityFactory.DirectionMappingTokens);
        Assert.Contains("Mixed>none", NewsJudgmentScoringIdentityFactory.DirectionMappingTokens);
        Assert.Contains("Unknown>none", NewsJudgmentScoringIdentityFactory.DirectionMappingTokens);
    }

    [Fact]
    public void TheFactory_CoversEveryDeclaredTrajectory()
    {
        // Enumerated from the enum, not listed: a NEW trajectory member cannot be added without moving the
        // stamp, which is correct — a new trajectory is a new mapping.
        Assert.Equal(
            Enum.GetValues<NewsJudgmentTrajectory>().Length,
            NewsJudgmentScoringIdentityFactory.DirectionMappingTokens.Count);
    }

    // ---- the config-time and run-time cohort compositions are ONE definition ---------------------------

    [Fact]
    public void TheCohortKey_IsComposedByTheSameMethodTheRunTimeResolutionUses()
    {
        // spec 194 §2 requires the CONFIGURED cohort and the PRODUCED cohort to be the same string. Both go
        // through NewsJudgmentPresentationCohort.ComposeCohortKey, which is itself just
        // NewsJudgmentReaderIdentity.CohortKeyFor over NewsTypingReaderIdentity.CohortKey — so this asserts
        // that no second composition exists to drift from the first.
        var judge = new NewsJudgmentReaderIdentity("display-name", "openai", "some-model");
        var extractor = new NewsTypingReaderIdentity("another-display-name", "openai", "typing-model");

        Assert.Equal(
            judge.CohortKeyFor(extractor.CohortKey),
            NewsJudgmentPresentationCohort.ComposeCohortKey(judge, extractor));
    }

    [Fact]
    public void TheCohortKey_IgnoresReaderDisplayNames_ButNotProviderOrModel()
    {
        // The spec-179 rule: a reader NAME is a provenance label, so renaming a reader must fork no cohort
        // and must therefore move no stamp. Provider and model are the identity and must move it.
        var a = NewsJudgmentPresentationCohort.ComposeCohortKey(
            new NewsJudgmentReaderIdentity("alpha", "openai", "m"),
            new NewsTypingReaderIdentity("alpha", "openai", "t"));
        var renamed = NewsJudgmentPresentationCohort.ComposeCohortKey(
            new NewsJudgmentReaderIdentity("beta", "openai", "m"),
            new NewsTypingReaderIdentity("gamma", "openai", "t"));
        var reModelled = NewsJudgmentPresentationCohort.ComposeCohortKey(
            new NewsJudgmentReaderIdentity("alpha", "openai", "m2"),
            new NewsTypingReaderIdentity("alpha", "openai", "t"));

        Assert.Equal(FingerprintFor(Enabled(a)), FingerprintFor(Enabled(renamed)));
        Assert.NotEqual(FingerprintFor(Enabled(a)), FingerprintFor(Enabled(reModelled)));
    }

    // ---- what must NOT be in it -----------------------------------------------------------------------

    [Fact]
    public void TheMediaCollapseVersion_AppearsExactlyOnceAcrossTheHashedInputs()
    {
        // spec 194 §2, explicitly: media-collapse-v2 is already folded through
        // MediaAttentionCollapse.CanonicalDescriptor() and must not be duplicated inside the news segment.
        // Counted across ALL the descriptor fields the fingerprint hashes, so this catches a duplicate
        // wherever it were introduced — not only in the segment this slice added.
        var descriptors = new SignalSourceDescriptor(
                EnabledCollectorVocabulary.FromNames(["sec-edgar"]), null, null, null, Enabled())
                .CanonicalDescriptor()
            + new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default).CanonicalDescriptor()
            + new InsiderMaterialityWeights().CanonicalDescriptor()
            + new MediaAttentionCollapse(new MediaCollapseOptions()).CanonicalDescriptor();

        var occurrences = 0;
        for (var i = descriptors.IndexOf(MediaAttentionCollapse.Version, StringComparison.Ordinal);
            i >= 0;
            i = descriptors.IndexOf(MediaAttentionCollapse.Version, i + 1, StringComparison.Ordinal))
        {
            occurrences++;
        }

        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void TheExtractorRuleSetVersion_IsUnmovedByThisSlice()
    {
        // spec 194 §2 changes no extraction rule: the segment is added BESIDE rules=, never instead of it.
        Assert.Equal("radar-keyword-rules-v8", KeywordSignalExtractor.RuleSetVersion);
    }
}
