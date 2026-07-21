# Task: Switch the default baseline earnings-read model to DeepInfra DeepSeek-V4-Flash

> **BASELINE MODEL SWITCH.** Follows the 2026-07-21 EOSE A/B (spec 118 provider): DeepSeek-V4-Flash correctly
> read EOSE's reported −70% gross margin as `Mixed 0.85` (llama3.1 read `Improving 0.90`, ignoring the margin —
> spec-116's prompt fix could not move the 8B model), and additionally caught that AEHR's *reported* quarter
> deteriorated (revenue $59M→$50M, GAAP net loss $(3.9)M→$(7.1)M) → `Mixed 0.75` where llama3.1 said `Improving`.
> The AEHR post-earnings price (popped to ~$110 on 07-15, faded to ~$77 by 07-20) independently corroborates the
> more cautious read (AD-14: price is validation-only, never a scoring input — cited as evidence for the switch,
> NOT wired into scoring). This makes DeepSeek the baseline earnings reader and drops the deterministic-margin-guard
> idea entirely (a capable model generalizes to margin, net-loss, and revenue-decline, not one red flag at a time).

## Overview

The DeepInfra / OpenAI-compatible provider shipped as an opt-in enabler (spec 118, default stayed Ollama). This
slice makes it the **default baseline** earnings reader, and — because the model materially changes which
directional signals are produced — folds the earnings-read model identity into the scoring fingerprint so runs
stay comparable (spec 69/95 comparability invariant) and the efficacy score line segments correctly at the model
boundary.

## Assignment

Worktree: any
Dependencies: 118 merged (the `openai` provider + model-in-cache-key). Independent of other queued work.
Estimated time: ~1–2 hours

## Project structure changes

Modify:
- `scripts/run-profiles/default.json` — change the `Ai` block from Ollama to the OpenAI-compatible provider:
  ```json
  "Ai": {
    "Provider": "openai",
    "Model": "deepseek-ai/DeepSeek-V4-Flash",
    "OpenAi": {
      "BaseUrl": "https://api.deepinfra.com/v1/openai",
      "Model": "deepseek-ai/DeepSeek-V4-Flash",
      "ApiKeyEnvVar": "DEEPINFRA_API_KEY"
    }
  }
  ```
  Update the profile `_comment`: the baseline earnings read is now DeepSeek-V4-Flash on DeepInfra; the key VALUE
  is never in the file (`ApiKeyEnvVar` names `DEEPINFRA_API_KEY`, read at runtime, like the SEC User-Agent); record
  the new AI-ON fingerprint (see below).
- **Fingerprint: fold the earnings-read model + provider identity into the spec-106 directional-filing descriptor.**
  The descriptor currently hashes `str/nov/minconf`; add the effective model id (and provider) so a model change
  re-stamps `ScoringConfigVersion` **by value** (exactly as spec 95 folded the collector set and spec 96 the
  insider tiers). Rationale: the model changes signal DIRECTION (Improving vs Mixed), so it is a scoring-affecting
  input like `RuleSetVersion` — leaving it out would let two runs with different models (and different AEHR scores,
  62 vs 38) share one fingerprint, breaking comparability and mis-drawing the efficacy line as continuous across a
  real change. Only the **AI-ON** fingerprint moves (AI-OFF has no directional path, so unchanged). Re-pin
  `ScoringConfigFingerprintTests` and record the new default AI-ON stamp (was `radar-scoring-fp-4c06fd2d2d8c`).
- Fail-loud on a missing key: confirm the spec-118 DI fail-fast fires when `Provider=openai` and `DEEPINFRA_API_KEY`
  is unset (name the env var, never the value), and that `run-radar.ps1` surfaces it clearly — mirror the existing
  SEC-User-Agent placeholder warning so a keyless baseline run fails loudly rather than silently degrading.
- `docs/next → docs` promotion aside, update the `radar-live-run` guidance (the live-run how-to) to note the
  baseline now requires `DEEPINFRA_API_KEY` in the environment and uses DeepSeek-V4-Flash.

Add (see Implementation details for the hard "write-don't-execute" constraint):
- `scripts/run-baseline-scheduled.ps1` — scheduled-run wrapper (loads the key from a `-KeyFile` path into
  `$env:DEEPINFRA_API_KEY`, sets the SEC UA, runs `run-radar.ps1` default; fails loud on a missing key).
- `scripts/setup-baseline-task.ps1` — parameterized `Register-ScheduledTask` helper for `RadarBaselineDaily`
  (written, never executed by the coder; the maintainer runs it once with elevation).

## Implementation details

