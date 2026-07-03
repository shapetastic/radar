# Task: `IFilingAnalyzer` — Radar's first REAL AI call (typed, validated directional read of an earnings release)

## Overview

The directional-filing-signals arc has, so far, been all plumbing: spec 72 (merged) built the AI **seam**
— a config-driven `Microsoft.Extensions.AI.IChatClient` behind `IChatClientFactory`, provider SDKs confined
to Infrastructure, opt-in DI (`AddRadarAi`); spec 73 (merged) built `ISecEarningsReleaseReader`, which turns
an earnings 8-K into the **plain text** of its `EX-99.1` press release. Neither slice ever calls a model.

**This slice makes Radar's first real AI call.** It adds `IFilingAnalyzer`: given the earnings-release text
from spec 73, it asks the config-selected `IChatClient` for a **typed, validated** directional sentiment —
whether the release describes an **improving** or **deteriorating** business trajectory — with a confidence
and a short, advice-free rationale. It uses `Microsoft.Extensions.AI`'s `GetResponseAsync<T>` structured-output
extension, so it is provider-neutral: it works identically against the Anthropic OR Ollama client the seam
produces, and — critically — it is **fully offline-testable with a FAKE `IChatClient`** (no key, no running
Ollama, no network in any test).

This is the deliberate AI slice the roadmap reserved. Radar's rule is "prefer deterministic code before AI"
(CLAUDE.md) and the deterministic keyword extractor is **unchanged**; this analyzer is an *additional*,
opt-in, AI-only capability that later (spec 75) feeds a directional beat/miss signal.

**Honest framing (state this in the interface doc):** Radar has **no analyst-consensus feed**, so this is
**not** a true beat-vs-consensus claim. It is a directional read of the results **as reported** — does the
release describe an improving vs deteriorating trajectory (record bookings, raised outlook, organic growth →
improving; declines, guidance cut, impairments → deteriorating). Frame it that way everywhere; never dress it
up as "beat the Street".

**Arc position (slice 3 of 4):** spec 72 (AI seam, merged) → spec 73 (earnings-release reader, merged) →
**THIS `IFilingAnalyzer`** (text in → typed sentiment out) → spec 75 (fetch in-window earnings 8-Ks, run the
reader → analyzer, emit the directional signal with low-confidence gating). **Fetching filings and emitting
signals are OUT of scope here.** This slice is proven by: given release text and a FAKE `IChatClient`, the
analyzer returns a mapped/validated `FilingSentiment`; given a garbage/failed/empty fake response it returns
`Unknown`/0 without throwing; truncation and confidence-clamping work — all offline, deterministic.

---

## Assignment

Worktree: any (self-contained: a new Application interface + a typed record + a new analyzer class + additive
opt-in DI + one `AiWorkerOptions` field + offline tests).
Dependencies: **72 merged** (the AI seam — `IChatClientFactory`, the registered singleton `IChatClient`,
`AddRadarAi`, `AiClientOptions`, the `Radar:Ai` config section, AD-11); **73 merged** (the earnings-release
reader that produces the text this analyzer consumes — no code dependency, but this analyzer's input *is* its
`PlainText`).
Conflicts with: **None on logic.** New Application/Infrastructure AI files + additive DI + **one new field on
`AiWorkerOptions`** (`MaxInputLength`) + the matching `appsettings.json` `Radar:Ai` entry. It touches the same
shared wiring files the AI seam touched (`InfrastructureServiceCollectionExtensions.cs` `AddRadarAi`,
`RadarWorkerOptions.cs`, `RadarWorkerServices.cs`, `appsettings.json`), so **do not run it in parallel with
another slice that edits those files**; sequence it. It does **not** touch collectors, the deterministic
extractor, scoring, the resolver, or the report.
Estimated time: ~2–3 h

---

## Verified integration facts (grounded against the merged spec-72/73 code — do NOT re-research)

Read these files before writing code; the shapes below are copied from the current tree, not invented:

