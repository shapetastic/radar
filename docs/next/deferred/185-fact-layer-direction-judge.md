# Task: Stage-2 direction judge — weigh canonical fact families, qualify the leaders

> ⚠️ **DEFERRED — dispatch after spec 181 (stage-1 fact layer) is implemented and its first audited-sample
> recall/citation numbers exist.** Drafted BEFORE 181 is built, deliberately: this is the consumer contract
> the fact layer must satisfy, so 181 cannot ship a fact shape nothing can consume. Changes here that alter
> the required fact shape must be reflected in 181 before either is dispatched.

## Overview

Spec 181 produces typed, citation-validated, attribution-aware facts with same-event family ids and no
verdicts. This spec adds the judge: a model call that receives ONLY canonical fact/event families — never
raw article prose — and produces directional risk/support judgments citing FactIds, extending provenance to
**judgment → fact → excerpt → observation → archive**.

Why the judge sees no prose: every motivating misread (llama/EOSE "Improving", DeepSeek/CASS "Positive") was
narrative framing contaminating judgment while the facts sat in context. Headlines are engineered prose;
typed facts with preserved comparisons/negation/modality/attribution are not. Why families, not articles:
syndication volume must not reach the judge as repetition — two StocksToTrade variants of one legal-scrutiny
story are ONE claim, or the 40-outlets problem is reborn at the judgment seam.

## 1. Consumer contract (what 181 must deliver)

The judge consumes, per company per checkpoint:

- canonical fact/event FAMILIES (one representative fact per family, family size and distinct-publisher
  count as metadata — countable by the judge as corroboration of REPORTING, never as N independent facts);
- each family's `EventTypes`, `Statement`, `TemporalScope`, `Attribution`, `AssertionStatus`, `Confidence`,
  `Citations`;
- nothing else: no raw text, no Radar score/rank/label, no price, no prior judgment.

Any fact-shape change this spec needs is an amendment to 181 §2, made before either spec is dispatched.

## 2. Judgment schema

Per company per checkpoint, closed result:

```text
Verdict            ThesisChallenged | ThesisSupported | Mixed | NothingMaterialInFacts | InsufficientFacts
RiskScore          0..100 | null
Findings[]         each: direction (Challenges | Supports), category (the spec-179 risk taxonomy, reused),
                   severity, confidence, supporting FactIds (≥1, must exist in the supplied set),
                   and an attribution caveat when every supporting fact is below `reported`
                   AssertionStatus (an alleged/solicited-only finding must say so)
Rationale          bounded, factual, no investment instruction
```

Mechanical validation mirrors 179 §6: every cited FactId supplied; enums/ranges valid; advice-language guard;
a challenged/supported verdict needs ≥1 surviving finding; invalid findings dropped and counted. Attribution
weighting is a PROMPT rule, not post-hoc: a plaintiff-firm solicitation is a weaker basis than a confirmed
filing, and "may face" is weaker than "was charged" — the judge must use `AssertionStatus`, which is why 181
carries it.

## 3. Cohorts and the A/B against the single-call read

- Cache/cohort identity: judge model + prompt/schema version + the ordered input fact-family hash + the
  upstream stage-1 cohort identity (extractor model/prompt/taxonomy). A stage-1 change is a new stage-2
  cohort by construction.
- The two-stage pipeline runs as a NEW cohort BESIDE the spec-179 single-call read over the same candidates
  (both bounded by the existing caps) and is judged by measurement, not assertion: citation-drop rates,
  category agreement with the single-call read and between readers, and the newly-localizable
  extraction-vs-judgment error split against the §181 audited sample.
- The asymmetric split (cheap local extractor + stronger judge; also strong extractor + local judge) are
  additional cohorts — this is where the "is a local model good enough per stage?" question gets its answer.
- Reader/cohort discipline is spec 179's verbatim: independent, never pooled, no merged verdict.

## 4. Qualifying the leaders — the first diagnostic allowed to touch presentation

The end-to-end acceptance case, stated as the EOSE chain: facts extracted and attributed → duplicated legal
stories collapse to one family → the judge records a thesis challenge → **EOSE cannot render as an
unqualified leader.**

Concretely: the live-strategy-leaders section (spec 176) gains an optional qualifier column/marker per row —
e.g. `⚠ news-risk: ThesisChallenged (see news-risk artifact)` — sourced from the same run's judge output,
with a link to the artifact. Rules:

- It is display METADATA: no score, rank, ordering, label or stored snapshot changes; the row's numbers are
  untouched. It is the spec-179 §7 "later small link" made real, coordinated with (not blocked on) the
  strategy-lifecycle spec.
- Only judge verdicts from the SAME run qualify a row (no stale carryover); a missing/failed judgment
  renders no marker, never a clean one.
- The marker's absence is not an all-clear, and the section's honesty line says so (spec 182's doctrine:
  absence claims need completeness; this marker only ever asserts presence).
- The weekly report's action labels (`WeeklyReportActionPolicyV1`) are untouched — the marker is not a label
  and appears in no label position.

## 5. Out of scope, recorded not built

- Any score/formula/strategy consuming judgments (a future named strategy, prospectively declared).
- Auto-demotion/promotion of strategies or companies (the lifecycle spec owns governance).
- Backfill judgment of the 13k legacy cohort (bounded, after the live path is measured).
- Changing 179's single-call read (it stays as the control cohort).

## Acceptance criteria

- [ ] The judge receives canonical fact families only — no prose, no score, no price — and every finding
      cites supplied FactIds; the provenance chain judgment → fact → excerpt → observation → archive resolves
      end to end.
- [ ] Syndicated duplicates reach the judge as one family with size metadata; family size never multiplies
      findings.
- [ ] Attribution/assertion status demonstrably changes judgments (alleged-only vs confirmed-filing fixtures
      produce different severity/caveats).
- [ ] The EOSE end-to-end case passes: extracted facts → collapsed families → recorded thesis challenge →
      qualified (never unqualified) leader row, with no score/rank/label change.
- [ ] Two-stage and single-call cohorts run side by side and never pool; the error split is reported per
      stage against the audited sample.
- [ ] Build and coordinated tests green.
