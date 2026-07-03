# Task: Directional filing signals — wire reader → analyzer → a confidence-gated directional GuidanceChange signal (COMPLETES the arc)

## Overview

This slice **completes the directional-filing-signals arc**. Specs 72 (AI `IChatClient` seam),
73 (`ISecEarningsReleaseReader` — EX-99.1 body reader), and 74 (`IFilingAnalyzer` — typed, validated
`FilingSentiment`) are all merged but **not yet connected to the pipeline**: nothing calls the reader or
the analyzer, so no filing has ever moved a company's Trajectory. This slice adds the missing consumer.

**The gap this closes (state precisely in the PR).** Today an earnings 8-K (item `2.02`) produces only a
deterministic **Neutral** `GuidanceChange` signal (spec 57): the keyword rule `"results of operations"` →
`GuidanceChange`/**Neutral** (strength 3, novelty 4, confidence 0.4 —
`KeywordSignalExtractor.cs` line 116). Neutral contributes **0** to `TrajectoryScore` (AD-6 v2:
`Neutral`/`Mixed` are excluded from both numerator and denominator). So a genuine **beat** and a genuine
**miss** score identically — the item code says "results were released" but never says beat-or-miss.
The AI arc reads the actual EX-99.1 numbers; this slice turns that read into a **directional** signal so a
strong beat lifts Trajectory (`Positive` `GuidanceChange`) and a miss lowers it (`Negative`). Radar's
whole premise (`Thesis improving` / `Thesis deteriorating`) depends on directional filing signals.

**Opt-in, no default change (load-bearing).** The new component runs **only when the AI seam is enabled**
(`Radar:Ai:Provider` non-blank — the same gate as spec 74, `RadarWorkerServices.cs` line 128). With AI
disabled (the shipped default) **nothing changes**: no network, no model call, no cost, and the
deterministic Neutral `GuidanceChange` from spec 57 stands. The default dependency graph is byte-for-byte
unchanged.

**Arc position (slice 4 of 4 — the last):** spec 72 (seam) → spec 73 (reader) → spec 74 (analyzer) →
**THIS directional signal**. This slice contains **no** AI/HTTP code itself — it composes the merged
reader + analyzer behind an Application interface and emits the signal.

---

## Assignment

Worktree: any
Dependencies: **56** (SEC collector — produces the `Filing` evidence), **57** (the deterministic Neutral
`GuidanceChange` for item `2.02` — this slice must not double-count it), **72** (AI seam / `AddRadarAi`
opt-in gate), **73** (`ISecEarningsReleaseReader`), **74** (`IFilingAnalyzer` / `FilingSentiment` /
`FilingDirection`) — all merged. Read specs 63 and 70 for the evidence-type → signal precedent and the
AD-10 `ScoringConfigVersion` bump convention.
Conflicts with: touches the pipeline **signal-production wiring** (`RadarPipelineRunner` + its DI/
`AddRadarPipeline` and the worker AI gate), `ScoringEngine.ScoringConfigVersion`, and **possibly**
`SecEdgarFilingCollector` metadata, plus all their tests. It must **NOT** run in parallel with any other
extractor / scoring / collector / runner slice — sequence it.
Estimated time: ~2.5–3 h

---

## Grounding facts (verified against the current tree — do NOT re-research)

- **The runner runs exactly ONE `ISignalExtractor`.** `RadarPipelineRunner` (`RadarPipelineRunner.cs`
  lines 32, 200) injects a single `ISignalExtractor _extractor` and, per newly-stored evidence, does
  `extract → map (ExtractedSignalMapper.ToSignal) → resolve → review → store signal + review + file`
  (lines 195–258). There is **no** `IEnumerable<ISignalExtractor>` composition seam — collectors are a
  set, extractors are not. So a "second `ISignalExtractor`" is **not** how the runner composes today.
- **The deterministic keyword extractor must stay untouched.** `KeywordSignalExtractor` is source-agnostic
  except for two documented branches (NewsArticle attention; GovernmentContract materiality) and carries an
  explicit WATCH-ITEM comment (lines 28–32) *not* to grow a third inline source-type branch. Do **not**
  add filing-directional logic to it. The AI read is a *separate* capability (deterministic-before-AI: the
  keyword layer is unchanged; AI only ADDS directional filing reads).
- **The reader + analyzer are ready and offline-testable.**
  - `ISecEarningsReleaseReader.ReadAsync(cik, accession, ct)` → `SecEarningsReleaseReadResult`
    (`Outcome ∈ {Success, NoEarningsExhibit, Unreachable, HttpError, Forbidden, Timeout, Malformed}`,
    `IsSuccess`, `PlainText`). It is **`internal`** to `Radar.Infrastructure`
    (`ISecEarningsReleaseReader.cs`), registered by `AddSecEarningsReleaseReader(SecCollectorOptions)`.
  - `IFilingAnalyzer.AnalyzeAsync(text, ct)` → `FilingSentiment(FilingDirection Direction, decimal
    Confidence, string Rationale)`; `FilingDirection ∈ {Unknown, Improving, Deteriorating, Mixed}`;
    degrades to `FilingSentiment.Unknown` (Direction `Unknown`, Confidence `0`), never throws except on
    caller cancellation. It is a public Application interface (`Radar.Application.Filings`), registered by
    `AddRadarFilingAnalyzer` **inside the `Ai.Provider`-non-blank gate**.
- **CIK + accession are recoverable from the persisted `Filing` `EvidenceItem`** (see the finding below).
- **Item `2.02` marks an earnings 8-K.** The SEC collector writes the item codes into the evidence `Title`
  (e.g. `"8-K — … [items: 2.02,9.01] Items: Results of Operations and Financial Condition."`) and the
  `RawText` (`"8-K item codes: 2.02,9.01."`), and `MetadataJson` carries `form = "8-K"` +
  `accessionNumber`. There is **no** discrete `items` metadata key today (see the metadata-hardening note).
- **A directional `GuidanceChange` moves Trajectory under `radar-formula-v2` (AD-6).** `Positive` →
  contributes `+strength` (weighted) to `TrajectoryScore` (mapped `50 + 5·T_raw`); `Negative` →
  contributes negatively; `Neutral`/`Mixed` → weight 0. So `Improving → Positive` lifts, `Deteriorating →
  Negative` lowers — exactly the desired effect.

---

## Integration-seam design — RECOMMENDED: an opt-in enrichment step in `RadarPipelineRunner`

I evaluated the three options against the real runner:

- **(a) A second `ISignalExtractor`.** Rejected. The runner injects a *single* `ISignalExtractor` and has
  no multi-extractor composition seam; adding one is a larger, riskier change than this slice warrants, and
  the directional read is per-*filing-evidence* + needs network/AI + a per-run cap — a poor fit for the
  per-evidence `ExtractAsync` contract. It would also invite folding AI logic next to the deterministic
  keyword rules, violating the WATCH-ITEM boundary.
- **(c) A dedicated collector or a new stage type.** Rejected as over-engineered for one capability.
- **(b) A dedicated OPT-IN enrichment step in `RadarPipelineRunner` — CHOSEN.** After the existing
  `extract → resolve → review → store` loop over `newEvidence` (and before scoring), the runner invokes a
  new **optional** Application-layer service that, for the run's **in-scoring-window earnings-8-K `Filing`
  evidence**, produces zero-or-one directional `GuidanceChange` `ExtractedSignal` per filing. Those signals
  flow through the **same** `ExtractedSignalMapper.ToSignal → resolve → review → store signal + review +
  file` path the keyword signals already use (so provenance, validation, review, and the on-disk twin are
  identical and not re-implemented).

### The seam interface (Application) and its no-op default

Add a tiny Application interface so the runner depends on an abstraction, not on Infrastructure (AD-5), and
so the default (AI-disabled) graph is unchanged:

```csharp
namespace Radar.Application.Filings;

/// <summary>
/// Opt-in enrichment: for in-scoring-window earnings-8-K Filing evidence, fetch the EX-99.1 body,
/// analyze its directional sentiment, and emit at most one confidence-gated directional GuidanceChange
/// ExtractedSignal per filing (Improving -> Positive, Deteriorating -> Negative; Mixed/Unknown/low-
/// confidence -> none). Returns ExtractedSignal + the source EvidenceItem so the runner threads them
/// through the SAME map -> resolve -> review -> store path as keyword signals (provenance preserved).
/// Every reader/analyzer failure degrades to "no directional signal for that filing" and NEVER aborts
/// the run. When AI is disabled this service is not registered and the step is skipped entirely.
/// </summary>
public interface IDirectionalFilingSignalSource
{
    Task<IReadOnlyList<DirectionalFilingSignal>> ProduceAsync(
        IReadOnlyList<EvidenceItem> candidateEvidence,
        DateTimeOffset asOfUtc,
        CancellationToken ct);
}

/// <summary>An extracted directional filing signal paired with its source evidence (provenance).</summary>
public sealed record DirectionalFilingSignal(ExtractedSignal Signal, EvidenceItem Evidence);
```

- The runner takes it as an **optional** dependency: `IDirectionalFilingSignalSource? source = null` in the
  constructor (or resolve via `IServiceProvider.GetService<>` / register a no-op default). **RECOMMENDED:**
  make the constructor parameter nullable with a `null` default so the existing runner construction and
  every current DI test keeps compiling; when `null`, the step is skipped. (Alternatively register a
  `NoOpDirectionalFilingSignalSource` returning `[]`; pick one and state it. The nullable-optional keeps
  the diff smallest and makes "AI disabled ⇒ no step" self-evident.)
- **Wiring:** register the concrete implementation **only inside the existing `Ai.Provider`-non-blank gate**
  in `RadarWorkerServices.AddRadarWorker` (right after `AddRadarFilingAnalyzer`, line 140), via a new
  additive `AddDirectionalFilingSignals(...)` Infrastructure method. When `Provider` is blank the service is
  not registered, the runner's optional dependency is `null`, and the step is skipped — default graph
  byte-for-byte unchanged. (Note: the earnings reader `AddSecEarningsReleaseReader` must also be registered
  inside that same gate here, since the implementation depends on it and it is not wired into the worker
  today — add that call in the gate too.)

### What the runner adds (one small block, after the extract loop, before scoring)

```csharp
// OPT-IN directional filing enrichment (AI only). Null when AI is disabled -> skipped entirely, so the
// default pipeline is byte-for-byte unchanged. Runs AFTER the deterministic extract loop and BEFORE
// scoring, over this run's newly-stored earnings-8-K Filing evidence that is in the scoring window, and
// threads each produced signal through the SAME map -> resolve -> review -> store path as keyword signals.
if (_directionalFilingSignals is not null)
{
    var candidates = newEvidence
        .Select(e => e.Evidence)
        .Where(ev => ev.SourceType == EvidenceSourceType.Filing)
        .ToList(); // the source itself applies the earnings-2.02 + in-window + cap filtering (see below)

    foreach (var produced in await _directionalFilingSignals
        .ProduceAsync(candidates, asOfUtc, ct).ConfigureAwait(false))
    {
        ct.ThrowIfCancellationRequested();
        signalsExtracted++;
        var mapping = ExtractedSignalMapper.ToSignal(produced.Signal, produced.Evidence, asOfUtc);
        if (!mapping.IsValid) { /* log + continue, exactly as the keyword path does */ continue; }
        // ... identical resolve -> review -> store signal + review + file + counter bump as the loop above.
    }
}
```

Factor the shared `map → resolve → review → store` tail into one private helper so the runner does not
duplicate it (the reviewer will flag copy-paste). The window/asOfUtc semantics are unchanged (AD-7: one run
instant; `asOfUtc` captured after collection).

**Why this fits with least churn:** it reuses the runner's existing provenance/validation/review/store
machinery verbatim, keeps AI + HTTP entirely behind Infrastructure interfaces (AD-5), leaves the
deterministic extractor untouched, and — because the service is only registered under the AI gate — makes
"AI off ⇒ zero change" structural, not incidental.

---

## CIK + accession source finding (verified — no collector change strictly required)

On the persisted `Filing` `EvidenceItem` produced by `SecEdgarFilingCollector.MapToEvidence`:

- **`SourceUrl` = `filing.IndexUrl`** = `https://www.sec.gov/Archives/edgar/data/{cik}/{accNoNoDashes}/{accession}-index.htm`.
  This embeds **both** the CIK (leading zeros already stripped) and the dashed accession — exactly the two
  values `ISecEarningsReleaseReader.ReadAsync(cik, accession, ct)` needs.
