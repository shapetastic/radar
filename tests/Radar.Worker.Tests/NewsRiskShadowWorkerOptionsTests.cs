using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Evaluation;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 179 §11: the <c>Radar:NewsResearch:Shadow</c> block is FAIL-CLOSED (unknown keys / invalid limits /
/// invalid readers fail startup), the step is registered ONLY for an unfiltered full-mode run with at least
/// one resolvable reader, and an omitted <c>Readers</c> list resolves to exactly the ambient
/// <c>Radar:Ai</c> reader.
/// </summary>
public sealed class NewsRiskShadowWorkerOptionsTests
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
        ("Radar:NewsResearch:Shadow:Enabled", "true"),
        ("Radar:Ai:Provider", "ollama"),
        ("Radar:Ai:Model", "llama3.1"),
        // Configuring ANY Radar:Ai provider also wires the SEC earnings reader (spec 144: the ai=
        // fingerprint segment), which requires a compliant contact-bearing SEC User-Agent.
        ("Radar:Sec:UserAgent", "Radar Tests test@example.com"),
        .. extra,
    ];

    [Fact]
    public void Default_RegistersNoShadowGenerator()
    {
        using var provider = BuildProvider();

        Assert.Null(provider.GetService<INewsRiskShadowGenerator>());
        Assert.Null(provider.GetService<INewsRiskEvaluationGenerator>());
    }

    [Fact]
    public void EnabledInFullMode_WithAmbientAi_RegistersGeneratorAndEvaluator()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama());

        Assert.NotNull(provider.GetService<INewsRiskShadowGenerator>());
        Assert.NotNull(provider.GetService<INewsRiskEvaluationGenerator>());
    }

    [Fact]
    public void OmittedReaders_ResolveToExactlyTheAmbientReader()
    {
        // Spec 179 §5: an omitted/empty Readers list is byte-identical to the single-reader behaviour —
        // exactly ONE reader, over the ambient Radar:Ai provider/model, named "ambient".
        using var provider = BuildProvider(EnabledWithAmbientOllama());

        var readers = provider.GetRequiredService<NewsRiskReaderSet>();
        var identity = Assert.Single(readers.Readers).Identity;
        Assert.Equal("ambient", identity.Name);
        Assert.Equal("ollama", identity.Provider);
        Assert.Equal("llama3.1", identity.ModelId);
    }

    [Fact]
    public void TwoReaders_ResolveAsTwoIndependentCohorts()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama(
            ("Radar:NewsResearch:Shadow:Readers:0:Name", "local-a"),
            ("Radar:NewsResearch:Shadow:Readers:0:Provider", "ollama"),
            ("Radar:NewsResearch:Shadow:Readers:0:Model", "llama3.1"),
            ("Radar:NewsResearch:Shadow:Readers:1:Name", "local-b"),
            ("Radar:NewsResearch:Shadow:Readers:1:Provider", "ollama"),
            ("Radar:NewsResearch:Shadow:Readers:1:Model", "qwen3")));

        var readers = provider.GetRequiredService<NewsRiskReaderSet>();
        Assert.Equal(2, readers.Readers.Count);
        // Cohort identity is provider + exact model id + prompt/schema version — two readers, two cohorts.
        Assert.NotEqual(readers.Readers[0].Identity.CohortKey, readers.Readers[1].Identity.CohortKey);
    }

    [Theory]
    [InlineData("score")]
    [InlineData("collect")]
    public void NonFullModes_NeverRegisterTheShadowStep(string mode)
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama(("Radar:RunMode", mode)));

        Assert.Null(provider.GetService<INewsRiskShadowGenerator>());
        Assert.Null(provider.GetService<INewsRiskEvaluationGenerator>());
    }

    [Fact]
    public void ReplayMode_NeverRegistersTheShadowStep()
    {
        using var provider = BuildProvider(EnabledWithAmbientOllama(
            ("Radar:Replay:Enabled", "true"),
            ("Radar:Replay:From", "2026-05-01"),
            ("Radar:Replay:To", "2026-05-03"),
            ("Radar:Replay:Step", "1d")));

        Assert.Null(provider.GetService<INewsRiskShadowGenerator>());
    }

    [Fact]
    public void CompanyFilteredCollectPass_NeverRegistersTheShadowStep()
    {
        // A filter is collect-only (spec 161), and collect never registers the shadow — the filtered case
        // is therefore structurally unreachable; assert it anyway so a future filter-mode change fails here.
        using var provider = BuildProvider(EnabledWithAmbientOllama(
            ("Radar:RunMode", "collect"),
            ("Radar:Companies:0", "CASS")));

        Assert.Null(provider.GetService<INewsRiskShadowGenerator>());
    }

    [Fact]
    public void EnabledWithGenerateReportFalse_FailsStartup_NamingReportConstruction()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(EnabledWithAmbientOllama(("Radar:GenerateReport", "false"))));

        Assert.Contains("Radar:GenerateReport", ex.Message);
        Assert.Contains("report", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnabledWithNoResolvableReader_FailsStartup()
    {
        // No ambient Radar:Ai and no Readers entry: a shadow that silently never runs is a fail-open.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:Shadow:Enabled", "true")));

        Assert.Contains("reader", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateProviderModelPair_FailsStartup_NamingBothReaders()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(EnabledWithAmbientOllama(
                ("Radar:NewsResearch:Shadow:Readers:0:Name", "first"),
                ("Radar:NewsResearch:Shadow:Readers:0:Provider", "ollama"),
                ("Radar:NewsResearch:Shadow:Readers:0:Model", "llama3.1"),
                ("Radar:NewsResearch:Shadow:Readers:1:Name", "second"),
                ("Radar:NewsResearch:Shadow:Readers:1:Provider", "Ollama"),
                ("Radar:NewsResearch:Shadow:Readers:1:Model", "llama3.1"))));

        Assert.Contains("'first'", ex.Message);
        Assert.Contains("'second'", ex.Message);
        Assert.Contains("llama3.1", ex.Message);
    }

    [Fact]
    public void InvalidReaderConfig_FailsStartup_NamingTheReaderAndTheExactPath()
    {
        // A reader with a provider but no model is invalid; the failure names the reader AND the path.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(EnabledWithAmbientOllama(
                ("Radar:NewsResearch:Shadow:Readers:0:Name", "broken"),
                ("Radar:NewsResearch:Shadow:Readers:0:Provider", "ollama"))));

        Assert.Contains("broken", ex.Message);
        Assert.Contains("Radar:NewsResearch:Shadow:Readers:0", ex.Message);
    }

    [Fact]
    public void BlankReaderName_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(EnabledWithAmbientOllama(
                ("Radar:NewsResearch:Shadow:Readers:0:Provider", "ollama"),
                ("Radar:NewsResearch:Shadow:Readers:0:Model", "llama3.1"))));

        Assert.Contains("Name", ex.Message);
    }

    [Fact]
    public void UnknownShadowKey_FailsStartup_NamingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:Shadow:LookbakDays", "30")));

        Assert.Contains("LookbakDays", ex.Message);
        Assert.Contains("LookbackDays", ex.Message);
    }

    [Fact]
    public void UnknownReaderKey_FailsStartup_NamingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(
                ("Radar:NewsResearch:Shadow:Readers:0:Name", "x"),
                ("Radar:NewsResearch:Shadow:Readers:0:Providr", "ollama")));

        Assert.Contains("Providr", ex.Message);
    }

    [Theory]
    [InlineData("Radar:NewsResearch:Shadow:LookbackDays", "0")]
    [InlineData("Radar:NewsResearch:Shadow:MaxCompaniesPerRun", "-1")]
    [InlineData("Radar:NewsResearch:Shadow:MaxArticlesPerCompany", "0")]
    [InlineData("Radar:NewsResearch:Shadow:MaxFetchedArticlesPerCompany", "-1")]
    public void InvalidLimits_FailStartup_EvenWhileTheShadowIsDisabled(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider((key, value)));

        Assert.Contains(key.Split(':')[^1], ex.Message);
    }
}