- **No formula / RuleSetVersion change.** This is a config/profile switch plus a fingerprint-descriptor widening
  (model id folded in). `_formula.Version` unchanged (`radar-formula-v7`); `RuleSetVersion` unchanged. The AI-ON
  fingerprint re-stamps by value; AI-OFF unchanged. The efficacy line segments at the model boundary (spec 108
  continuity-segmentation renders it cleanly).
- **Secret hygiene.** The key is only ever the env var value at runtime — never in `default.json`, never committed,
  never logged as a value (only the env-var name). Same precedent as the SEC User-Agent.
- **Analyzed-filing cache.** Already keyed by model (spec 118), so the switch re-reads every earnings filing on
  DeepSeek rather than replaying llama3.1 verdicts — no manual cache bust needed.
- **Scheduled baseline task — codify the setup in repo scripts (the coder WRITES them, does NOT run them).** The
  scheduled `RadarBaselineDaily` task needs `DEEPINFRA_API_KEY` in its environment or its AI path fails-loud. Add:
  - `scripts/run-baseline-scheduled.ps1` — the wrapper the task invokes: takes a **`-KeyFile` path parameter** (so
    no machine-specific path and no key VALUE is committed), reads it into `$env:DEEPINFRA_API_KEY`, takes/sets the
    SEC User-Agent (`-SecUserAgent`/`$env:RADAR_SEC_UA`), then calls `run-radar.ps1` (default profile). Fails loud
    if the key file is missing/empty (never echo the value).
  - `scripts/setup-baseline-task.ps1` — a parameterized `Register-ScheduledTask` helper that (re)points
    `RadarBaselineDaily` at the wrapper (daily 09:00, passing `-KeyFile`/`-SecUserAgent` through). **The maintainer
    runs this once, with elevation** — it is the single machine-mutating step, kept out of the pipeline.
  **HARD CONSTRAINT (unattended coder):** WRITE these scripts only — do **NOT** execute them, do **NOT**
  register/modify any Windows scheduled task, and do **NOT** write the API key value anywhere (no `setx`, no
  committed key, no key in logs). They are inert repo artifacts; the maintainer performs the one-time registration.
- Ollama remains a supported provider (the `ollama` branch is untouched) — this only changes the *default profile's*
  choice, so a maintainer can still run `-Profile`-override back to local Ollama.

## Tests

- The `default` profile resolves to `Provider=openai` + `deepseek-ai/DeepSeek-V4-Flash` + the DeepInfra BaseUrl +
  `ApiKeyEnvVar=DEEPINFRA_API_KEY` (a profile-resolution/`-WhatIf` assertion; no live call in unit tests).
- `ScoringConfigFingerprintTests`: the AI-ON default fingerprint re-stamps to the new pinned value; the directional
  descriptor now includes the model id; recompute-from-`EffectiveScoringConfig` matches. AI-OFF unchanged.
- Keyless `Provider=openai` fails fast with a message naming `DEEPINFRA_API_KEY` (value never interpolated).
- Ollama and Anthropic provider paths unchanged (regression).

## Constraints

- Target .NET 10 / `net10.0`, C# 14. Provider SDK stays in Infrastructure (AD-5).
- Config/profile + fingerprint-descriptor change only — no `_formula.Version` / `RuleSetVersion` bump.
- Price stays validation-only (AD-14) — it justified the switch but is never a scoring input.
- Key via env var only; never committed or logged as a value.

## Acceptance criteria

- [ ] `default.json` uses the DeepInfra `openai` provider with `deepseek-ai/DeepSeek-V4-Flash`; key via
      `DEEPINFRA_API_KEY` env var (never in the file).
- [ ] Earnings-read model+provider folded into the directional-filing fingerprint descriptor; AI-ON default
      fingerprint re-stamped and re-pinned (AI-OFF unchanged); no formula/RuleSetVersion bump.
- [ ] Keyless `openai` run fails loud (env-var name surfaced, value never); Ollama/Anthropic unchanged.
- [ ] `radar-live-run` guidance updated (baseline needs `DEEPINFRA_API_KEY`, uses DeepSeek-V4-Flash).
- [ ] `scripts/run-baseline-scheduled.ps1` + `scripts/setup-baseline-task.ps1` added (key via `-KeyFile`, no
      machine-specific path or key value committed); the coder did NOT execute them, registered NO scheduled task,
      and wrote the key value nowhere. Maintainer runs `setup-baseline-task.ps1` once (elevation) to finish setup.
- [ ] LIVE re-measure (maintainer, key set): the baseline reproduces the A/B — EOSE `Mixed`/no-signal, AEHR `Mixed`,
      JNJ `Improving` (v7-discounted); board matches `data/experiments/deepinfra/`.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
