# Task: AI chat-client seam — a config-driven `IChatClient` factory (Radar's first AI capability)

## Overview

Radar has, so far, been deliberately AI-free: four real collectors (RSS, SEC, USASpending, GDELT) feed a
deterministic keyword extractor, a versioned scoring formula, and a markdown report. This slice adds Radar's
**first AI capability** — but **no AI *behaviour* yet**. It installs the **seam**: a config-driven factory that
yields a provider-neutral `Microsoft.Extensions.AI.IChatClient`, with **Anthropic** (hosted Claude) and
**Ollama** (local, keyless) providers to start.

This is the **FOUNDATION slice of a multi-spec arc** toward the maintainer's chosen next feature — directional
filing signals (beat/miss extraction from 8-K exhibits):

1. **THIS spec (72)** — the `IChatClient` seam + config + DI + fail-fast. No consumer.
2. (future) 8-K / exhibit body fetch (the SEC collector today reads filing *metadata*, not exhibit text).
3. (future) `IFilingAnalyzer` — a typed, validated structured-output analyzer over an exhibit body.
4. (future) a directional **beat/miss** signal wired into extraction/scoring.

**Filing-specific logic, prompts, exhibit fetching, and any `IChatClient` *consumer* are explicitly OUT of
scope here.** This slice is proven by: *Radar can obtain a config-selected `IChatClient`; provider SDKs stay
in `Radar.Infrastructure`; the factory fails fast on bad config; and existing runs are byte-for-byte
unchanged.* Everything below is testable **offline** — no network, no API key, no running Ollama.

Why a seam first: `Microsoft.Extensions.AI`'s `IChatClient` is the universal abstraction every future AI
consumer codes against. Introducing it standalone lets the arc's later slices depend on a stable, tested,
provider-neutral interface instead of re-litigating provider wiring inside a feature.

---

## Assignment

Worktree: any (self-contained: new Infrastructure files + a new Application interface + a new option class +
additive DI + new package refs — the same shape as the collector slices).
Dependencies: None — foundational. (It does not depend on any collector; it adds a parallel seam.)
Conflicts with: None on logic. It touches the same *shared wiring files* the collector slices touch —
`InfrastructureServiceCollectionExtensions.cs`, `RadarWorkerOptions.cs`, `RadarWorkerServices.cs`,
`appsettings.json`, and both `.csproj` files (new package refs) — so **do not run it in parallel with another
slice that edits those files**; sequence it instead. It does NOT touch collectors, the extractor, scoring, or
the report.
Estimated time: ~2 h

---

## Verified integration facts (do NOT re-research; use these)

These were verified live against the official docs/SDKs on 2026-07-03. The headless implementer **cannot reach
the network**, so treat every value here as authoritative and do NOT try to re-verify or "improve" the API
shapes. (Mirrors the Verified-facts blocks in specs 62/67.)

- **Abstraction.** `Microsoft.Extensions.AI.IChatClient` is the provider-neutral interface every AI consumer
  codes against. The interface itself lives in NuGet **`Microsoft.Extensions.AI.Abstractions`**. The typed
  structured-output extension `GetResponseAsync<T>(...)` (used by LATER specs in the arc, **not this one**)
  lives in NuGet **`Microsoft.Extensions.AI`**. Per **AD-5**, `Radar.Application` MAY reference the
  `Microsoft.Extensions.AI` abstraction family — so the **factory interface + `IChatClient` may live in
  Application**; the **concrete provider SDKs must stay in `Radar.Infrastructure` only**.
