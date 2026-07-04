# Task: Attention source-tier calibration + publisher-name normalization (make spec 88's tiering actually differentiate)

> **VERSION + APPROVAL (read first).** This slice has **two parts**: (A) a genuinely-CODE change —
> robust **publisher-name normalization** in `ConfiguredAttentionSourceWeights` so the observed
> domain-form / punctuation variants (`marketscreener.com`, `simplywall.st`) resolve to the outlets
> already curated in the mill list; and (B) a **recalibration of the default tier map / unknown weight**
> (mostly config) so an all-aggregator name scores materially LOWER Attention than a genuinely-covered
> one. **MAINTAINER SIGN-OFF: GRANTED 2026-07-04 (authoritative — overrides "Proposed pending sign-off"
> references below).** The maintainer approved the calibration posture this session: the normalization
> approach + explicit `Simplywall.st` alias, the **denylist-expand + `UnknownWeight 0.5→0.25`** recommendation
> (the expanded mill list incl. Finviz/Investing.com/Benzinga/TipRanks/…), and the recalibrated defaults —
> ship them as **Accepted · 2026-07-04**, treat every "Proposed until sign-off" note as SATISFIED. This is a
> **directed** slice (the maintainer asked for it), **NOT** the generic planner loop, and **NOT**
> architecture-gated.
>
> **NO formula-shape change and NO new formula-version class.** The reach formula is unchanged — only the
> *weights* it reads (the tier map / unknown default) and the *matcher* that resolves a `SourceName` to a
> weight move. It therefore stays **`radar-formula-v5`** (post-89); do **not** introduce a `v6`.

