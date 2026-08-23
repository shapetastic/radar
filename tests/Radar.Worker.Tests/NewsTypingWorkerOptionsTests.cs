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
    public void InvalidLimits_FailStartup_EvenWhileTypingIsDisabled(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider((key, value)));

        Assert.Contains(key.Split(':')[^1], ex.Message);
    }

    [Fact]
    public void BlankOutputDirectory_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:Typing:OutputDirectory", " ")));

        Assert.Contains("OutputDirectory", ex.Message);
    }
}