- **Anthropic provider.** NuGet **`Anthropic`** — the **OFFICIAL** SDK, version **10+**. (Do NOT use
  `tryAGI.Anthropic`; that is the old community package.) Usage:
  ```csharp
  using Anthropic;
  using Microsoft.Extensions.AI;

  IChatClient client = new AnthropicClient { ApiKey = key }
      .AsIChatClient(modelId)          // adapter: AnthropicClient -> IChatClient
      .AsBuilder()                     // .AsBuilder().Build() is optional plumbing;
      .Build();                        // (.UseFunctionInvocation() etc. are NOT needed here)
  ```
  - API key via the `ApiKey` property (or the `ANTHROPIC_API_KEY` env var). `BaseUrl` is overridable (default
    `https://api.anthropic.com`) — you do NOT need to set it for this slice.
  - Default model id: **`claude-opus-4-8`**.
  - `.AsIChatClient(modelId)` is the adapter that produces the `IChatClient`. Constructing it does **not** make
    a network call, so the factory can build it with a dummy key and the test asserts only the returned type.
- **Ollama provider.** NuGet **`OllamaSharp`**. Usage:
  ```csharp
  using OllamaSharp;
  using Microsoft.Extensions.AI;

  IChatClient client = new OllamaApiClient(new Uri(endpoint), model);  // implements IChatClient DIRECTLY
  ```
  - `OllamaApiClient` implements `IChatClient` directly — **no adapter call needed**.
  - Default endpoint: **`http://localhost:11434`**. **No key required** (local/keyless — the whole AI pipeline
    can be built and demoed with no API key or cost).
  - Constructing `OllamaApiClient` does **not** connect, so the factory can build it with no Ollama running and
    the test asserts only the returned type/endpoint.
- **Target framework.** `net10.0` (CLAUDE.md). Both provider packages and the `Microsoft.Extensions.AI*`
  packages support `net10.0` (Anthropic targets `netstandard2.0+`). Adding the NuGet packages must **restore
  cleanly** on `net10.0`; if a specific version fails to restore, pin to the nearest version that does — the
  exact pinned versions are an implementation detail (see Open questions), the seam shape is not.

---

## Project structure changes

```text
src/Radar.Application/Ai/
  IChatClientFactory.cs        # NEW: application-facing seam. IChatClient Create(); returns a provider-neutral client.

src/Radar.Application/Radar.Application.csproj
  # MODIFIED: add PackageReference Microsoft.Extensions.AI.Abstractions (AD-5 permits the abstraction family).

src/Radar.Infrastructure/Ai/
  AiClientOptions.cs           # NEW: Provider, Model, AnthropicApiKey, OllamaEndpoint (the Infrastructure option
                               #      record, mirroring SecCollectorOptions / UsaSpendingCollectorOptions).
  ChatClientFactory.cs         # NEW: IChatClientFactory impl; switches on Provider; news up the provider SDK.

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs
  # MODIFIED: add AddRadarAi(AiClientOptions options) — additive, opt-in registration + fail-fast validation.

src/Radar.Infrastructure/Radar.Infrastructure.csproj
  # MODIFIED: add PackageReferences Microsoft.Extensions.AI, Anthropic, OllamaSharp (concrete SDKs — Infra only).

src/Radar.Worker/
  RadarWorkerOptions.cs        # MODIFIED: add Ai (AiWorkerOptions) with nested Anthropic/Ollama blocks.
  RadarWorkerServices.cs       # MODIFIED: wire AddRadarAi ONLY when AI is configured (blank Provider = disabled).
  appsettings.json             # MODIFIED: add a documented, DISABLED-by-default Radar:Ai section.

tests/Radar.Infrastructure.Tests/Ai/
  ChatClientFactoryTests.cs    # NEW: offline — provider switch returns the right IChatClient; fail-fast cases.
  AddRadarAiTests.cs           # NEW: offline — DI composition + AI-disabled registers nothing new.
```

No new project is created. `Radar.Infrastructure.Tests` already has `InternalsVisibleTo`, so the factory and
options may be `internal` (mirroring the collector readers) — see below.

---

## Implementation details

### Application seam — `IChatClientFactory` (Radar.Application/Ai)