- **`MetadataJson`** carries `accessionNumber` (dashed), `form = "8-K"`, `filingDate`, and `secFeedUrl`
  (`https://data.sec.gov/submissions/CIK##########.json`, zero-padded CIK).

**Recommendation (most robust): parse CIK + dashed accession from the index `SourceUrl`** with a small,
defensive regex/segment split (`.../edgar/data/{cik}/{accNoNoDashes}/{dashedAccession}-index.htm`). It is
the single field that carries both identifiers in the exact shapes the reader consumes, and it avoids
depending on two separate metadata keys. Cross-check the accession against `MetadataJson.accessionNumber`
when present; if the URL cannot be parsed, **skip that filing** (no directional signal — never guess a
CIK/accession). Provenance is unaffected: the emitted signal references the same `EvidenceItem`.

**"Is it an earnings 8-K?" (the `2.02` gate).** The item codes live in the evidence **`Title`** (and
`RawText`), e.g. `[items: 2.02,9.01]`. The source must confirm `form == "8-K"` (metadata) **and** that the
item list contains `2.02` before doing any network work — parse the `[items: …]` / `8-K item codes: …`
segment from Title/RawText, or the `Items:` clause. Only `Filing` evidence with `2.02` is eligible;
non-earnings filings (no `2.02`) are never fetched.

