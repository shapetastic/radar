using Microsoft.Extensions.AI;
using OllamaSharp;
using Radar.Infrastructure.Ai;

namespace Radar.Infrastructure.Tests.Ai;

public sealed class ChatClientFactoryTests
{
    [Fact]
    public void Create_Ollama_ReturnsOllamaApiClient()
    {
        var factory = new ChatClientFactory(new AiClientOptions
        {
            Provider = "ollama",
            Model = "llama3.1",
            OllamaEndpoint = "http://localhost:11434",
        });

        var client = factory.Create();

        // OllamaApiClient implements IChatClient directly, so asserting the concrete type is fine (no network I/O).
        var ollama = Assert.IsType<OllamaApiClient>(client);
        Assert.IsAssignableFrom<IChatClient>(ollama);
    }

    [Fact]
    public void Create_Anthropic_ReturnsIChatClient()
    {
        var factory = new ChatClientFactory(new AiClientOptions
        {
            Provider = "anthropic",
            Model = "claude-opus-4-8",
            AnthropicApiKey = "test-key",
        });

        var client = factory.Create();

        // The Anthropic adapter wraps the client in an internal type — assert only the abstraction, no network.
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Theory]
    [InlineData("Ollama")]
    [InlineData("OLLAMA")]
    public void Create_Ollama_IsCaseInsensitive(string provider)
    {
        var factory = new ChatClientFactory(new AiClientOptions
        {
            Provider = provider,
            Model = "llama3.1",
            OllamaEndpoint = "http://localhost:11434",
        });

        Assert.IsType<OllamaApiClient>(factory.Create());
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("ANTHROPIC")]
    public void Create_Anthropic_IsCaseInsensitive(string provider)
    {
        var factory = new ChatClientFactory(new AiClientOptions
        {
            Provider = provider,
            Model = "claude-opus-4-8",
            AnthropicApiKey = "test-key",
        });

        Assert.IsAssignableFrom<IChatClient>(factory.Create());
    }

    [Fact]
    public void Create_Ollama_TrimsWhitespaceInConfig()
    {
        // Trailing/leading whitespace (e.g. copied from env vars) must not defeat the endpoint URI parse.
        var factory = new ChatClientFactory(new AiClientOptions
        {
            Provider = "  ollama  ",
            Model = "  llama3.1  ",
            OllamaEndpoint = "  http://localhost:11434  ",
        });

        Assert.IsType<OllamaApiClient>(factory.Create());
    }

    [Fact]
    public void Create_Anthropic_TrimsWhitespaceInConfig()
    {
        var factory = new ChatClientFactory(new AiClientOptions
        {
            Provider = "  anthropic  ",
            Model = "  claude-opus-4-8  ",
            AnthropicApiKey = "  test-key  ",
        });

        Assert.IsAssignableFrom<IChatClient>(factory.Create());
    }

    [Fact]
    public void Create_OpenAi_ReturnsIChatClient()
    {
        var factory = new ChatClientFactory(new AiClientOptions
        {
            Provider = "openai",
            Model = "deepseek-ai/DeepSeek-V3",
            OpenAiBaseUrl = "https://api.deepinfra.com/v1/openai",
            OpenAiApiKey = "test-key",
        });

        var client = factory.Create();

        // The OpenAI adapter wraps the client in an internal type — assert only the abstraction, no network I/O.
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("OPENAI")]
    public void Create_OpenAi_IsCaseInsensitive(string provider)
    {
        var factory = new ChatClientFactory(new AiClientOptions
        {
            Provider = provider,
            Model = "deepseek-ai/DeepSeek-V3",
            OpenAiBaseUrl = "https://api.deepinfra.com/v1/openai",
            OpenAiApiKey = "test-key",
        });

        Assert.IsAssignableFrom<IChatClient>(factory.Create());
    }

    [Fact]
    public void Create_OpenAi_TrimsWhitespaceInConfig()
    {
        // Trailing/leading whitespace (e.g. copied from env vars) must not defeat the base-URL URI parse.
        var factory = new ChatClientFactory(new AiClientOptions
        {
            Provider = "  openai  ",
            Model = "  deepseek-ai/DeepSeek-V3  ",
            OpenAiBaseUrl = "  https://api.deepinfra.com/v1/openai  ",
            OpenAiApiKey = "  test-key  ",
        });

        Assert.IsAssignableFrom<IChatClient>(factory.Create());
    }

    [Fact]
    public void Create_UnknownProvider_Throws()
    {
        var factory = new ChatClientFactory(new AiClientOptions
        {
            Provider = "bogus",
            Model = "whatever",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create());
        Assert.Contains("anthropic", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ollama", ex.Message, StringComparison.Ordinal);
        Assert.Contains("openai", ex.Message, StringComparison.Ordinal);
    }
}
