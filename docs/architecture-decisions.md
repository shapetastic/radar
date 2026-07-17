# Architecture Decisions (Radar)

A running ledger of consciously-made architecture/convention decisions and accepted trade-offs.

**`radar-architecture-reviewer` and `radar-work-planner` MUST read this and treat every decision
recorded here as settled — do not re-flag it as drift, and do not propose work to undo it.** To
change a decision, update its entry here (status → `Superseded`) and record the replacement.

Each entry: the decision, why, status, and date (UTC, absolute).

---

## AD-1 — Persistence write semantics: evidence is immutable, everything else is upsert-by-Id

**Decision.** `EvidenceItem` is **insert-only / immutable**: an existing record is never overwritten,
and a duplicate `ContentHash` is rejected (`IEvidenceRepository.AddIfNewAsync` returns `false`).
All other aggregates — `Company`, `CompanyAlias`, `Signal`, `CompanyScoreSnapshot`,
`ScoreEvidenceLink`, `RadarReport` — use **upsert by `Id` (last-write-wins)** in the repositories.

**Why.** The schema/pipeline specs mandate immutability for *evidence* only (provenance is anchored
there). For the MVP, last-write-wins on the others is simple and in-spec. The contract is documented
as `<remarks>` on the repository interfaces, and the future Dapper implementation **must preserve
these exact semantics** — do not silently switch evidence to upsert or the others to insert-only.

**Status.** Accepted · 2026-06-27 (spec 07). Revisit if append-only history for signals/scores is
needed later.

---

## AD-2 — In-memory repositories do not observe the CancellationToken

**Decision.** The in-memory repository implementations complete synchronously and **do not** check
the `CancellationToken` (the parameter stays on the interface for the contract). Cancellation is the
responsibility of the real (Dapper) implementations, where it is meaningful.

**Why.** Honoring the token on instantaneous in-memory work is noise, and having it observed in one
method but not others (the pre-07 state) was worse — it read as an accident. Uniform non-observance
is the clear convention. Recorded so the reviewer does not re-flag the in-memory methods for "ignoring
`ct`".

**Status.** Accepted · 2026-06-27 (spec 07).

---

## AD-3 — Collection queries return a deterministic order

**Decision.** Every repository method that returns a collection applies a stable
`OrderBy(...).ThenBy(Id)` (never returns raw `ConcurrentDictionary.Values`). Established keys:
companies/aliases by `CreatedAtUtc`, evidence by `CollectedAtUtc`, signals by `ObservedAtUtc`, score
snapshots by `CreatedAtUtc`, score-evidence links by `Id`, report items by `Rank` — each with `Id` as
the tiebreaker.

**Why.** Radar is an evidence-first, **replayable** pipeline; observable output order must be stable.
This is now a positive convention — the reviewer should flag *violations* (unordered query output),
not re-debate the convention itself.

**Status.** Accepted · 2026-06-27 (spec 07).

---

## AD-4 — Application test project may reference Infrastructure

**Decision.** `Radar.Application.Tests` may take a `ProjectReference` on `Radar.Infrastructure` in
order to seed real in-memory repositories (e.g. `InMemoryCompanyRepository`) in tests.

**Why.** It is a test-only dependency with no production layering cycle, and it keeps tests exercising
the real persistence behaviour. Accepted for now. If the team later prefers to keep
`Radar.Application.Tests` free of an Infrastructure dependency, the alternative is an in-test fake
`ICompanyRepository`; until then this is not drift.

**Status.** Accepted · 2026-06-27 (spec 06).

---

## AD-5 — Application may use Microsoft.Extensions.* abstractions (supersedes "package-free Application")

**Decision.** `Radar.Domain` stays pure — **no package references** (records/enums only).
`Radar.Application` **MAY reference the `Microsoft.Extensions.*` abstraction packages**:
`Microsoft.Extensions.Logging.Abstractions` (`ILogger<T>`), `…DependencyInjection.Abstractions`,
`…Options`, `…Configuration.Abstractions`, and `Microsoft.Extensions.AI`. **Concrete provider /
infrastructure SDKs** — database drivers (Npgsql, Dapper), and concrete LLM client SDKs — remain in
`Radar.Infrastructure` only.

This **reverses** the earlier implicit "`Radar.Application` keeps zero package references" rule that
the planner had baked into specs 04/09/10/11 (it forced spec 11 to drop a requested `ILogger`). That
rule was an over-strict extrapolation, not a master-spec requirement.

**Why.** Depending on framework *abstractions* (logging, DI, options, config, `Microsoft.Extensions.AI`)
from the Application layer is standard Clean Architecture and keeps the app testable while still
keeping concrete providers behind interfaces in Infrastructure. The real hard rule is unchanged: **no
concrete AI/data provider SDK outside `Radar.Infrastructure`** — Application gets the abstractions, not
the implementations.

**Scope note.** This is about the `Microsoft.Extensions.*` *abstraction* family, not full ASP.NET Core
hosting/web packages (`Microsoft.AspNetCore.*`), which belong in the `Radar.Api`/`Radar.Worker` host
layer, not in Application.

**Status.** Accepted · 2026-06-27 (decision by maintainer). Existing merged slices are not retrofitted;
new work may add these packages to Application as needed.

---

## AD-6 — Scoring formula v1 (`radar-formula-v1`): shape, constants, and the previous-window input

**Decision.** The first real `IScoreFormula`, `RadarScoreFormulaV1` (`Version = "radar-formula-v1"`),
was **co-designed with and approved by the maintainer**. Its five components are:

- **TrajectoryScore** — confidence-and-recency-weighted mean of directional strength, mapped `50 + 5·T_raw`
  (50 = neutral). Direction signs: `Positive +1`, `Negative −1`, **`Neutral` and `Mixed` = 0**.
- **AttentionScore** — saturating breadth `100·reach/(reach+5)`, `reach = distinctSourceNames + 0.5·mediaSignals`.
  *(Superseded by radar-formula-v4 for the Attention component — see the spec-88 refinement below: tier-weighted
  distinct-publisher breadth and `+3` saturation; the spec-87 v3 step took it via `+12` / `0.25·mediaSignals`.)*
- **EvidenceConfidenceScore** — `100·avgConf·(0.6+0.4·qualFactor)·(0.7+0.3·divFactor)`; quality weights
  Primary 1.0 / High .85 / Med .6 / Low .35 / Unknown .4; diversity saturates at 3 distinct source types.
- **SignalVelocityScore** — `50·(actNow+10)/(actPrev+10)` over `Strength` sums (50 = steady).
- **OpportunityScore** — **multiplicative** `Trajectory·(EC/100)·(1 − Attention/200)` (under-the-radar:
  high attention halves, never zeroes). *(Divisor superseded by radar-formula-v3 — `÷250`, see below.)*

To feed velocity, **`ScoringInput` carries `PreviousSignals`** — the immediately-preceding equal-length
window `(start−W, start]`, **signals only, no evidence loaded** (velocity needs `Strength` magnitude, not
provenance). **Only current-window signals build `ScoreContribution`s / `ScoreEvidenceLink`s**;
`PreviousSignals` never carries provenance. A signal observed exactly at `windowStart` belongs to the
**previous** window (shared inclusive-end boundary, no double-count).