**Optional hardening (include only if trivial): add a discrete `items` metadata key** to
`SecEdgarFilingCollector.MapToEvidence` (`metadata["items"] = filing.Items`) so the `2.02` check reads a
clean field instead of parsing the Title. This is a **one-line, additive** change that does not alter the
`ContentHash` inputs (Title/RawText are unchanged) or any existing evidence text, so it is safe — but it is
**not required** (Title parsing works). If added, it is a scoring-neutral collector metadata change (no
`ScoringConfigVersion` implication by itself); note it in the PR and add a collector test asserting the new
key. **Recommendation:** add it — it makes the source-agnostic-ish invariant cleaner and the gate robust —
but keep it strictly additive.

---

## Confidence gate, direction mapping, and no double-count

### Confidence gate (CLAUDE.md — "if AI confidence is low … do NOT create high-confidence signals")

- Surface a configurable `MinConfidence` threshold (see config below). For each in-window earnings 8-K:
  read → on `Success`, analyze. Then:
  - `FilingSentiment.Confidence < MinConfidence` → **emit NO directional signal.** The deterministic Neutral
    `GuidanceChange` from spec 57 stands. (Persist-the-evidence-but-not-a-high-confidence-signal: the
    evidence and its Neutral signal already exist; we simply add nothing.)
  - `Confidence ≥ MinConfidence` **and** `Direction == Improving` → one **`Positive`** `GuidanceChange`.
  - `Confidence ≥ MinConfidence` **and** `Direction == Deteriorating` → one **`Negative`** `GuidanceChange`.
  - `Direction ∈ {Mixed, Unknown}` → **no directional signal** (regardless of confidence).