> **STRICT SEQUENCING — implement AFTER spec 89 (`radar-formula-v5`) merges. Do NOT run in parallel.**
> Spec 89 (`docs/next/89-config-driven-scoring-weights-fingerprint-stamp.md`, in flight now) moves the
> scoring magnitudes into a bound `ScoringWeights` config and makes `ScoringEngine.ScoringConfigVersion`
> a **deterministic content fingerprint of the effective resolved scoring config** — a fingerprint that
> **already includes the attention tier-map descriptor** (`IAttentionSourceWeights.CanonicalDescriptor()`
> / the injected tier options). Because of that, changing the tier map, the tier weights, the unknown
> default, **or** the matcher's normalization (which changes which publishers map to which weight, hence
> the effective map's identity) **re-stamps the fingerprint automatically** — see **Version/obligations**.
> This slice touches `ConfiguredAttentionSourceWeights` (the matcher), `AttentionSourceTierOptions` (the
> default map), and the attention/fingerprint tests — **overlapping files with spec 89** (89 pins the
> default fingerprint, and if it adds `CanonicalDescriptor()` this slice's normalization changes that
> descriptor's content). It MUST **NOT** run in parallel with spec 89 or with any scoring / formula /
> engine / extractor / attention slice — both affect Attention output and the fingerprint (and its
> default-fingerprint test pin). Sequence it strictly after 89 merges; **re-read the merged
> `ConfiguredAttentionSourceWeights.cs`, `AttentionSourceTierOptions.cs`, `IAttentionSourceWeights.cs`,
> and the default-fingerprint pin before starting** so the change is against the real post-89 tree.

## Overview

Spec 88 (`radar-formula-v4`, merged) introduced source-quality tiering of the Attention reach breadth:
each distinct third-party publisher contributes its tier weight (content mills `0.1`, unknown `0.5`,
genuine `1.0`) instead of a flat `1`, and the half-saturation was re-tuned `12 → 3`. The **mechanism** is
sound. But the **post-88 live re-measure (2026-07-04, formula-v4)** showed it did **not** actually
differentiate: the covered cluster stayed jammed at **Attention 69–80**.

Root cause, verified on **HLIO** (ranked #1, Attention 76, with **zero genuine outlets — entirely
aggregator coverage**): of its 10 distinct news publishers, only **4** hit the `0.1` mill weight
(MarketBeat, Moomoo, Stock Titan, TradingView); the other **6** fell through to `unknown = 0.5` for three
distinct reasons:

1. **Name-matching is too literal.** `"marketscreener.com"` ≠ `"MarketScreener"` and `"simplywall.st"` ≠
   `"Simply Wall St"` — these are the **same outlets already in the mill list**, missed only because of a
   `.com`/`.st` domain-suffix, punctuation, and spacing gap. Google News `SourceName`s are often the bare
   outlet name but frequently arrive as a domain form. (`AttentionSourceTierOptions`'s own comment already
   predicts this: *"publisher name strings vary ('Simply Wall St' vs 'Simplywall.st')"*.) The current
   `Normalize` only trims + collapses internal whitespace and relies on `OrdinalIgnoreCase` — it does
   **not** strip a domain TLD or punctuation, so every domain-form aggregator silently becomes `unknown`.
2. **Mill list is too short.** Real aggregators observed in the wild — **Finviz, Investing.com** — aren't
   listed → they land at `unknown = 0.5`.
3. **`UnknownWeight = 0.5` is too generous.** Google News's long tail is dominated by low-value
   aggregators, so an unrecognised publisher counting as *half a genuine outlet* over-credits noise. With
   6 of HLIO's 10 publishers at `0.5`, its weighted breadth was `4·0.1 + 6·0.5 = 3.4` → Attention `≈ 53`
   from pure aggregator coverage (and higher once the media term and its other feeds are added), keeping
   an all-aggregator name near the top of the board.

The net effect: because so much aggregator coverage leaked into `unknown = 0.5`, tier-weighting collapsed
back toward a near-flat count and the covered cluster re-saturated — exactly the symptom spec 88 set out
to fix. **This slice makes the tiering bite** by closing the leak on both sides: (A) normalize publisher
names so the observed variants resolve to their real (already-curated) mill entries, and (B) recalibrate
the defaults so unrecognised long-tail publishers no longer count as half a genuine outlet.

---

## Assignment

Worktree: any (but see sequencing)
Dependencies: **spec 89 (`radar-formula-v5`, config-driven weights + content-fingerprint
`ScoringConfigVersion`) MUST be merged first.** Also: spec 88 (`radar-formula-v4`, the tier seam:
`IAttentionSourceWeights` / `ConfiguredAttentionSourceWeights` / `AttentionSourceTierOptions`), spec 84
(publisher `SourceName`), AD-3 (determinism), AD-5 (layering), AD-6 (formula identity), AD-10 (the
scoring stamp — now the auto-derived fingerprint per 89). Expected tree state at start:
`radar-formula-v5` with `ScoringConfigVersion` a content fingerprint that includes the attention tier
descriptor.
Conflicts with: touches `ConfiguredAttentionSourceWeights.cs` (the matcher — normalization) and
`AttentionSourceTierOptions.cs` (the code-default tier map + unknown default), the attention lookup tests
(`ConfiguredAttentionSourceWeightsTests`), and the **fingerprint pins** (the default-fingerprint test that
spec 89 introduces — changing the default tier map / unknown default / normalization changes the
descriptor, hence the pinned default fingerprint, so that pin must be recomputed). Reads (verify only):
`RadarScoreFormulaV5` reach, `IAttentionSourceWeights`, DI registration, `ScoringEngine`. Must **NOT** run
in parallel with spec 89 or any scoring / formula / engine / extractor / attention slice — **sequence it
strictly after 89 merges** (both affect Attention output + the fingerprint and its default pin).
Estimated time: ~1.5–2 h (a focused, config-plus-matcher change with careful normalization edge tests and
a fingerprint-pin recompute).

---

## Grounding facts (verified against the current tree — do NOT re-research)

> These describe the tree **as it will be after spec 89 merges**. Re-read the merged files listed under
> Sequencing before starting; make the change against the real post-89 tree.

- **The matcher today (`src/Radar.Infrastructure/Attention/ConfiguredAttentionSourceWeights.cs`).** Builds
  an `OrdinalIgnoreCase` `Dictionary<string,double>` at construction, keyed by `Normalize(publisher)`.
  `WeightFor(sourceName)` returns the mapped weight or `_unknownWeight`. **`Normalize` only** trims and
  collapses internal whitespace runs (`string.Join(' ', value.Split(null, RemoveEmptyEntries))`) — it does
  **NOT** strip a trailing domain TLD (`.com`/`.st`/…) or strip punctuation, and relies on the dictionary's
  `OrdinalIgnoreCase` comparer for case. So `"marketscreener.com"` and `"simplywall.st"` do **not** match
  the listed `"MarketScreener"` / `"Simply Wall St"`. The class already fails fast on `UnknownWeight` or a
  tier `Weight` outside `[0,1]`. **This `Normalize` is the code piece Part (A) changes.**
- **The default tier map (`src/Radar.Infrastructure/Attention/AttentionSourceTierOptions.cs`).**
  `UnknownWeight = 0.5`. `Mill (0.1)` = { MarketBeat, Zacks, Simply Wall St, StockStory, Moomoo,
  TradingView, Stock Titan, GuruFocus, Defense World, Pluang, MarketScreener }. `Genuine (1.0)` = {
  Reuters, Bloomberg, The Wall Street Journal, CNBC, Associated Press, Financial Times, SpaceNews }.
  `AttentionSourceTierOptions.Default` is the code-level fallback used when no `Radar:Attention` config is
  present. **This is the config piece Part (B) recalibrates.** Its `<summary>` already documents the
  maintenance / false-positive risk and the "publisher name strings vary" caveat.
- **The reach usage (`RadarScoreFormulaV5`, post-89 — the port of v4).** Reach breadth is
  `signals.Where(IsThirdPartyAttentionSource).Select(SourceName).Where(!IsNullOrWhiteSpace)
  .Distinct(OrdinalIgnoreCase).Sum(name => _weights.WeightFor(name))`; `reach = breadth +
  MediaReachWeight·mediaCount`; `Attention = Score(100·reach/(reach + AttentionHalfSaturation))`. **This
  formula is UNCHANGED by this slice** — only `WeightFor`'s answers (via normalization + recalibrated
  defaults) change. `AttentionHalfSaturation` (default `3.0`) and `MediaReachWeight` (`0.25`) are **not**
  touched here (the saturation stays tuned for the filtered scale; recalibration works by making the
  filtered breadth genuinely reflect genuine coverage, not by re-tuning saturation again).
