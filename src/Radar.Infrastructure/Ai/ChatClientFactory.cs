using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Radar.Application.Ai;

namespace Radar.Infrastructure.Ai;

/// <summary>
/// Config-driven <see cref="IChatClientFactory"/>. Switches on <see cref="AiClientOptions.Provider"/>
/// (case-insensitive, trimmed) — <c>anthropic</c> / <c>ollama</c> / <c>openai</c> — and news up the
/// provider-neutral <see cref="IChatClient"/> from the concrete provider SDK — the <b>only</b> place those SDK
/// types are referenced (AD-5). Constructing a client performs no network I/O, so the factory is deterministic
/// given config.
/// </summary>
internal sealed class ChatClientFactory(AiClientOptions options) : IChatClientFactory
{
    private readonly AiClientOptions _options = options;

    public IChatClient Create()
    {
        // Trim every config string at the point of use so trailing newlines/spaces (common when values come from
        // env vars or copied JSON) don't reach the provider SDK and surface as hard-to-diagnose failures.
        var provider = _options.Provider?.Trim() ?? string.Empty;
        var model = _options.Model?.Trim() ?? string.Empty;

        if (string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return new AnthropicClient { ApiKey = _options.AnthropicApiKey?.Trim() ?? string.Empty }
                .AsIChatClient(model)
                .AsBuilder()
                .Build();
        }

        if (string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            return new OllamaApiClient(new Uri((_options.OllamaEndpoint ?? string.Empty).Trim()), model);
        }

        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = (_options.OpenAiBaseUrl ?? string.Empty).Trim();
            var apiKey = (_options.OpenAiApiKey ?? string.Empty).Trim();
            // openai-compatible (DeepInfra/Groq/Together): OpenAI SDK ChatClient with the endpoint overridden to the
            // host's base URL. The SDK carries response_format json-schema natively, so typed GetResponseAsync<T>
            // structured output works without an extra wrapper (unlike Ollama). AD-5: OpenAI SDK types stay in this file.
            //
            // Structured output (spec acceptance): because the OpenAI SDK emits response_format json-schema, the
            // typed ChatFilingAnalyzer.GetResponseAsync<FilingSentiment> round-trip works over DeepInfra with
            // structured-output-capable models (DeepSeek / GLM / Qwen). That is verified as a gated LIVE smoke check
            // (a real endpoint + DEEPINFRA_API_KEY), not a unit test — construction here does no network I/O.
            var clientOptions = new OpenAI.OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
            var chatClient = new OpenAI.Chat.ChatClient(model, new System.ClientModel.ApiKeyCredential(apiKey), clientOptions);
            return chatClient.AsIChatClient().AsBuilder().Build();
        }

        throw new InvalidOperationException(
            $"Unknown AI provider '{_options.Provider}'; Radar:Ai:Provider must be \"anthropic\", \"ollama\", or \"openai\".");
    }
}