- **Signal field mapping** (all within domain ranges so `ExtractedSignalMapper.ToSignal` /
  `SignalValidation` pass):
  - `SignalType = GuidanceChange`, `Direction = Positive|Negative`.
  - `Confidence` = the AI `FilingSentiment.Confidence` (already clamped to [0,1] by spec 74).
  - `Strength` / `Novelty`: pick modest, in-range constants consistent with the existing `GuidanceChange`
    directional rules (which use Strength 6 / Novelty 6). **RECOMMENDED starting point: Strength 6,
    Novelty 6** (present as tunable). These clear the `DeterministicSignalReviewer` floors
    (`MinMaterialStrength 3`, `MinNovelty 3`) so, given a resolved company and a not-low confidence, the
    signal is `Approved` and reaches scoring.
  - `SupportingExcerpt`: must be a **verbatim slice found in the evidence** (the mapper enforces
    excerpt-in-evidence). The AI rationale is NOT guaranteed to appear in the evidence Title/RawText, so use
    a verbatim slice of the evidence searchable text (e.g. the `results of operations` / `Items:` region of
    the Title, reusing the same excerpt approach as the keyword path) as the `SupportingExcerpt`, and carry
    the analyzer's advice-scrubbed **`Rationale`** in the signal **`Reason`** field (Reason is not
    provenance-checked). This keeps provenance intact while surfacing the AI basis for audit/report.
  - `CompanyMention = evidence.SourceName` (identical placeholder convention; resolution stays downstream).

### No double-count vs the deterministic Neutral (state which and why)

The spec-57 Neutral `GuidanceChange` and this directional `GuidanceChange` are **two distinct signals over
the same filing evidence**. **RECOMMENDATION: let them coexist — do NOT dedupe/supersede.** Justification:
under `radar-formula-v2` (AD-6) the **Neutral** signal contributes **0** to `TrajectoryScore` (excluded from
both numerator and denominator), so it cannot dilute or double-count the directional read; the directional
`Positive`/`Negative` signal carries the entire trajectory effect. Both still contribute their evidence to
the `Filing`-source diversity term (that is the intended spec-57 value and is not "double counting" — it is
one filing, counted once per its distinct source *type*). Coexistence keeps the seam simple and avoids
mutating/removing an already-stored, already-reviewed signal mid-run. (If a future slice wants a single
`GuidanceChange` per filing, that is a deliberate follow-up — call it out, do not implement here.)

### Cost / robustness (mirror collector graceful degradation)

- **Cap** the number of filings analyzed per run (`MaxFilingsPerRun`, small default, e.g. **5**). The source
  orders candidates deterministically (e.g. by evidence `ObservedAtUtc` desc, then `Id`) and analyzes at
  most the cap; the rest are skipped (no signal, logged at Debug).
- **Strictly sequential + polite:** one filing at a time (SEC 10 req/s + AI latency/cost); honour `ct`
  between filings.
- **Every failure degrades to "no directional signal for that filing" and NEVER aborts the run:** reader
  `NoEarningsExhibit`/`Forbidden`/`HttpError`/`Unreachable`/`Timeout`/`Malformed` → skip; analyzer
  `Unknown`/`Mixed`/low-confidence → skip; any unexpected exception (other than caller cancellation) → log
  at Warning and skip. Only `OperationCanceledException` with `ct.IsCancellationRequested` propagates
  (mirrors the spec-73 reader / spec-74 analyzer discipline).
- Network + AI happen **only** when AI is enabled AND the collector produced in-window earnings-8-K
  evidence this run.

---

## Scoring version bump (AD-10)

