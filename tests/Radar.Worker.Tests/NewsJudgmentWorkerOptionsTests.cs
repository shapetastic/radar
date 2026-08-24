using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.NewsRisk.Judgment;
using Radar.Application.Reporting;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 185 §4/§5: the <c>Radar:NewsResearch:Judgment</c> block is FAIL-CLOSED (unknown keys / invalid
/// limits / invalid judges / a dangling presentation cohort fail startup), the step requires typing
/// (naming both keys), is registered ONLY for an unfiltered full-mode run, and is DISABLED by default (the
/// default graph is byte-unchanged).
/// </summary>
public sealed class NewsJudgmentWorkerOptionsTests
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
        ("Radar:NewsResearch:Judgment:Enabled", "true"),
        ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "ambient"),
        ("Radar:NewsResearch:Judgment:PresentationCohort:Extractor", "ambient"),
        .. AmbientAi(),
        .. extra,
    ];

    // Configuring ANY Radar:Ai provider also wires the SEC earnings reader (spec 144: the ai=
    // fingerprint segment), which requires a compliant contact-bearing SEC User-Agent.
    private static (string, string)[] AmbientAi() =>
    [
        ("Radar:Ai:Provider", "ollama"),
        ("Radar:Ai:Model", "llama3.1"),
        ("Radar:Sec:UserAgent", "Radar Tests test@example.com"),
    ];

    [Fact]
    public void Default_RegistersNoJudgmentGeneratorAndNoRerenderer()
    {
        using var provider = BuildProvider();

        Assert.Null(provider.GetService<INewsJudgmentGenerator>());
        Assert.Null(provider.GetService<IWeeklyReportJudgmentRerenderer>());
    }

    [Fact]
    public void EnabledInFullMode_WithTypingAndAmbientAi_RegistersTheGeneratorStoreAndRerenderer()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama());

        Assert.NotNull(provider.GetService<INewsJudgmentGenerator>());
        Assert.NotNull(provider.GetService<INewsJudgmentStore>());
        Assert.NotNull(provider.GetService<IWeeklyReportJudgmentRerenderer>());
    }

    [Fact]
    public void OmittedJudges_ResolveToExactlyTheAmbientReader_WithTheComposedStage2CohortKey()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama());

        var judges = provider.GetRequiredService<NewsJudgmentReaderSet>();
        var identity = Assert.Single(judges.Readers).Identity;
        Assert.Equal("ambient", identity.Name);
        Assert.Equal("ollama", identity.Provider);
        Assert.Equal("llama3.1", identity.ModelId);
        // The stage-2 cohort key composes the stage-1 cohort AND the family-builder identity.
        var key = identity.CohortKeyFor("stage1-key");
        Assert.Contains("news-judgment-prompt-v1", key);
        Assert.Contains("stage1=stage1-key", key);
        Assert.Contains("families=fact-family-v2", key);
    }

    [Fact]
    public void EnabledWithoutTyping_FailsStartup_NamingBothKeys()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(
            [
                ("Radar:NewsResearch:Judgment:Enabled", "true"),
                ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "ambient"),
                ("Radar:NewsResearch:Judgment:PresentationCohort:Extractor", "ambient"),
                .. AmbientAi(),
            ]));

        Assert.Contains("Radar:NewsResearch:Judgment:Enabled", ex.Message);
        Assert.Contains("Radar:NewsResearch:Typing:Enabled", ex.Message);
    }

    [Theory]
    [InlineData("score")]
    [InlineData("collect")]
    public void NonFullModes_NeverRegisterTheJudgmentStep(string mode)
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama(("Radar:RunMode", mode)));

        Assert.Null(provider.GetService<INewsJudgmentGenerator>());
        Assert.Null(provider.GetService<IWeeklyReportJudgmentRerenderer>());
    }

    [Fact]
    public void MissingPresentationCohort_FailsStartup_StatingProspectiveDesignation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(
            [
                ("Radar:NewsResearch:Typing:Enabled", "true"),
                ("Radar:NewsResearch:Judgment:Enabled", "true"),
                .. AmbientAi(),
            ]));

        Assert.Contains("PresentationCohort", ex.Message);
        Assert.Contains("PROSPECTIVELY", ex.Message);
    }

    [Fact]
    public void PresentationJudgeNamingAnUnknownReader_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(
            [
                ("Radar:NewsResearch:Typing:Enabled", "true"),
                ("Radar:NewsResearch:Judgment:Enabled", "true"),
                ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "no-such-judge"),
                ("Radar:NewsResearch:Judgment:PresentationCohort:Extractor", "ambient"),
                .. AmbientAi(),
            ]));

        Assert.Contains("no-such-judge", ex.Message);
        Assert.Contains("PresentationCohort:Judge", ex.Message);
    }

    [Fact]
    public void PresentationExtractorNamingAnUnknownTypingReader_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(
            [
                ("Radar:NewsResearch:Typing:Enabled", "true"),
                ("Radar:NewsResearch:Typing:Readers:0:Name", "hosted"),
                ("Radar:NewsResearch:Typing:Readers:0:Provider", "ollama"),
                ("Radar:NewsResearch:Typing:Readers:0:Model", "qwen3"),
                ("Radar:NewsResearch:Judgment:Enabled", "true"),
                ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "ambient"),
                // With a configured typing reader list, "ambient" is no longer a valid extractor name.
                ("Radar:NewsResearch:Judgment:PresentationCohort:Extractor", "ambient"),
                .. AmbientAi(),
            ]));

        Assert.Contains("PresentationCohort:Extractor", ex.Message);
        Assert.Contains("hosted", ex.Message);
    }

    [Fact]
    public void UnknownJudgmentKey_FailsStartup_NamingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:Judgment:MaxCompaniesPerRunn", "30")));

        Assert.Contains("MaxCompaniesPerRunn", ex.Message);
    }

    [Fact]
    public void UnknownPresentationCohortKey_FailsStartup_NamingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:Judgment:PresentationCohort:Judgee", "x")));

        Assert.Contains("Judgee", ex.Message);
    }

    [Theory]
    [InlineData("Radar:NewsResearch:Judgment:MaxCompaniesPerRun", "0")]
    [InlineData("Radar:NewsResearch:Judgment:MaxFamiliesPerJudgment", "-1")]
    public void InvalidLimits_FailStartup_EvenWhileJudgmentIsDisabled(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider((key, value)));

        Assert.Contains(key.Split(':')[^1], ex.Message);
    }

    [Fact]
    public void GenerateReportDisabled_FailsStartup_NamingBothKeys()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(EnabledWithAmbientOllama(("Radar:GenerateReport", "false"))));

        Assert.Contains("Radar:NewsResearch:Judgment:Enabled", ex.Message);
        Assert.Contains("Radar:GenerateReport", ex.Message);
    }

    [Fact]
    public void DuplicateJudgeProviderModelPair_FailsStartup_NamingBothJudges()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(
            [
                ("Radar:NewsResearch:Typing:Enabled", "true"),
                ("Radar:NewsResearch:Judgment:Enabled", "true"),
                ("Radar:NewsResearch:Judgment:PresentationCohort:Judge", "first"),
                ("Radar:NewsResearch:Judgment:PresentationCohort:Extractor", "ambient"),
                ("Radar:NewsResearch:Judgment:Judges:0:Name", "first"),
                ("Radar:NewsResearch:Judgment:Judges:0:Provider", "ollama"),
                ("Radar:NewsResearch:Judgment:Judges:0:Model", "llama3.1"),
                ("Radar:NewsResearch:Judgment:Judges:1:Name", "second"),
                ("Radar:NewsResearch:Judgment:Judges:1:Provider", "Ollama"),
                ("Radar:NewsResearch:Judgment:Judges:1:Model", "llama3.1"),
                .. AmbientAi(),
            ]));

        Assert.Contains("'first'", ex.Message);
        Assert.Contains("'second'", ex.Message);
        Assert.Contains("News-judgment", ex.Message);
    }
}