- A minimal interface: `IChatClient Create();` (single method; provider is fixed at startup by config, so no
  parameters are needed). Add a short XML doc: "Yields the config-selected, provider-neutral `IChatClient`.
  Concrete provider SDKs live in `Radar.Infrastructure` (AD-5); no consumer exists yet."
- **Public** (it is the seam future Application consumers depend on). It references only
  `Microsoft.Extensions.AI.IChatClient` — permitted in Application by AD-5.

### Options — `AiClientOptions` (Radar.Infrastructure/Ai)

- A sealed record/class mirroring `SecCollectorOptions` / `UsaSpendingCollectorOptions` (which live in
  Infrastructure and are passed into their `AddXxx`):
  - `string Provider` — `"anthropic"` or `"ollama"` (compared **case-insensitively**).
  - `string Model` — the model id (e.g. `claude-opus-4-8`, or an Ollama tag like `llama3.1`).
  - `string AnthropicApiKey` — the Anthropic key (only required when `Provider == "anthropic"`).
  - `string OllamaEndpoint` — the Ollama base URL (default `http://localhost:11434`; only used when
    `Provider == "ollama"`).
- Keep the nesting *shallow* in the Infrastructure options (flat `AnthropicApiKey` / `OllamaEndpoint`); the
  Worker-side `AiWorkerOptions` carries the tidy nested `Anthropic {}` / `Ollama {}` config blocks and flattens
  into this when building it (exactly how `RadarWorkerServices` builds `SecCollectorOptions` from
  `options.Sec`).

### Factory — `ChatClientFactory` (Radar.Infrastructure/Ai)

- `internal sealed class ChatClientFactory : IChatClientFactory`, constructed with the `AiClientOptions`.
- `Create()` switches on `Provider` (case-insensitive, trimmed):
  - `"anthropic"` → `new AnthropicClient { ApiKey = options.AnthropicApiKey }.AsIChatClient(options.Model).AsBuilder().Build();`
    (the `.AsBuilder().Build()` is fine to include or omit; `.AsIChatClient(model)` is the load-bearing adapter).
  - `"ollama"` → `new OllamaApiClient(new Uri(options.OllamaEndpoint), options.Model);`
  - default → `throw new InvalidOperationException(...)` with the documented unknown-provider message
    (defense-in-depth; `AddRadarAi` also validates up front — see below).
- The factory is **deterministic given config** and performs **no network I/O** at construction/`Create` time.
  All provider SDK types (`AnthropicClient`, `OllamaApiClient`) are referenced **only** inside this
  Infrastructure class (AD-5 — the entire point of the slice).

### DI — `AddRadarAi(AiClientOptions options)` (opt-in, fail-fast — mirror `AddSecEdgarCollector`)

- Additive registration mirroring `AddSecEdgarCollector` / `AddUsaSpendingContractCollector` exactly:
  ```csharp
  public static IServiceCollection AddRadarAi(this IServiceCollection services, AiClientOptions options)
  ```