This slice **changes scoring output** (a beat now lifts Trajectory; a miss lowers it — output that did not
exist pre-75). Per AD-10, **bump `ScoringEngine.ScoringConfigVersion`** `"radar-scoring-config-v2"` →
`"radar-scoring-config-v3"`, and update the accompanying comment to record that spec 75 (directional filing
signals) ships this generation so a cross-run delta across the pre/post-75 boundary renders
`(scoring updated)` instead of a fabricated `Thesis improving`/`Thesis deteriorating`. Do **not** touch
`ScoringVersion`, `EngineVersion`, or `RadarScoreFormulaV2.Version` — the formula math is unchanged. Update
any test asserting the old `"radar-scoring-config-v2"` string.

> Note the subtlety worth a sentence in the PR: the directional signal only ever appears when AI is
> enabled, so with AI **disabled** scoring output is identical to v2 — but the stamp still bumps to v3
> because the *generation* (the code that can now emit different scores) has changed. This is the correct,
> conservative AD-10 behaviour.

---

## Project structure changes

```text
src/Radar.Application/Filings/
  IDirectionalFilingSignalSource.cs   # NEW: the opt-in enrichment interface + DirectionalFilingSignal record

src/Radar.Application/Pipeline/
  RadarPipelineRunner.cs              # MODIFIED: optional IDirectionalFilingSignalSource dependency (nullable,
                                      #   default null); enrichment block after the extract loop; shared
                                      #   map->resolve->review->store helper

src/Radar.Infrastructure/Filings/
  DirectionalFilingSignalSource.cs    # NEW (internal): parses CIK/accession + 2.02 gate, calls
                                      #   ISecEarningsReleaseReader + IFilingAnalyzer, applies MinConfidence
                                      #   gate + cap + graceful degradation, emits directional ExtractedSignals
  DirectionalFilingSignalOptions.cs   # NEW (internal): MinConfidence, MaxFilingsPerRun, (Strength/Novelty)

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs  # MODIFIED: AddDirectionalFilingSignals(options) additive
                                      #   registration (registers the source; TryAdd its reader/analyzer deps)

src/Radar.Infrastructure/Sec/
  SecEdgarFilingCollector.cs          # OPTIONAL (hardening): add metadata["items"] = filing.Items

src/Radar.Application/Scoring/
  ScoringEngine.cs                    # MODIFIED: bump ScoringConfigVersion -> "radar-scoring-config-v3"

src/Radar.Worker/
  RadarWorkerServices.cs              # MODIFIED: inside the Ai.Provider-non-blank gate, also call
                                      #   AddSecEarningsReleaseReader(...) + AddDirectionalFilingSignals(...)
  RadarWorkerOptions.cs              # MODIFIED: AiWorkerOptions gains MinConfidence + MaxFilingsPerRun
  appsettings.json                   # MODIFIED: Radar:Ai gains "MinConfidence" + "MaxFilingsPerRun";
                                      #   Provider stays "" (AI DISABLED by default); document Ollama + Anthropic

tests/Radar.Infrastructure.Tests/Filings/
  DirectionalFilingSignalSourceTests.cs   # NEW: offline (fake reader + fake analyzer)
tests/Radar.Application.Tests/Pipeline/
  RadarPipelineRunnerTests.cs             # MODIFIED: runner threads directional signals; null source = no-op
tests/Radar.Application.Tests/Scoring/
  (existing scoring tests)                # MODIFIED: directional GuidanceChange moves Trajectory; v3 stamp
tests/Radar.Infrastructure.Tests/Sec/
  SecEdgarFilingCollectorTests.cs         # MODIFIED (only if the items metadata key is added)
```

`Radar.Domain` is unchanged (`GuidanceChange`/`Positive`/`Negative`/`Neutral`, `FilingDirection`,
`FilingSentiment` all already exist). No DB (AD-8).

---

## Implementation details

### `DirectionalFilingSignalSource` (Infrastructure, internal)

- Inject `ISecEarningsReleaseReader`, `IFilingAnalyzer`, `DirectionalFilingSignalOptions`, and
  `ILogger<DirectionalFilingSignalSource>`. Guard-clause null-check all (mirror the reader/analyzer style).
- `ProduceAsync`:
  1. From `candidateEvidence` (already `Filing`-typed), keep those that are **earnings 8-Ks**
     (`form == "8-K"` from metadata AND item list contains `2.02`) AND whose CIK+accession parse from the
     index `SourceUrl`. Order deterministically (e.g. `ObservedAtUtc` desc, `Id`), take at most
     `MaxFilingsPerRun`.
  2. Sequentially, for each: `ReadAsync(cik, accession, ct)`; if not `Success` → skip (log Debug/Warning).
     `AnalyzeAsync(result.PlainText, ct)` → `FilingSentiment`.
  3. Apply the **gate + mapping** above. On a directional result, build an `ExtractedSignal`
     (`GuidanceChange`, `Positive`/`Negative`, AI confidence, Strength/Novelty constants, verbatim excerpt
     from the evidence, `Reason` = the analyzer rationale) and add `DirectionalFilingSignal(signal,
     evidence)` to the output.
  4. Never throw except caller cancellation; wrap per-filing work so one bad filing cannot abort the batch.
