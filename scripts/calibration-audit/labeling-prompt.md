# Spec 162 — blinded filing-read labeling prompt (protocol cal-v2)

> This file is the `promptHash` source: every label's `protocol.promptHash` is the SHA-256 of this file's
> bytes, so a mid-study edit of this template is visible in the label provenance. **Precommitted second
> reader for the whole study: `anthropic:claude-fable-5`, invoked as the `radar-skeptic-reviewer` agent —
> a different model family from the DeepSeek production reader.** Recording `protocol.labeler` per batch
> detects drift; precommitting prevents it. Changing the labeler (or materially editing this template)
> mid-study is a protocol-version bump (`cal-v2` → `cal-v3`) that RESTARTS the affected labels.

---

## Prompt template

You are a blinded second reader in a calibration study of an AI earnings-release analyst. You are
deliberately given nothing but the filing itself — judge only what the text supports.

**You receive ONLY:**

- Company: `{company}`
- CIK: `{cik}`
- Accession: `{accession}`
- Exhibit text file (local path): `{modelInputPath}`

That file contains the EXACT text the production reader saw (its input is truncated to a fixed character
cap; yours is truncated identically). **Read that one local file and nothing else. Do not read any other
local file. Do not access the web, EDGAR, news, or price data. Do not use any prior knowledge of what any
other model concluded about this filing.** If the text appears cut off mid-sentence, judge on what is
present and flag the doubt in `keyFacts` (e.g. `"input appears truncated before guidance section"`).

**Your task — judge the release AS REPORTED (this is not a beat-vs-consensus judgement):**

1. `direction` — the business trajectory the release describes, exactly one of:
   - `Positive` — reported results/outlook describe an improving trajectory.
   - `Negative` — reported results/outlook describe a deteriorating trajectory.
   - `Mixed` — materially two-sided (e.g. record top line alongside deteriorating margin, a guidance cut,
     or heavy cash burn — or a headline decline explained by a one-off with underlying improvement).
   - `Neutral` — no material directional read (boilerplate, in-line, or no reported results).
2. `directionConfidence` — your confidence in that direction, in [0,1].
3. `comparisonClean` — `true` only when the headline year-over-year comparison is CLEAN: no one-time
   items, tax swings, acquisitions/divestitures, perimeter or accounting changes materially distorting it.
4. `comparabilityItems` — every item that breaks or distorts comparability, one short string each,
   with amounts when stated (e.g. `"$11.9M discrete tax release"`, `"Jolt acquisition inflates revenue"`,
   `"RHB deconsolidation"`, `"prior-year $27.2M IPR&D charge"`). Empty array when genuinely clean.
5. `material` — how much the reported change matters at the company's scale: `low` / `moderate` / `high`.
6. `keyFacts` — 1–5 short strings: the decisive reported facts (with numbers), plus any
   identification/parity/truncation doubts you have about the input itself.

**REIT framing note (apply when the company is a REIT):** judge the trajectory on FFO/AFFO (and same-store
metrics) rather than GAAP EPS — GAAP depreciation makes EPS uninformative for REITs. Note in `keyFacts`
which measure you judged on.

**Output — exactly one JSON object, no prose, matching the canonical label schema (label body only; the
harness wraps it with `accession`/`protocol`/`adjudication`):**

```json
{
  "direction": "Positive|Negative|Mixed|Neutral",
  "directionConfidence": 0.0,
  "comparisonClean": false,
  "comparabilityItems": ["..."],
  "material": "low|moderate|high",
  "keyFacts": ["..."]
}
```

Never include advice language ("buy", "sell", price targets). Judge only the reported facts.

---

## Operational rules (for the dispatching session, not the labeler)

- Blinding is STRUCTURAL: the labeler gets the model-input file path and the four fields above — never the
  worksheet, never the cache, never another model's answer, never the full (untruncated) exhibit.
- ≤ 5 concurrent labeling agents (the pilot's 20-at-once drew a 529 wave). Labeling agents make ZERO SEC
  requests — every exhibit was archived beforehand by the `Radar.CalibrationAudit` console.
- Batches follow SHA-256(accession) hex ascending order (the worksheet's order).
- Every label is wrapped with the full provenance block (`protocol.version = "cal-v2"`, labeler
  provider/model, this file's hash as `promptHash`, UTC timestamp, attempt number); retries carry
  `attempt: n+1` and `replacedLabelOfAttempt: n`.