- **`IAttentionSourceWeights` (Application).** `double WeightFor(string? sourceName)`. **Post-89 it may
  also expose `string CanonicalDescriptor()`** (spec 89's preferred path, to fold the tier map into the
  scoring fingerprint). If present, `ConfiguredAttentionSourceWeights.CanonicalDescriptor()` serializes the
  effective publisher→weight map + unknown default; **the recalibrated defaults + the normalized keys
  change that descriptor's content**, which is *correct and intended* (the generation identity must move
  when the effective attention map moves). Verify whether 89 shipped `CanonicalDescriptor()` or the
  injected-`AttentionSourceTierOptions` alternative, and update whichever carries the descriptor content.
- **The scoring stamp is now auto-derived (post-89, AD-10 amended).** `ScoringEngine.ScoringConfigVersion`
  is a SHA256 content fingerprint of `(EngineVersion, formulaVersion, ScoringWeights, attention tier
  descriptor)`. **No manual bump exists to change.** A default-fingerprint value is **pinned in a test**
  (spec 89) — that pin **must be recomputed** in this slice because the default tier map / unknown default
  / normalization all feed the descriptor. See **Version/obligations**.
- **Existing lookup tests (`tests/Radar.Infrastructure.Tests/Attention/ConfiguredAttentionSourceWeights
  Tests.cs`).** Already assert: listed publisher → tier weight; unlisted → `UnknownWeight`; blank/null →
  `UnknownWeight`; case-insensitive + internal-whitespace-collapse (`"Simply  Wall  St"`); the `Default`
  map tiers sensibly (Zacks `0.1`, Bloomberg `1.0`, unlisted `0.5`); null options throws; tier `Weight`
  and `UnknownWeight` outside `[0,1]` fail fast. **These must stay green** (the `0.5` unknown assertions
  move to whatever recalibrated default is chosen — update them in lockstep). Add the new normalization +
  collision-guard cases here.
- **Provenance / determinism / layering unaffected by the mechanism.** The matcher stays a pure,
  deterministic, immutable lookup (AD-3); it stays in `Radar.Infrastructure/Attention` (AD-5); no Domain,
  no collector, no report-renderer, no DB (AD-8), no provider SDK change.

---

## Design

### Part (A) — Publisher-name normalization in `ConfiguredAttentionSourceWeights` (the CODE piece)

Make matching robust to the observed variants by normalizing **both** the configured keys (at load) and
the incoming `SourceName` (at lookup) through the **same** function, so `"marketscreener.com"` resolves
to the `"MarketScreener"` entry and `"simplywall.st"` to `"Simply Wall St"`.

**Recommended normalization (conservative — order matters):**

1. `Trim()` and lowercase (invariant / ordinal-ignore-case — reuse the existing case-insensitive posture).
2. **Strip a single trailing common-TLD token** if the string ends in one of a small, closed set of
   web-domain suffixes: `.com`, `.st`, `.io`, `.net`, `.org`, `.co`, `.ai`, `.news` (curated to the
   observed / plausible Google-News domain forms — do **not** strip arbitrary dotted tokens, which would
   mangle real names). Strip the suffix **before** removing punctuation so `"marketscreener.com"` →
   `"marketscreener"`, `"simplywall.st"` → `"simplywall"`. Strip only a *trailing* TLD, and only once.
3. **Remove all non-alphanumeric characters** (spaces, dots, hyphens, punctuation) so
   `"Simply Wall St"` → `"simplywallst"` and `"simplywall"` (after TLD strip) → `"simplywall"`.

> **Collision caveat — the one real risk.** Step 3 alone would map `"Simply Wall St"` → `"simplywallst"`
> but the TLD-stripped `"simplywall.st"` → `"simplywall"` — **these differ** (`simplywallst` vs
> `simplywall`). To make the observed `simplywall.st` variant resolve, the TLD-strip in step 2 is what
> closes the gap **only if** `.st` is in the TLD set; then `"simplywall.st"` → `"simplywall"` and
> `"Simply Wall St"` → `"simplywallst"` — still different. **Therefore add `Simplywall.st` / `simplywall`
> as an explicit alias entry in the mill list (Part B) rather than relying on normalization to bridge a
> word-boundary difference** the outlet's own domain drops. This is the honest, low-risk fix: normalization
> handles the mechanical `.com`/punctuation/spacing/case gap (`"marketscreener.com"` → `"marketscreener"`
> matches a `"MarketScreener"` → `"marketscreener"` key), and genuinely different name *spellings* (domain
> that elides a word) are handled by **listing the variant explicitly** in config. Do NOT over-normalize
> (e.g. stripping vowels or fuzzy-matching) to force `simplywall`≡`simplywallst` — that risks collapsing
> genuinely distinct outlets. The coder should confirm, with a test on the exact strings, which variants
> normalization alone bridges and which need an explicit alias entry, and choose the smallest combination
> that makes the observed variants match without collapsing distinct outlets.