- All HTTP/AI specifics stay behind the injected `ISecEarningsReleaseReader`/`IFilingAnalyzer` (AD-5) — this
  class contains no provider SDK and no `HttpClient`.

### `DirectionalFilingSignalOptions` (Infrastructure, internal)

```csharp
internal sealed class DirectionalFilingSignalOptions
{
    public decimal MinConfidence { get; init; } = 0.6m;   // gate: below this -> no directional signal
    public int MaxFilingsPerRun { get; init; } = 5;       // cost cap: analyze at most N filings per run
    public int Strength { get; init; } = 6;               // in-range; clears reviewer MinMaterialStrength
    public int Novelty  { get; init; } = 6;               // in-range; clears reviewer MinNovelty
}
```

### DI — `AddDirectionalFilingSignals`, wired inside the opt-in gate

- New additive Infrastructure method mirroring `AddRadarFilingAnalyzer`:
  `public static IServiceCollection AddDirectionalFilingSignals(this IServiceCollection services,
  DirectionalFilingSignalOptions options)`. `ArgumentNullException.ThrowIfNull(options)`; validate
  `MinConfidence ∈ [0,1]` and `MaxFilingsPerRun > 0` (fail fast with clear `Radar:Ai:MinConfidence` /
  `Radar:Ai:MaxFilingsPerRun` messages). Register `AddSingleton(options)` and
  `AddSingleton<IDirectionalFilingSignalSource, DirectionalFilingSignalSource>()`. It **depends on** the
  `ISecEarningsReleaseReader` (`AddSecEarningsReleaseReader`) and `IFilingAnalyzer` (`AddRadarFilingAnalyzer`)
  — do not re-register those here; wire them in the worker gate.
- In `RadarWorkerServices.AddRadarWorker`, **inside** the existing `if
  (!string.IsNullOrWhiteSpace(options.Ai.Provider))` block (after `AddRadarFilingAnalyzer`), add:
  `services.AddSecEarningsReleaseReader(new SecCollectorOptions { UserAgent = options.Sec.UserAgent, … });`
  and `services.AddDirectionalFilingSignals(new DirectionalFilingSignalOptions { MinConfidence =
  options.Ai.MinConfidence, MaxFilingsPerRun = options.Ai.MaxFilingsPerRun });`. When `Provider` is blank,
  none of these run — the runner's optional `IDirectionalFilingSignalSource` is `null` and the step is
  skipped.
- Register the runner (`AddRadarPipeline`) so it resolves the optional dependency (nullable ctor param with
  a default, or `GetService<IDirectionalFilingSignalSource>()`); the existing `AddRadarPipeline` must keep
  working when the service is absent.

### Config surface (AI disabled by default; document both providers)

Add to `AiWorkerOptions` (`RadarWorkerOptions.cs`): `public decimal MinConfidence { get; init; } = 0.6m;`
and `public int MaxFilingsPerRun { get; init; } = 5;`. Add to the `Radar:Ai` block in `appsettings.json`:
`"MinConfidence": 0.6, "MaxFilingsPerRun": 5`. **Keep `Provider: ""` (AI DISABLED).** Extend the `_comment`
to document that directional filing signals require AI enabled, via **either**:
- **Ollama** (keyless/local): set `Provider: "ollama"`, `Model` to an installed tag (e.g. `"llama3.1"`),
  ensure the model is pulled and Ollama is running at `Ollama:Endpoint`;
- **Anthropic**: set `Provider: "anthropic"`, `Model` (e.g. `"claude-opus-4-8"`), and
  `Anthropic:ApiKey` (or `ANTHROPIC_API_KEY`).
Do **not** hard-code any model or key.

---

## Tests

### `DirectionalFilingSignalSourceTests` (Infrastructure.Tests/Filings — OFFLINE: fake reader + fake analyzer)

Implement a `FakeSecEarningsReleaseReader : ISecEarningsReleaseReader` and a `FakeFilingAnalyzer :
IFilingAnalyzer` (both `internal`; the csproj already has `InternalsVisibleTo` for the test project) that
return scripted results and count calls. Build earnings-8-K `Filing` `EvidenceItem`s with a real index
`SourceUrl` and Title carrying `[items: 2.02,…]`. Cases:

1. **Improving @ high confidence → one Positive `GuidanceChange`** referencing the filing evidence
   (`Direction == Positive`, `Confidence ==` the fake's confidence, excerpt is a verbatim `Contains` of the
   evidence searchable text, `Reason` carries the rationale). Round-trips valid via
   `ExtractedSignalMapper.ToSignal`.
2. **Deteriorating @ high confidence → one Negative `GuidanceChange`.**
3. **Below `MinConfidence` → NO directional signal** (empty output); reader/analyzer may still have been
   called, but no signal is produced (the deterministic Neutral, produced elsewhere, is unaffected).