**Why.** These are deliberate, visible, versioned product choices (full-pipeline spec §Stage 6). They
are settled — the reviewer/planner must **not** re-flag as drift: Neutral/Mixed contributing 0 to
trajectory, the multiplicative Opportunity, the no-evidence-for-previous-window rule, or the
`windowStart`→previous boundary. To change the formula, bump `Version` and update this entry; existing
snapshots remain reproducible under their recorded `ScoringVersion`.

### Refinement — `radar-formula-v2` (spec 58): corroboration and diversity must *raise* scores

The first two-collector live run (`["rss","sec"]`, RSS press releases + SEC 8-K filings) exposed a
structural flaw in v1: **adding corroborating evidence *lowered* every company it touched** (Helios fell
Opportunity 32→22 despite gaining two real 8-K signals). Three v1 component formulas moved the wrong way:
Neutral filings dragged the trajectory *mean* toward 50; a company's own SEC feed inflated Attention (which
the Opportunity `(1 − Attention/200)` term penalises); and *mean* confidence averaged the 0.40 filing down
against the 0.60 press release. `RadarScoreFormulaV2` (`Version = "radar-formula-v2"`, **maintainer-approved**)
fixes exactly three components; **Opportunity, Velocity, the window, and the provenance/contribution rules are
unchanged**. The v1 component formulas above are therefore *superseded by radar-formula-v2*:

- **TrajectoryScore** — now the confidence/recency-weighted mean of directional strength over **only
  `Positive`/`Negative` signals**; `Neutral`/`Mixed` are excluded from **both** numerator and denominator (no
  directional signals → `T_raw = 0` → 50). Neutrals no longer dilute the directional read. (Contributions still
  emit one row per current-window signal in input order — Neutral/Mixed simply carry weight 0.)
- **AttentionScore** — `reach = distinctThirdPartySourceNames + 0.5·mediaSignals`, counting distinct source
  names **only among third-party (market-attention) evidence source types** (`NewsArticle`, `SocialMedia`,
  `ConferenceMention` — see `EvidenceSourceTypes.IsThirdPartyAttentionSource`). A company's own disclosures
  (press releases, filings, RSS, …) are first-party and add nothing. With only first-party collectors today
  `reach → 0` and Attention → 0 (correct: market attention is unmeasurable from own disclosures); a
  news/media collector makes it meaningful automatically.
- **EvidenceConfidenceScore** — *best-anchored + diversity bonus*:
  `100·bestConf·(0.6+0.4·bestQualWeight)·(0.7+0.3·divFactor)`, where `bestConf` is the **max** signal
  confidence and `bestQualWeight` the **max** quality weight among contributing evidence (was avg for both).
  Because the diversity factor now multiplies a max-anchored base, adding a signal/evidence item is
  **monotonic non-decreasing** — corroboration can never lower confidence.

Rationale: for a research tool whose whole premise is corroboration, more (and more diverse) evidence must
*earn* a stronger label, not a weaker one. Existing on-disk snapshots keep their recorded `ScoringVersion`
(`radar-formula-v1`) and remain reproducible under it; only the live formula moved to v2. Per the
spec-implementation checklist, `RadarScoreFormulaV1` was **deleted** (not left dormant) and its tests ported.

### Refinement — `radar-formula-v3` (spec 87): re-tune attention saturation and the under-the-radar discount

Two live runs on 2026-07-04 (the 8-company watch universe, after spec 84 made attention breadth real by
mapping `SourceName` to the actual publisher) exposed that **`AttentionScore` no longer discriminated**. The
v2 formula `100·reach/(reach+5)` with the small `+5` half-saturation put every ticker with normal coverage
(reach ≈ 16–28) on the flat top of the curve: seven of the eight companies clustered at **Attention 76–85**,
only the thinly-covered SPNS (~4 articles) sat low. The `(1 − Attention/200)` discount then haircut a
near-uniform **38–43%** off almost everyone, which **compressed the whole board** (the quality cluster jammed
at Opportunity ~40) and **penalised the most-covered quality names** — MRCY slid Investigate→Watch and AGYS
fell to Ignore purely because good coverage inflated its Attention into the saturated top of the curve.
`RadarScoreFormulaV3` (`Version = "radar-formula-v3"`, **maintainer-approved**) re-tunes exactly two
components via three constants; **Trajectory, EvidenceConfidence, SignalVelocity, the recency weighting, the
empty-window behaviour, and the `PreviousSignals`/window/provenance/contribution rules are byte-for-byte
unchanged from v2**. The v1/v2 Attention and Opportunity component formulas above are therefore *superseded by
radar-formula-v3* for those two components:

- **AttentionScore** — `AttentionHalfSaturation 5 → 12` (`100·reach/(reach+12)`): at the live reach values the
  covered cluster now lands at **57–70** and thinly-covered names at ~**15**, restoring a real 42–55-point
  spread; same saturating shape (asymptotic to 100, monotone in reach), gentler slope. `MediaReachWeight 0.5 →
  0.25` (`reach = distinctThirdPartySourceNames + 0.25·mediaSignals`): one event routinely spawns many
  near-duplicate articles, so raw media volume is duplication-prone — lean on distinct-publisher breadth
  (unchanged) while still letting a media-only source contribute *something*. *(The Attention component — the
  breadth definition and the `+12` saturation — is further superseded by radar-formula-v4 for Attention; see
  the spec-88 refinement below. The `0.25·mediaSignals` media term and the `÷250` divisor carry forward to v4
  unchanged.)*
- **OpportunityScore** — `OpportunityAttentionDivisor 200 → 250` (`Trajectory·(EC/100)·(1 − Attention/250)`):
  the near-uniform ~40% haircut is what compressed the board and demoted MRCY/AGYS. Combined with the raised
  saturation, `/250` softens the covered cluster's haircut to ~24–28% while a genuinely under-followed name
  (Att 15) keeps ~94% of its base.

The **under-the-radar principle is preserved**: Opportunity still falls monotonically as Attention rises, low
attention still earns a strictly larger multiplier than high attention, and it **never zeroes** — the maximum
haircut at Attention 100 is `100/250 = 40%`, still leaving 60%. Existing on-disk snapshots keep their recorded
`ScoringVersion` (`…+radar-formula-v2`) and remain reproducible under it; only the live formula moved to v3.
Per the spec-implementation checklist, `RadarScoreFormulaV2` was **deleted** (not left dormant) and its tests
ported. This is the sanctioned AD-6 formula-change mechanism (bump `Version`, update this entry), not drift;
`ScoringVersion` advances automatically via `_formula.Version` and `ScoringEngine.ScoringConfigVersion` bumped
`v8 → v9` (AD-10). *Accepted · 2026-07-04 — maintainer reviewed and approved the exact constants (`+12`,
`0.25`, `÷250`) and the 8-company before/after Opportunity table.*

### Refinement — `radar-formula-v4` (spec 88): source-quality tiering of attention breadth

