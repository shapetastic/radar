using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.Collectors;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Scoring;

namespace Radar.Worker.Tests;

/// <summary>
/// SPEC 194 §2, asserted through the REAL composed Worker graphs (the spec-147
/// <see cref="CollectorVocabularyTests.ScoreAndFullModes_StillStampTheSameFingerprint_TheMarkerIsHashedIntoNothing"/>
/// pattern): the news-read scoring identity must be constructible in every scoring-capable mode from
/// validated configuration alone, so <c>full</c>, <c>score</c> and <c>replay</c> over the same effective
/// judgment configuration stamp the SAME <c>ScoringConfigVersion</c> — while judgment off/on and a
/// judge-model/presentation-cohort change move it, and cost controls do not.
/// </summary>
public sealed class NewsJudgmentScoringIdentityModeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"radar-news-identity-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Builds the graph from an in-memory configuration, with LAST-WINS de-duplication of repeated keys so
    /// a test can express "the baseline configuration, except this one key" by appending an override
    /// instead of restating the whole block (AddInMemoryCollection itself throws on a duplicate key).
    /// </summary>
    private static ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            merged[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(merged).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>Every file-store root pointed into this test's own temp directory, so nothing writes cruft.</summary>
    private (string Key, string Value)[] TempDirectories() =>
    [
        ("Radar:EvidenceSourceDirectory", Path.Combine(_root, "evidence")),
        ("Radar:EvidenceRawDirectory", Path.Combine(_root, "evidence-raw")),
        ("Radar:SignalsDirectory", Path.Combine(_root, "signals")),
        ("Radar:ScoresDirectory", Path.Combine(_root, "scores")),
        ("Radar:ReportDirectory", Path.Combine(_root, "reports")),
        ("Radar:RunsDirectory", Path.Combine(_root, "runs")),
        ("Radar:ScoringConfigsDirectory", Path.Combine(_root, "scoring-configs")),
        ("Radar:NewsResearch:ObservationDirectory", Path.Combine(_root, "news-observations")),
        ("Radar:NewsResearch:Typing:OutputDirectory", Path.Combine(_root, "news-typing")),
        ("Radar:NewsResearch:Shadow:OutputDirectory", Path.Combine(_root, "news-risk")),
    ];

    /// <summary>
    /// Typing + judgment enabled over a single named local reader on both stages, with the presentation
    /// cohort designating it. Local ollama, so no API key is involved: the identity is resolved from
    /// provider + model, and this composition never constructs a client in score/replay mode anyway.
    /// </summary>
    private (string Key, string Value)[] JudgmentEnabled(
        string judgeModel = "judge-model",
        string extractorModel = "extractor-model",
        params (string Key, string Value)[] extra) =>
    [
        ("Radar:Collectors:0", "rss"),
        ("Radar:NewsResearch:Typing:Enabled", "true"),
        ("Radar:NewsResearch:Typing:Readers:0:Name", "reader-one"),
        ("Radar:NewsResearch:Typing:Readers:0:Provider", "ollama"),
        ("Radar:NewsResearch:Typing:Readers:0:Model", extractorModel),
        ("Radar:NewsResearch:Judgment:Enabled", "true"),
        ("Radar:NewsResearch:Judgment:Judges:0:Name", "judge-one"),
        ("Radar:NewsResearch:Judgment:Judges:0:Provider", "ollama"),
        ("Radar:NewsResearch:Judgment:Judges:0:Model", judgeModel),
        ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "judge-one"),
        ("Radar:NewsResearch:Judgment:PresentationCohort:Extractor", "reader-one"),
        .. TempDirectories(),
        .. extra,
    ];

    private static string FingerprintOf(ServiceProvider provider) =>
        provider.GetRequiredService<IScoringStrategyFactory>().Primary.Engine.EffectiveConfig.Fingerprint;

    // ---- full / score / replay stamp the SAME identity -------------------------------------------------

    [Theory]
    [InlineData("score")]
    [InlineData("collect")]
    public void FullAndNonFullModes_StampTheSameFingerprint(string mode)
    {
        // THE constructibility criterion. A score pass registers NO collector and NO judgment step — the
        // step's structural gate is unfiltered-full-mode only — yet it must stamp exactly what a full run
        // over the same configuration stamps, or collect-then-score would stop being equivalent to the
        // combined run (spec 144's invariant) the moment judgment was enabled.
        using var full = BuildProvider(JudgmentEnabled());
        using var other = BuildProvider(JudgmentEnabled(extra: [("Radar:RunMode", mode)]));

        Assert.Equal(FingerprintOf(full), FingerprintOf(other));
    }

    [Fact]
    public void ReplayMode_StampsTheSameFingerprint()
    {
        using var full = BuildProvider(JudgmentEnabled());
        using var replay = BuildProvider(JudgmentEnabled(extra: [
            ("Radar:Replay:Enabled", "true"),
            ("Radar:Replay:From", "2026-05-01"),
            ("Radar:Replay:To", "2026-05-03"),
            ("Radar:Replay:Step", "1d"),
        ]));

        Assert.Equal(FingerprintOf(full), FingerprintOf(replay));
    }

    [Fact]
    public void AScorePass_ResolvesTheIdentity_WithoutConstructingAnyJudgeOrExtractor()
    {
        // The identity holds strings (the spec-147 EnabledCollectorVocabulary posture), so knowing the
        // cohort costs no client and no request. Asserted structurally: the judgment/typing steps are not
        // registered at all in score mode, and neither is any collector — yet the identity is present and
        // says "enabled".
        using var score = BuildProvider(JudgmentEnabled(extra: [("Radar:RunMode", "score")]));

        Assert.Null(score.GetService<INewsJudgmentGenerator>());
        Assert.Null(score.GetService<INewsTypingGenerator>());
        Assert.Empty(score.GetServices<IEvidenceCollector>());

        var identity = score.GetRequiredService<NewsJudgmentScoringIdentity>();
        Assert.Contains("news=enabled:", identity.Segment, StringComparison.Ordinal);
        Assert.Contains("judge-model", identity.Segment, StringComparison.Ordinal);
    }

    // ---- what MUST move it -----------------------------------------------------------------------------

    [Fact]
    public void JudgmentOffVersusOn_MovesTheFingerprint_InTheComposedGraph()
    {
        using var on = BuildProvider(JudgmentEnabled());
        using var off = BuildProvider([
            ("Radar:Collectors:0", "rss"),
            .. TempDirectories(),
        ]);

        Assert.NotEqual(FingerprintOf(off), FingerprintOf(on));
        Assert.Contains(
            "news=disabled:",
            off.GetRequiredService<NewsJudgmentScoringIdentity>().Segment,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheJudgeModel_MovesTheFingerprint()
    {
        using var a = BuildProvider(JudgmentEnabled());
        using var b = BuildProvider(JudgmentEnabled(judgeModel: "some-other-judge-model"));

        Assert.NotEqual(FingerprintOf(a), FingerprintOf(b));
    }

    [Fact]
    public void ChangingTheStage1ExtractorModel_MovesTheFingerprint()
    {
        // The stage-1 cohort identity rides INSIDE the stage-2 cohort key, so a typing-model change forks
        // the presentation cohort too — which is correct: the judge would be reading different facts.
        using var a = BuildProvider(JudgmentEnabled());
        using var b = BuildProvider(JudgmentEnabled(extractorModel: "some-other-extractor-model"));

        Assert.NotEqual(FingerprintOf(a), FingerprintOf(b));
    }

    [Fact]
    public void ChangingTheDesignatedPresentationCohort_MovesTheFingerprint()
    {
        // Two configured judges, and the designation switched between them. Nothing else differs — same
        // readers, same budgets — so this isolates the DESIGNATION itself, which is what decides whose
        // verdict may mint a directional signal (§1.2).
        (string, string)[] twoJudges =
        [
            .. JudgmentEnabled(),
            ("Radar:NewsResearch:Judgment:Judges:1:Name", "judge-two"),
            ("Radar:NewsResearch:Judgment:Judges:1:Provider", "ollama"),
            ("Radar:NewsResearch:Judgment:Judges:1:Model", "judge-model-two"),
        ];

        using var designatingOne = BuildProvider(twoJudges);
        using var designatingTwo = BuildProvider([
            .. twoJudges,
            ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "judge-two"),
        ]);

        Assert.NotEqual(FingerprintOf(designatingOne), FingerprintOf(designatingTwo));
    }

    // ---- what must NOT move it -------------------------------------------------------------------------

    [Theory]
    [InlineData("Radar:NewsResearch:Judgment:MaxCompaniesPerRun", "7")]
    [InlineData("Radar:NewsResearch:Judgment:MaxFamiliesPerJudgment", "11")]
    [InlineData("Radar:NewsResearch:Judgment:MaxJudgmentAttempts", "2")]
    [InlineData("Radar:NewsResearch:Typing:MaxNewTypingsPerRun", "411")]
    [InlineData("Radar:NewsResearch:Typing:MaxCandidateTypingsPerRun", "37")]
    [InlineData("Radar:NewsResearch:Typing:MaxRetryTypingsPerRun", "9")]
    [InlineData("Radar:NewsResearch:Typing:MaxTypingAttempts", "5")]
    [InlineData("Radar:NewsResearch:Typing:LookbackDays", "45")]
    public void CostControlsAndBudgets_DoNotMoveTheFingerprint(string key, string value)
    {
        // spec 194 §2's explicit non-goal. A budget or retry cap changes how much Radar SPENDS discovering a
        // judgment; it never changes what a judgment means, and folding it in would re-stamp a whole series
        // — breaking every accrued snapshot's comparability — for a throttle change. This is the spec-141
        // rule that a fingerprint records identity, not operational posture.
        using var baseline = BuildProvider(JudgmentEnabled());
        using var throttled = BuildProvider(JudgmentEnabled(extra: [(key, value)]));

        Assert.Equal(FingerprintOf(baseline), FingerprintOf(throttled));
    }

    [Fact]
    public void AReaderApiKeyEnvironmentVariable_IsNeverPartOfTheIdentity()
    {
        // Two graphs whose judge differs ONLY in which environment variable names its key. The key VALUE is
        // never read into the identity and the variable NAME is not either: the cohort is provider + exact
        // model id + contract versions. A rotated key must not re-stamp a series, and a key must never be
        // recoverable from a fingerprint input.
        const string keyEnvVarA = "RADAR_TEST_NEWS_IDENTITY_KEY_A";
        const string keyEnvVarB = "RADAR_TEST_NEWS_IDENTITY_KEY_B";
        Environment.SetEnvironmentVariable(keyEnvVarA, "key-value-a");
        Environment.SetEnvironmentVariable(keyEnvVarB, "key-value-b");
        try
        {
            (string, string)[] Hosted(string envVar) =>
            [
                ("Radar:Collectors:0", "rss"),
                ("Radar:NewsResearch:Typing:Enabled", "true"),
                ("Radar:NewsResearch:Typing:Readers:0:Name", "reader-one"),
                ("Radar:NewsResearch:Typing:Readers:0:Provider", "ollama"),
                ("Radar:NewsResearch:Typing:Readers:0:Model", "extractor-model"),
                ("Radar:NewsResearch:Judgment:Enabled", "true"),
                ("Radar:NewsResearch:Judgment:Judges:0:Name", "judge-one"),
                ("Radar:NewsResearch:Judgment:Judges:0:Provider", "openai"),
                ("Radar:NewsResearch:Judgment:Judges:0:OpenAi:BaseUrl", "https://example.invalid/v1"),
                ("Radar:NewsResearch:Judgment:Judges:0:OpenAi:Model", "hosted-judge-model"),
                ("Radar:NewsResearch:Judgment:Judges:0:OpenAi:ApiKeyEnvVar", envVar),
                ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "judge-one"),
                ("Radar:NewsResearch:Judgment:PresentationCohort:Extractor", "reader-one"),
                .. TempDirectories(),
            ];

            using var a = BuildProvider(Hosted(keyEnvVarA));
            using var b = BuildProvider(Hosted(keyEnvVarB));

            Assert.Equal(FingerprintOf(a), FingerprintOf(b));

            var segment = a.GetRequiredService<NewsJudgmentScoringIdentity>().Segment;
            Assert.DoesNotContain("key-value-a", segment, StringComparison.Ordinal);
            Assert.DoesNotContain(keyEnvVarA, segment, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyEnvVarA, null);
            Environment.SetEnvironmentVariable(keyEnvVarB, null);
        }
    }

    [Fact]
    public void RenamingAReader_DoesNotMoveTheFingerprint()
    {
        // The spec-179 rule at the composed-graph level: a reader NAME is a provenance label, so renaming
        // one (and the designation with it) forks no cohort and must move no stamp.
        using var a = BuildProvider(JudgmentEnabled());
        using var renamed = BuildProvider([
            ("Radar:Collectors:0", "rss"),
            ("Radar:NewsResearch:Typing:Enabled", "true"),
            ("Radar:NewsResearch:Typing:Readers:0:Name", "renamed-reader"),
            ("Radar:NewsResearch:Typing:Readers:0:Provider", "ollama"),
            ("Radar:NewsResearch:Typing:Readers:0:Model", "extractor-model"),
            ("Radar:NewsResearch:Judgment:Enabled", "true"),
            ("Radar:NewsResearch:Judgment:Judges:0:Name", "renamed-judge"),
            ("Radar:NewsResearch:Judgment:Judges:0:Provider", "ollama"),
            ("Radar:NewsResearch:Judgment:Judges:0:Model", "judge-model"),
            ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "renamed-judge"),
            ("Radar:NewsResearch:Judgment:PresentationCohort:Extractor", "renamed-reader"),
            .. TempDirectories(),
        ]);

        Assert.Equal(FingerprintOf(a), FingerprintOf(renamed));
    }

    // ---- the designation is validated in EVERY mode, because it IS the identity ------------------------

    [Theory]
    [InlineData("score")]
    [InlineData("collect")]
    public void AnUnknownDesignatedJudge_FailsStartupInNonFullModesToo(string mode)
    {
        // The spec-147 precedent applied: Radar:Collectors became a vocabulary read in every mode once it
        // was recorded provenance. The presentation-cohort designation is now a hashed identity input, so a
        // designation that names no configured reader must fail wherever it is read — the alternative is a
        // score pass stamping an identity it cannot justify.
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(JudgmentEnabled(extra: [
            ("Radar:RunMode", mode),
            ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "no-such-judge"),
        ])));

        Assert.Contains("no-such-judge", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JudgmentDisabled_ResolvesTheDisabledIdentity_WithoutReadingAnyReader()
    {
        // Disabled means disabled: no reader list is resolved, so a graph that configures nothing at all
        // still starts and still records an EXPLICIT disabled segment rather than silence.
        using var provider = BuildProvider(TempDirectories());

        Assert.Same(
            NewsJudgmentScoringIdentity.Disabled,
            provider.GetRequiredService<NewsJudgmentScoringIdentity>());
    }
}