- `ArgumentNullException.ThrowIfNull(options);` then **fail fast** (each an `InvalidOperationException` with a
  clear, advice-free, `Radar:Ai:*`-referencing message, in the exact style of the SEC/USASpending/GDELT
  validators) on:
  1. **blank/unknown `Provider`** — not `anthropic` and not `ollama` (case-insensitive). Message names the two
     valid providers and the `Radar:Ai:Provider` key.
  2. **blank `Model`** — message names `Radar:Ai:Model`.
  3. **`anthropic` with a blank `AnthropicApiKey`** — message names `Radar:Ai:Anthropic:ApiKey` and notes it is
     required for the hosted provider (analogous to SEC's blank-UserAgent 403 guard).
  4. **`ollama` with a blank OR non-absolute-URI `OllamaEndpoint`** — validate with
     `Uri.TryCreate(options.OllamaEndpoint, UriKind.Absolute, out _)`; message names `Radar:Ai:Ollama:Endpoint`
     and the default `http://localhost:11434`.
  Validate provider FIRST (so a blank provider yields the provider message, not a spurious key/endpoint
  message), then model, then the provider-specific field.
- On success: `services.AddSingleton(options);` and register the factory as a **singleton**:
  `services.AddSingleton<IChatClientFactory, ChatClientFactory>();`.
- **Register a single process `IChatClient`** produced by the factory, so future consumers can inject either
  `IChatClientFactory` or `IChatClient` directly (provider is fixed at startup, so one client for the process
  is correct — this honours the maintainer's explicit "factory" intent while giving consumers the plain
  abstraction):
  ```csharp
  services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<IChatClientFactory>().Create());
  ```
- **No `AddHttpClient`.** Both provider SDKs manage their own HTTP transport, so — unlike the collector
  registrations — plain `AddSingleton` is correct here (do not wire a named `HttpClient`).

### Worker wiring — opt-in, disabled by default (existing runs byte-for-byte unchanged)

- `RadarWorkerOptions` gains `public AiWorkerOptions Ai { get; init; } = new();` with a new
  `AiWorkerOptions` mirroring the SEC/UsaSpending/Gdelt `*WorkerOptions` classes:
  - `string Provider { get; init; } = string.Empty;` — **blank by default = AI DISABLED**.
  - `string Model { get; init; } = string.Empty;`
  - `AiAnthropicWorkerOptions Anthropic { get; init; } = new();` → `{ string ApiKey = string.Empty; }`
  - `AiOllamaWorkerOptions Ollama { get; init; } = new();` → `{ string Endpoint = "http://localhost:11434"; }`
  Defaulting to blank Provider means the default configuration surfaces **no** AI and needs no provider
  packages at runtime.
- `RadarWorkerServices.AddRadarWorker` wires AI **only when `options.Ai.Provider` is non-blank** (this is the
  opt-in gate — AI has no entry in the `Collectors` list, so it is gated on Provider presence, analogous to how
  a blank SEC UserAgent means "don't enable"). When `Provider` is non-blank, call:
  ```csharp
  services.AddRadarAi(new AiClientOptions
  {
      Provider = options.Ai.Provider,
      Model = options.Ai.Model,
      AnthropicApiKey = options.Ai.Anthropic.ApiKey,
      OllamaEndpoint = options.Ai.Ollama.Endpoint,
  });
  ```
  Place this call alongside the other `AddRadar*` registrations. When `Provider` is blank, do **nothing** — the
  graph is identical to today (no `IChatClientFactory`/`IChatClient` registered, no provider packages loaded).
  Do NOT add an entry to the `Collectors` switch or change the valid-kinds messages — AI is not a collector.
- `appsettings.json` gains a documented, **DISABLED** `Radar:Ai` section: a `_comment` explaining it is off by
  default (blank `Provider`), that `ollama` is local/keyless and `anthropic` is hosted, and showing the shape,
  with `Provider` and `Model` **empty** so nothing changes for existing runs:
  ```jsonc
  "Ai": {
    "_comment": "AI chat-client seam (Microsoft.Extensions.AI IChatClient). DISABLED by default: a blank Provider means no AI is wired and no provider packages load. Set Provider to 'ollama' (local, keyless — set Model to an installed tag e.g. 'llama3.1', Endpoint defaults to http://localhost:11434) or 'anthropic' (hosted — set Model e.g. 'claude-opus-4-8' and Anthropic:ApiKey). No consumer exists yet; this only proves a config-selected IChatClient can be obtained.",
    "Provider": "",
    "Model": "",
    "Anthropic": { "ApiKey": "" },
    "Ollama": { "Endpoint": "http://localhost:11434" }
  }
  ```

### Packages

- `Radar.Application.csproj`: add `Microsoft.Extensions.AI.Abstractions` (the abstraction that carries
  `IChatClient`). Align the version with the `Microsoft.Extensions.*` family already used
  (`Microsoft.Extensions.Logging.Abstractions 10.0.8`) if a matching version exists; otherwise pin to the
  nearest version that restores on `net10.0`.
- `Radar.Infrastructure.csproj`: add `Microsoft.Extensions.AI` (for the builder/extensions the adapters use),
  `Anthropic` (official, v10+), and `OllamaSharp`. **Concrete SDKs Infrastructure-only** (AD-5). Pin whatever
  versions restore cleanly on `net10.0` (see Open questions).

### Architecture-decision note (for the implementing PR — the planner does not edit ADs)

This is a **foundational architectural addition** (a config-driven `IChatClient` factory + the "provider SDKs
in Infrastructure only" rule for AI, materialising AD-5's `Microsoft.Extensions.AI` clause into a concrete
seam). The implementing PR **should propose a new AD entry** in `docs/architecture-decisions.md` recording:
(a) `IChatClient` (`Microsoft.Extensions.AI`) is Radar's single AI abstraction; (b) the config-driven
provider selection (`Radar:Ai:Provider`) with Anthropic + Ollama as the initial providers; (c) provider SDKs
(`Anthropic`, `OllamaSharp`) are confined to `Radar.Infrastructure`; (d) AI is opt-in (blank `Provider` =
disabled). The planner does not write ADs — flag it for the PR author to draft and the maintainer to approve.

---

## Tests

All tests are **offline** — no live provider, no network, no key, no running Ollama. Assert construction and
type, never a chat round-trip.

### `ChatClientFactoryTests` (Radar.Infrastructure.Tests/Ai)

- `Provider = "ollama"` (+ a `Model` and the default endpoint) → `Create()` returns a non-null `IChatClient`
  that **is an `OllamaApiClient`** pointed at the configured endpoint/model. Assert `is IChatClient` at minimum;
  asserting the concrete `OllamaApiClient` type is fine because it implements `IChatClient` directly (a direct
  `Assert.IsType<OllamaApiClient>` is acceptable).
- `Provider = "anthropic"` (+ a `Model` and a **dummy** key like `"test-key"`) → `Create()` returns a non-null
  **`IChatClient`**. The Anthropic adapter wraps the client in an internal type, so assert `is IChatClient`
  (the minimal assertion) — do **not** reflect for the concrete wrapper and do **not** make a network call.
- Provider case-insensitivity: `"Ollama"` / `"ANTHROPIC"` resolve the same as lowercase.
- `Create()` with an **unknown provider** throws `InvalidOperationException` with the documented message
  (defense-in-depth; independent of the DI validation).

### `AddRadarAiTests` (Radar.Infrastructure.Tests/Ai)

- **Composition (ollama):** `new ServiceCollection().AddRadarAi(ollamaOptions)` then `BuildServiceProvider()`
  resolves a non-null `IChatClientFactory` **and** a non-null `IChatClient` (proving the factory-produced
  singleton composes).
- **Composition (anthropic):** same with a dummy key — resolves `IChatClientFactory` + `IChatClient`, no
  network call.
- **Fail-fast, each throwing `InvalidOperationException` with the documented message:**
  - unknown/blank `Provider`;
  - blank `Model`;
  - `anthropic` with a blank `AnthropicApiKey`;
  - `ollama` with a blank endpoint **and** `ollama` with a non-absolute-URI endpoint (e.g. `"not a url"`).
- **`AddRadarAi(null)` throws `ArgumentNullException`** (mirrors the collector guards).

### DI graph unchanged when AI disabled

- A test that builds the **Worker** graph (via `RadarWorkerServices.AddRadarWorker` with the default/blank
  `Radar:Ai`) and asserts **no `IChatClientFactory` and no `IChatClient`** are registered — the existing graph
  is unchanged (opt-in). If there is an existing worker-graph composition test, extend it; otherwise add a
  focused test building configuration with a blank `Ai:Provider`. Also assert that a non-blank
  `Ai:Provider = "ollama"` (+ Model) **does** register both — proving the opt-in gate flips correctly.

Existing tests (collectors, runner, DI, scoring, report) stay green; nothing in the default pipeline changes.

---

## Constraints

- Target `net10.0`, C# 14.
- **Provider SDKs (`Anthropic`, `OllamaSharp`) confined to `Radar.Infrastructure`** — AD-5, the whole point.
  `Radar.Application` may reference `Microsoft.Extensions.AI` / `.Abstractions` **only** (the abstraction
  family), never a concrete provider SDK.
- **No consumer** of `IChatClient` in this slice: no analyzer, no prompt, no filing/exhibit logic, no
  `GetResponseAsync<T>` call. Filing-specific logic is a LATER spec in the arc.
- **Opt-in / no drift:** blank `Provider` = disabled; the default configuration and the existing pipeline are
  byte-for-byte unchanged and load no provider packages at runtime.
- **Deterministic** where it matters: the factory is deterministic given config and performs no network I/O.
- **No DB** (AD-8, files-first). No scoring/extractor/report change (AD-10 is not triggered — this slice does
  not affect scoring output).
- Never emit advice language.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green (adding the
  NuGet packages must restore cleanly on `net10.0`).

---

## Acceptance criteria

- [ ] `IChatClientFactory` lives in `Radar.Application/Ai` and exposes `IChatClient Create()`, referencing only
      the `Microsoft.Extensions.AI` abstraction (AD-5).
- [ ] `ChatClientFactory` (Infrastructure) switches on `Provider` (case-insensitive) and returns the correct
      `IChatClient`: `ollama` → `OllamaApiClient(endpoint, model)`; `anthropic` →
      `AnthropicClient{ApiKey}.AsIChatClient(model)`; unknown → `InvalidOperationException`. All provider SDK
      types are referenced only inside Infrastructure.
- [ ] `AddRadarAi(AiClientOptions)` registers `IChatClientFactory` (singleton) and a factory-produced singleton
      `IChatClient`; it uses plain `AddSingleton` (no `AddHttpClient`) and fails fast — with clear,
      `Radar:Ai:*`-referencing messages in the SEC/USASpending style — on blank/unknown `Provider`, blank
      `Model`, `anthropic` with a blank `ApiKey`, and `ollama` with a blank or non-absolute-URI `Endpoint`;
      `AddRadarAi(null)` throws `ArgumentNullException`.
- [ ] `RadarWorkerOptions` surfaces a `Radar:Ai` section via `AiWorkerOptions` (Provider/Model + nested
      `Anthropic:ApiKey` and `Ollama:Endpoint`), blank `Provider` by default; `appsettings.json` documents a
      DISABLED-by-default `Radar:Ai` section.
- [ ] `RadarWorkerServices` wires `AddRadarAi` **only when `Ai:Provider` is non-blank**; with the default
      (blank) config the DI graph registers no `IChatClientFactory`/`IChatClient` and is byte-for-byte
      unchanged; a `Provider = "ollama"` config registers both.
- [ ] `Radar.Application` references `Microsoft.Extensions.AI.Abstractions`; `Radar.Infrastructure` references
      `Microsoft.Extensions.AI`, `Anthropic` (official, v10+), and `OllamaSharp`; all restore on `net10.0`.
- [ ] Offline tests cover: factory returns an `IChatClient` for ollama and anthropic (dummy key, no network);
      provider case-insensitivity; unknown-provider throw; every fail-fast case; DI composition for both
      providers; and the AI-disabled graph registering nothing new. No production scoring/extraction/report
      change.
- [ ] The PR proposes a new `docs/architecture-decisions.md` entry for the config-driven `IChatClient` factory
      + provider-in-Infrastructure-only rule (drafted for maintainer approval).
- [ ] `dotnet build`/`dotnet test` on `Radar.sln -c Release` are green.