Two live runs on 2026-07-04 (the 8-company watch universe, after spec 84 made `SourceName` the real publisher)
exposed the **root cause** behind the undifferentiated Attention that v3 recalibrated around: the distinct
third-party "publishers" driving reach are dominated by **algorithmic finance-content mills that cover
essentially every ticker** (MarketBeat, Zacks, Simply Wall St, StockStory, Moomoo, TradingView, Stock Titan,
GuruFocus, Defense World, Pluang, MarketScreener, …). Because v3 counted *distinct third-party `SourceName`s
equally*, "20 content mills auto-generated a blurb" scored the same breadth as "Reuters, Bloomberg, WSJ, CNBC
and an industry trade covered a real development" — Attention measured **media-noise breadth**, not genuine
market notice, so every normally-covered small-cap saturated. `RadarScoreFormulaV4`
(`Version = "radar-formula-v4"`) fixes the **Attention component only**; **Trajectory, EvidenceConfidence,
SignalVelocity, the media term (`0.25·mediaSignals`), the Opportunity discount *shape* (`÷250`), recency, the
empty-window behaviour, and the `PreviousSignals`/window/provenance/contribution rules are byte-for-byte
unchanged from v3**. The v1/v2/v3 Attention component formula above is therefore *superseded by
radar-formula-v4* for Attention:

- **AttentionScore breadth** — the flat distinct-publisher count becomes a **tier-weighted distinct-publisher
  sum**: `breadth = Σ over distinct third-party publishers of tierWeight(publisher)`, with content mills
  ≈`0.1`, unknown outlets `0.5`, and genuine outlets (Reuters, Bloomberg, WSJ, CNBC, AP, Financial Times,
  industry trades such as SpaceNews) `1.0`. `reach = breadth + 0.25·mediaSignals` (media term unchanged;
  it is a `MediaAttention` count, not a per-publisher term, so tiering does not apply). Distinct-by-publisher
  is preserved — a mill that appears 10× still contributes its weight once. The tier map is **config data in
  Infrastructure** (`Radar:Attention`, bound to `AttentionSourceTierOptions` / `ConfiguredAttentionSource
  Weights`), injected into the formula behind the Application `IAttentionSourceWeights` abstraction (AD-5); the
  formula stays a pure, deterministic function of `(input, immutable weights)` (AD-3). **Unknown publishers
  default to a non-zero weight (`0.5`)** so real coverage is never silently zeroed — worst case an un-listed
  real outlet is *under*-counted, not dropped.
- **AttentionHalfSaturation `12 → 3`** — tiering *shrinks* reach: a covered name drops from ~20 distinct
  publishers to ≈**2–6** genuine-equivalent ones. At that filtered scale v3's `+12` would re-collapse Attention
  at the *bottom* (everyone back near zero), so the saturation is re-tuned down to `+3`, re-centring the
  filtered covered cluster at ~**40–70** and leaving thin/mill-only names low (~15–20). Same saturating shape
  (asymptotic to 100, monotone in reach).

Because only Attention moves, the **under-the-radar principle is preserved**: Opportunity still falls
monotonically as Attention rises and never zeroes (the `÷250` divisor is unchanged), a mill-covered name now
gets a *low* Attention and thus a *smaller* discount but it also has a low reach so it is not spuriously
boosted, and a name with **more genuine outlets** now sits above one with fewer even at similar article counts
— differentiation on the right axis (genuine breadth over mill breadth). Existing on-disk snapshots keep their
recorded `ScoringVersion` and remain reproducible; only the live formula moved to v4. Per the
spec-implementation checklist, `RadarScoreFormulaV3` was **deleted** (not left dormant) and its tests ported.
This is the sanctioned AD-6 formula-change mechanism (bump `Version`, update this entry), not drift;
`ScoringVersion` advances automatically via `_formula.Version` and `ScoringEngine.ScoringConfigVersion` bumped
`v9 → v10` (AD-10). *Accepted · 2026-07-04 — maintainer reviewed and approved the tier weights (mill `0.1`,
unknown `0.5`, genuine `1.0`), the curated mill/genuine publisher lists, and the re-tuned
`AttentionHalfSaturation = 3.0`.*

### Refinement — `radar-formula-v5` (spec 89): magnitudes become config; structure stays versioned

`radar-formula-v2 → v3 → v4` all shipped within about a week purely to change **numbers** (attention
half-saturation, media weight, discount divisor, source-tier weights). Each number change spawned a new
`IScoreFormula` class (delete-old, port-tests) and a manual `ScoringConfigVersion` bump, because the ~20
magnitude constants lived as `const`s in the formula — the cost of encoding *tunable numbers* as *code
identity*. `RadarScoreFormulaV5` (`Version = "radar-formula-v5"`, **maintainer-approved**) ends that treadmill
by separating **structure** (which stays versioned code) from **magnitudes** (which move to config):

- **Magnitudes → `ScoringWeights`.** The ~20 magnitude `const`s (`RecencyFloor`, `TrajectoryNeutral`/`Scale`,
  `AttentionHalfSaturation`, `MediaReachWeight`, the five quality weights, the four EC base/span values,
  `DiversityTarget`, `VelocitySmoothing`/`Steady`, `OpportunityAttentionDivisor`) move into an immutable
  `Radar.Application.Scoring.ScoringWeights` record, bound from `Radar:Scoring:*` (a named-profile map:
  `Radar:Scoring:Profile` selects `Radar:Scoring:Profiles:{name}`, bound onto code defaults) and injected into
  the formula, which reads `_weights.X` instead of `const`s. **Every `ScoringWeights` default EQUALS the v4
  constant**, so a blank/absent config is **byte-identical** to v4 (pinned by test). This makes weight
  experimentation a **config edit** (run different profiles in parallel to distinct `--Radar:*Directory`
  outputs), **not** a new formula class. The v1–v4 magnitude/constant references above are therefore
  *superseded by radar-formula-v5*: the magnitudes now live in `ScoringWeights` and the recorded default values
  are the v4 values.
- **Only structure stays versioned.** The component shape, the fixed field-ordering used by the fingerprint,
  and the **direction signs** (`Positive +1` / `Negative −1`, `Neutral`/`Mixed` = 0) remain structural `const`s
  in the formula — flipping a sign is a structural change, not a weight experiment. A structural/shape change
  still bumps `_formula.Version` (a new `radar-formula-vN` class); a magnitude change no longer does.
- Fail-fast validation (`ScoringWeights.Validate`, called from the formula ctor AND the DI binder) throws on a
  nonsensical weight (zero/negative denominators `DiversityTarget` / `OpportunityAttentionDivisor` /
  `AttentionHalfSaturation`, negative quality/EC weights) so a misconfiguration cannot silently distort scoring.

Because defaults == v4, numeric output is identical; only the *identity* advances v4 → v5, marking the
structural change (a new injected dependency plus the content-fingerprint stamp — see AD-10). Existing on-disk
snapshots keep their recorded `ScoringVersion` and remain reproducible. Per the spec-implementation checklist,
`RadarScoreFormulaV4` was **deleted** (not left dormant) and its tests ported to `RadarScoreFormulaV5Tests`.
*Accepted · 2026-07-04 — maintainer approved the named-profile ergonomic and the magnitudes-→-config
refinement.*

