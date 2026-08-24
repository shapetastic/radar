using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.NewsTyping;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 181 §4/§6: the <c>Radar:NewsResearch:Typing</c> block is FAIL-CLOSED (unknown keys / invalid limits /
/// invalid readers fail startup), the step is registered ONLY for an unfiltered full-mode run with at least
/// one resolvable reader, DISABLED by default (the default graph is byte-unchanged), and an omitted
/// <c>Readers</c> list resolves to exactly the ambient <c>Radar:Ai</c> reader.
/// </summary>
public sealed class NewsTypingWorkerOptionsTests
{
    private static ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        return services.BuildServiceProvider();
    }

    private static (string, string)[] EnabledWithAmbientOllama(params (string, string)[] extra) =>
    [
        ("Radar:NewsResearch:Typing:Enabled", "true"),
        ("Radar:Ai:Provider", "ollama"),
        ("Radar:Ai:Model", "llama3.1"),
        // Configuring ANY Radar:Ai provider also wires the SEC earnings reader (spec 144: the ai=
        // fingerprint segment), which requires a compliant contact-bearing SEC User-Agent.
        ("Radar:Sec:UserAgent", "Radar Tests test@example.com"),
        .. extra,
    ];

    [Fact]
    public void Default_RegistersNoTypingGenerator()
    {
        using var provider = BuildProvider();

        Assert.Null(provider.GetService<INewsTypingGenerator>());
    }

    [Fact]
    public void EnabledInFullMode_WithAmbientAi_RegistersTheGenerator()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama());

        Assert.NotNull(provider.GetService<INewsTypingGenerator>());
        Assert.NotNull(provider.GetService<INewsTypingStore>());
        Assert.NotNull(provider.GetService<IFactFamilySnapshotStore>());

        // Spec 187 3: the durable PRE-CALL attempt ledger is registered beside the outcome store, as ONE
        // singleton - a per-resolution instance would re-hydrate (and re-race) on every use.
        var ledger = provider.GetService<INewsTypingAttemptLedger>();
        Assert.NotNull(ledger);
        Assert.Same(ledger, provider.GetService<INewsTypingAttemptLedger>());
    }

    [Fact]
    public void OmittedReaders_ResolveToExactlyTheAmbientReader()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama());

        var readers = provider.GetRequiredService<NewsTypingReaderSet>();
        var identity = Assert.Single(readers.Readers).Identity;
        Assert.Equal("ambient", identity.Name);
        Assert.Equal("ollama", identity.Provider);
        Assert.Equal("llama3.1", identity.ModelId);
        // The typing cohort key folds the TAXONOMY version — a different cohort universe from news-risk.
        Assert.Contains("news-event-taxonomy-v1", identity.CohortKey);
    }

    [Fact]
    public void TypingReaders_AreTheirOwnList_IndependentOfTheShadowReaders()
    {
        // Typing can run hosted-only while the news-risk shadow runs both cohorts (spec 181 §1's verdict:
        // the local model is not a viable solo typing reader).
        using var provider = BuildProvider(EnabledWithAmbientOllama(
            ("Radar:NewsResearch:Typing:Readers:0:Name", "hosted-only"),
            ("Radar:NewsResearch:Typing:Readers:0:Provider", "ollama"),
            ("Radar:NewsResearch:Typing:Readers:0:Model", "qwen3")));

        var readers = provider.GetRequiredService<NewsTypingReaderSet>();
        var identity = Assert.Single(readers.Readers).Identity;
        Assert.Equal("hosted-only", identity.Name);
        Assert.Equal("qwen3", identity.ModelId);
    }

    [Theory]
    [InlineData("score")]
    [InlineData("collect")]
    public void NonFullModes_NeverRegisterTheTypingStep(string mode)
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama(("Radar:RunMode", mode)));

        Assert.Null(provider.GetService<INewsTypingGenerator>());
    }

    [Fact]
    public void ReplayMode_NeverRegistersTheTypingStep()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama(
            ("Radar:Replay:Enabled", "true"),
            ("Radar:Replay:From", "2026-05-01"),
            ("Radar:Replay:To", "2026-05-03"),
            ("Radar:Replay:Step", "1d")));

        Assert.Null(provider.GetService<INewsTypingGenerator>());
    }

    [Fact]
    public void CompanyFilteredCollectPass_NeverRegistersTheTypingStep()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama(
            ("Radar:RunMode", "collect"),
            ("Radar:Companies:0", "CASS")));

        Assert.Null(provider.GetService<INewsTypingGenerator>());
    }

    [Fact]
    public void EnabledWithNoResolvableReader_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:Typing:Enabled", "true")));

        Assert.Contains("typing reader", ex.Message);
    }

    [Fact]
    public void DuplicateProviderModelPair_FailsStartup_NamingBothReaders()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(EnabledWithAmbientOllama(
                ("Radar:NewsResearch:Typing:Readers:0:Name", "first"),
                ("Radar:NewsResearch:Typing:Readers:0:Provider", "ollama"),
                ("Radar:NewsResearch:Typing:Readers:0:Model", "llama3.1"),
                ("Radar:NewsResearch:Typing:Readers:1:Name", "second"),
                ("Radar:NewsResearch:Typing:Readers:1:Provider", "Ollama"),
                ("Radar:NewsResearch:Typing:Readers:1:Model", "llama3.1"))));

        Assert.Contains("'first'", ex.Message);
        Assert.Contains("'second'", ex.Message);
        Assert.Contains("News-typing", ex.Message);
    }

    [Fact]
    public void InvalidReaderConfig_FailsStartup_NamingTheReaderAndTheExactPath()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(EnabledWithAmbientOllama(
                ("Radar:NewsResearch:Typing:Readers:0:Name", "broken"),
                ("Radar:NewsResearch:Typing:Readers:0:Provider", "ollama"))));

        Assert.Contains("broken", ex.Message);
        Assert.Contains("Radar:NewsResearch:Typing:Readers:0", ex.Message);
    }

    [Fact]
    public void UnknownTypingKey_FailsStartup_NamingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:Typing:LookbakDays", "30")));

        Assert.Contains("LookbakDays", ex.Message);
        Assert.Contains("LookbackDays", ex.Message);
    }

    [Fact]
    public void UnknownTypingReaderKey_FailsStartup_NamingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(
                ("Radar:NewsResearch:Typing:Readers:0:Name", "x"),
                ("Radar:NewsResearch:Typing:Readers:0:Providr", "ollama")));

        Assert.Contains("Providr", ex.Message);
    }

    [Theory]
    [InlineData("Radar:NewsResearch:Typing:MaxNewTypingsPerRun", "0")]
    [InlineData("Radar:NewsResearch:Typing:MaxNewTypingsPerRun", "-5")]
    [InlineData("Radar:NewsResearch:Typing:LookbackDays", "0")]
    // Spec 186 §2: the retry bounds are validated at the SAME boundary as their siblings. Zero is rejected
    // for the retry lane specifically — it would re-permit total retry starvation.
    [InlineData("Radar:NewsResearch:Typing:MaxTypingAttempts", "0")]
    [InlineData("Radar:NewsResearch:Typing:MaxTypingAttempts", "-1")]
    [InlineData("Radar:NewsResearch:Typing:MaxRetryTypingsPerRun", "0")]
    [InlineData("Radar:NewsResearch:Typing:MaxRetryTypingsPerRun", "-1")]
    public void InvalidLimits_FailStartup_EvenWhileTypingIsDisabled(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider((key, value)));

        Assert.Contains(key.Split(':')[^1], ex.Message);
    }

    [Fact]
    public void RetryLaneAtOrAboveThePerRunCap_FailsStartup_NamingBothKeys()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(
            ("Radar:NewsResearch:Typing:MaxNewTypingsPerRun", "10"),
            ("Radar:NewsResearch:Typing:MaxRetryTypingsPerRun", "10")));

        Assert.Contains("MaxRetryTypingsPerRun", ex.Message);
        Assert.Contains("MaxNewTypingsPerRun", ex.Message);
    }

    [Fact]
    public void RetryBounds_HaveTheirDeclaredDefaults()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama());

        var options = provider.GetRequiredService<NewsTypingOptions>();
        Assert.Equal(3, options.MaxTypingAttempts);
        Assert.Equal(25, options.MaxRetryTypingsPerRun);

        // Spec 187 §2: the candidate lane's default rides the SAME limits record, trailing and nullable so
        // a pre-187 typing record hydrates as "not recorded" rather than as a fabricated lane width.
        Assert.Equal(100, options.MaxCandidateTypingsPerRun);
        Assert.Equal(
            new NewsTypingLimitsRecord(options.MaxNewTypingsPerRun, options.LookbackDays, 3, 25, 100),
            options.ToLimitsRecord());
    }

    /// <summary>
    /// Spec 187 §2: a limits record written before this slice carries NO candidate lane width, and that
    /// <c>null</c> must survive as "not recorded" — never default to the shipped 100, which would claim a
    /// pre-187 attempt ran under a lane that did not exist.
    /// </summary>
    [Fact]
    public void LimitsRecord_WithoutTheCandidateLane_HydratesAsNotRecorded()
    {
        var legacy = new NewsTypingLimitsRecord(200, 30, 3, 25);

        Assert.Null(legacy.MaxCandidateTypingsPerRun);
        Assert.NotEqual(new NewsTypingLimitsRecord(200, 30, 3, 25, 100), legacy);
    }

    /// <summary>
    /// Spec 187 §2: the candidate lane must be at least 1. Zero would restore the exact live failure the
    /// lane exists to fix — a whole budget spent on the global queue while every judged company stayed
    /// untyped — so it is a startup error, never a silently inert lane.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void CandidateLaneBelowOne_FailsStartup_EvenWhileTypingIsDisabled(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(
            ("Radar:NewsResearch:Typing:MaxCandidateTypingsPerRun", value)));

        Assert.Contains("MaxCandidateTypingsPerRun", ex.Message);
    }

    /// <summary>
    /// Spec 187 §2's three-way reservation: with judgment ENABLED, candidate + retry must stay strictly
    /// below the per-run budget so at least one GENERAL first-attempt slot survives — candidate priority
    /// must never be able to stop the legacy backlog draining. The message names the rule and all three
    /// actual values.
    /// </summary>
    [Fact]
    public void CandidatePlusRetryLaneFillingTheBudget_FailsStartup_WhenJudgmentIsEnabled()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(EnabledWithAmbientOllama(
            ("Radar:NewsResearch:Judgment:Enabled", "true"),
            ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "ambient"),
            ("Radar:NewsResearch:Judgment:PresentationCohort:Extractor", "ambient"),
            ("Radar:NewsResearch:Typing:MaxNewTypingsPerRun", "200"),
            ("Radar:NewsResearch:Typing:MaxCandidateTypingsPerRun", "175"),
            ("Radar:NewsResearch:Typing:MaxRetryTypingsPerRun", "25"))));

        Assert.Contains("MaxCandidateTypingsPerRun", ex.Message);
        Assert.Contains("MaxRetryTypingsPerRun", ex.Message);
        Assert.Contains("MaxNewTypingsPerRun", ex.Message);
        Assert.Contains("175", ex.Message);
        Assert.Contains("25", ex.Message);
        Assert.Contains("200", ex.Message);
        Assert.Contains("general", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The SAME values are ACCEPTED with judgment disabled: no candidate plan can exist (the planner is
    /// registered with judgment), so the candidate lane is structurally empty and the reservation it
    /// protects is vacuous. Stated explicitly because the rule is deliberately conditional.
    /// </summary>
    [Fact]
    public void CandidatePlusRetryLaneFillingTheBudget_IsAccepted_WhenJudgmentIsDisabled()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama(
            ("Radar:NewsResearch:Typing:MaxNewTypingsPerRun", "200"),
            ("Radar:NewsResearch:Typing:MaxCandidateTypingsPerRun", "175"),
            ("Radar:NewsResearch:Typing:MaxRetryTypingsPerRun", "25")));

        var options = provider.GetRequiredService<NewsTypingOptions>();
        Assert.Equal(175, options.MaxCandidateTypingsPerRun);
        Assert.Null(provider.GetService<Application.NewsRisk.Judgment.INewsJudgmentCandidatePlanner>());
    }

    /// <summary>The shipped defaults satisfy the rule with room to spare: 100 + 25 &lt; 200 leaves 75 general slots.</summary>
    [Fact]
    public void ShippedDefaults_SatisfyTheThreeWayReservation_WithJudgmentEnabled()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama(
            ("Radar:NewsResearch:Judgment:Enabled", "true"),
            ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "ambient"),
            ("Radar:NewsResearch:Judgment:PresentationCohort:Extractor", "ambient")));

        var options = provider.GetRequiredService<NewsTypingOptions>();
        Assert.Equal(100, options.MaxCandidateTypingsPerRun);
        Assert.Equal(25, options.MaxRetryTypingsPerRun);
        Assert.Equal(200, options.MaxNewTypingsPerRun);
        Assert.Equal(
            75,
            options.MaxNewTypingsPerRun
                - options.MaxCandidateTypingsPerRun
                - options.MaxRetryTypingsPerRun);
        Assert.NotNull(provider.GetService<Application.NewsRisk.Judgment.INewsJudgmentCandidatePlanner>());
    }

    [Fact]
    public void BlankOutputDirectory_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:Typing:OutputDirectory", " ")));

        Assert.Contains("OutputDirectory", ex.Message);
    }
}