4. **`Mixed` and `Unknown` (each) → no directional signal**, regardless of confidence.
5. **Reader `NoEarningsExhibit` / `Forbidden` / `Timeout` (Theory) → no signal, no throw**; the analyzer is
   not called for a non-`Success` read (assert analyzer call-count 0 for that filing).
6. **Analyzer failure path → no signal, no throw.** (Analyzer returns `FilingSentiment.Unknown` — spec 74
   guarantees it never throws — assert no directional signal.)
7. **Non-earnings filing (no `2.02`) is not analyzed** (reader call-count 0); a `Filing` with `2.02` is.
8. **Per-run cap honoured.** More earnings-8-K candidates than `MaxFilingsPerRun` → at most
   `MaxFilingsPerRun` reads/analyses occur (assert the fake reader call-count).
9. **Caller cancellation propagates.** An already-cancelled token → `ProduceAsync` throws
   `OperationCanceledException`.
10. **CIK/accession parse.** The fake reader captures the `(cik, accession)` it was handed; assert they
    equal the values embedded in the evidence index `SourceUrl` (CIK zeros stripped, dashed accession).

### `RadarPipelineRunnerTests` (Application.Tests/Pipeline)

11. **Runner threads directional signals through the standard path.** With a fake
    `IDirectionalFilingSignalSource` returning one `DirectionalFilingSignal` over an in-window earnings-8-K
    evidence, the run stores a `Positive` `GuidanceChange` signal (resolved/reviewed/persisted like keyword
    signals) and the evidence→signal provenance holds.
12. **Null source = no-op (default).** With the optional source `null` (AI disabled), the run behaves
    exactly as today — no directional signal, existing runner assertions unchanged. (Guards the byte-for-byte
    default.)

### Scoring (Application.Tests/Scoring)

13. **Directional GuidanceChange moves Trajectory the right way.** A window whose only directional signal is
    a `Positive` `GuidanceChange` (paired with `Filing` evidence) scores `TrajectoryScore > 50`; a
    `Negative` one scores `< 50`; the signal passes `SignalValidation`. A Neutral-only window still scores
    `TrajectoryScore == 50` (spec-57 behaviour intact).
14. **`ScoringConfigVersion` stamp.** Update the assertion to expect `"radar-scoring-config-v3"`.

### Worker graph / DI opt-in

15. **AI disabled (blank `Radar:Ai:Provider`) → no `IDirectionalFilingSignalSource`/`ISecEarningsReleaseReader`
    registered; runner's optional dep is null; default graph unchanged.** `Provider = "ollama"` (+ `Model`) →
    both are registered and the runner resolves the source.
16. **`AddDirectionalFilingSignals` fail-fast:** `MinConfidence` outside [0,1] or `MaxFilingsPerRun <= 0`
    throws `InvalidOperationException` with the documented `Radar:Ai:*` message; `null` options throws
    `ArgumentNullException`.

### (If the `items` metadata key is added) `SecEdgarFilingCollectorTests`

17. An 8-K with `items = "2.02,9.01"` produces evidence whose `MetadataJson` contains `"items":"2.02,9.01"`;
    Title/RawText (and thus `ContentHash`) are unchanged.

All emitted text is advice-free (rationale already scrubbed in spec 74; re-affirm no banned tokens surface).
Existing collector/runner/DI/scoring/report tests stay green.

---

## Spec-implementation checklist

1. **Code paths replaced:** none removed. The deterministic Neutral `GuidanceChange` (spec 57) is
   unchanged and coexists (contributes 0 to Trajectory). The runner gains an opt-in step; the keyword
   extractor is untouched.
2. **Tests:** add the source cases (1–10), the runner cases (11–12), scoring (13–14), DI opt-in (15–16),
   and the collector metadata case (17, if added); update the `ScoringConfigVersion` assertion.
3. **Delete nothing still used.**
4. **CLAUDE.md / architecture-decisions.md:** no new architecture rule; this slice realises the arc AD-11
   reserved and follows AD-5 (AI/HTTP in Infra), AD-6 (formula unchanged), AD-8 (files-first), AD-10
   (`ScoringConfigVersion` bump). No new AD entry required — note this in the PR. Update the CLAUDE.md
   "sub-agents"/pipeline notes only if the runner's new stage warrants a one-line mention.

---

## Constraints

- Target `net10.0`, C# 14.
- **AD-5:** all HTTP/AI/provider specifics stay behind the merged Infrastructure interfaces
  (`ISecEarningsReleaseReader`, `IFilingAnalyzer`); the runner depends only on the Application interface
  `IDirectionalFilingSignalSource`. No provider SDK outside Infrastructure.
- **Deterministic-before-AI preserved:** the keyword extractor and scoring formula are unchanged; AI only
  ADDS directional filing reads.
- **Typed + validated + confidence-gated (CLAUDE.md):** directional signals only at/above `MinConfidence`;
  every emitted signal passes `ExtractedSignalMapper.ToSignal`/`SignalValidation`; all fields in domain
  range.