- **The seam registers BOTH the factory and a singleton client** (confirmed in
  `src/Radar.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`, `AddRadarAi`):
  ```csharp
  services.AddSingleton<IChatClientFactory, ChatClientFactory>();
  services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<IChatClientFactory>().Create());
  ```
  So the analyzer should **inject `Microsoft.Extensions.AI.IChatClient` directly** (the plainest dependency).
  Do NOT inject `IChatClientFactory` and call `Create()` per analysis — the singleton client is the intended
  consumer surface (spec 72 registered it precisely so consumers "can inject either").
- **`IChatClientFactory`** lives in `src/Radar.Application/Ai/IChatClientFactory.cs` (`IChatClient Create()`),
  `namespace Radar.Application.Ai`. **`ChatClientFactory`** + **`AiClientOptions`** live in
  `src/Radar.Infrastructure/Ai/`. Provider SDKs (`Anthropic`, `OllamaSharp`) are referenced **only** inside
  `ChatClientFactory` — do not touch that (AD-11 / AD-5).
- **AI is opt-in** (AD-11): `RadarWorkerServices.AddRadarWorker` calls `AddRadarAi(...)` **only when
  `options.Ai.Provider` is non-blank** (see `RadarWorkerServices.cs` line ~127). Blank provider = no
  `IChatClient` registered. The analyzer registration must sit **inside that same opt-in gate** so that when
  AI is disabled, no `IFilingAnalyzer` (and no `IChatClient` to satisfy it) is registered and the default
  graph is byte-for-byte unchanged.
- **`Microsoft.Extensions.AI` is already referenced** by `Radar.Infrastructure` (the seam uses it) and
  `Microsoft.Extensions.AI.Abstractions` by `Radar.Application` (AD-5). The typed structured-output extension
  `GetResponseAsync<T>(...)` is defined in the **`Microsoft.Extensions.AI`** package (class
  `ChatClientStructuredOutputExtensions`) — see the placement decision below for which project must reference
  it.