Spec 90 (attention tier-calibration + publisher-name normalization) recalibrated the *attention weights*
without touching the formula: the unknown default dropped `0.5 → 0.25`, the mill denylist was expanded with
the observed long-tail aggregators (Finviz, Investing.com, Insider Monkey, Benzinga, TipRanks, StockAnalysis,
plus an explicit `Simplywall.st` alias), and `ConfiguredAttentionSourceWeights.Normalize` now folds domain-form
/ punctuation / spacing / case variants onto their curated key (lowercase, strip one trailing common-TLD token,
remove non-alphanumerics). This is **NOT a new formula version** — the reach *shape* is byte-for-byte unchanged
(same weighted-distinct-sum, `+3` saturation, media term); only `WeightFor`'s answers move. It therefore stays
`radar-formula-v5`, and the fingerprint **auto-re-stamps** (the effective attention descriptor changed) — no
manual `ScoringConfigVersion` bump; only the pinned default-fingerprint test constant was recomputed.
*Accepted · 2026-07-04 — maintainer sign-off granted on the recalibrated defaults / posture (denylist-expand +
`UnknownWeight 0.25`, with the allowlist flip as the documented config-only alternative).*

Spec 94 (recalibrate the default `MediaReachWeight` `0.25 → 0.10`) de-saturates `AttentionScore` without
touching the formula. A post-spec-91 live re-measure across the watch universe found Attention **saturated** —
every normally-covered small-cap landed ~**64–75** — because in
`reach = weightedBreadth + MediaReachWeight·mediaCount` the raw **article-count** term (`0.25·mediaCount`)
dominated the tier-weighted **distinct-publisher**
breadth term roughly **5:1**, so Attention tracked article **volume** (the content-mill noise every ticker gets),
not genuine market **notice**, and the under-the-radar discount fired ~uniformly. A live `MediaReachWeight` sweep
(baseline `0.25` vs `0.15 / 0.10 / 0.05`, via `scripts/run-radar.ps1` profiles into isolated output dirs) widened
the quality gap between a genuinely-covered name (ERII) and a known all-aggregator name (HLIO) monotonically as
the weight dropped — ERII−HLIO Attention gap `4 → 7 → 9 → 14`. **`0.10` is the chosen de-saturating middle**
(Attention spread ~**49–63**), keeping Attention a light, breadth-leaning modifier while Trajectory + Evidence
drive the score. This is a `ScoringWeights` **magnitude** change, **NOT** a structural one: the reach *shape* is
byte-for-byte unchanged, so it **stays `radar-formula-v5`** — no new formula class, no manual `ScoringConfigVersion`
bump. Because `MediaReachWeight` is in the hashed canonical string, the **default fingerprint re-stamps
automatically** (`radar-scoring-fp-c1e71b26adf3 → radar-scoring-fp-5cd50423f408`); the pinned default-fingerprint
test **and** the v4-equivalence pin were **intentionally** updated (representative-input Attention `44 → 42`) — the
spec-89 "blank config == v4 byte-identical" property is **deliberately superseded** here, not regressed. A
tier-weighted-article-count `v6` (weighting the media term by publisher tier) was considered and **skipped as
marginal** for this mostly-aggregator-covered universe. *Accepted · 2026-07-04.*

### Refinement — `radar-formula-v6` (spec 111): corroboration-aware Trajectory

**Problem.** The v5 `TrajectoryScore` was a confidence/recency-weighted **mean** of `sign·strength` over the
current-window directional signals. A mean gives a lone dissenting signal weight comparable to *each* of many
corroborating signals, so **corroboration was not rewarded**: five agreeing customer wins moved Trajectory no
more decisively than one, and a single countervailing signal could overturn the read. On the live 2026-07-17
run AEHR had a strong, corroborated positive thesis (~4 `CustomerWin` + a `StrategicPartnership`) yet a single
uncorroborated insider-sale Negative dragged its Trajectory **79 → 68**. Radar's philosophy is
"evidence before opinions, corroboration matters" — a direction backed by many independent high-strength
signals should be more robust than one asserted by a single signal.