- **Provenance preserved:** each directional signal references the filing `EvidenceItem`
  (evidence→signal→score trace); verbatim `SupportingExcerpt` survives the mapper's excerpt-in-evidence check.
- **Never emit advice language** (rationale already scrubbed in spec 74; re-affirm — no
  "buy"/"sell"/"guaranteed upside"/"safe bet"/price targets in `Reason` or excerpt).
- **Graceful degradation:** every reader/analyzer failure → no directional signal for that filing, never
  aborts the run; only caller cancellation propagates. Sequential + polite; per-run cap honoured.
- **Files-first, no DB (AD-8).**
- **Opt-in:** registered only inside the `Ai.Provider`-non-blank gate; with AI disabled the default graph
  and pipeline are byte-for-byte unchanged (no network, no cost).
- **Bump `ScoringEngine.ScoringConfigVersion`** to `"radar-scoring-config-v3"` (AD-10).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Acceptance criteria

- [ ] A new opt-in `IDirectionalFilingSignalSource` (Application interface; Infrastructure implementation)
      is invoked by `RadarPipelineRunner` as an enrichment step **after** the deterministic extract loop and
      **before** scoring, threading each produced signal through the SAME
      `ExtractedSignalMapper.ToSignal → resolve → review → store signal + review + file` path as keyword
      signals (provenance preserved; the deterministic `KeywordSignalExtractor` is untouched).
- [ ] For an **in-scoring-window earnings-8-K `Filing`** evidence (`form == "8-K"` + item `2.02`), the
      source parses CIK + dashed accession from the index `SourceUrl`, calls `ISecEarningsReleaseReader`, and
      on `Success` calls `IFilingAnalyzer`; a **non-earnings** (no `2.02`) or **out-of-window** filing is
      never fetched/analyzed.
- [ ] **Confidence-gated directional mapping:** `Improving` @ `Confidence ≥ MinConfidence` → one **`Positive`**
      `GuidanceChange`; `Deteriorating` @ `≥ MinConfidence` → one **`Negative`** `GuidanceChange`; below
      `MinConfidence` → **no directional signal** (the deterministic Neutral stands); `Mixed`/`Unknown` → no
      directional signal. The signal carries the AI confidence, in-range Strength/Novelty, a verbatim
      evidence `SupportingExcerpt`, and the advice-scrubbed rationale in `Reason`; it passes
      `ExtractedSignalMapper.ToSignal`/`SignalValidation`.
- [ ] **Graceful degradation & cost control:** any reader failure (`NoEarningsExhibit`/`Forbidden`/
      `HttpError`/`Unreachable`/`Timeout`/`Malformed`) or analyzer `Unknown`/`Mixed`/low-confidence result →
      **no directional signal for that filing, no throw**; the run is never aborted (only caller cancellation
      propagates). Analysis is strictly sequential and capped at `MaxFilingsPerRun` per run.
- [ ] **Opt-in / no default change:** the source (and `AddSecEarningsReleaseReader`) are registered **only**
      inside the existing `Ai.Provider`-non-blank gate; with AI disabled (default `Provider: ""`) no
      directional component is registered, the runner's optional dependency is `null`, the step is skipped,
      and the default graph/pipeline is **byte-for-byte unchanged** (no network, no AI, no cost) — asserted
      by a worker-graph test.
- [ ] **`ScoringEngine.ScoringConfigVersion` is bumped** `"radar-scoring-config-v2"` → `"radar-scoring-config-v3"`
      (AD-10), the comment records spec 75, and any test asserting the old value is updated; `ScoringVersion`/
      `EngineVersion`/formula `Version` are unchanged.
- [ ] A directional `Positive` `GuidanceChange` moves `TrajectoryScore > 50` and a `Negative` one `< 50`
      (with matching `ScoringConfigVersion`), while a Neutral-only window still scores `50` — asserted by a
      scoring/validation test.
- [ ] Offline tests (fake `ISecEarningsReleaseReader` + fake `IFilingAnalyzer`, no network/model) cover:
      Improving→Positive, Deteriorating→Negative, below-`MinConfidence`→none, Mixed/Unknown→none, reader
      failure→none, analyzer failure→none, non-earnings/out-of-window not analyzed, cap honoured,
      CIK/accession parse, caller-cancellation; plus runner threading, null-source no-op, DI opt-in +
      fail-fast. No advice language surfaced.
- [ ] `dotnet build`/`dotnet test` on `Radar.sln -c Release` are green. This **COMPLETES** the
      directional-filing arc (seam → reader → analyzer → **this signal**). Live running requires `Radar:Ai`
      enabled: **Ollama** (keyless/local, model pulled) or **Anthropic** (`ANTHROPIC_API_KEY`) — both
      documented in the `appsettings.json` `_comment`; no model/key hard-coded.
```