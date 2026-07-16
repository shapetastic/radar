using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Radar.Application.Filings;
using Radar.Infrastructure.Ai;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Filings;

public sealed class AddDirectionalFilingSignalsTests
{
    [Fact]
    public void AddDirectionalFilingSignals_ComposesAgainstReaderAndAnalyzer_ResolvesSource()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services
            .AddRadarAi(new AiClientOptions
            {
                Provider = "ollama",
                Model = "llama3.1",
                OllamaEndpoint = "http://localhost:11434",
            })
            .AddRadarFilingAnalyzer(new FilingAnalyzerOptions { MaxInputLength = 12000 })
            .AddSecEarningsReleaseReader(new SecCollectorOptions { UserAgent = "Radar Research test@example.com" })
            .AddDirectionalFilingSignals(new DirectionalFilingSignalOptions())
            // The source now depends on IAnalyzedFilingCache (spec 107) — register it so the provider resolves.
            .AddFileAnalyzedFilingCache(Path.GetTempPath())
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<IDirectionalFilingSignalSource>());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    public void AddDirectionalFilingSignals_NegativeMaxConsecutiveRateLimited_FailsFast(int breaker)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddDirectionalFilingSignals(
                new DirectionalFilingSignalOptions { MaxConsecutiveRateLimited = breaker }));

        Assert.Contains("Radar:Ai:MaxConsecutiveRateLimited", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddSecEarningsReleaseReader_NegativeMinRequestInterval_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddSecEarningsReleaseReader(
                new SecCollectorOptions { UserAgent = "Radar Research test@example.com" },
                new SecEarningsReleaseReaderOptions { MinRequestInterval = TimeSpan.FromMilliseconds(-1) }));

        Assert.Contains("Radar:Sec:MinRequestIntervalMs", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void AddDirectionalFilingSignals_MinConfidenceOutOfRange_FailsFast(double minConfidence)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddDirectionalFilingSignals(
                new DirectionalFilingSignalOptions { MinConfidence = (decimal)minConfidence }));

        Assert.Contains("Radar:Ai:MinConfidence", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddDirectionalFilingSignals_NonPositiveMaxFilingsPerRun_FailsFast(int maxFilingsPerRun)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddDirectionalFilingSignals(
                new DirectionalFilingSignalOptions { MaxFilingsPerRun = maxFilingsPerRun }));

        Assert.Contains("Radar:Ai:MaxFilingsPerRun", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void AddDirectionalFilingSignals_StrengthOutOfRange_FailsFast(int strength)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddDirectionalFilingSignals(
                new DirectionalFilingSignalOptions { Strength = strength }));

        Assert.Contains("Radar:Ai:Strength", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void AddDirectionalFilingSignals_NoveltyOutOfRange_FailsFast(int novelty)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddDirectionalFilingSignals(
                new DirectionalFilingSignalOptions { Novelty = novelty }));

        Assert.Contains("Radar:Ai:Novelty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDirectionalFilingSignals_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddDirectionalFilingSignals(null!));
    }
}
