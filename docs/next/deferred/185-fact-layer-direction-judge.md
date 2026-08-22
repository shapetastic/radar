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

- canonical fact/event FAMILIES from 181 §4's versioned deterministic family builder (one representative
  fact per family, family size and distinct-publisher count as metadata — countable by the judge as
  corroboration of REPORTING, never as N independent facts);
- each family's `EventTypes`, `Statement`, `TemporalScope`, `Attribution`, `AssertionStatus`, `Confidence`,
  `Citations`;
- nothing else: no raw text, no Radar score/rank/label, no prior judgment, and **no independently joined
  price series or future returns** — contemporaneously REPORTED price-movement facts (a `MarketReaction`
  family: "shares fell 11.8%") remain available, because they are part of what the news said at the cutoff,
  not a look-ahead.

Any fact-shape change this spec needs is an amendment to 181 §2, made before either spec is dispatched.

## 2. Judgment target and schema — the thesis is FIXED, the warning is POLICY

**The evaluation target is stated in the prompt verbatim and never varies per company: "the company's recent
business trajectory," Radar's founding question.** The judge receives no per-company thesis, score or label
to defend, so there is nothing circular to confirm — it reads facts against one fixed rubric.

**v1 findings are CHALLENGE-ONLY, deliberately.** The spec-179 risk taxonomy categorizes challenges well and
supportive findings not at all (it has no "customer win" bucket), and a single score cannot faithfully carry
"supported" and "challenged" at once. Rather than invent a support taxonomy nothing consumes yet, v1 keeps
the thesis-breaker shape and adds ONE factual axis for balance:

```text
BusinessTrajectory   Improving | Deteriorating | Mixed | Unknown     (factual read over the families)
ChallengeStrength    0..100 | null                                    (null when no findings survive)
Findings[]           challenge-only; each: category (spec-179 risk taxonomy), severity, confidence,
                     supporting FactIds (≥1, must exist in the supplied set), and an attribution caveat
                     whenever every supporting fact is below `reported` AssertionStatus
                     (an alleged/solicited-only finding must say so)
Rationale            bounded, factual, no investment instruction
```

There is no `ThesisSupported` verdict and no support score in v1: `BusinessTrajectory=Improving` with zero
findings IS the supportive read, expressed factually. The §4 leader marker is DERIVED BY POLICY from this
result — the model never chooses presentation. A future support-finding taxonomy is out of scope until
something consumes it.

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

Concretely: the live-strategy-leaders section (spec 176) gains a **mandatory semantic-read status on EVERY
leader row** — a marker whose absence is impossible, because silent ignorance is the failure this whole arc
exists to end. The three states, derived by policy from the presentation cohort's result:

```text
⚠ challenged        ≥1 validated finding survived (severity/category summarized; links to the artifact)
? unassessed        judgment unavailable/incomplete for ANY reason — no facts, extractor/judge/provider
                    failure, validation failure, stale run — the reason token rendered
· no challenge found in supplied facts    narrowly worded, only from a completed validated judgment;
                    never rendered as clean/safe, and the honesty line says so
```

Rules:

- **A missing or failed judgment renders `? unassessed` with its reason — never nothing.** An absent marker
  is not a state; every row carries exactly one of the three.
- **The presentation cohort is DESIGNATED, prospectively**: config names ONE reader cohort (e.g. the
  DeepSeek two-stage cohort) as the marker source, `PairedPrimaryStrategy`-style — declared before results,
  never switched after seeing them. Other cohorts render in the artifact, never in the marker; cohorts still
  never pool.
- Only same-run judgments qualify a row (no stale carryover — a prior run's verdict renders `? unassessed
  (stale)`).
- It is display METADATA: no score, rank, ordering, label or stored snapshot changes; the row's numbers are
  untouched. It is the spec-179 §7 "later small link" made real, coordinated with (not blocked on) the
  strategy-lifecycle spec.
- The weekly report's action labels (`WeeklyReportActionPolicyV1`) are untouched — the marker is not a label
  and appears in no label position.

## 5. Execution, storage and failure states

- Runs in-process in the same post-run step as stage-1 typing (the 179 shadow-generator precedent), after
  the family builder, before the live artifact renders; bounded by the same candidate caps.
- Judgments persist under `data/news-risk/judgments/{judge-model-policy}/{companyId}/...` — one record per
  attempt (success, `InsufficientFacts`, provider failure, parse failure, validation failure), carrying the
  run id, stage-1 cohort identity, family-set hash, raw-response hash, status and validated result. The
  live artifact (the existing `data/news-risk/live/news-risk-{date}.{md,json}`, schema version bumped)
  gains the per-company judgment sections and the marker states.
- Completeness/failure vocabulary reuses spec 182's three capture/search/bundle dimensions verbatim plus the
  judgment's own status — nothing here re-invents coverage language, and no combination of failures ever
  renders as clean.

## 6. Out of scope, recorded not built

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
- [ ] EVERY leader row renders exactly one of the three semantic-read states; a failed/missing/stale
      judgment renders `? unassessed` with its reason — an absent marker is unrepresentable.
- [ ] The presentation cohort is named in config prospectively; marker content comes only from it; other
      cohorts render in the artifact only.
- [ ] `BusinessTrajectory` is factual and judged against the one fixed rubric; findings are challenge-only
      v1; no support score exists; the model never selects a presentation state.
- [ ] Two-stage and single-call cohorts run side by side and never pool; the error split is reported per
      stage against the audited sample.
- [ ] Build and coordinated tests green.