**Shape (the ONLY component that changed vs v5).** `RadarScoreFormulaV6` (`Version = "radar-formula-v6"`,
**maintainer-gated — sign-off requested in PR #114, not yet granted**) splits the current-window directional signals into a **positive mass** and a
**negative mass**, each the per-signal `strengthᵢ·wᵢ` sum over that direction where the per-signal weight
`wᵢ = confidenceᵢ·recencyᵢ` is **byte-identical to v5** (Neutral/Mixed still contribute 0 to both masses), and
combines them as

```
T_raw = TrajectoryBand · (Mpos − Mneg) / (Mpos + Mneg + k)          ∈ [-10, 10]
trajectoryScore = Score(TrajectoryNeutral + TrajectoryScale · T_raw)   (50 + 5·T_raw, clamped)
```

`TrajectoryBand` (= `10.0`) is a **structural** `const` in the formula — the strength ceiling / band
half-width (the same implicit `[-10,10]` band the v5 mean of `sign·strength` occupied), a shape decision, not a
tunable magnitude (it sits beside the direction-sign consts). `k` is the new config **magnitude**
`ScoringWeights.TrajectoryCorroborationK` (default `10.0`) — the corroboration-smoothing constant: the
directional mass (≈ one full-strength·full-confidence·full-recency signal) that must accrue before Trajectory
swings halfway; larger `k` damps small directional sets more. It is a denominator smoother, so
`ScoringWeights.Validate()` requires it strictly positive.

**Invariants (checked by tests).** Monotone (adding a Positive never lowers Trajectory; adding a Negative never
raises it); direction-**symmetric** (a corroborated negative cluster moves Trajectory down as decisively as a
corroborated positive cluster moves it up — no positive bias); empty directional set → neutral `50`
(`0/(0+k)=0`, the same `sumMass<=0` guard shape v5 used); an **isolated** dissenter against a strong agreeing
majority is **damped** relative to the v5 mean but **not zeroed** (the dissent is recorded — its Trajectory is
strictly below the no-dissenter majority); a **corroborated** dissenting cluster still **bites** decisively.
**Only** Trajectory changed — Attention (incl. the spec-109 collapsed media set), EvidenceConfidence,
SignalVelocity, Opportunity, recency, the empty-window behaviour, the `PreviousSignals` handling, the direction
SIGNS, and the per-signal provenance `ScoreContribution` weights (`sign·strength·conf·recency`, provenance is
per-signal; the consensus shaping is an aggregate) are **byte-for-byte** as v5 (proven by the ported tests).

**Version obligation.** This is a formula **STRUCTURE** change → `_formula.Version` advanced
`radar-formula-v5 → v6`; `ScoringVersion` advances automatically and `ScoringConfigVersion` **re-stamps via the
derived fingerprint** (the `FormulaVersion` input changed) — default
`radar-scoring-fp-abbdf9fab44f → radar-scoring-fp-c45fb79092ea`. Every new magnitude lives in `ScoringWeights`
(config), so future tuning of `k` is a config edit, not another formula class. Per the spec-implementation
checklist `RadarScoreFormulaV5` was **deleted** (not left dormant) and its tests ported to
`RadarScoreFormulaV6Tests` with the trajectory-dependent pins recomputed (representative headline input
Trajectory `86 → 72`, Opportunity `43 → 36`; the lone-directional Helios input Trajectory `80 → 61`). *Proposed
· 2026-07-17 — maintainer-gated structure; sign-off requested in PR #114, not yet granted.*

**Status.** Accepted · 2026-06-28 (specs 16–17; formula co-designed with maintainer). Refined ·
2026-07-01 (spec 58, `radar-formula-v2` — maintainer-approved). Refined · 2026-07-04 (spec 87,
`radar-formula-v3` — maintainer-approved). Refined · 2026-07-04 (spec 88, `radar-formula-v4` — Accepted,
source-quality tiering). Refined · 2026-07-04 (spec 89, `radar-formula-v5` — Accepted, magnitudes → config;
structure stays versioned). Refined · 2026-07-04 (spec 90 — attention tier recalibration + publisher-name
normalization; **not** a formula-version bump, fingerprint auto-re-stamps; Accepted · 2026-07-04). Refined ·
2026-07-04 (spec 94 — default `MediaReachWeight 0.25 → 0.10` de-saturating recalibration; a `ScoringWeights`
magnitude change, **not** a formula-version bump; fingerprint auto-re-stamps and the v4-byte-identical property is
deliberately superseded; Accepted · 2026-07-04). Refined · 2026-07-17 (spec 111, `radar-formula-v6` —
maintainer-gated structure: corroboration-aware Trajectory splitting the directional signals into positive vs
negative mass combined through the config constant `k` = `TrajectoryCorroborationK`; only Trajectory changed,
every other component byte-identical to v5; fingerprint re-stamped `abbdf9fab44f → c45fb79092ea`; **Proposed —
sign-off requested in PR #114, not yet granted**).

---

## AD-7 — Evidence quality is a declared input; the pipeline run-instant is captured after collection

**Decision (two related conventions, spec 25).**

1. **Evidence quality is an input, not hard-coded.** `LocalFileEvidenceCollector` reads an optional
   `quality` from each evidence document and maps it to `EvidenceQuality` (case-insensitive,
   defined-enum-only, digit-only rejected), defaulting to `Unknown` when absent/unparseable. It no
   longer hard-codes `Unknown`. Quality legitimately drives downstream behaviour — the reviewer's
   weak-source rule and `EvidenceConfidenceScore` — so `Unknown`-quality evidence stays conservative
   ("Needs more evidence") while higher-quality evidence can reach stronger labels. `SourceType` for
   this collector stays `LocalFile`.

2. **`RadarPipelineRunner` captures `asOfUtc` *after* collection.** The single run-instant (which feeds
   the mapper `createdAtUtc`, the scoring `windowEndUtc`, and the report `periodEndUtc`) is taken once,
   immediately after `CollectAsync` returns — never at method entry. Otherwise freshly collected
   evidence (whose `ObservedAtUtc` falls back to `CollectedAtUtc`) sorts just *after* `asOfUtc` and
   drops out of the `(start, end]` window, scoring from zero signals in the same run.

**Why.** Both came out of an end-to-end smoke run. They are intentional and settled — the
reviewer/planner must **not** re-flag "the collector should set a fixed quality" or "capture the
clock at method entry". The run-instant remains a single value used identically everywhere; only its
capture *timing* moved.

**Status.** Accepted · 2026-06-28 (spec 25).

---

## AD-8 — MVP direction is collector-driven; persistence is files-first (no PostgreSQL for MVP)

**Decision.** `docs/radar-full-pipeline-spec.md` was replaced by the **collector-driven** master spec
(the prior content is superseded). The MVP is **collector-driven**: Radar automatically *fetches*
public evidence (first real collector = **RSS press-release collector** reading per-company
`sourceFeeds` from the watch universe), rather than depending on manually-dropped inbox files — the
local-file collector is retained for tests/debug only. Persistence stays **files-first** for the MVP:
file-based JSON/Markdown under `data/`, with the current in-memory repositories acceptable until a
spec explicitly needs more. **PostgreSQL/Dapper is deferred** and must not be introduced unless a spec
explicitly requires it; the six queued Postgres specs (26–31) were **dropped** in favour of this
direction. `docs/radar-schema-spec.md` remains the domain-record reference (its Postgres orientation
is roadmap, not MVP).

**Why.** Maintainer redirection: prove the collect → evidence → signal → score → weekly-report loop on
real fetched evidence before investing in a database. The reviewer/planner must **not** re-propose
Postgres for the MVP, and must treat the collector-driven spec as the authoritative pipeline master.

**Status.** Accepted · 2026-06-28 (maintainer; supersedes the queued persistence specs 26–31).

---

## AD-9 — Allowed report labels: union of six (incl. `Ignore`)

**Decision.** The allowed human-action labels are the **union** of the prior set and the collector-driven
spec's set: `Investigate`, `Watch`, `Ignore`, `Needs more evidence`, `Thesis improving`,
`Thesis deteriorating`. This **re-admits `Ignore`** (previously deliberately excluded in spec 18) while
keeping `Thesis improving`/`Thesis deteriorating`. The advice-language ban is unchanged: never emit
"buy", "sell", "guaranteed upside", "safe bet" (or `Buy`/`Sell`/`Strong Buy`/`Price Target`). CLAUDE.md
and `.claude/agents/radar-philosophy.md` are updated to match.

**Why.** The collector-driven master spec lists `Ignore` as an allowed action (it has an "Ignore / Low
Signal" report section); the maintainer chose the permissive union so low-signal companies can be
labelled `Ignore` without losing the thesis-trajectory labels. A follow-up code slice updates
`MarkdownWeeklyReportRenderer` (allowed-label set) and `WeeklyReportActionPolicyV1` (may now emit
`Ignore`, e.g. for low-signal companies) with tests. Until that slice lands, the renderer still rejects
`Ignore`; the policy does not yet emit it.

**Status.** Accepted · 2026-06-28 (maintainer; supersedes the spec-18 exclusion of `Ignore`).

---

## AD-10 — Any scoring-affecting change MUST bump `ScoringEngine.ScoringConfigVersion`

**Decision.** `ScoringEngine.ScoringConfigVersion` (a code constant, currently
`"radar-scoring-config-v2"`) stamps every `CompanyScoreSnapshot` and identifies the whole
scoring-affecting pipeline **generation** — distinct from the formula/engine identity `ScoringVersion`
(AD-6). Any change that can move scoring output — the scoring formula, the extractor rules (including the
`GovernmentContract` materiality tiers), or `ScoringOptions` — **MUST bump `ScoringConfigVersion`** in the
same slice. It is a **code constant**, never an ops-tunable config value: bumping it must require a code
edit that trips the spec-implementation checklist, and it must move in lockstep with the code.

**Why.** The stamp (spec 69) gates the cross-run delta clause **and** the
`Thesis improving`/`Thesis deteriorating` action label: two snapshots are compared only when their
`ScoringConfigVersion` values are non-null and equal. When they differ, the report renders
`(scoring updated)` instead of a numeric delta and the policy falls back to its no-previous behaviour —
so a scoring **recalibration** can never fabricate a thesis-trajectory label (the exact defect spec 69
fixed, where spec 66's materiality change dropped Mercury Systems' Trajectory 80→75 and produced a false
`Thesis deteriorating`). This correctness property holds **only** if every scoring-affecting change bumps
the stamp; a forgotten bump silently re-creates that bug. Spec 70 correctly bumped v1→v2 — but only by
author discipline against a convention that lived nowhere discoverable. Recording it here (and in the
`CLAUDE.md` checklist) gives the next scoring-affecting change a single documented obligation.
Cross-reference AD-6 (formula versioning) and spec 69 (the stamp and its comparability gate).

### Amendment — spec 89: the stamp becomes a derived content fingerprint (property preserved, made automatic)

Spec 89 makes scoring magnitudes runtime-configurable (`ScoringWeights`, AD-6 v5 refinement). A hand-typed
`ScoringConfigVersion` string can no longer *uniquely determine* the score — two runs with the same string but
different bound weights would be wrongly judged comparable, silently re-creating the spec-69 defect. So
`ScoringConfigVersion` is **no longer a hand-bumped code constant** but a **deterministic content fingerprint of
the effective resolved scoring config**: the structure identity (`EngineVersion` + `_formula.Version`) **plus
every `ScoringWeights` value plus the attention tier-map descriptor**
(`IAttentionSourceWeights.CanonicalDescriptor()`), serialized with a fixed explicit field ordering and
culture-invariant round-trip number formatting, then hashed via a canonical lowercase-hex SHA256 (the shared
`EvidenceNormalizer` idiom, AD-3). It is computed **once** in `ScoringEngine` (`ScoringConfigFingerprint.Compute`)
and stamped on every snapshot. The AD-10 correctness property is **preserved and strengthened**: any
output-affecting change (formula shape, any weight, the tier map) changes the fingerprint **automatically**, so
it can no longer be silently forgotten — the "bump" obligation is now discharged by *derivation*. The spec-69
comparability gate is **unchanged in shape** (still `Ordinal` string equality of `ScoringConfigVersion`, now
comparing fingerprints); a pinned default fingerprint keeps default runs comparable and catches accidental
default-weight/tier drift. The **only remaining human code-version obligation** is bumping `_formula.Version`
(structure) when the formula *shape* changes (AD-6). Cross-reference AD-6 (formula/weight versioning), AD-3
(determinism), and spec 69 (the stamp and its comparability gate). *Accepted · 2026-07-04 — maintainer
approved the content-fingerprint stamp.*

### Amendment — spec 91: the effective config is persisted content-addressed by the fingerprint (weights become recoverable)

Spec 89 made `ScoringConfigVersion` a **one-way** SHA256 fingerprint: it gates comparability and proves
integrity, but the actual weight **values** cannot be recovered from the hash. Spec 91 closes that provenance
gap **additively** — it does **not** change scoring output, the formula, the component math, or the fingerprint
**value** (no `_formula.Version` bump, no `ScoringConfigVersion` change). On each run the `ScoringEngine` exposes
its `EffectiveConfig` (the same tuple the fingerprint hashes: engine + `_formula.Version` + every `ScoringWeights`
value + the attention `CanonicalDescriptor()`, plus the resulting fingerprint), and the runner persists it
**once per run** via `IScoringConfigStore` to `data/scoring-configs/{fingerprint}.json`. The store is
**content-addressed** (filename == the fingerprint) and **insert-if-new / immutable** — a given fingerprint's
config is by definition fixed, so an existing file is never overwritten (the AD-1 evidence-immutability mirror,
the deliberate opposite of `FileScoreSnapshotStore`'s upsert-by-Id). This makes the hash **checkable** rather
than opaque: recomputing the fingerprint from the stored config equals the filename. A historical snapshot's
`ScoringConfigVersion` stamp now dereferences back to the exact weights that produced it — the natural
completion of AD-10-as-amended, required **before** any custom-`Radar:Scoring:Profile` experiment run persists
snapshots whose weights would otherwise live nowhere durable. Files-first + best-effort/graceful-degrade
posture (AD-8): a disk failure logs + continues and never aborts scoring (the snapshot still carries the stamp).
No run-record pointer was added (default) — the snapshot→fingerprint→config chain already closes the loop.
Cross-reference AD-1 (insert-if-new immutability), AD-3 (canonical/deterministic serialization reused from
spec 89 — the store must not invent a second serialization), AD-8 (files-first), and spec 89 (the fingerprint).
*Accepted · 2026-07-04 — provenance completion (natural completion of AD-10-as-amended-by-89), not a
settled-convention reversal.*

### Amendment — spec 95: the fingerprint folds the enabled signal-source set

Spec 89 folded structure + weights + attention descriptor into the fingerprint, but **not the enabled
signal-source set** — the set of enabled evidence collectors, nor the deterministic extractor's rule identity.
So enabling/disabling a collector changed scoring **output** while leaving the stamp **unchanged**: a run
*with* the `secform4` insider collector (spec 93, which adds directional `InsiderBuying` signals that move
`TrajectoryScore`) and a run *without* it carried the **same** fingerprint and were therefore **falsely judged
comparable** — the exact spec-69 defect the stamp exists to prevent. Spec 95 closes that gap: the derived
fingerprint now **also folds a canonical signal-source descriptor** — the enabled collector **NAMES** (distinct,
`Ordinal`-ordered, escaped) plus the extractor rule-set identity `KeywordSignalExtractor.RuleSetVersion` —
appended as a new `srcDesc` field **after** the attention descriptor (existing field ordering unchanged). It is
computed once in `ScoringEngine` from the injected `ISignalSourceDescriptor` (default `SignalSourceDescriptor`,
DI-resolved over `IEnumerable<IEvidenceCollector>` at resolution time so it sees every collector even though the
Worker registers them after `AddRadarApplicationServices`; it reads only `CollectorName`, never collects). So
enabling/disabling a collector (or bumping `RuleSetVersion` for a scoring-affecting rule-STRUCTURE change) now
re-stamps `ScoringConfigVersion` **automatically**, restoring the spec-69 comparability guarantee across a
collector-set transition. The self-verifying content-fingerprint property is **preserved and strengthened**: no
new hand-bumped constant gates comparability — the descriptor is derived from the composed graph; the persisted
`EffectiveScoringConfig` carries the `SignalSourceDescriptor` field verbatim so recompute-from-stored still
equals the filename. No scoring **math** change — only the fingerprint *input* widens; the default fingerprint
re-stamps automatically **`radar-scoring-fp-5cd50423f408 → radar-scoring-fp-55270b9d8fad`** (default descriptor
`rules=radar-keyword-rules-v1;collectors=RssPressReleaseCollector,newssearch,sec-edgar,sec-form4,usaspending;` —
the collector tokens are the concrete `IEvidenceCollector.CollectorName` values, `Ordinal`-sorted, NOT the
`Radar:Collectors` config "kind" tokens; e.g. `rss` reports `RssPressReleaseCollector` and `sec` reports
`sec-edgar`). This is
the first of two sequenced slices; spec 96 (move the insider materiality tiers to config) builds on this
plumbing and, once those magnitudes are hashed by value, they will no longer require a `RuleSetVersion` bump —
only rule STRUCTURE changes will. *Accepted · 2026-07-05 — comparability-gap closure; property preserved and
strengthened, no math change.*

### Amendment — spec 96: the insider materiality tiers move to config and are hashed by value

Spec 93's `InsiderBuying` materiality — the buy/sell net-value **tier tables** and the multi-insider
**cluster boost** — lived as **code constants** in `KeywordSignalExtractor`, so tuning the buy-vs-sell
asymmetry required a code change (and, being part of the extractor rule identity, a `RuleSetVersion` bump).
Spec 96 relocates those magnitudes into a config-bound Application options record `InsiderMaterialityWeights`
(`BuyTiers`, `SellTiers`, `ClusterBoost`; `Radar:Insider:Profiles:{name}:*`), exactly mirroring the spec-89
`ScoringWeights` pattern — injected into the extractor (which `Validate()`s it in its ctor) and bound via a new
`AddRadarInsiderMateriality` binder (named-profile select, fail-fast on a missing profile or an invalid tier).
**The code defaults == the spec-93 values**, so default insider signal Strengths are **byte-identical** (pinned
by the extractor tests); only the fingerprint *input* widens. Splitting the single symmetric spec-93 table into
separate buy/sell tables (both defaulting to the same values) is what makes a deliberate buy-vs-sell asymmetry
expressible from a run profile with **no code change**. Because the tiers are now part of the **effective scoring
config**, their values are folded into the `ScoringConfigVersion` fingerprint **by value** (building on spec 95):
`ScoringConfigFingerprint.Compute` gains an `insiderDesc` field appended **after** `srcDesc` (existing ordering
unchanged), computed once in `ScoringEngine` from `InsiderMaterialityWeights.CanonicalDescriptor()`; the persisted
`EffectiveScoringConfig` carries the descriptor verbatim so recompute-from-stored still equals the filename. So an
insider **magnitude** change now re-stamps the fingerprint **automatically** and is a **config edit** — it needs
**no `RuleSetVersion` bump**; only a rule **STRUCTURE** change (the phrase→direction table shape) still bumps
`RuleSetVersion`. No scoring **math** change — the default fingerprint re-stamps automatically
**`radar-scoring-fp-55270b9d8fad → radar-scoring-fp-7e56a8007342`** (default insider descriptor
`buy=5000000:8,1000000:7,250000:6,50000:4,-79228162514264337593543950335:2;sell=<same>;cluster=1;`). The
`GovernmentContract` award tiers deliberately remain code constants (a parallel config move is a possible future
slice). *Accepted · 2026-07-05 — magnitude→config relocation; property preserved and strengthened, no math change.*

### Lineage — spec 103: `RuleSetVersion` v2 → v3 (new `HiringActivity` rule group); default re-stamps automatically

Spec 103 adds the ATS job-board hiring collector (`hiringats`, opt-in **OFF** by default) and one new
`KeywordSignalExtractor` rule group mapping its fixed phrase `hiring activity (open roles)` to a **Neutral**
`SignalType.HiringActivity` — a rule-**STRUCTURE** change, so `RuleSetVersion` bumps
`radar-keyword-rules-v2 → radar-keyword-rules-v3` and the spec-95 signal-source descriptor re-stamps the default
fingerprint **automatically**: **`radar-scoring-fp-8d638b90d4aa → radar-scoring-fp-c9e609ed53e9`**. The enabled
default collector set is **unchanged** (still the 6-collector baseline — `hiringats` is not in `default.json`);
the fingerprint moves solely on the rules identity. **Scoring math is byte-identical** — the new rule matches
only the hiring phrase, which no existing evidence contains, and the collector is opt-in-off, so every company
scores exactly as before; there is no fingerprint-safe way to add a scoring-affecting signal type (spec 95
working as intended). No `_formula.Version` / weight / attention-tier / insider-tier change (`radar-formula-v5`
stays). Note for the efficacy visual (spec 101 / AD-14 read side): the current renderer segments on raw
`ScoringConfigVersion` equality, so it will draw a **cosmetic** segment boundary at this re-stamp even though the
scores are fully continuous — an input-hash artifact, not a measurement break; the real fix is the deferred
efficacy slice-2 score-continuity-aware segmentation.

**Status.** Accepted · 2026-07-02 (trunk cleanup slice; convention introduced by spec 69, first bumped
by spec 70). Amended · 2026-07-04 (spec 89 — stamp becomes a derived content fingerprint; property preserved
and made automatic; Accepted). Amended · 2026-07-04 (spec 91 — the effective config is persisted
content-addressed by the fingerprint so the weights behind a historical snapshot are recoverable; additive,
no fingerprint-value change; Accepted). Amended · 2026-07-05 (spec 95 — the fingerprint folds the enabled
signal-source set (collector names + extractor rule-set identity); enabling/disabling a collector re-stamps
automatically; default re-stamps radar-scoring-fp-5cd50423f408 → radar-scoring-fp-55270b9d8fad; Accepted).
Amended · 2026-07-05 (spec 96 — the insider buy/sell materiality tiers + cluster boost move to config
(`InsiderMaterialityWeights`, default == spec 93) and are folded into the fingerprint by value; an insider
magnitude change is now a config edit needing no `RuleSetVersion` bump; default re-stamps
radar-scoring-fp-55270b9d8fad → radar-scoring-fp-7e56a8007342; Accepted). Lineage · 2026-07-07 (spec 103 —
`RuleSetVersion` radar-keyword-rules-v2 → v3 for the new `HiringActivity` rule group; default re-stamps
radar-scoring-fp-8d638b90d4aa → radar-scoring-fp-c9e609ed53e9; scoring math byte-identical, `hiringats`
collector opt-in-off).

---

## AD-11 — AI capability seam: a config-driven `IChatClient` factory, provider SDKs Infrastructure-only, opt-in

**Decision (proposed for maintainer approval — spec 72).** Radar's AI capability is introduced as a **seam**,
not a behaviour:

- **`IChatClient` (`Microsoft.Extensions.AI`) is Radar's single AI abstraction.** Every future AI consumer codes
  against `IChatClient` (and the typed `GetResponseAsync<T>` structured-output extension in later slices), never
  against a provider SDK. The seam is exposed through `Radar.Application.Ai.IChatClientFactory` (`IChatClient Create()`)
  — Application depends only on the `Microsoft.Extensions.AI` abstraction family (permitted by AD-5).
- **Config-driven provider selection.** `Radar:Ai:Provider` (case-insensitive) selects the provider at startup, with
  **Anthropic** (hosted Claude) and **Ollama** (local, keyless) as the initial providers. `ChatClientFactory`
  (Infrastructure) switches on the provider and news up the concrete client; `AddRadarAi` fails fast with clear
  `Radar:Ai:*` messages on blank/unknown provider, blank model, `anthropic` with a blank key, and `ollama` with a
  blank/non-absolute-URI endpoint. Both the factory and a factory-produced singleton `IChatClient` are registered
  (plain `AddSingleton`; the provider SDKs manage their own HTTP transport, so no named `HttpClient`).
- **Provider SDKs are confined to `Radar.Infrastructure`.** `Anthropic` and `OllamaSharp` are referenced **only**
  inside `ChatClientFactory` — no provider SDK type leaks to Application/Domain/Worker (materialises AD-5's
  `Microsoft.Extensions.AI` clause and its "concrete provider SDKs stay in Infrastructure" rule into a concrete seam).
- **AI is opt-in.** A blank `Radar:Ai:Provider` (the default) means **AI is DISABLED** — `AddRadarAi` is not called,
  no `IChatClientFactory`/`IChatClient` is registered, and no provider packages load at runtime. The default pipeline
  is byte-for-byte unchanged.

**Why.** `IChatClient` is the universal abstraction later AI slices (the directional filing-signal arc) will depend on.
Introducing the seam standalone — with no consumer, no prompt, no `GetResponseAsync` call — lets those slices build on
a stable, tested, provider-neutral interface instead of re-litigating provider wiring inside a feature, while keeping
concrete providers behind the AD-5 boundary and leaving existing runs untouched.

**Status.** Proposed 2026-07-03; Accepted 2026-07-03 (spec 72; cross-references AD-5). The seam now has a real
consumer: `IFilingAnalyzer`'s implementation (`ChatFilingAnalyzer`, spec 74) codes directly against
`IChatClient` / `IChatClientFactory` behind Infrastructure. The surrounding directional-filing arc exercises the
seam through that analyzer rather than touching `IChatClient` itself: `ISecEarningsReleaseReader` (spec 73) is a
plain SEC HTTP reader with no AI dependency, and `IDirectionalFilingSignalSource` (spec 75) depends on
`IFilingAnalyzer`. All three slices are merged — confirming the abstraction held.

---

## AD-12 — AI enrichment is an opt-in `RadarPipelineRunner` step behind an Application interface (not a second extractor)

**Decision.** AI enrichment of the pipeline is an **opt-in step in `RadarPipelineRunner`** behind a
nullable-optional Application interface (the first being `IDirectionalFilingSignalSource`, spec 75), threaded
through the **same** `map → resolve → review → store` tail (`MapResolveReviewStoreAsync`) as deterministic
keyword signals. It is **not** a second `ISignalExtractor` (the runner injects a single extractor and has no
multi-extractor composition seam), **not** a new collector, and **not** a new stage type. When AI is disabled
(blank `Radar:Ai:Provider`) the service is not registered, the runner's optional dependency is `null`, and the
step is skipped — the default graph is byte-for-byte unchanged.

**Why.** Reuses the runner's existing provenance/validation/review/store machinery verbatim; keeps AI/HTTP
entirely behind Infrastructure interfaces (materialises AD-5 + AD-11); leaves the deterministic extractor
untouched (deterministic-before-AI); and makes "AI off ⇒ zero change" structural (the service is registered only
inside the `Ai.Provider`-non-blank gate). The alternatives — a second extractor, a dedicated collector/stage —
were evaluated and rejected in spec 75. Future AI consumers (further filing reads, other enrichment) should follow
this same opt-in-runner-step-behind-an-Application-interface shape rather than re-debating the integration seam.

**Status.** Accepted · 2026-07-03 (pattern established by spec 75; cross-references AD-5, AD-11).

---

## AD-13 — Domain `FilingSentiment` doubles as the AI structured-output DTO

**Decision.** The Domain `FilingSentiment` record (`FilingDirection Direction`, `decimal Confidence`,
`string Rationale`) is **reused as the `GetResponseAsync<T>` structured-output DTO** for the AI filing analyzer
(spec 74), rather than maintaining a separate wire/DTO type. Accepted as-is for the MVP.

**Why.** The Domain shape and the AI structured-output shape are currently identical; a separate DTO would be
duplicative ceremony with a hand-written mapping for no present benefit. The analyzer already validates and clamps
the AI output (spec 74) before it becomes a Domain value, so the coupling does not weaken the
typed-and-validated-before-persistence rule.

**Status.** Accepted · 2026-07-03 (L3, deferred by spec 76). **Revisit if** the AI wire shape must diverge from
the Domain record (e.g. extra provider-specific fields, a different confidence encoding), or a second AI
structured output needs its own DTO — at which point separate the DTO from the Domain record in a dedicated slice.
Recorded so the reviewer does not flag the Domain-as-DTO coupling as unrecorded drift.

---

## AD-14 — Price data is validation/reference-only: never evidence, never a signal, never a scoring input

**Decision.** Daily stock-price history is acquired and persisted as a **reference / validation dataset**
(`data/prices/{ticker}.json`) via a **dedicated seam** — `IPriceHistoryReader` (Application) + an
Infrastructure HTTP reader + `IPriceHistoryStore` — that is **structurally separate** from the evidence
pipeline. Price is **NOT** an `IEvidenceCollector`, produces **no** `CollectedEvidence`/`EvidenceItem`, is
**not** in the collector `IEnumerable` the runner consumes, and its acquisition step runs **outside**
`IRadarPipeline` (the collect→map→resolve→review→store→score→report path). Price is therefore **never**
extracted into a signal and **never** an input to scoring. The `data/prices/` store is consumed by nothing
in the scoring/evidence/signal/report path today; it exists solely for a **future** price-efficacy
validation/backtest spec. Price acquisition is **opt-in** (`Radar:Prices:Enabled`, default `false`); when
disabled the pipeline graph is byte-for-byte unchanged.

**Why.** Radar is a research assistant, not a trading bot ("signals before stories", "avoid hype loops" —
philosophy). If price entered the evidence pipeline it would become eligible for signal extraction and
scoring, turning business-trajectory research into price-chasing — the exact failure mode Radar exists to
avoid. Making the boundary **structural** (a separate seam and store, not a convention) means a future
change cannot accidentally let price influence a signal or a score without deleting this seam and tripping a
reviewer. The price reference dataset lets a later spec **validate** whether Radar's signals preceded
business improvement, without ever feeding price back into the signals being validated. The reviewer/planner
must **not** propose making price a collector/evidence/signal/scoring input; doing so requires superseding
this decision.

**Status.** Accepted · 2026-07-04 (maintainer established this intent; spec 92). **Amended 2026-07-06 (spec
101 — the read side: a price-efficacy visual, read-only over score history + price; see below).**
Cross-references the philosophy (signals before stories / not a trading bot), AD-5 (layering), AD-8
(files-first), AD-9 (no advice language), AD-3 (determinism). Surfacing a reference price in the report is
**deferred** to the future validation-report spec.

### Amendment — spec 101: the efficacy/validation-reporting layer is the READ side of AD-14 (read-only over score history + price)

The price reference dataset (this AD) gains its first consumer: a **price-efficacy visual** that JOINs a
company's persisted score-snapshot history (`IScoreSnapshotFileStore`) to its daily price series
(`IPriceHistoryStore`) and emits a per-company score-vs-price **SVG + CSV** under `data/efficacy/`. This
efficacy subsystem is **strictly read-only over score history + price and emits artifacts only** — it
**never** writes back into `evidence → signal → score`, is **not** in `IRadarPipeline`, and depends on no
collector/evidence/signal/scoring **write** path. It runs as an **opt-in** Worker step
(`Radar:Efficacy:Enabled`, default `false`); disabled leaves the graph byte-for-byte unchanged. The score
series is **segmented by `ScoringConfigVersion`** (AD-10) so a trend line is never drawn across a
formula/weight change. Framing stays AD-9-clean: a score-vs-price overlay is a **research statistic**, never
a performance/advice claim (no "return/outperform/buy"). This amendment records that the READ side of AD-14
exists and is bounded: **price (and score history) may be READ for validation/visualisation but must never
flow back into scoring** — doing so still requires superseding AD-14. *Accepted · 2026-07-06 — the read side
of the price-validation boundary; no scoring math change.*
