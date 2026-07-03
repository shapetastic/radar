using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Radar.Application.Filings;
using Radar.Infrastructure.Ai;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

public sealed class AddRadarFilingAnalyzerTests
{
    [Fact]
    public void AddRadarFilingAnalyzer_ComposesAgainstSeamClient_ResolvesAnalyzer()
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
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<IFilingAnalyzer>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddRadarFilingAnalyzer_NonPositiveMaxInputLength_FailsFast(int maxInputLength)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarFilingAnalyzer(
                new FilingAnalyzerOptions { MaxInputLength = maxInputLength }));

        Assert.Contains("Radar:Ai:MaxInputLength", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRadarFilingAnalyzer_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddRadarFilingAnalyzer(null!));
    }
}
