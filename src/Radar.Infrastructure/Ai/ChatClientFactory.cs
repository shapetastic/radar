using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Radar.Application.Ai;

namespace Radar.Infrastructure.Ai;

/// <summary>
/// Config-driven <see cref="IChatClientFactory"/>. Switches on <see cref="AiClientOptions.Provider"/>
/// (case-insensitive, trimmed) and news up the provider-neutral <see cref="IChatClient"/> from the concrete
/// provider SDK — the <b>only</b> place those SDK types are referenced (AD-5). Constructing a client performs
/// no network I/O, so the factory is deterministic given config.
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

        throw new InvalidOperationException(
            $"Unknown AI provider '{_options.Provider}'; Radar:Ai:Provider must be \"anthropic\" or \"ollama\".");
    }
}
