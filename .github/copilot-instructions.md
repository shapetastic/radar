# Copilot instructions for Project Radar

Radar surfaces public companies whose business trajectory may be improving before the market
notices. It is a **research assistant, not a trading bot or recommendation engine**.

> Signals before stories. Evidence before opinions. AI assists. Humans decide.

When reviewing changes, prioritise the rules below. The reference specs are
`docs/radar-full-pipeline-spec.md`, `docs/radar-schema-spec.md`, and
`.claude/agents/radar-philosophy.md`.

## Provenance (most important)
- Evidence is the source of truth and is **immutable** — never overwrite a stored `EvidenceItem`
  (e.g. flag indexer writes like `dict[id] = item` that can silently replace an existing record;
  prefer `TryAdd`/insert-only). Re-collection that changes content creates a new record.
- Signals must reference evidence (`Signal.EvidenceId` is a non-nullable `Guid`).
- Scores must trace back to contributing signals and evidence; reports must reference score
  snapshots and evidence. A score without evidence is invalid.

## Architecture & layering
- Project references: `Radar.Domain` → nothing; `Radar.Application` → Domain; `Radar.Infrastructure`
  → Application + Domain; `Radar.Worker` → Application + Infrastructure. **Nothing references Worker.**
  `Radar.Domain` and `Radar.Application` must stay free of third-party/provider packages.
- **No AI provider SDK may be called outside `Radar.Infrastructure`.** Use provider-independent
  application interfaces (e.g. `IAiStructuredOutputService`).
- Prefer deterministic code before AI. AI outputs must be typed records and **validated before
  persistence**; on low confidence, keep the evidence but do not create high-confidence signals.

## Conventions
- Target `net10.0` (C# latest); nullable + implicit usings on; `TreatWarningsAsErrors=true` — code
  must build warning-free.
- IDs are `Guid`; all timestamps are **UTC** (`DateTimeOffset`). Keep code deterministic where it
  claims to be (no `DateTime.Now`, no `Math.random`, no reliance on dictionary enumeration order —
  sort explicitly when order matters).
- Domain types are immutable `record`s transcribed from the schema spec; don't add fields not in the
  spec without reason.

## Output language (hard rule)
Radar must never produce financial advice. Flag any "buy", "sell", "guaranteed upside", or "safe
bet" wording. The only allowed action labels are: `Investigate`, `Watch`, `Needs more evidence`,
`Thesis improving`, `Thesis deteriorating`.

## Tests
- Every change keeps `dotnet build`/`dotnet test` green. New behaviour ships with meaningful tests
  (assert real behaviour and edge cases — nulls, empty input, duplicate evidence, unresolved
  companies — not just that a thing constructs).
