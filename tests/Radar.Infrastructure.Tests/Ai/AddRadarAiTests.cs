using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Radar.Application.Ai;
using Radar.Infrastructure.Ai;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.Infrastructure.Tests.Ai;

public sealed class AddRadarAiTests
{
    [Fact]
    public void AddRadarAi_Ollama_ResolvesFactoryAndClient()
    {
        using var provider = new ServiceCollection()
            .AddRadarAi(new AiClientOptions
            {
                Provider = "ollama",
                Model = "llama3.1",
                OllamaEndpoint = "http://localhost:11434",
            })
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<IChatClientFactory>());
        Assert.NotNull(provider.GetService<IChatClient>());
    }

    [Fact]
    public void AddRadarAi_Anthropic_ResolvesFactoryAndClient_NoNetwork()
    {
        using var provider = new ServiceCollection()
            .AddRadarAi(new AiClientOptions
            {
                Provider = "anthropic",
                Model = "claude-opus-4-8",
                AnthropicApiKey = "test-key",
            })
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<IChatClientFactory>());
        Assert.NotNull(provider.GetService<IChatClient>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    public void AddRadarAi_BlankOrUnknownProvider_FailsFast(string providerValue)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAi(new AiClientOptions
            {
                Provider = providerValue,
                Model = "whatever",
            }));

        Assert.Contains("Radar:Ai:Provider", ex.Message, StringComparison.Ordinal);
        Assert.Contains("anthropic", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ollama", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRadarAi_BlankModel_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAi(new AiClientOptions
            {
                Provider = "ollama",
                Model = "",
            }));

        Assert.Contains("Radar:Ai:Model", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRadarAi_AnthropicBlankApiKey_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAi(new AiClientOptions
            {
                Provider = "anthropic",
                Model = "claude-opus-4-8",
                AnthropicApiKey = "",
            }));

        Assert.Contains("Radar:Ai:Anthropic:ApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRadarAi_OllamaBlankEndpoint_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAi(new AiClientOptions
            {
                Provider = "ollama",
                Model = "llama3.1",
                OllamaEndpoint = "",
            }));

        Assert.Contains("Radar:Ai:Ollama:Endpoint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRadarAi_OllamaNonAbsoluteUriEndpoint_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAi(new AiClientOptions
            {
                Provider = "ollama",
                Model = "llama3.1",
                OllamaEndpoint = "not a url",
            }));

        Assert.Contains("Radar:Ai:Ollama:Endpoint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRadarAi_OllamaWhitespacePaddedEndpoint_DoesNotFailValidation()
    {
        // A valid endpoint with surrounding whitespace must be trimmed, not rejected by the absolute-URI check.
        using var provider = new ServiceCollection()
            .AddRadarAi(new AiClientOptions
            {
                Provider = "  ollama  ",
                Model = "  llama3.1  ",
                OllamaEndpoint = "  http://localhost:11434  ",
            })
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<IChatClient>());

        // The registered options are the normalized (trimmed) copy so downstream consumers see clean values.
        var options = provider.GetRequiredService<AiClientOptions>();
        Assert.Equal("ollama", options.Provider);
        Assert.Equal("llama3.1", options.Model);
        Assert.Equal("http://localhost:11434", options.OllamaEndpoint);
    }

    [Fact]
    public void AddRadarAi_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddRadarAi(null!));
    }
}