**Determinism (AD-3) preserved:** normalization is a pure `static` string function; the lookup dictionary
is built once at construction and is immutable; identical input always yields identical weight. The
`Distinct(OrdinalIgnoreCase)` in the formula is on the raw `SourceName` (upstream of the matcher) and is
unchanged — two raw names that normalize to the same key will each be looked up and return the same
weight, so a publisher appearing as both `"MarketScreener"` and `"marketscreener.com"` contributes its
mill weight for *each distinct raw name*; that is acceptable (and rare) and does not multiply within a
single raw name. (If the maintainer wants same-outlet-different-spelling to also collapse the *distinct*
count, that is a larger change to the formula's `Distinct` key and is **out of scope** here — noted.)

**Collision guard (mandatory test).** Add a test proving normalization does **not** collapse two
genuinely-distinct outlets — e.g. `"Barron's"` and `"Barrons Weekly"` (or two curated genuine outlets that
share a prefix) must remain distinct keys / distinct weights. Keep the TLD set and the strip conservative
so a real outlet name is never mangled into another's key.

### Part (B) — Recalibrate the default tier map / unknown weight (the CONFIG piece, post-89)

Three levers were evaluated against the live evidence (HLIO: all-aggregator, must score **low** Attention;
a genuinely-covered name must outrank it):

- **(i) Expand the mill denylist.** Add the observed / plausible long-tail aggregators to the `Mill (0.1)`
  list: **Finviz, Investing.com, Insider Monkey, Benzinga, TipRanks, StockAnalysis** (and the explicit
  `Simplywall.st` alias per Part A's caveat). Low false-positive risk (these are unambiguous aggregators),
  but is *reactive* — the long tail regrows and needs upkeep.
- **(ii) Lower `UnknownWeight`.** `0.5 → 0.25`. Directly attacks the leak: an unrecognised publisher now
  counts as a quarter of a genuine outlet, so a pile of unknown aggregators no longer sums to a
  high breadth. Simple, one number, immediately effective; slight risk of *under*-counting a genuine niche
  outlet not yet on the allowlist (mitigated by it still being non-zero — never silently zeroed).
- **(iii) Flip to an ALLOWLIST posture.** `UnknownWeight` very low (e.g. `0.15`, ≈ mill level), only
  curated **genuine** outlets earn full weight; the mill list becomes almost redundant (everything
  unrecognised is already treated as near-mill). Strongest differentiation and lowest maintenance on the
  *mill* side (no need to chase every new aggregator); the cost is that a **real** outlet not on the
  genuine allowlist is under-counted until curated — a maintenance burden that shifts to the genuine list.

**RECOMMENDATION: (i) denylist-expand + (ii) lower UnknownWeight (0.5 → 0.25).** This is the smallest,
lowest-risk change that makes the tiering bite while preserving spec 88's "never silently zeroed"
guarantee (unknown stays non-zero at `0.25`). It attacks the leak from both directions — the *known*
long-tail aggregators drop to `0.1`, and the *residual* unknown tail drops to `0.25` — without adopting
the allowlist posture's aggressive under-counting of un-curated real outlets on Radar's tiny universe.
The allowlist flip (iii) is the right destination if the long tail proves unmanageable, but it is a
bigger posture change; recommend it as a **documented alternative the maintainer may prefer**, tunable by
just editing `UnknownWeight` + the genuine list in config (no code change). **Do NOT touch
`AttentionHalfSaturation` (3.0) or `MediaReachWeight` (0.25)** — recalibration works by making the
filtered breadth honest, not by re-tuning saturation.

### Before/after Attention (8-company universe, proposed defaults: mill list expanded + `UnknownWeight 0.25`)

Grounded in the post-88 live re-measure. **HLIO's real publisher split is known** (10 distinct: 4 mill +
6 that leaked to unknown, of which — per the observation — `marketscreener.com`, `simplywall.st`, Finviz,
Investing.com are recoverable to mill via normalization + denylist-expand, leaving ~2 genuinely unknown).
For the others, the stated 88 assumption (of ~20 publishers, ≈2–3 genuine / ≈3–4 unknown / rest mill) is
re-applied with the new defaults; **the mechanism and direction, not exact per-company values, is what
this locks.** Weighted breadth `≈ Σ tier weights`; `Att = 100·r/(r+3)` (media term omitted for clarity —
unchanged).

| Company | genuine / unknown-recovered-to-mill / residual-unknown / mill | **v4-as-shipped breadth (unk 0.5, no norm)** | **Att (before)** | **proposed breadth (unk 0.25, norm + denylist)** | **Att (after)** | direction |
|---|---|---:|---:|---:|---:|---|
| HLIO | 0 / 2 recovered / ~2 resid / 4 mill | 4·0.1 + 6·0.5 = **3.4** | **53** | (4+2)·0.1 + 2·0.25 = **1.1** | **27** | **big drop (all-aggregator → low)** |
| MRCY | 3 / 2 / 2 / 12 | 3·1.0 + 4·0.5 + 14·0.1 = **6.4** | **68** | 3·1.0 + 2·0.1 + 2·0.25 + 14·0.1 = **5.1** | **63** | modest drop (genuine coverage holds it up) |
| ERII | 3 / 2 / 3 / 15 | 3 + 2.5 + 2.0 = **7.5** | **71** | 3·1.0 + 2·0.1 + 3·0.25 + 15·0.1 = **5.45** | **65** | modest drop; still top-tier (most genuine) |
| AGYS | 2 / 2 / 1 / 15 | 2 + 3·0.5 + 1.8 = **5.3** | **64** | 2·1.0 + 2·0.1 + 1·0.25 + 15·0.1 = **3.95** | **57** | drop |
| AEHR | 1 / 2 / 1 / 15 | 1 + 3·0.5 + 1.7 = **4.2** | **58** | 1·1.0 + 2·0.1 + 1·0.25 + 15·0.1 = **2.95** | **50** | drop |
| CYRX | 1 / 2 / 1 / 12 | 1 + 3·0.5 + 1.4 = **3.9** | **57** | 1·1.0 + 2·0.1 + 1·0.25 + 12·0.1 = **2.65** | **47** | drop |
| EOSE | 2 / 1 / 1 / 12 | 2 + 2·0.5 + 1.2 = **4.2** | **58** | 2·1.0 + 1·0.1 + 1·0.25 + 12·0.1 = **3.55** | **54** | modest drop |
| SPNS | 0 / 1 / 0 / 2 | 0 + 0.5 + 0.1 = **0.6** | **17** | 0 + 1·0.1 + 0 + 2·0.1 = **0.3** | **9** | drop (thin, mill-only → very low) |

**Reading the recalibration (grounded in the observed HLIO split + the stated 88 assumption for the
rest — the mechanism, not exact per-company values, is what this locks):**

- **The differentiation now bites.** HLIO — the concrete all-aggregator case that motivated this slice —
  drops from **Att 53 (near the jammed 69–80 cluster once its media term is added) to Att 27**, well below
  every genuinely-covered name. That is the headline property: **an all-aggregator name scores materially
  lower than any name with real outlets.**
- **Ordering is on the RIGHT axis.** Names with genuine outlets (MRCY/ERII, 3 genuine) now sit clearly
  above names with fewer (AEHR/CYRX, 1 genuine) — the genuine-breadth-over-mill-breadth ordering spec 88
  intended, which the `unknown = 0.5` leak had washed out.
- **Under-the-radar principle preserved (unchanged discount shape).** Lower Attention → smaller discount,
  so genuinely under-followed names keep more of their base; the discount still falls monotonically with
  Attention and never zeroes (Opportunity's `÷250` is untouched).
- **Never silently zeroed (spec 88 guarantee preserved).** `UnknownWeight` stays **non-zero** (`0.25`), so
  a real outlet not yet on the allowlist is *under*-counted, never dropped.

> **Flag for maintainer sign-off (Proposed).** The recalibrated defaults — `UnknownWeight 0.5 → 0.25`, the
> expanded mill denylist (Finviz, Investing.com, Insider Monkey, Benzinga, TipRanks, StockAnalysis + the
> `Simplywall.st` alias), and the recommendation of **denylist-expand + lower-default over the allowlist
> flip** — are the **proposed** calibration; confirm before merge (the AD-6 note stays **Proposed** until
> then). The maintenance / false-positive trade-off is honest: expanding the denylist is reactive upkeep;
> lowering the unknown default risks slightly under-counting un-curated real outlets; the allowlist flip
> would be lower mill-side maintenance but under-counts un-curated real outlets more aggressively. All are
> tunable in config without a code change.

---

## Version / obligations (confirm: NO manual bump)

- **Post-89, `ScoringConfigVersion` auto-re-stamps.** Spec 89 made it a SHA256 content fingerprint of
  `(EngineVersion, formulaVersion, ScoringWeights, attention tier descriptor)`. Changing the tier map, the
  tier weights, the unknown default, **or** the matcher's normalization (which changes which publishers map
  to which weight → the effective map's descriptor content) **changes the fingerprint automatically**.
  **There is no manual `ScoringConfigVersion` bump to perform** — state this in the PR. The one concrete
  obligation is to **recompute and re-pin the default-fingerprint test constant** spec 89 introduced (the
  default tier map + unknown default feed the descriptor, so the pinned default fingerprint moves). Compute
  the new value, paste it, assert equality — this is the automatic AD-10 property doing its job (an
  output-affecting change forces a deliberate acknowledgement via the failing pin).
- **Fallback if 89 has NOT merged when this is implemented.** This slice sequences strictly after 89 and
  MUST NOT run in parallel. In the unlikely event 89 is not yet merged at implementation time, **stop and
  wait for 89** (do not proceed on the pre-89 tree). If forced to proceed on the pre-89 tree, fall back to
  the **order-robust manual bump** of `ScoringEngine.ScoringConfigVersion` (read current, `+1`) with an
  updated comment recording the tier calibration + normalization — and say so explicitly in the PR. The
  post-89 auto-fingerprint path is strongly preferred; the manual bump is only the pre-89 fallback.
- **No formula-shape change → NO new formula-version class.** The reach formula is byte-for-byte unchanged
  (same weighted-distinct-sum, same `+3` saturation, same media term); only `WeightFor`'s answers change.
  It **stays `radar-formula-v5`** — do **not** create a `v6`. `ScoringVersion` is unchanged. No `RadarScore
  FormulaVN` add/delete.

---

## Project structure changes

```text
src/Radar.Infrastructure/Attention/
  ConfiguredAttentionSourceWeights.cs   # MODIFIED (Part A): extend Normalize — strip a trailing common-TLD
                                        #   token (.com/.st/.io/.net/.org/.co/.ai/.news) then remove all
                                        #   non-alphanumerics (lowercase); apply to BOTH configured keys (at
                                        #   load) and incoming SourceName (at lookup). Stays pure/deterministic.
                                        #   If it exposes CanonicalDescriptor() (post-89), its content shifts
                                        #   naturally with the normalized keys — verify it still serializes
                                        #   deterministically.
  AttentionSourceTierOptions.cs         # MODIFIED (Part B): recalibrate Default — UnknownWeight 0.5 -> 0.25;
                                        #   expand Mill list (Finviz, Investing.com, Insider Monkey, Benzinga,
                                        #   TipRanks, StockAnalysis, + explicit "Simplywall.st" alias); update
                                        #   the <summary> maintenance note to reflect the new default + posture.

tests/Radar.Infrastructure.Tests/Attention/
  ConfiguredAttentionSourceWeightsTests.cs  # MODIFIED: add normalization cases for the exact observed variants
                                        #   ("marketscreener.com" -> mill 0.1, "simplywall.st" -> mill 0.1 via
                                        #   alias/normalization) + a NON-COLLISION guard (two distinct outlets
                                        #   stay distinct); update the unknown-default assertions 0.5 -> 0.25 and
                                        #   the Default-map assertions to the recalibrated defaults.

tests/Radar.Application.Tests/Scoring/
  RadarScoreFormulaV5Tests.cs           # MODIFIED: add/adjust an Attention pin proving an all-aggregator name
                                        #   (HLIO-like: all mill/unknown-at-0.25) scores MATERIALLY LOWER
                                        #   Attention than a genuine-outlet name at equal publisher count,
                                        #   under the recalibrated + normalized weights. Existing v5 pins whose
                                        #   inputs use publishers whose weight changed must be recomputed.
  ScoringConfigFingerprintTests.cs      # MODIFIED: recompute + re-pin the DEFAULT fingerprint constant (the
                                        #   default tier map / unknown default changed → descriptor changed →
                                        #   fingerprint changed). This is the automatic AD-10 acknowledgement.
  ScoringEngineTests.cs                 # VERIFY/MODIFY: any assertion of the pinned default ScoringConfigVersion
                                        #   fingerprint updates to the recomputed value; presence guard stays green.

docs/architecture-decisions.md          # MODIFIED (optional, Proposed): a short note under the AD-6 v4/v5
                                        #   attention lineage recording the tier-calibration + normalization
                                        #   (unknown 0.5->0.25, denylist expanded, domain-form normalization),
                                        #   marked Proposed pending maintainer sign-off. NO new formula version.
```

No `Radar.Domain` change. No collector, no report-renderer change. No new formula class. No new package,
no provider SDK, no DB (AD-8, files-first). `RadarScoreFormulaV5`, `ScoringEngine`, DI registration, and
`IAttentionSourceWeights` are **read/verify only** (the descriptor content shifts naturally; no signature
change unless 89's descriptor path needs a trivial touch — confirm against the merged 89 tree).

---

## Implementation details

### `ConfiguredAttentionSourceWeights.Normalize` (Part A — Infrastructure)

- Replace the whitespace-only `Normalize` with the conservative pipeline in Design §A: lowercase; strip a
  single trailing common-TLD token from a small closed set; remove all non-alphanumeric characters. Keep it
  a pure `static` method. Because keys are now normalized to alphanumeric-lowercase, the dictionary comparer
  can become `StringComparer.Ordinal` (keys are already lowercased) — or keep `OrdinalIgnoreCase`; either is
  fine as long as **the same `Normalize` is applied to keys at load and to the input at lookup**.
- Apply `Normalize` to the configured publisher strings when building the map (as today) **and** to
  `sourceName` in `WeightFor` (as today) — the fix is that `Normalize` now does more, symmetrically.
- Keep the fail-fast on out-of-range weights unchanged.
- If `CanonicalDescriptor()` exists (post-89), ensure it emits its entries in a **stable ordinal order** of
  the (normalized) keys with culture-invariant weight formatting so the fingerprint stays deterministic
  (AD-3) — verify, don't assume.

### `AttentionSourceTierOptions.Default` (Part B — Infrastructure config)

- `UnknownWeight = 0.5 → 0.25`.
- Expand the `Mill (0.1)` list with the observed / plausible long-tail aggregators (Finviz, Investing.com,
  Insider Monkey, Benzinga, TipRanks, StockAnalysis) **and** an explicit `Simplywall.st` alias entry (per
  the Design §A collision caveat — the domain form elides a word, so list it rather than force a fuzzy
  match). Leave `Genuine (1.0)` as-is unless the maintainer opts for the allowlist flip.
- Update the `<summary>` maintenance note to record the recalibrated default (`UnknownWeight 0.25`) and the
  denylist-expand posture (and mention the allowlist flip as the documented alternative).

### AD note (optional, Proposed)

- A short note in `docs/architecture-decisions.md` under the AD-6 attention lineage (v4/v5) recording the
  calibration + normalization change, **explicitly stating it is NOT a new formula version** (reach shape
  unchanged; only weights + matcher) and that the fingerprint auto-re-stamps (no manual bump). Mark it
  **Proposed pending maintainer sign-off** on the recalibrated defaults and posture. This is a note, not a
  new AD, and not a formula-version bump.

---

## Tests

Match the existing test style. Keep all scoring / pipeline / report / extractor tests green.

`ConfiguredAttentionSourceWeightsTests` (Infrastructure):
1. **Observed variant `marketscreener.com` resolves to the mill weight.** `WeightFor("marketscreener.com")
   == 0.1` (normalizes to the same key as `"MarketScreener"`). Also `"MarketScreener.COM"`, `"marketbeat.com"`.
2. **Observed variant `simplywall.st` resolves to the mill weight.** `WeightFor("simplywall.st") == 0.1`
   (via the explicit `Simplywall.st` alias and/or normalization — assert the end weight, not the mechanism).
3. **Non-collision guard (mandatory).** Two genuinely-distinct outlets do **not** collapse to the same key
   / weight — e.g. a listed genuine outlet and a made-up outlet sharing a prefix return their **own**
   distinct weights (the listed one its tier, the other `UnknownWeight`); assert they are not equal when
   they should differ. Prove normalization did not over-collapse.
4. **Existing cases updated to the recalibrated defaults.** Unknown/blank/null → `0.25` (was `0.5`); the
   `Default`-map test asserts Zacks `0.1`, Bloomberg `1.0`, a newly-listed aggregator (e.g. `"Finviz"`,
   `"Investing.com"`) `0.1`, and a truly-unlisted name `0.25`. Case-insensitivity + whitespace-collapse
   still hold. Fail-fast on out-of-range tier `Weight` / `UnknownWeight` unchanged.

`RadarScoreFormulaV5Tests` (Application):
5. **All-aggregator vs genuine-coverage Attention (the headline property, HLIO-grounded).** Two inputs with
   the **same** distinct-publisher count: input A all mill/`0.25`-unknown (the HLIO all-aggregator shape),
   input B with several genuine outlets. Assert `Attention(B) − Attention(A)` is a **material** margin
   (e.g. `> 25`), and that A's Attention is **low** in absolute terms (e.g. `< 30` for a 10-publisher
   all-aggregator name) — proving the recalibration makes an all-aggregator name score materially lower.
6. **Recompute any existing v5 Attention pin** whose input publishers' weights changed under the new
   defaults / normalization (e.g. a pin that used `unknown = 0.5`). Keep the shape-unchanged pins
   (Trajectory / EC / Velocity / contributions / empty-window / determinism / version string) green.

`ScoringConfigFingerprintTests` + `ScoringEngineTests` (Application):
7. **Recompute + re-pin the default fingerprint.** The default tier map + `UnknownWeight 0.25` change the
   attention descriptor → the pinned default-fingerprint constant changes. Recompute it, paste it, assert
   equality (and that the engine stamps it). This is the automatic AD-10 re-stamp acknowledgement — **no
   manual `ScoringConfigVersion` bump.** Determinism / culture-invariance pins stay green.

Search the tree for any assertion pinning the old `UnknownWeight 0.5`, the old default fingerprint, or the
old mill/genuine lists and update it.

---

## Constraints

- Target `net10.0`, C# 14. The matcher (`ConfiguredAttentionSourceWeights`) and the config
  (`AttentionSourceTierOptions`) stay in `Radar.Infrastructure/Attention` (AD-5); no Application/Domain
  signature change (the descriptor content shifts naturally). No provider SDK, no AI, no HTTP, no DB
  (AD-8, files-first).
- **NO new formula-version class and NO manual `ScoringConfigVersion` bump** — the reach *shape* is
  unchanged (stays `radar-formula-v5`); only `WeightFor`'s answers (normalization + recalibrated defaults)
  change, and post-89 the fingerprint auto-re-stamps. The only concrete obligation is recomputing the
  pinned default fingerprint. (Pre-89 fallback: order-robust manual bump — but sequence after 89.)
- **Determinism (AD-3) is load-bearing:** `Normalize` is a pure static function; the lookup is an immutable
  dictionary built once; identical input → identical weight; the descriptor (if present) serializes in a
  stable ordinal order with culture-invariant number formatting.
- **Never silently zeroed (spec 88 guarantee preserved):** `UnknownWeight` stays **non-zero** (`0.25`) — a
  real outlet not yet curated is under-counted, never dropped.
- **Conservative normalization:** strip only a *trailing* TLD from a small closed set, then strip
  punctuation — no fuzzy/vowel-stripping/fuzzy-matching. A mandatory non-collision test proves two distinct
  outlets are not collapsed.
- **Provenance is sacred and preserved:** the formula's contribution / `ScoreEvidenceLink` construction is
  untouched; `Distinct(OrdinalIgnoreCase)` on raw `SourceName` is unchanged; only per-publisher *weights*
  move.
- **AD-9:** no advice language; all component scores stay clamped in `[0,100]`. Recalibration only moves
  ranking/labels via legitimate Attention changes — no banned tokens.
- The recalibrated defaults + posture recommendation ship **Proposed pending maintainer sign-off** (as
  87/88/89 were).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Out of scope (note, do NOT implement this round)

- **News-volume within-source signal-multiplicity dedup** (collapsing many near-duplicate articles about
  one event into a single reach / `MediaReachWeight·mediaCount` contribution) — still deferred (spec 88/89
  Out of scope). This slice does not touch `MediaReachWeight` or the media term.
- **Re-tuning `AttentionHalfSaturation` (3.0) or `MediaReachWeight` (0.25)** — the saturation stays tuned
  for the filtered scale; recalibration works by making the filtered breadth honest, not by re-tuning
  saturation.
- **Collapsing same-outlet-different-spelling into a single `Distinct` count** in the formula (a change to
  the formula's `Distinct` key) — out of scope; normalization only affects the per-name *weight*, not the
  distinct-publisher count.
- **A comprehensive source-reputation database / auto-classification of publishers** — this stays a small
  curated config map with conservative mechanical normalization.
- **The price-efficacy visual / any price data ingestion; going-concern / skeptic-reviewer wiring.**
- **Any new formula-version class** — the reach shape is unchanged.

---

## Acceptance criteria

- [ ] **Implemented AFTER spec 89 (`radar-formula-v5`) merges** (auto-fingerprint stamp); not run in
      parallel with any scoring / formula / engine / extractor / attention slice.
- [ ] **Part (A):** `ConfiguredAttentionSourceWeights.Normalize` strips a trailing common-TLD token from a
      small closed set and removes non-alphanumerics (lowercase), applied symmetrically to configured keys
      and incoming `SourceName`; `"marketscreener.com"` and `"simplywall.st"` resolve to the mill weight
      `0.1`; a mandatory non-collision test proves two distinct outlets are **not** collapsed. Pure /
      deterministic (AD-3).
- [ ] **Part (B):** `AttentionSourceTierOptions.Default` recalibrated — `UnknownWeight 0.5 → 0.25`, mill
      denylist expanded (Finviz, Investing.com, Insider Monkey, Benzinga, TipRanks, StockAnalysis +
      `Simplywall.st` alias); `<summary>` maintenance note updated. Unknown stays **non-zero** (never
      silently zeroed).
- [ ] An **all-aggregator name (HLIO-like)** scores **materially lower** Attention than a genuine-outlet
      name at equal publisher count (tested, e.g. margin `> 25` and the aggregator name `< 30` absolute).
- [ ] **NO new formula-version class** (stays `radar-formula-v5`, reach shape unchanged) and **NO manual
      `ScoringConfigVersion` bump** — the post-89 fingerprint auto-re-stamps; the only obligation
      discharged is **recomputing + re-pinning the default fingerprint** test constant. (Pre-89 fallback:
      order-robust manual bump — but sequence strictly after 89.)
- [ ] The recalibrated defaults + the denylist-expand-vs-allowlist-flip recommendation ship **Proposed
      pending maintainer sign-off** (optional short AD-6-lineage note, marked Proposed).
- [ ] Existing `ConfiguredAttentionSourceWeightsTests` updated (unknown `0.5 → 0.25`, Default-map + new
      normalization + non-collision cases); v5 Attention pins whose weights changed recomputed; the pinned
      default fingerprint recomputed; all scoring / pipeline / report / extractor tests green.
- [ ] Layering (AD-5), determinism (AD-3), files-first (AD-8), provenance, and AD-9 label/advice rules
      preserved; no Domain / collector / report-renderer / formula-shape change.
      `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.
