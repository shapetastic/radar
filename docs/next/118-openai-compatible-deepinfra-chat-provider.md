# Task: Add an OpenAI-compatible (DeepInfra) IChatClient provider — enabler for a stronger earnings-read model

> **AI PROVIDER ENABLER.** Motivated by the 2026-07-20 finding that llama3.1 (8B, local) reads a spun −70% gross
> margin as "Improving" (spec 116 could not fix it via prompt — a model-capability ceiling). This slice lets Radar
> point the filing read at a frontier model on the maintainer's DeepInfra account. **Default stays Ollama** — this
> is an opt-in enabler, so the shipped pipeline is unchanged until a follow-up A/B validates a switch.

## Overview

Radar's AI is already provider-neutral behind `Microsoft.Extensions.AI` `IChatClient`, constructed in the single
Infrastructure factory `src/Radar.Infrastructure/Ai/ChatClientFactory.cs` (AD-5 — the only place provider SDKs are
referenced). It currently switches on `anthropic` / `ollama`. This adds an **`openai`-type** (OpenAI-compatible)
provider so any OpenAI-compatible host — **DeepInfra**, Groq, Together — can back the read.

A proven implementation of exactly this pattern exists in the sibling project
`C:\Users\scm9d\source\repos\claude-pipeline\CoachingPipeline\CoachingPipeline.Services\Llm\ChatClientFactory.cs`
(`BuildOpenAi`) and `…\Embedding\DeepInfraAccountClient.cs`. Mirror it:

```csharp
// openai-compatible (DeepInfra): OpenAI SDK ChatClient with the endpoint overridden to the host's base URL.
var options  = new OpenAI.OpenAIClientOptions { Endpoint = new Uri(baseUrl) };          // e.g. https://api.deepinfra.com/v1/openai
var chatClient = new OpenAI.Chat.ChatClient(model, new System.ClientModel.ApiKeyCredential(apiKey), options);
return chatClient.AsIChatClient().AsBuilder().Build();
```

**Structured output is the key de-risk.** `ChatFilingAnalyzer` calls typed `GetResponseAsync<FilingSentiment>`
(json-schema structured output). The reference project needed a custom `OllamaSchemaAwareChatClient` wrapper for
Ollama but **nothing extra for the OpenAI-compatible path** — the OpenAI SDK carries `response_format` json-schema
natively, so `GetResponseAsync<FilingSentiment>` works over DeepInfra with the models that support structured
output (DeepSeek / GLM / Qwen do). Verify this in an integration check.

## Assignment

Worktree: any
Dependencies: current main (post 113–117). Independent of scoring; no fingerprint impact.
Estimated time: ~1–2 hours

## Project structure changes

Add:
- The **OpenAI SDK** package(s) to `Radar.Infrastructure` — `OpenAI` + `Microsoft.Extensions.AI.OpenAI`
  (for `AsIChatClient()`). Infrastructure only (AD-5).

Modify:
- `src/Radar.Infrastructure/Ai/ChatClientFactory.cs` — add an `"openai"` branch building the OpenAI-compatible
  client per the snippet above. Trim config strings at point of use (matches the existing branches).
- `src/Radar.Infrastructure/Ai/AiClientOptions.cs` — add flat fields for the openai provider:
  `OpenAiBaseUrl` (required for `openai`; no sensible default — a blank BaseUrl is a config error, name the fix)
  and the **API key sourced from an env var** (`OpenAiApiKeyEnvVar`, resolved via
  `Environment.GetEnvironmentVariable` — mirror `DeepInfraAccountClient`/`ResolveApiKey`; the key is **never
  committed and never logged as a value**, only the env-var name).
- The Worker AI options (`AiWorkerOptions` / the nested `Radar:Ai` config) — add a nested `OpenAi` block
  (`BaseUrl`, `Model`, `ApiKeyEnvVar`) that flattens into `AiClientOptions`, consistent with the existing
  `Anthropic`/`Ollama` nesting.
- DI validation (`AddRadar…`/the AI registration) — fail-fast when `Provider=openai` but `OpenAiBaseUrl` is blank
  or the env-var key is unset (name the env var in the message, never the value).
- The analyzed-filing cache key/version (spec 114 `CacheVersion`) — **fold the model id (and provider) into the
  cache identity**, so switching the earnings-read model re-analyzes filings instead of replaying the old model's
  reads. (Without this, a switch to DeepInfra would serve stale llama3.1 verdicts from cache.)

## Implementation details

- **Default unchanged.** `default.json` / the baseline profile keep `Provider=ollama`. This spec only makes
  `openai` *available*; the actual switch is a later config change gated on the A/B result. Byte-for-byte
  unchanged behaviour when the provider is not `openai`.
- **No scoring/fingerprint impact.** The provider/model are not scoring-fingerprint inputs (the directional-filing
  fingerprint hashes only Strength/Novelty/MinConfidence — spec 106). Confirm the default fingerprint is unchanged.
- **Secret hygiene.** API key only from the env var (e.g. `DEEPINFRA_API_KEY`); never in the repo, never in a
  committed profile, never logged as a value. Follows the established SEC-User-Agent precedent.
- **AD-5 preserved.** The OpenAI SDK types appear only inside `ChatClientFactory`; nothing outside Infrastructure
  references them. The rest of the pipeline sees only `IChatClient`.
- **Structured-output verification.** Add an integration-style test (or a documented live smoke check) that a
  `GetResponseAsync<FilingSentiment>` round-trip against an openai-compatible endpoint yields a valid typed result
  (can use a stub OpenAI-compatible server or be a gated live test, per how the repo handles external-dep tests).

## Tests

- `ChatClientFactory` builds an `IChatClient` for `Provider=openai` given a BaseUrl + a resolvable env key;
  fails fast with a clear message when BaseUrl is blank or the key env var is unset (name the var, not the value).
- `anthropic` and `ollama` branches unchanged (regression).
- Cache identity includes the model/provider: switching the model is a cache miss (re-analyze), not a replay.
- No default `ScoringConfigVersion` change (confirm pins unmoved).

## Constraints

- Target .NET 10 / `net10.0`, C# 14. Provider SDK confined to Infrastructure (AD-5).
- Default provider stays `ollama`; this is an opt-in enabler (default byte-identical).
- API key via env var only; never committed/logged as a value.
- No scoring/formula/fingerprint version change.

## Acceptance criteria

- [ ] `Provider=openai` with `OpenAiBaseUrl` (e.g. DeepInfra `https://api.deepinfra.com/v1/openai`) + an env-var
      key builds a working `IChatClient`; `ChatFilingAnalyzer.GetResponseAsync<FilingSentiment>` returns a valid
      typed verdict against it (structured output confirmed).
- [ ] Fail-fast validation for blank BaseUrl / missing key (env-var name surfaced, value never).
- [ ] Analyzed-filing cache identity includes model/provider, so a model switch re-reads rather than replays.
- [ ] Default profile still `ollama`; behaviour byte-identical when provider ≠ openai; no fingerprint change.
- [ ] AD-5 intact (OpenAI SDK only inside `ChatClientFactory`).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
