namespace Radar.Infrastructure.Ai;

/// <summary>
/// Options for the AI chat-client seam (<c>Microsoft.Extensions.AI</c> <c>IChatClient</c>). <see cref="Provider"/>
/// selects the provider (<c>"anthropic"</c>, <c>"ollama"</c>, or <c>"openai"</c>, compared case-insensitively) and
/// <see cref="Model"/> is the model id; each is <b>required</b> and validated fail-fast at registration.
/// <see cref="AnthropicApiKey"/> is required only for the hosted Anthropic provider; <see cref="OllamaEndpoint"/>
/// (default <c>http://localhost:11434</c>) is used only for the local, keyless Ollama provider;
/// <see cref="OpenAiBaseUrl"/> + <see cref="OpenAiApiKey"/> are required only for the OpenAI-compatible
/// (<c>"openai"</c>, e.g. DeepInfra) provider. The nesting is kept flat here (the Worker-side
/// <c>AiWorkerOptions</c> carries the tidy nested <c>Anthropic</c>/<c>Ollama</c>/<c>OpenAi</c> config blocks and
/// flattens into this — env-var resolution of the OpenAI key happens in the Worker, so this record carries the
/// already-resolved key value).
/// </summary>
public sealed class AiClientOptions
{
    /// <summary>The AI provider: <c>"anthropic"</c> (hosted Claude) or <c>"ollama"</c> (local, keyless). Compared case-insensitively.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>The model id (e.g. <c>claude-opus-4-8</c>, or an Ollama tag like <c>llama3.1</c>). Required.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The Anthropic API key. Required only when <see cref="Provider"/> is <c>"anthropic"</c>.</summary>
    public string AnthropicApiKey { get; init; } = string.Empty;

    /// <summary>The Ollama base URL. Used only when <see cref="Provider"/> is <c>"ollama"</c>. Defaults to <c>http://localhost:11434</c>.</summary>
    public string OllamaEndpoint { get; init; } = "http://localhost:11434";

    /// <summary>
    /// The OpenAI-compatible endpoint base URL (e.g. DeepInfra <c>https://api.deepinfra.com/v1/openai</c>).
    /// Required when <see cref="Provider"/> is <c>"openai"</c>; there is no sensible default (a blank BaseUrl is a
    /// config error — it has no host to address).
    /// </summary>
    public string OpenAiBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// The resolved OpenAI-compatible API key VALUE. Required when <see cref="Provider"/> is <c>"openai"</c>. This
    /// is the already-resolved value of the environment variable named in the Worker config
    /// (<c>Radar:Ai:OpenAi:ApiKeyEnvVar</c>); the key is <b>never</b> logged as a value — only the env-var name may
    /// appear in messages/logs.
    /// </summary>
    public string OpenAiApiKey { get; init; } = string.Empty;
}