- **Domain enum style** to mirror (`src/Radar.Domain/Signals/SignalDirection.cs`): a plain `public enum`,
  values only. **Typed-validation discipline** to mirror: `src/Radar.Domain/Validation/SignalValidation.cs`
  and the `DeterministicSignalReviewer` — Radar validates/coerces typed values into a known-good shape before
  anything downstream trusts them. This analyzer does the same for AI output ("typed and validated before
  persistence", CLAUDE.md).
- **`Radar:Ai` config section** (in `src/Radar.Worker/appsettings.json`) is currently:
  ```jsonc
  "Ai": {
    "_comment": "…DISABLED by default: a blank Provider means no AI is wired…",
    "Provider": "",
    "Model": "",
    "Anthropic": { "ApiKey": "" },
    "Ollama": { "Endpoint": "http://localhost:11434" }
  }
  ```
  `AiWorkerOptions` (in `RadarWorkerOptions.cs`) carries `Provider`, `Model`, nested `Anthropic.ApiKey`,
  `Ollama.Endpoint`. This slice **adds one field**, `MaxInputLength` (see below).

### `Microsoft.Extensions.AI` structured-output API — CONFIRM the accessor at compile time

The typed-output call is `await chatClient.GetResponseAsync<FilingSentiment>(messages, cancellationToken: ct)`
(or a single-prompt overload), which returns a **`ChatResponse<FilingSentiment>`**. The exact way to read the
typed value off that response — the property (`.Result`) vs. a try-pattern (`.TryGetResult(out var value)`) —
and whether deserialization succeeded, **must be confirmed against the referenced package version at build
time** (per the C# compile-fix approach: let the compiler/IntelliSense tell you the real member; do NOT invent
a signature). The spec's contract is behavioural: **obtain a typed `FilingSentiment` candidate, or detect that
none could be parsed** — however the installed API exposes that. If a `TryGet`-style accessor exists, prefer it
(it makes the "unparseable → `Unknown`" path explicit); otherwise wrap the `.Result` read and treat a
null/throwing deserialization as the unparseable case. Requesting native structured output (JSON schema) via
the extension is preferred; a prompt-based JSON fallback is acceptable if the installed version needs it — the
mapping/validation step below is identical either way.

---

## Placement decision (RECOMMENDED: `Radar.Infrastructure`) — and why

Both are AD-5-legal because the analyzer uses **only** `Microsoft.Extensions.AI` abstractions (no provider
SDK). Weighing them:

- **Application** — keeps prompt/AI logic provider-neutral and lets `Radar.Application.Tests` unit-test it with
  a fake `IChatClient`. But it would require adding the **`Microsoft.Extensions.AI`** package (the one carrying
  `GetResponseAsync<T>`) to `Radar.Application`; today Application references only
  `Microsoft.Extensions.AI.Abstractions`. That is AD-5-permitted, but it widens Application's AI surface.
- **Infrastructure** (RECOMMENDED) — `Radar.Infrastructure` already references `Microsoft.Extensions.AI` and is
  where "the thing that talks to the model" belongs, consistent with `ChatClientFactory` and the SEC reader
  living there. `Radar.Infrastructure.Tests` already has `InternalsVisibleTo`, so the analyzer can be
  `internal` (mirroring `ChatClientFactory` and `HttpSecEarningsReleaseReader`) and still be fully unit-tested
  offline with a fake `IChatClient` — the fake is a test double, no provider SDK is involved. **The interface
  `IFilingAnalyzer` and the record `FilingSentiment` stay provider-neutral in the Application/Domain layers**
  (see below); only the `IChatClient`-calling *implementation* lives in Infrastructure.

**Decision:** put the **interface** (`IFilingAnalyzer`) in `Radar.Application`, the **record**
(`FilingSentiment`) + **enum** (`FilingDirection`) in `Radar.Domain`, and the **implementation**
(`ChatFilingAnalyzer`) + its options in `Radar.Infrastructure`. This keeps the contract provider-neutral and
in the right layers while confining the model-calling code to Infrastructure, and avoids adding a new package
to Application. (If the implementer strongly prefers Application for the impl, that is AD-5-legal too — but the
recommendation is Infrastructure; whichever is chosen, provider SDKs stay Infra-only, unchanged from spec 72.)

---

## `FilingSentiment` shape (RECOMMENDED — Domain record)

Put the record and its enum in **`Radar.Domain`** (it is a pure typed result, no packages — mirrors
`SignalDirection`; downstream spec 75's signal will map from it, and Domain is where such contracts live).

```csharp
namespace Radar.Domain.Filings;

/// <summary>
/// The directional trajectory a filing's earnings release describes, AS REPORTED. Radar has no analyst-
/// consensus feed, so this is NOT a beat-vs-consensus claim — it captures whether the release reads as an
/// improving or deteriorating business trajectory. Never advice.
/// </summary>
public enum FilingDirection
{
    Unknown,        // could not be read / low-signal / malformed AI output — the safe default
    Improving,      // beat/growth/raised outlook — record bookings, organic growth, guidance raised
    Deteriorating,  // miss/declines/cut outlook — revenue decline, guidance cut, impairment
    Mixed           // materially both (e.g. revenue up, margins down / one segment up, another cut)
}

/// <summary>Typed, validated directional read of an earnings release. Confidence is [0,1]; Rationale is a
/// bounded, plain-language, advice-free basis quoting the release (never "buy"/"sell"/etc.).</summary>
public sealed record FilingSentiment(FilingDirection Direction, decimal Confidence, string Rationale)
{
    public static FilingSentiment Unknown { get; } =
        new(FilingDirection.Unknown, 0m, string.Empty);
}
```

Notes for the spec:
- Enum values map cleanly to spec 75's signal: `Improving` → a positive directional signal,
  `Deteriorating` → negative, `Mixed`/`Unknown` → non-directional / gated out. Ordering with `Unknown = 0`
  makes it the natural default.
- `Confidence` is `decimal` (Radar's convention for confidences/scores) in **[0,1]**; the analyzer **clamps**
  it. It is what spec 75 will use for the "if AI confidence is low, do not create a high-confidence signal"
  gate (CLAUDE.md) — carry it through faithfully.
- `Rationale` is bounded (cap length, e.g. ~500 chars, truncate if the model over-produces) and must never
  contain advice language. It exists for report/audit transparency (like the deterministic reviewer's
  rationale), not for decisions.

---

## `IFilingAnalyzer` (Application interface)

```csharp
namespace Radar.Application.Filings;

/// <summary>
/// Reads an earnings-release plain text (from the SEC earnings-release reader, spec 73) and returns a typed,
/// validated <see cref="FilingSentiment"/> — a directional read of the results AS REPORTED (improving vs
/// deteriorating trajectory), NOT a beat-vs-consensus claim (Radar has no consensus feed). Implementations
/// MUST validate the model output before returning it and MUST degrade to <see cref="FilingSentiment.Unknown"/>
/// (Direction = Unknown, Confidence = 0) rather than throw on a malformed/empty/failed AI response; only
/// genuine caller cancellation propagates. Output must never contain advice language.
/// </summary>
public interface IFilingAnalyzer
{
    Task<FilingSentiment> AnalyzeAsync(string earningsReleaseText, CancellationToken ct);
}
```

---

## `ChatFilingAnalyzer` (Infrastructure implementation)

- `internal sealed class ChatFilingAnalyzer : IFilingAnalyzer`, injecting `IChatClient` (the registered
  singleton), a small options record (`FilingAnalyzerOptions { int MaxInputLength }`), and
  `ILogger<ChatFilingAnalyzer>`. Guard-clause null-check all three (mirror the SEC reader / `ChatClientFactory`
  discipline).
- **Truncate first (cost/latency control).** Before building the prompt, truncate `earningsReleaseText` to
  `MaxInputLength` characters (default from config; see below). The headline bullets carrying the beat/miss are
  at the top of an EX-99.1 release, so a leading-substring truncation is correct. This cap bounds token
  cost/latency; spec 75 calls the analyzer only for in-window earnings 8-Ks, but the cap belongs **here**.
  Handle null/empty input by returning `FilingSentiment.Unknown` (never call the model on empty text).
- **Build a fixed, deterministic prompt.** A constant **system instruction** + a user message containing the
  (truncated) release text. The system instruction must:
  - state the task: classify the trajectory the release **describes as reported** into
    `Improving` / `Deteriorating` / `Mixed` / `Unknown`, with a confidence in `[0,1]` and a one-sentence
    rationale quoting the release;
  - be explicit this is **not** investment advice and the rationale must contain **no** advice language
    (never "buy"/"sell"/"guaranteed"/"safe bet"/price targets) — the output-language ban applies to any text
    the analyzer surfaces;
  - instruct: when the text is ambiguous, boilerplate, or lacks results, return `Unknown` with low confidence
    (deterministic-before-AI honesty — don't manufacture a directional read).
  Keep the prompt a compile-time constant (no per-call randomness) so the analyzer is deterministic given the
  same client + input.
- **Call structured output.** `var response = await chatClient.GetResponseAsync<FilingSentiment>(messages, ct)`
  (confirm the exact overload + result accessor at build — see the API note above). Obtain a candidate
  `FilingSentiment` (or detect that none parsed).
- **Map + VALIDATE into a known-good `FilingSentiment`** (the "typed and validated before persistence" rule):
  - **Direction:** if the parsed direction is a defined `FilingDirection` value, keep it; otherwise coerce to
    `Unknown`. (Enum deserialization of an unknown string/number must NOT yield an out-of-range enum — validate
    with `Enum.IsDefined` or an explicit switch.)
  - **Confidence:** **clamp to [0,1]** (`Math.Clamp(candidate.Confidence, 0m, 1m)`). An out-of-range value
    (e.g. `1.7`) becomes `1.0`; negative becomes `0`. If Direction coerced to `Unknown`, force Confidence `0`.
  - **Rationale:** trim; cap to the bounded length; if null, use empty. (Optional defensive hardening: this is
    not a place to run an advice-word filter as a feature — the system prompt forbids advice language and the
    rationale is transparency-only — but do not surface unbounded model text.)
- **Never throw on a bad AI response.** Wrap the model call + parse: on `GetResponseAsync` throwing (network,
  provider error), on an empty/whitespace response, on deserialization failure, or on a null candidate →
  **log at Warning and return `FilingSentiment.Unknown`** (Direction = `Unknown`, Confidence = `0`). The ONLY
  exception that propagates is genuine caller cancellation: `catch (OperationCanceledException) when
  (ct.IsCancellationRequested) { throw; }` — re-throw that, degrade everything else. This mirrors the spec-73
  reader's "typed graceful degradation; re-throw only genuine caller cancellation" discipline.
- All `Microsoft.Extensions.AI` usage stays in Infrastructure; no provider SDK is referenced here.

### Options — `FilingAnalyzerOptions` (Infrastructure) and its config surface

- `internal sealed class FilingAnalyzerOptions { public int MaxInputLength { get; init; } = 12000; }`
  (default 12k chars — enough to carry the headline results block of a typical EX-99.1; keeps token cost
  bounded). Registered as a singleton by the DI method below.
- **Worker config surface:** add `public int MaxInputLength { get; init; } = 12000;` to `AiWorkerOptions`
  (`RadarWorkerOptions.cs`), and a matching key in the `Radar:Ai` block in `appsettings.json`. This is the
  only new config field this slice adds.

---

## DI — register the analyzer inside the opt-in AI gate

Add one additive Infrastructure method mirroring `AddRadarAi`'s style:

```csharp
public static IServiceCollection AddRadarFilingAnalyzer(
    this IServiceCollection services, FilingAnalyzerOptions options)
```

- `ArgumentNullException.ThrowIfNull(options);` validate `MaxInputLength > 0` (fail fast with a clear
  `Radar:Ai:MaxInputLength` message in the SEC/AI validator style); register `services.AddSingleton(options)`
  and `services.AddSingleton<IFilingAnalyzer, ChatFilingAnalyzer>()`. It does NOT register an `IChatClient` —
  it **depends** on the one `AddRadarAi` already registered.
- **Wire it inside the same opt-in gate** in `RadarWorkerServices.AddRadarWorker`: in the existing
  `if (!string.IsNullOrWhiteSpace(options.Ai.Provider)) { services.AddRadarAi(...); }` block, after
  `AddRadarAi`, call `services.AddRadarFilingAnalyzer(new FilingAnalyzerOptions { MaxInputLength =
  options.Ai.MaxInputLength });`. When `Provider` is blank, neither `AddRadarAi` nor `AddRadarFilingAnalyzer`
  runs — no `IFilingAnalyzer` and no `IChatClient` are registered; the default graph is byte-for-byte
  unchanged.

### Config default — keep AI DISABLED by default (see Open questions)

The context brief suggested defaulting the provider to `ollama` with a placeholder model so the pipeline is
demoable keyless. **Recommendation: do NOT flip the shipped default `Provider` from blank.** Spec 72's
acceptance criteria and AD-11 guarantee the default configuration is byte-for-byte unchanged / AI DISABLED, and
spec 75's signal is not wired yet, so a default-on `ollama` would try to talk to a (probably absent) local
Ollama during a normal run for no benefit. Instead:
- **Keep `Provider: ""`** (DISABLED) in `appsettings.json`.
- **Document the keyless demo path in the `_comment`**: to demo the analyzer keyless, set `Provider` to
  `"ollama"` and `Model` to an installed tag (e.g. `"llama3.1"`); Anthropic is a config flip + key away
  (`Provider: "anthropic"`, `Model` e.g. `"claude-opus-4-8"`, `Anthropic:ApiKey`).
- **Add `"MaxInputLength": 12000`** to the `Radar:Ai` block.
- Do **not** hard-code any model or key anywhere (the Ollama tag / Anthropic model / key remain config-only).

This satisfies "demoable keyless, Anthropic a flip away" without breaking spec 72's opt-in guarantee. If the
maintainer wants the default flipped to `ollama`, that is a one-line config change — raised as an Open question,
not baked in.

---

## Tests (ALL OFFLINE — a FAKE `IChatClient`; no network, no key, no Ollama)

Implement a **test double `IChatClient`** (`FakeChatClient`) in `Radar.Infrastructure.Tests` that returns a
scripted `ChatResponse` / structured value and **captures the messages it was handed** (so tests can assert on
the prompt/length). Confirm the exact members to implement/return against the referenced `Microsoft.Extensions.AI`
version at build time (the `IChatClient` interface surface — `GetResponseAsync` + streaming + `GetService` etc.
— the fake can throw `NotSupportedException` for members the analyzer never calls). Construct `ChatFilingAnalyzer`
with the fake, a `FilingAnalyzerOptions`, and a `NullLogger<ChatFilingAnalyzer>`. Deterministic given the fake.

`ChatFilingAnalyzerTests` (Radar.Infrastructure.Tests/Filings):

- **Valid structured sentiment is mapped through.** Fake returns `Improving`, `Confidence = 0.8`, a clean
  rationale → `AnalyzeAsync` returns `FilingSentiment(Improving, 0.8, "…")` unchanged (direction preserved,
  confidence preserved, rationale carried).
- **Out-of-range confidence is clamped.** Fake returns `Confidence = 1.7` → result confidence is `1.0`; fake
  returns `-0.4` → `0`. Direction otherwise preserved.
- **Unknown/undefined direction is coerced.** Fake returns a direction that does not map to a defined
  `FilingDirection` (an out-of-range enum / unknown string, however the API surfaces it) → result is
  `Unknown` with confidence `0`.
- **Failed/empty/unparseable response → `Unknown`/0, no throw.** Three cases: (a) fake `GetResponseAsync`
  **throws** a provider-style exception; (b) fake returns an **empty**/whitespace response with no typed
  result; (c) fake returns content that **cannot be deserialized** into `FilingSentiment` → each returns
  `FilingSentiment(Unknown, 0, "")` and does **not** throw.
- **Caller cancellation propagates.** An already-cancelled `CancellationToken` (or a fake that throws
  `OperationCanceledException` with the token cancelled) → `AnalyzeAsync` **throws**
  `OperationCanceledException`, not an `Unknown` result.
- **Truncation before the call.** Input longer than `MaxInputLength` (set a small cap, e.g. 50, in the test's
  options) → the fake's **captured** prompt/messages contain at most `MaxInputLength` characters of the release
  text (assert the captured text length / that the tail was dropped). Proves the cap is applied *before* the
  model call.
- **Empty/null input short-circuits.** `AnalyzeAsync("", ct)` (and null) returns `Unknown` **without** invoking
  the fake (assert the fake's call-count is 0).
- **(Optional) No advice language surfaced.** If a rationale is returned, assert it contains none of the banned
  tokens — belt-and-braces on the output-language rule.

`AddRadarFilingAnalyzerTests` (Radar.Infrastructure.Tests/Filings):

- **Composition.** A `ServiceCollection` with `AddRadarAi(ollamaOptions)` **then** `AddRadarFilingAnalyzer(new
  FilingAnalyzerOptions { MaxInputLength = 12000 })` → `BuildServiceProvider()` resolves a non-null
  `IFilingAnalyzer` (proving it composes against the seam-registered `IChatClient`). No network call.
- **Fail-fast.** `AddRadarFilingAnalyzer` with `MaxInputLength <= 0` throws `InvalidOperationException` with the
  documented `Radar:Ai:MaxInputLength` message; `AddRadarFilingAnalyzer(null)` throws `ArgumentNullException`.

Worker-graph opt-in test (extend the spec-72 worker-graph test if present, else add one):

- Default/blank `Radar:Ai:Provider` → the worker graph registers **no `IFilingAnalyzer`** (and no
  `IChatClient`) — default graph unchanged.
- `Radar:Ai:Provider = "ollama"` (+ `Model`) → the worker graph registers **both** `IChatClient` and
  `IFilingAnalyzer` — proving the analyzer rides the existing opt-in gate.

Existing tests (collectors, runner, DI, scoring, report, the spec-72 AI seam) stay green; nothing in the
default pipeline changes.

---

## Constraints

- Target `net10.0`, C# 14.
- **No provider SDK outside `Radar.Infrastructure`** (AD-5 / AD-11) — the analyzer uses **only**
  `Microsoft.Extensions.AI` abstractions (`IChatClient` + `GetResponseAsync<T>`), never `Anthropic`/`OllamaSharp`.
- **Deterministic before AI honored:** this is the deliberate AI slice the roadmap reserved; the deterministic
  keyword extractor, scoring formula, resolver, and report are **unchanged** (AD-10 not triggered — this slice
  does not affect scoring output).
- **Typed + validated AI output** (CLAUDE.md): direction validated to a defined enum value, confidence clamped
  to [0,1], rationale bounded, before the `FilingSentiment` is returned. A malformed/failed/empty response
  degrades to `Unknown`/0 and **never throws** the pipeline; only genuine caller cancellation propagates.
- **Confidence carried through** faithfully for spec 75's low-confidence gating.
- **Never emit advice language** (the output-language ban applies to the prompt and any rationale surfaced).
- **Opt-in / no drift:** the analyzer is registered only inside the existing `Ai.Provider`-non-blank gate; the
  default configuration and existing pipeline are byte-for-byte unchanged and load no provider packages at
  runtime. `appsettings.json` keeps `Provider: ""` (DISABLED) by default.
- **No DB** (AD-8, files-first). Scope: the analyzer + its record/enum/options + additive DI + one
  `AiWorkerOptions` field + offline tests. Do **not** fetch filings, emit signals, or touch
  collectors/extractor/scoring/resolver/report (that is spec 75).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Acceptance criteria

- [ ] `IFilingAnalyzer.AnalyzeAsync(string earningsReleaseText, CancellationToken ct)` lives in
      `Radar.Application/Filings`, referencing only the `Microsoft.Extensions.AI` abstraction / Domain types
      (AD-5); its doc states this is a directional read AS REPORTED, not a beat-vs-consensus claim.
- [ ] `FilingSentiment` (record) + `FilingDirection` (enum: `Unknown`/`Improving`/`Deteriorating`/`Mixed`) live
      in `Radar.Domain` (no package refs), with `Confidence` a decimal in [0,1] and a bounded, advice-free
      `Rationale`; a `FilingSentiment.Unknown` default is provided.
- [ ] `ChatFilingAnalyzer` (Infrastructure, `internal`) injects the seam-registered `IChatClient`, truncates
      the input to a configurable `MaxInputLength` before the call, builds a fixed deterministic prompt, calls
      `GetResponseAsync<FilingSentiment>` (accessor confirmed at build), and **validates** the result:
      direction coerced to a defined enum value, confidence `Math.Clamp`ed to [0,1], rationale bounded — all
      before returning. No provider SDK is referenced.
- [ ] A malformed / failed / empty / unparseable AI response degrades to `FilingSentiment(Unknown, 0, "")` and
      **never throws**; only `OperationCanceledException` on a requested `ct` propagates. Empty/null input
      short-circuits to `Unknown` without calling the model.
- [ ] `AddRadarFilingAnalyzer(FilingAnalyzerOptions)` registers `IFilingAnalyzer` (singleton), fails fast on
      `MaxInputLength <= 0` (clear `Radar:Ai:MaxInputLength` message) and on `null` options; it depends on the
      `IChatClient` that `AddRadarAi` registers and is wired **only inside the existing `Ai.Provider`-non-blank
      opt-in gate** in `RadarWorkerServices`.
- [ ] `AiWorkerOptions` gains `MaxInputLength` (default 12000) and `appsettings.json`'s `Radar:Ai` block adds
      `"MaxInputLength": 12000`; `Provider` stays `""` (AI DISABLED by default) with the `_comment` documenting
      the keyless-`ollama` demo path and the Anthropic flip. No model or key is hard-coded.
- [ ] With the default (blank) `Radar:Ai:Provider` the worker graph registers **no `IFilingAnalyzer`/`IChatClient`**
      (byte-for-byte unchanged); a `Provider = "ollama"` config registers **both**.
- [ ] Offline tests (FAKE `IChatClient`, no network/key/Ollama) cover: valid sentiment mapped through;
      confidence clamped; unknown-direction coerced; throw/empty/unparseable → `Unknown`/0 no-throw; caller
      cancellation propagates; input truncated to `MaxInputLength` before the call (asserted via the fake's
      captured messages); empty/null input short-circuits; DI composition + fail-fast. No production
      scoring/extraction/report/collector change.
- [ ] `dotnet build`/`dotnet test` on `Radar.sln -c Release` are green. This is **slice 3** of the
      directional-filing arc (seam → reader → **this analyzer** → beat/miss signal); fetching filings and
      emitting signals are OUT of scope (spec 75).
```