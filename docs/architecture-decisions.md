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
**maintainer-approved · 2026-07-17** — shape signed off in-session; `k=10` default, retunable via config)
splits the current-window directional signals into a **positive mass** and a
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
Trajectory `86 → 72`, Opportunity `43 → 36`; the lone-directional Helios input Trajectory `80 → 61`). *Accepted
· 2026-07-17 — maintainer-approved structure (shape signed off in-session; `k=10` default retunable via config).*

### Refinement — `radar-formula-v7` (spec 117): notedness-aware Opportunity discount

**Problem.** Radar's mission is to surface improvement **before the market notices**, and Opportunity already
discounts by Attention — but Attention measures third-party publisher breadth *in Radar's own feeds*, which is
blind to **true notedness**. On the live 2026-07-20 baseline JNJ (a $400B mega-cap on a real but fully-priced
quarter) surfaced at #2 "Thesis improving," Opp 45, right beside AEHR — because JNJ's Attention is **21** and
AEHR's is **19**, nearly identical. No divisor tightening can separate them (it would discount both equally);
the missing ingredient is a "how-followed-already" input. Market cap is the obvious proxy but is
**price-derived → forbidden as a scoring input (AD-14)**; the clean, deterministic, non-price proxy is a
**curated following tier in the company seed**.

**Shape (the ONLY component that changed vs v6).** `RadarScoreFormulaV7` (`Version = "radar-formula-v7"`;
**maintainer-approved · 2026-07-20** — shape + constants signed off in-session, AD-6 structure gate) keeps the
measured-attention discount as one term and adds a curated-following term:

```
followingDiscount = 1 − (Attention / OpportunityAttentionDivisor) · OpportunityAttentionDiscountWeight
                      − TierDiscount(tier) · FollowingTierDiscountWeight
Opportunity       = Trajectory · (EvidenceConfidence/100) · clamp(followingDiscount, OpportunityDiscountFloor, 1)
```

`tier` is the company's `FollowingTier` (`Small`/`Mid`/`Large`/`Mega`, a new Domain enum) — **curated seed
metadata** in `data/companies.json` (`followingTier`, case-insensitive; absent/unrecognized fail-safes to
`Small`), **never price/market-cap/volume-derived (AD-14)**; the engine loads it via `ICompanyRepository`
(missing company ⇒ `Small`, no throw). All seven magnitudes are `ScoringWeights` config, folded into the
fingerprint **by value**: `OpportunityAttentionDiscountWeight = 1.0`, `FollowingTierDiscountMega = 0.45`,
`FollowingTierDiscountLarge = 0.30`, `FollowingTierDiscountMid = 0.15`, `FollowingTierDiscountSmall = 0.0`,
`FollowingTierDiscountWeight = 1.0`, `OpportunityDiscountFloor = 0.05`. `Validate()` requires the floor in
(0, 1], the two term weights non-negative, and the tier discounts in [0, 1] **and monotone
Mega ≥ Large ≥ Mid ≥ Small**.

**Invariants (checked by tests).** A **graded lean, never a filter**: the strictly-positive floor means a
strong-enough trajectory still surfaces a mega-cap (Opportunity clamps at `Trajectory·EC/100·floor > 0`, never
hard-excluded). **Monotone**: a higher tier never raises Opportunity. **Small-tier output at default weights is
byte-identical to v6** (attention weight 1.0, Small discount 0.0, clamp inert on `[0.6, 1]`), so the
under-followed names Radar exists for are untouched. The tier feeds ONLY the Opportunity discount — Trajectory
(v6 corroboration math), Attention, EvidenceConfidence, SignalVelocity, recency, empty-window, direction SIGNS,
and the per-signal provenance contributions are **byte-for-byte** as v6 (ported tests prove it). Worked
before/after at the defaults: JNJ (mega, Attention 21) discount multiplier `1 − 21/250 = 0.916 → 0.466`, so its
v6 Opp 45 lands ≈ **23** — below the actionable surface; AEHR (small) is **unchanged**. No ticker-specific
logic — entirely seed-tier-driven.

**Version obligation.** Formula **STRUCTURE** change → `_formula.Version` advanced `radar-formula-v6 → v7`;
`ScoringConfigVersion` re-stamps via the derived fingerprint (the `FormulaVersion` input changed AND the seven
new weights joined the canonical string): AI-OFF default `radar-scoring-fp-c45fb79092ea →
radar-scoring-fp-8f4b59efd288`, live AI-ON default `radar-scoring-fp-454984785732 → radar-scoring-fp-4c06fd2d2d8c`.
Future tuning of any tier magnitude/weight/floor is a **config edit** (re-stamps by value, no formula class).
Per the spec-implementation checklist `RadarScoreFormulaV6` was **deleted** (not left dormant) and its tests
ported to `RadarScoreFormulaV7Tests`. Alternatives recorded for the decision: (A) config-only divisor tighten —
rejected, cannot separate JNJ 21 from AEHR 19; (C) benchmark bucketing in the report — complementary, not done
here. *Accepted · 2026-07-20 — **maintainer-approved** (AD-6 structure gate: shape + constants signed off
in-session).*

### Refinement — `radar-formula-v8` (spec 122): breadth-preserving collapse in the Attention reach

**Problem.** The spec-109 `MediaAttentionCollapse` (`media-collapse-v1`) keeps ONE representative per
same-event bucket before the formula ever sees the signals. That correctly kills duplicate media **volume**,
but v7 then counted distinct third-party publishers over the *post-collapse* set, so it also threw away the
distinct-publisher **breadth** of everything the collapse dropped. The spec-124 characterization proved the
whole gap is structural, not a bug: 15 distinct outlets (7 curated-genuine at 1.0, 8 unknown at 0.25) covering
ONE event scored Attention **10**, the *same* 15 spread across distinct events scored **78**, and that same
burst handed straight to the formula — bypassing only the collapse — also scored **78**. Fifteen *different*
genuine outlets choosing to cover one story is genuine notedness, and Radar was reading it as one outlet. This
is OPEN DECISION (b) — "the Attention metric understates notedness" (AEHR popped 60% intraday at Attention 21).

**Decision (shape).** Separate the two concerns: keep the volume collapse exactly as it is, and let the
**breadth** term count the distinct publishers of a collapsed event.

```
breadthSurvivors      = Σ tierWeight(p) over distinct third-party publishers in the POST-collapse set
breadthCollapsedExtra = Σ tierWeight(p) over distinct third-party publishers present ONLY in the
                        PRE-collapse set (i.e. not already survivors)
reach     = breadthSurvivors + CollapsedBreadthCredit · breadthCollapsedExtra
                             + MediaReachWeight · mediaSignalCount     // mediaSignalCount stays POST-collapse
Attention = 100 · reach / (reach + AttentionHalfSaturation)
```

The engine already held the pre-collapse list where it calls `Collapse(...)`; it now passes it as the
breadth-only `ScoringInput.PreCollapseSignals`. The formula remains the single owner of the breadth math —
Attention is not split across engine + formula — and `MediaAttentionCollapse`'s transform and its
`media-collapse-v1` descriptor are **unchanged**.

**Constant (AD-6).** New `ScoringWeights.CollapsedBreadthCredit`, default **1.0**, `Validate()`-ranged to
`[0, 1]`, bound under `Radar:Scoring` and hashed into the fingerprint by value. Rationale for 1.0: a distinct
genuine outlet is breadth regardless of whether the coverage clustered on one event — that *is* the complaint.
Retunable as a pure config edit (like spec 94's `MediaReachWeight`) with no formula bump.

**Invariants (checked by tests).** At `CollapsedBreadthCredit = 0.0`, `breadthCollapsedExtra` drops out and v8
reproduces `radar-formula-v7` **byte-for-byte** — v8 is a pure superset, pinned by test (the spec-124 burst
still reads **10** there). Attention is **monotone** in the credit. `mediaSignalCount` stays post-collapse, so
no loudness/velocity term is re-admitted — the transient-burst shape spec 109 closed stays closed, and spec
94's anti-raw-volume posture holds. The **anti-mill guard is intact** because the extra is tier-weighted: 15
mill re-posts of one event add ≈1.5, 15 genuine outlets add 15. First-party sources are still excluded from the
new term. **AD-14 clean**: no price / market-cap / trading-volume / intraday-move input — only Radar's own
publisher-breadth evidence. Every other component (v6 Trajectory, the v7 following discount, EvidenceConfidence,
SignalVelocity, recency, empty-window, direction SIGNS, per-signal provenance contributions) is byte-for-byte as
v7. Measured effect on the spec-124 fixture: the burst rises **10 → 75**, against the spread control's unmoved
**78** (the 3-point residual is the legitimately-different volume term: 1 surviving media event vs 15).

**Version obligation.** Formula **STRUCTURE** change → `_formula.Version` advanced `radar-formula-v7 → v8`;
`ScoringConfigVersion` re-stamps via the derived fingerprint (the `FormulaVersion` input changed AND
`CollapsedBreadthCredit` joined the canonical string): AI-OFF default `radar-scoring-fp-8f4b59efd288 →
radar-scoring-fp-cb80a5809882`, live AI-ON default `radar-scoring-fp-2ef5ef96cce2 →
radar-scoring-fp-c908f03a554a` (both re-pinned by RUNNING the tests, never hand-computed). Per the
spec-implementation checklist `RadarScoreFormulaV7` was **deleted** (not left dormant) and its tests ported to
`RadarScoreFormulaV8Tests`. Alternatives recorded: (A) audit for a measurement bug — done as spec 124, no bug
found; (B) a bounded recency-gated velocity term — rejected, re-admits loudness/velocity, kept as a fallback if
D under-delivers on live data; (C) a report-only caveat — already shipped as spec 123's Notedness line,
complementary but does not fix ranking.

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
every other component byte-identical to v5; fingerprint re-stamped `abbdf9fab44f → c45fb79092ea`; **Accepted ·
2026-07-17 — maintainer-approved** (shape signed off in-session; `k=10` default retunable via config)). Refined ·
2026-07-20 (spec 117, `radar-formula-v7` — maintainer-gated structure: notedness-aware Opportunity discount
folding the curated, non-price seed `FollowingTier` (AD-14) alongside measured Attention, floored/clamped as a
graded lean; only the Opportunity discount changed, every other component byte-identical to v6; Small tier at
default weights byte-identical to v6; AI-OFF fingerprint re-stamped `c45fb79092ea → 8f4b59efd288`, AI-ON
`454984785732 → 4c06fd2d2d8c`; **maintainer-approved · 2026-07-20**, shape signed off in-session). Refined ·
2026-07-21 (spec 122, `radar-formula-v8` — maintainer-gated structure: breadth-preserving collapse, crediting
the tier-weighted distinct publishers the spec-109 same-event collapse dropped back into the Attention
**breadth** term via the new `CollapsedBreadthCredit` (default 1.0, `[0,1]`) while `mediaSignalCount` stays
post-collapse so no volume/velocity is re-admitted (AD-14 clean); only the Attention reach changed, every other
component byte-identical to v7, and at credit 0.0 v8 is v7 byte-for-byte; the spec-124 burst rises 10 → 75
against the unmoved 78 spread control; AI-OFF fingerprint re-stamped `8f4b59efd288 → cb80a5809882`, AI-ON
`2ef5ef96cce2 → c908f03a554a`; **shape + default `CollapsedBreadthCredit` awaiting maintainer sign-off at PR
review** — the AD-6 structure gate is NOT self-approved).

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

### Lineage — spec 127: `RuleSetVersion` v3 → v4 (new `PatentActivity` rule group); default re-stamps automatically

Spec 127 adds the PatentsView granted-patent activity collector (`patents`, opt-in **OFF** by default) and one
new `KeywordSignalExtractor` rule group mapping its fixed phrase `patent activity (recent grants)` to a
**Neutral** `SignalType.PatentActivity` — a rule-**STRUCTURE** change, so `RuleSetVersion` bumps
`radar-keyword-rules-v3 → radar-keyword-rules-v4` and the spec-95 signal-source descriptor re-stamps **both**
default fingerprints **automatically**: AI-OFF **`radar-scoring-fp-cb80a5809882 → radar-scoring-fp-b4a040144f66`**
and AI-ON **`radar-scoring-fp-c908f03a554a → radar-scoring-fp-63c096e531ec`**. The enabled default collector set
is **unchanged** (still the 6-collector baseline — `patents` is not in `default.json`); the fingerprint moves
solely on the rules identity. **Scoring math is byte-identical** — the new rule matches only the patent phrase,
which no existing evidence contains, and the collector is opt-in-off, so every company scores exactly as before;
there is no fingerprint-safe way to add a scoring-affecting signal type (spec 95 working as intended). No
`_formula.Version` / weight / attention-tier / insider-tier change (`radar-formula-v8` stays). For the efficacy
visual (spec 101 / AD-14 read side), this re-stamp is a **score-neutral cosmetic boundary** — spec 108's
continuity-aware segmentation connects the score line across it because the scores are fully continuous (an
input-hash artifact, not a measurement break).

### Lineage — spec 129: `RuleSetVersion` v4 → v5 (new `RegulatoryApproval` rule group); default re-stamps automatically

Spec 129 adds the openFDA 510(k)/PMA device clearance/approval collector (`fda`, opt-in **OFF** by default) and one
new `KeywordSignalExtractor` rule group mapping its fixed phrase `fda clearance or approval (recent)` to a
**Positive** (routine-strength, one-signal-per-run) `SignalType.RegulatoryApproval` — a rule-**STRUCTURE** change,
so `RuleSetVersion` bumps `radar-keyword-rules-v4 → radar-keyword-rules-v5` and the spec-95 signal-source
descriptor re-stamps **both** default fingerprints **automatically**: AI-OFF
**`radar-scoring-fp-b4a040144f66 → radar-scoring-fp-1251d4e0373e`** and AI-ON
**`radar-scoring-fp-63c096e531ec → radar-scoring-fp-2be98e738684`**. The enabled default collector set is
**unchanged** (still the 6-collector baseline — `fda` is not in `default.json`); the fingerprint moves solely on
the rules identity. **Scoring math is byte-identical** — the new rule matches only the FDA phrase, which no
existing evidence contains, and the collector is opt-in-off, so every company scores exactly as before; there is
no fingerprint-safe way to add a scoring-affecting signal type (spec 95 working as intended). No
`_formula.Version` / weight / attention-tier / insider-tier change (`radar-formula-v8` stays). For the efficacy
visual (spec 101 / AD-14 read side), this re-stamp is a **score-neutral cosmetic boundary** — spec 108's
continuity-aware segmentation connects the score line across it because the scores are fully continuous (an
input-hash artifact, not a measurement break). (Note: spec 128 (FCC) never merged, so this v4 → v5 bump is
spec 129's, not spec 128's.)

### Lineage — spec 130: `RuleSetVersion` v5 → v6 (new `TrademarkActivity` rule group); default re-stamps automatically

Spec 130 adds the USPTO trademark-activity collector (`trademarks`, opt-in **OFF** by default) and one new
`KeywordSignalExtractor` rule group mapping its fixed phrase `trademark activity (recent filings)` to a
**Neutral** (routine-strength) `SignalType.TrademarkActivity` — a rule-**STRUCTURE** change, so `RuleSetVersion`
bumps `radar-keyword-rules-v5 → radar-keyword-rules-v6` and the spec-95 signal-source descriptor re-stamps
**both** default fingerprints **automatically**: AI-OFF
**`radar-scoring-fp-1251d4e0373e → radar-scoring-fp-c1e126884b7c`** and AI-ON
**`radar-scoring-fp-2be98e738684 → radar-scoring-fp-74c5e077f728`**. The enabled default collector set is
**unchanged** (still the 6-collector baseline — `trademarks` is not in `default.json`); the fingerprint moves
solely on the rules identity. **Scoring math is byte-identical** — the new rule matches only the trademark
phrase, which no existing evidence contains, and the collector is opt-in-off, so every company scores exactly as
before; there is no fingerprint-safe way to add a scoring-affecting signal type (spec 95 working as intended). No
`_formula.Version` / weight / attention-tier / insider-tier change (`radar-formula-v8` stays). For the efficacy
visual (spec 101 / AD-14 read side), this re-stamp is a **score-neutral cosmetic boundary** — spec 108's
continuity-aware segmentation connects the score line across it because the scores are fully continuous (an
input-hash artifact, not a measurement break). (Note: spec 128 (FCC) never merged, so this is a v5 → v6 bump,
not v6 → v7.)

### Lineage — spec 133: the `fda` collector is promoted into the baseline (a COLLECTOR-SET re-stamp, not a rule/formula change)

Spec 133 adds `"fda"` to `Radar:Collectors` in `scripts/run-profiles/default.json`, switching the spec-129 openFDA
510(k)/PMA device-clearance collector **on** in the canonical live run profile (6 → **7** collectors). The cause of
this re-stamp is therefore the **enabled-collector set**, *not* a rule or formula change: the spec-95 signal-source
descriptor folds the enabled set into the fingerprint by concrete `IEvidenceCollector.CollectorName` (`"fda"`,
Ordinal-sorted second after `RssPressReleaseCollector`), so **both** default fingerprints re-stamp **automatically**
under AD-10 (as amended): AI-OFF **`radar-scoring-fp-c1e126884b7c → radar-scoring-fp-6b2f468041b9`** and AI-ON
**`radar-scoring-fp-74c5e077f728 → radar-scoring-fp-57356123e09b`**. There is **no `KeywordSignalExtractor.RuleSetVersion`
bump** (`radar-keyword-rules-v6` stands — the `RegulatoryApproval` rule group already shipped with spec 129 and had
simply never fired, because no *enabled* collector produced its phrase) and **no `_formula.Version` bump**
(`radar-formula-v8` stands); no `ScoringWeights`, attention-tier or insider-materiality value moved, and **no
production code changed** — only the run profile, the fingerprint test's descriptor constant and its two pins.
**Scoring math is byte-identical**: the 41 companies with no `fda` feed in `data/companies.json` keep an identical
evidence set, identical signals and identical component scores, and only their stamped `ScoringConfigVersion`
differs. The two seeded companies (TMDX `applicant=TransMedics`, AXGN `applicant=Axogen`) can now gain **Positive**
routine-strength `RegulatoryApproval` signals — Radar's first *directional* non-filing collector going live.
openFDA is **keyless**, so this introduces no secret, env var or key gate. For the efficacy visual (spec 101 /
AD-14 read side), this boundary opens a genuinely new segment (the signal-production surface really did widen), so
spec 108's continuity-aware segmentation will show a short score series for the next few runs — expected and
correct. `patents`, `trademarks` and `hiringats` remain opt-in **OFF**; `appsettings.json`'s code default
`Radar:Collectors` (`[ "rss" ]`) is **unchanged** — this slice changes *how we run*, not the code default.

### Amendment — spec 119: the AI earnings-read model identity is a fingerprint input (folded by value)

Spec 106 folded the AI directional-filing source's per-signal magnitudes (`str`/`nov`/`minconf`) into the
fingerprint, but **not the model doing the reading**. Spec 119 makes the DeepInfra `deepseek-ai/DeepSeek-V4-Flash`
read the default baseline (replacing local `ollama`/`llama3.1`) and, at the same time, appends the effective
`provider:model` identity to that descriptor —
`directional-filing:str=8;nov=6;minconf=0.6;model=openai:deepseek-ai/DeepSeek-V4-Flash` (fixed field order, model
LAST and escaped, so the pre-119 prefix is unchanged). Rationale: the reading model changes signal **DIRECTION**,
not just throughput — in the 2026-07-21 A/B `llama3.1` read EOSE's reported −70% gross margin as
`Improving 0.90` where DeepSeek-V4-Flash read the same release as `Mixed 0.85`, and DeepSeek additionally caught
AEHR's deteriorating *reported* quarter. Leaving the model out would let two runs with materially different
directional signal sets (and different scores) share one `ScoringConfigVersion`, breaking the spec-69/95
comparability invariant and drawing the spec-101/108 efficacy line as continuous across a real change. It is
therefore hashed **by value**, exactly like the spec-95 collector set and the spec-96 insider tiers: swapping the
model re-stamps automatically and is a **config edit** — **no `_formula.Version` and no `RuleSetVersion` bump**
(`radar-formula-v7` / `radar-keyword-rules-v3` both stand). Only the **AI-ON** fingerprint moves,
**`radar-scoring-fp-4c06fd2d2d8c → radar-scoring-fp-2ef5ef96cce2`** (that re-stamp also absorbs a pin correction:
the old AI-ON pin was computed from an *unescaped* `ai=` segment, so it was not the value a live run stamped; the
test now builds it through the real `DescriptorEscaping`). The **AI-OFF** pin `radar-scoring-fp-8f4b59efd288` is
**unmoved** — with no AI provider configured nothing is appended, byte-for-byte as before. The key itself stays
out of config entirely (`Radar:Ai:OpenAi:ApiKeyEnvVar` names `DEEPINFRA_API_KEY`, read at runtime; a missing key
fails the run loudly) — the SEC-User-Agent secret precedent. *Accepted · 2026-07-21 — comparability-input
widening; property preserved and strengthened, no scoring-math change.*

### Amendment — spec 141: the series is keyed by strategy NAME; the fingerprint is demoted to a tripwire; collection provenance is recorded, not hashed

AD-10 conflated two obligations. **"Stamp the config correctly" is KEPT** — the fingerprint is still derived,
still automatic, still stamped on every snapshot, and still dereferences back to the persisted effective config.
**"The stamp must never change" is DROPPED** — it was never true, and treating it as an invariant did active harm.

**The evidence.** Counted on `origin/main` @ `ba63d56` over the live baseline store (`data/scores`, 851
snapshots): **17 distinct `ScoringConfigVersion` values already exist** — 11 `radar-scoring-fp-*` fingerprints
plus 6 legacy `radar-scoring-config-vN` stamps. **The largest single cohort is 133 snapshots ≈ 3 runs** at 43
companies. **The pinned AI-ON fingerprint `57356123e09b` has exactly 43 snapshots — one single run.** So the
"no pin edit" criterion that specs 137/138/140 each carried has been protecting *one run's worth of history*
while the fingerprint moved 17 times. There is no continuous efficacy series to preserve; the migration cost
is near zero now and grows with every slice built on the wrong key.

**The defect.** `SignalSourceDescriptor` welded two different facts with different lifetimes into one hash:
*collection provenance* ("what was collected on this run") and *strategy identity* ("what hypothesis produced
this score"). A strategy declaring `SignalTypes: ["InsiderBuying"]` hashed the full seven-collector CSV, so
enabling an eighth collector emitting only `RegulatoryApproval` changed its `ScoringConfigVersion` while its
scores stayed **bit-for-bit identical** — a new series, for no behavioural reason.

**The new position.**

- **The score series is keyed by `StrategyName`** (`ScoreSeriesKey`, the one definition), with `null`/blank
  canonicalised to `"default"` so the legacy 851 snapshots read as the primary series rather than being
  orphaned. Every consumer routes through it: the weekly report's comparability gate and the spec-101/108
  efficacy segmentation. Comparison is case-insensitive, matching `ScoringStrategySet`'s uniqueness rule.
- **A strategy is IMMUTABLE BY CONVENTION.** To change one, add a new named strategy (`momentum` →
  `momentum-v2`). The name is then a stable, human-meaningful key that a collector toggle cannot move.
- **The fingerprint is a TRIPWIRE, not a primary key.** `StrategyIdentityGuard` runs at the very start of
  `RadarPipelineRunner.RunAsync` (before Stage 1, so a misconfiguration costs no network calls) and compares
  each strategy's computed fingerprint against the one recorded for that NAME in
  `data/scoring-configs/strategies/{name}.json` (a per-name, mutable, upsert record living **beside** — never
  inside — the immutable content-addressed `{fingerprint}.json` files). No record ⇒ record and continue; equal
  ⇒ continue; different ⇒ throw, naming the strategy, both fingerprints and the new-name remedy. A read
  failure degrades to "unrecorded" and never trips (AD-8): "cannot tell" must not be reported as "changed".
- **`CollectionProvenance` is recorded, never hashed.** `ISignalSourceDescriptor` splits into
  `CanonicalDescriptor()` (identity — `rules=…;[ai=…;]`, the fingerprint input) and `CollectionProvenance()`
  (`collectors=<csv>;`, hashed into nothing), stamped as a trailing nullable field on `CompanyScoreSnapshot`
  and persisted by `FileScoreSnapshotStore`. It is deliberately **not** added to `EffectiveScoringConfig`:
  that store is content-addressed and insert-if-new, so a per-run collector set stored there would be
  permanently pinned to whichever run wrote the file first. Provenance is not weakened — per-signal and
  per-evidence source attribution already names the collector behind each item (AD-3), and the run-level set
  is now recorded *alongside* the score instead of *inside* its identity.
- **The `ai=` segment stays on the IDENTITY side.** It is not a collector set: it carries the AI
  directional-filing read's per-signal magnitudes and the reading model, which change signal DIRECTION
  (spec 119) — genuinely different scorings that must never share a stamp.
- **Scores are byte-identical.** Identity/record-keeping only: an engine-level test asserts that two engines
  differing solely in the enabled collector set stamp the SAME `ScoringConfigVersion`, DIFFERENT
  `CollectionProvenance`, and produce identical components, explanation, component JSON and evidence links.
- **The pins MOVED, deliberately — that move IS the deliverable, not scope leakage.** AI-OFF
  **`radar-scoring-fp-6b2f468041b9 → radar-scoring-fp-2ce20f8fc497`** and AI-ON
  **`radar-scoring-fp-57356123e09b → radar-scoring-fp-3457da53489d`**, caused solely by removing the collector
  CSV from the hashed identity. `ScoringConfigFingerprintTests` now documents the pins as **change-detectors**:
  a move is a normal, intended act requiring a conscious update and a lineage note. No `_formula.Version` bump
  (`radar-formula-v8` stands), no `KeywordSignalExtractor.RuleSetVersion` bump (`radar-keyword-rules-v6`
  stands), no weight/tier edit. `Compute_ChangedSignalSourceDescriptor_ChangesFingerprint` — which asserted
  that dropping a collector re-stamps — was **retargeted** onto the extractor rule-set identity, because the
  old assertion is now the opposite of the intended behaviour.
- **History was NOT regenerated.** Spec 141 §5 permits taking the discontinuity and saying so; the 27 days of
  fragmented history are left exactly as they are (append-only, AD-8). Nothing was rewritten, deleted or
  backfilled — the standing spec-145 rule against retro-healing accrued evidence is untouched.

*Accepted · 2026-07-26 — key correction: the conflated invariant is dropped, the correctness property (never
compare two different scorings) is preserved on a key that actually distinguishes them.*

### Amendment (spec 148) — the fold is COMPLETE: the scoring window and `TrajectoryCorroborationK` are hashed

The `radar-architecture-reviewer` sweep of `main` @ `b9b3f65` found that `ScoringConfigFingerprint.Compute`
folded every output-affecting input **except two**, and both genuinely change scores:

- **`ScoringOptions.Window`** (bound from `Radar:ScoringWindowDays`) bounds the current window *and* the
  previous/velocity window, so a 14-day and a 30-day run over identical evidence produce materially different
  Trajectory, SignalVelocity and Attention — and stamped the **same** `ScoringConfigVersion`.
  `EffectiveScoringConfig` carried no window field at all, so the difference was not even recoverable
  after the fact.
- **`ScoringWeights.TrajectoryCorroborationK`** — the denominator smoother in v8's
  `T_raw = 10·(Mpos−Mneg)/(Mpos+Mneg+k)` and, since spec 146, in v9's per-channel direction factor. It was
  recorded as a known gap at the spec-146 hand-back; the audit confirmed it was the **only** missing
  `ScoringWeights` field. Verified again here by enumerating all 27 public properties against the fold.

**This was worse after spec 141, not merely older.** A window edit is an in-place edit to a NAMED strategy —
precisely the category `StrategyIdentityGuard` now *promises* to catch and structurally could not see — while
`ScoreSeriesKey` kept both cohorts in the same `default` series. An unseeable category is a broken promise
rather than a gap.

- **Encoding: ticks, invariant-culture.** The window is appended as a new fixed-position `window` field after
  `mediaCollapse`, following the pattern specs 96/109 used. Ticks is INJECTIVE over every `TimeSpan` (AD-3);
  whole-days is not — a 36-hour and a 24-hour window would truncate onto the same value and two genuinely
  different scorings would share one stamp, the exact failure the field exists to prevent.
- **`EffectiveScoringConfig.Window` is trailing and NULLABLE.** A config file written before this slice has no
  window field, and deserializing that absence as `TimeSpan.Zero` would be a FALSE record of a zero-length
  window. `null` means "written pre-148; not recorded". New writes always populate it, so the store's
  descriptor↔fingerprint self-verification still holds for everything written from here on.
- **Completeness is now enforced by reflection, not by review.** `ScoringConfigFingerprintTests` perturbs every
  public `ScoringWeights` property in turn and asserts the fingerprint moves, and pins `ScoringOptions`'
  property set to exactly `{ Window }`. A future unfolded knob fails the day it is added.
- **Scores are byte-identical, and that is MEASURED.** `ScoringOutputStabilityTests` pins one fixture's entire
  output under the real `radar-formula-v8` — all five components, the explanation, the `ComponentJson` and the
  ordered evidence-link chain — and that file was compiled and run against the pre-148 sources
  (`origin/main` @ `b9b3f65`), where it also passes. No formula file was touched.
- **The pins MOVED, deliberately, once — the move IS the deliverable.** AI-OFF
  **`radar-scoring-fp-2ce20f8fc497 → radar-scoring-fp-0c46e07b94db`** and AI-ON
  **`radar-scoring-fp-3457da53489d → radar-scoring-fp-28226897f97b`**. No `_formula.Version` bump
  (`radar-formula-v8` stands), no `KeywordSignalExtractor.RuleSetVersion` bump (`radar-keyword-rules-v6`
  stands), no weight or tier edit. AD-10 as amended by spec 141 explicitly permits an intended pin move
  recorded with its lineage; this is one.
- **⚠ THIS SLICE ALSO BROKE "THE PIN IS THE LIVE STAMP", and that consequence is part of the decision.** Until
  now every hashed input was a code default, so the value pinned in `ScoringConfigFingerprintTests` was also
  the value a live baseline run wrote, and every prior amendment above could quote one pair. The window is not
  a code default. The pins above are computed at the `ScoringOptions` code default of **30 days** — which the
  Worker never uses — while the baseline runs at `Radar:ScoringWindowDays` = **60**
  (`RadarWorkerOptions.ScoringWindowDays`, `src/Radar.Worker/appsettings.json`; `run-profiles/default.json`
  does not override it) and stamps AI-OFF **`radar-scoring-fp-4eb2fe5d3cdf`** / AI-ON
  **`radar-scoring-fp-4da4b5ff6ec9`**; `-Profile long-window` (120 days) stamps
  `radar-scoring-fp-0a7058d94582` / `radar-scoring-fp-81e9fab711f8`. Both sets are correct at their own
  window and must not be reconciled onto one value. Accepted deliberately: the alternative — hashing a
  "canonical" window rather than the one actually used — would reintroduce exactly the false-comparability
  defect this amendment exists to close. The operator-facing live record is `default.json`'s comment; the
  test pins are the unit-level change-detector. The same split now applies to any future config-bound
  fingerprint input.
- **History was NOT regenerated**, exactly as in spec 141: append-only (AD-8), nothing rewritten, deleted or
  backfilled. Accrued snapshots keep their old stamps, and `StrategyIdentityGuard` will trip once per strategy
  name on the next run — which is the correct, visible outcome of a deliberate identity change (delete the
  affected `data/scoring-configs/strategies/{name}.json` record to acknowledge it, or add a new strategy name
  if the two cohorts must stay separable).

*Accepted · 2026-07-27 — the stamp now covers every input that can move a score; nothing else about AD-10
changes.*

### Amendment (spec 148, Part B) — replay records the provenance it writes

`ReplayRunner` took neither `IScoringConfigStore` nor the startup tripwire, while all three forward runners
take both. A replay-only run in a fresh data root therefore emitted snapshots whose `ScoringConfigVersion`
**dereferenced to nothing** — the weights that produced those scores were unrecoverable. That was the weakest
provenance in the system sitting on exactly the path `Radar:Efficacy:Comparison:ReplayLabel` and spec 140's
leaderboard are meant to rank strategies from.

- `StrategyIdentityGuard.VerifyAsync` is now the FIRST statement of `RunAsync`, mirroring the forward runners:
  a misconfiguration costs no scoring, and no snapshot lands in a labelled series under a name whose meaning
  has changed. Confirmed (not assumed) that `FileScoringConfigStore.ReadStrategyFingerprintAsync` degrades to
  "unrecorded" on `IOException`/`UnauthorizedAccessException`/`JsonException`, so a disk hiccup cannot fail a
  read-only mode; `OperationCanceledException` still propagates.
- `WriteIfNewAsync(strategy.Engine.EffectiveConfig, ct)` runs once per strategy in the outer loop —
  insert-if-new, so it is free when the forward pipeline already wrote that config.
- **Writing the scoring-config store is a PROVENANCE RECORD, not a scoring mutation.** Replay still mutates no
  signal or evidence store, still never writes the live scores directory, and `replay ⊆ forward` (spec 139)
  still holds field for field. The read-only test now names the config store as the ONE sanctioned outside
  write and pins its exact two files, so the distinction is asserted rather than argued.
- **Same-label overwrite: WARN, aggregated per strategy.** As-of-keyed file names make a re-replay idempotent
  and equally make it replace an already-ranked series. Failing would break the legitimate "re-replay after
  fixing a data problem" workflow; silence is how a comparison quietly becomes wrong. One `LogWarning` per
  (label, strategy) carries the count and what it means. Detected in `FileScoreSnapshotStore`, the only place
  that knows the target path before the write (an optional `OnSnapshotOverwritten` probe the live/forward path
  never wires), and surfaced through `IReplayScoreSnapshotFileStoreFactory.OverwrittenCount`.
- **Part B moves NO fingerprint input**: it touches neither `Compute`, nor `EffectiveScoringConfig`'s hashed
  content, nor any descriptor. Both pin moves above belong to Part A.

*Accepted · 2026-07-27 — after this slice every snapshot Radar writes (forward, score-only or replay) has a
dereferenceable `ScoringConfigVersion`.*

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
collector opt-in-off). Amended · 2026-07-21 (spec 119 — the AI earnings-read `provider:model` identity is folded
into the directional-filing descriptor by value; the default baseline read moves to DeepInfra
`deepseek-ai/DeepSeek-V4-Flash` and the AI-ON stamp re-stamps radar-scoring-fp-4c06fd2d2d8c →
radar-scoring-fp-2ef5ef96cce2, AI-OFF unmoved, no formula/`RuleSetVersion` bump; Accepted). Lineage · 2026-07-23
(spec 127 — `RuleSetVersion` radar-keyword-rules-v3 → v4 for the new `PatentActivity` rule group; BOTH defaults
re-stamp AI-OFF radar-scoring-fp-cb80a5809882 → radar-scoring-fp-b4a040144f66 and AI-ON
radar-scoring-fp-c908f03a554a → radar-scoring-fp-63c096e531ec; scoring math byte-identical, `patents` collector
opt-in-off). Lineage · 2026-07-23 (spec 129 — `RuleSetVersion` radar-keyword-rules-v4 → v5 for the new
`RegulatoryApproval` rule group; BOTH defaults re-stamp AI-OFF radar-scoring-fp-b4a040144f66 →
radar-scoring-fp-1251d4e0373e and AI-ON radar-scoring-fp-63c096e531ec → radar-scoring-fp-2be98e738684; scoring
math byte-identical, opt-in-off openFDA `fda` collector; spec 128 (FCC) never merged so this is spec 129's v4 →
v5 bump). Lineage · 2026-07-23 (spec 130 — `RuleSetVersion` radar-keyword-rules-v5 → v6 for the new
`TrademarkActivity` rule group; BOTH defaults re-stamp AI-OFF radar-scoring-fp-1251d4e0373e →
radar-scoring-fp-c1e126884b7c and AI-ON radar-scoring-fp-2be98e738684 → radar-scoring-fp-74c5e077f728; scoring
math byte-identical, opt-in-off USPTO `trademarks` collector; spec 128 (FCC) never merged so this is a v5 → v6
bump). Lineage · 2026-07-25 (spec 133 — the openFDA `fda` collector is promoted INTO
`scripts/run-profiles/default.json`, so the **enabled-collector set** moves 6 → 7 and BOTH defaults re-stamp
automatically: AI-OFF radar-scoring-fp-c1e126884b7c → radar-scoring-fp-6b2f468041b9 and AI-ON
radar-scoring-fp-74c5e077f728 → radar-scoring-fp-57356123e09b; the cause is a **collector-set** change, **not** a
rule or formula change — no `RuleSetVersion` bump (`radar-keyword-rules-v6` stands), no `_formula.Version` bump
(`radar-formula-v8` stands), no weight/tier edit, no production code change, scoring math byte-identical).
Amended · 2026-07-26 (spec 141 — the score series is keyed by `StrategyName`, strategies are immutable by
convention, the fingerprint is demoted from invariant to startup tripwire (`StrategyIdentityGuard`), and the
enabled-collector set leaves the hash entirely to be recorded per-snapshot as `CollectionProvenance`; BOTH
defaults re-stamp deliberately — AI-OFF radar-scoring-fp-6b2f468041b9 → radar-scoring-fp-2ce20f8fc497 and AI-ON
radar-scoring-fp-57356123e09b → radar-scoring-fp-3457da53489d — with no `RuleSetVersion` / `_formula.Version`
bump and byte-identical scoring math; evidence: 17 distinct stamps already existed over 851 snapshots, largest
cohort ≈ 3 runs, the AI-ON pin exactly 1 run; Accepted). Amended · 2026-07-27 (spec 148 — the fold is completed:
`ScoringOptions.Window` (as ticks) and `ScoringWeights.TrajectoryCorroborationK`, the last two output-affecting
inputs hashed into nothing, are folded by value and the window is carried on `EffectiveScoringConfig` as a
trailing nullable field; BOTH defaults re-stamp deliberately — AI-OFF radar-scoring-fp-2ce20f8fc497 →
**radar-scoring-fp-0c46e07b94db** and AI-ON radar-scoring-fp-3457da53489d → **radar-scoring-fp-28226897f97b** —
with no `RuleSetVersion` / `_formula.Version` bump and scoring math proven byte-identical against pre-148
sources; a reflection completeness guard now makes the next unfolded knob fail immediately. Part B, moving no
fingerprint input, gives `ReplayRunner` the effective-config write and the startup tripwire the forward runners
already had; Accepted).

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

---

## AD-15 — A composite strategy adds value only if it beats **every** baseline out-of-sample

**Decision.** The standard the project holds itself to, verbatim:

> A composite strategy may only be described as adding value if it beats **every** baseline
> **out-of-sample**, on an honest N, by more than the spread between the baselines themselves.

> ### ⚠️ AMENDMENT · 2026-07-28 — POSITIVE EFFICACY CLAIMS ARE SUSPENDED
>
> **No strategy may be described as adding value until (a) the primary outcome variable required by AD-16 is
> accepted, and (b) a valid comparison method is accepted.** Until both exist, the leaderboard ranks and
> reports; it does not license a claim, and neither does any number computed from it.
>
> The reason is that the rule quoted above **is not a test of difference**. "Beats … by more than the spread
> between the baselines" compares each strategy's *marginal* Spearman ρ, computed over observations the
> renderer itself labels "pooled across companies and dates and therefore not independent … dispersion, not
> significance". A gap between two such numbers carries no uncertainty estimate of its own, and the
> baseline spread is a heuristic stand-in for one — it has no coverage guarantee and it *shrinks* when the
> baselines agree, which is exactly when it should not.
>
> Additionally, and independently: with several composite arms configured, reporting whichever one beats the
> baselines is **selection**, not evidence. Any claim made once this suspension lifts must state how many
> arms were compared — the leaderboard already renders that count ("a leader chosen from many needs a
> stronger effect than one chosen from few"), and the claim must carry it.
>
> This amendment costs little to hold, and the reason needs no strong empirical claim: spec 152 established
> that every number published before 2026-07-28 was measuring 4-to-11-day reactions labelled as 21-day
> returns, so there is no standing result the suspension withdraws. (The ρ ≈ −0.1 cluster often quoted
> covered the five *composite* arms on the pre-152 replay only; the three baselines added by spec 154 have
> no history at all, so no statement about "every arm" is available and none is needed here.) Lifting the suspension
> requires accepting an outcome variable and a comparison method — see the parked
> `docs/next/deferred/155-paired-date-blocked-strategy-comparison.md` for the latter's open problems
> (dependence across overlapping windows defeats both a naïve parametric interval and a sign test).

Radar therefore ships a small, deliberate **control group** of *dumb baseline* strategies (spec 154),
declared in `scripts/run-profiles/default.json` and scored through the **normal** seam — same
`ScoringEngine`, same stores, same `ScoringConfigVersion` fingerprints, same spec-140 leaderboard. There is
**no special-casing** of a baseline anywhere in the harness, the leaderboard or the renderers: a baseline is
just a strategy.

- **Every baseline is prefixed `baseline-`**, so nobody reads one as a candidate strategy in a report or a
  leaderboard. They exist to be **beaten**.
- The three shipped controls: **`baseline-earnings-only`** (does the latest guidance read alone track price? —
  config-only, `radar-formula-v8` over `SignalTypes: ["GuidanceChange"]`), **`baseline-activity-only`** (is the
  score just "something happened"? — one collector channel over every enabled collector, scored as the
  saturated plain COUNT of in-window signals), and **`baseline-media-only`** (is Radar just tracking press
  coverage? — the same formula over the press/news collectors only).
- **A baseline winning is a FINDING ABOUT RADAR, not a recommendation.** If "count the signals" or "how much
  media covered it" tracks price as well as the composite does, the composite is expensive decoration — that
  is a cheap thing to find out and an expensive thing to assume. Nothing is auto-tuned or auto-promoted on the
  strength of which arm wins; the leaderboard ranks, a human decides (spec 140).
- **Keep the set small and deliberate.** Each added strategy scores every company on every run and adds a
  ranked table to the weekly report, and every extra arm makes a chance winner more likely — the exact trap
  spec 140's out-of-sample hold-out exists to resist. This is a control group, not a sweep.
- **`baseline-following-tier` is DEFERRED, not approximated.** "Is the score just 'small company'?" is a real
  question, but a tier-only score traces back to **no contributing evidence** — the curated `FollowingTier` is
  a *company attribute*, not evidence — which violates the provenance invariant that a score without evidence
  is invalid. Such snapshots would carry zero score-evidence links and would be dropped from the weekly report
  by the spec-53 exclusion anyway. The same question is answerable **read-side**, by relating the existing
  strategies' ranks to the curated tier, without minting evidence-less snapshots. Implementing it by some
  proxy would be **worse than not having it**: it would look like a control while testing something else.
- **This rule depends on spec 152's partial-window honesty.** Until enough price history has accrued, the
  leaderboard correctly reports "No strategy could be ranked" at the 21-day horizon, and **none of these
  baselines mean anything before then**. A "beats the baseline" claim made on partial-window returns is a
  claim about missing data.

**Why.** Nothing Radar produced answered whether the composite adds anything over a trivial heuristic. The
**2026-07-28** replay backtest ranked five deliberately-different strategies and they came in at in-sample
Spearman ρ −0.0849 / −0.0969 / −0.0999 / −0.1000 / −0.1009 — a spread of **0.016**. That is not a ranking; it
is what one common factor dominating all five looks like. When everything correlates with everything, the
useful comparison is not strategy-vs-strategy but **strategy vs. embarrassingly simple baseline**, and the
"by more than the spread between the baselines themselves" clause is what stops a 0.016 gap being reported as
a result. Requiring **out-of-sample** is not decoration either: spec 140 computes the ranking in-sample inside
the harness and hands the caller an already-ordered list precisely so an in-sample leaderboard is not
expressible.

Because a baseline's definition is what every such claim is measured against, `radar-baseline-activity-v1`
carries an `IScoreFormula.CompositionRevision` and a composition-guard test (spec 153's mechanism): a silent
drift in what "baseline" means would retroactively and invisibly invalidate every "beats the baseline"
statement made against it. The control formula is deliberately **not** numbered into the `radar-formula-vN`
lineage — that sequence is the lineage of Radar's *composite* (AD-6), and a control is not an evolution of it.

**Status.** Accepted · 2026-07-28 (spec 154). Cross-references AD-6 (formula structure is code), AD-10 as
amended (identity/fingerprint), AD-14 (price is validation-only and never a scoring input — a baseline is
ranked *against* price, never scored *from* it), AD-9 (no advice language: a leaderboard position is a
research statistic, not a recommendation), and spec 141's immutable-by-convention rule (re-tuning a
baseline's saturation constants means a NEW NAME, e.g. `baseline-activity-only` →
`baseline-activity-only-v2`, not an in-place edit).

---

## AD-16 — Radar tests a STEALTH thesis: evidence accumulates before attention arrives

> **Status: ACCEPTED · 2026-07-28.** Written and accepted before the next scoring change deliberately, because the two
> candidate theses imply *opposite* fixes to `radar-formula-v10` and the choice must not be made implicitly
> by whoever edits it next.

**Decision.** Radar's hypothesis is that a company's business trajectory improves, and evidence of it
accumulates in slow structured sources, **before broad attention arrives** — and that the interval between
those two events is where the research value lies. Radar is therefore **deliberately not a reaction
detector**, and "the score should move the day the news lands" is explicitly **rejected** as a design goal.

Each consequence below is binding on future work:

- **A same-day spike is a coincident indicator wearing a leading indicator's clothes.** By the time there is
  a burst of coverage and a good print, the market is reading the same wire Radar is. A score that peaks
  then is scoring highest exactly when its information is *least* private.
- **The long scoring window and flat recency curve are the design, not a defect.** `ScoringWindowDays` 60 and
  `RecencyFloor` 0.5 exist to accumulate quiet evidence. Shortening them to chase reactivity requires
  superseding this AD, not a config edit.
- **`SignalVelocity` is the shape of the thesis** — a rising *rate* while the absolute level is still low is
  what "something is happening that nobody has written about yet" looks like in this data.
- **The notedness discount is load-bearing, not a tweak.** Stealth means deliberately penalising the
  well-covered name. The three `filings-led*` arms (full / half / no discount) are the experiment that tests
  whether Radar's founding assumption is true, and they matter *more* under this AD, not less.
- **Neutral volume must never amplify a directional read.** This settles the open v10 question against
  amplification: under a reactivity thesis, heavy routine activity could be argued to signal a live
  situation; under THIS thesis heavy routine volume is the *noticed* company Radar is trying to avoid, and
  it correlates with size. `RadarScoreFormulaV10`'s conditional amplification therefore contradicts this AD
  and must be corrected under AD-6 / `CompositionRevision`.
- **The primary outcome variable is ATTENTION ARRIVING LATER, not next-week price.** If a high score today
  predicts coverage, publisher breadth and volume arriving later — with any re-rating following *that* —
  the thesis holds even where raw return is noisy. This is also the only formulation that is testable in
  weeks rather than quarters, because attention is observable long before fundamentals move.
- **Price stays validation-only (AD-14) and must be benchmark-adjusted.** A raw share return conflates "this
  company improved" with "the market went up"; under a thesis about *re-rating*, the market component is
  pure contamination.

**Pre-commitment — the anti-unfalsifiability clause.** A stealth thesis fails in one characteristic way:
every disappointing result is rescued with "the market has not noticed yet", and the tool quietly becomes a
belief system. Therefore the horizon and the outcome variable are **declared before results are seen**, and
a miss at the declared horizon **is a miss** — not evidence that the horizon was too short. Any change to a
declared horizon or outcome variable is an amendment to this AD, recorded with its reason, and invalidates
comparisons across the change.

**Why.** Two reasons; the second is decisive.

First, it is what the product claims to be — "surfaces public companies whose business trajectory may be
improving **before the market notices**".

Second, and more importantly, **the reactive thesis is not one Radar could execute even if it were correct.**
Radar is a batch job that runs once daily over RSS feeds and SEC filings. Post-announcement reaction is the
most heavily competed information in markets, priced in milliseconds by infrastructure built for exactly
that race. A daily batch job does not lose that race narrowly — it is not in it. Meanwhile the sources Radar
actually collects (Form 4 clusters, 13D/G positions building, contract awards accruing) are structurally
*slow* information that rewards patience. Stealth is the only thesis where the instrument matches the claim.

**What this does NOT license.**

- It does not make short-horizon measurement forbidden. Measuring reaction at h=1..5 is a legitimate
  **development feedback loop** and is encouraged — provided a short-horizon number is never quoted as
  evidence for the thesis. Reporting both, distinctly labelled, is the required form (the same separation
  spec 152 made between "no price" and "some price, but not the horizon asked for").
- It does not assert the thesis is true. It fixes what Radar is *testing*, so that a negative result is
  interpretable. AD-15 still governs whether any strategy may be described as adding value.
- It does not change the product's category: Radar remains a research-triage assistant, not a trading
  system (AD-9). **Open question, deliberately left open:** whether rank correlation with forward price is
  even the right success criterion for a triage tool, or whether shortlist precision and analyst time saved
  are closer to the real bar. Price is the ground truth Radar happens to have, not self-evidently the one
  its claim requires.

**Known prerequisite, recorded because it blocks the whole thesis.** A stealth score needs directional
evidence to accumulate a slope from, and Radar currently produces little: **87.6 % of 49,793 signals are
Neutral**, and spec 153 measured almost no usable direction in the ownership/insider channels (of 32
companies with an active `sec-form4` channel, 13 all-Neutral, 18 net-negative and 1 marginally net-positive;
`sec-13dg` was dark in spec 158's pinned window). Spec 158 did find sparse direction in `sec-edgar` — 13/43
companies net-positive among the resolvable inputs — which is why the amended arm uses that source alone.
A slower, more patient score computed over evidence that carries no direction will accumulate nothing more
reliably; collector/extraction coverage remains prior to tuning what consumes it.

### AMENDMENT · 2026-07-28 — THE PRECOMMITTED OUTCOME, fixed here in concrete terms

AD-16 requires the outcome and horizon to be declared **before results are seen**. This amendment *is* that
declaration. Every value below is fixed; none is a default for an implementer to choose, and none may be
changed after a v11 snapshot has been inspected except by a further recorded amendment that invalidates
comparisons across it.

**1 · Primary attention metric.** The count of **distinct third-party publishers with at least one
`MediaAttention` signal for that company whose evidence resolves, observed in `(D, D+h]`**. Distinct
publishers rather than article count, because one outlet syndicating itself is not the market noticing.

**2 · Publisher novelty: NOT USED, deliberately.** No "new publisher" test and no historical lookback.
**89.5 % of accrued evidence does not resolve on disk** (spec 142), so a publisher would appear novel
whenever its earlier evidence is simply missing — novelty would measure the gap, not the market. The metric
is therefore a pure forward count within the window, requiring no history at all.

**3 · Horizon h = 21 calendar days, with NO exit tolerance.** Equal to the price horizon on purpose, but
spec 152's four-day PRICE tolerance does not transfer: that tolerance exists because markets close at
weekends and holidays and a nearby trading bar still represents price near the target. An attention window
ending early is simply missing possible events. The outcome therefore requires collection coverage through
`D + 21` in full. Attention is *expected* to arrive sooner than a re-rating; that is a hypothesis this metric
can test, not a reason to pre-shorten the window.

**4 · First eligible primary-screen as-of date: the first on or after 2026-09-26.** The original date was
2026-08-22, chosen only to keep the comparator's trailing 21-day window inside the resolvable attention
cohort. Spec 158 then measured that the *predictor* had dropped **14,089 of 17,616** in-window signals (80 %)
because its 60-day scoring window still reached into pre-spec-145 evidence. That is not a lower bound on
score quality: newly resolvable Positive or Negative evidence can move preponderance and ties either way.

The first full-collection run after spec 145 merged ended at
`2026-07-27T08:04:44.9959802Z` (`PipelineRunRecord`
`17bf7a17-8bb6-46c8-90fe-e96dc5d4b3be`). A 60-day scoring window is wholly post-fix after
`2026-09-25T08:04:44.9959802Z`; **2026-09-26** is the first unambiguous calendar-day eligibility boundary.
This is later than the comparator's 2026-08-22 boundary and therefore governs. The arms may accrue before
then, but transitional snapshots do not enter the primary screen. Amended before any v11 snapshot or
forward outcome existed; metric and horizon are unchanged.

**5 · Missing data and the valid zero.** Eligibility depends on recorded successful coverage by every
collector enabled to produce third-party `MediaAttention` throughout **both** the comparator's trailing
window `(D − 21, D]` **and** the outcome window `(D, D + 21]` — the comparator is a publisher count on the
same construction as the outcome, so a gap corrupts it identically. A sufficiently recent *global*
collection date is not proof of coverage: collectors fail individually, and an aggregate run timestamp says
nothing about whether the news collectors succeeded on a given day. A gap or an unavailable coverage record
is dropped and counted as `IncompleteAttentionCollection`.

> **Implementation dependency, named so it is not discovered late.** This coverage test needs a store the
> efficacy path does not currently read. ~~It is satisfiable from existing data — `PipelineRunRecord` carries
> `Collectors`, `SourcesFailed` and `CollectionWarnings`, and records are persisted per run under
> `data/runs/{yyyy}/…`~~ — **CORRECTED by the 2026-08-03 amendment (spec 169): it is NOT.** An aggregate
> `SourcesFailed` cannot separate a failed RSS feed from a failed `newssearch` feed, carries no per-company
> granularity, and cannot reveal that a *successful* query hit its result limit. See that amendment for the
> prospective per-collector + per-company coverage contract that replaces this claim; `null` there means
> UNPROVEN, never success, and is never backfilled. The evaluator must read run records alongside snapshots.
> Per-day granularity holds while collection runs daily; a change to that cadence would need this rule
> revisited.

Within a complete window, **the absence of any `MediaAttention` signal is a valid outcome of zero
and must remain in the sample**. It is the central negative case, not missing data. If one or more
`MediaAttention` signals exist but any required evidence fails to resolve, the company-date is dropped and
counted as `UnresolvedOutcomeEvidence` rather than treated as a lower publisher count. When all relevant evidence
resolves, the outcome is the distinct-publisher count, including zero.

**6 · Persistence comparators — read-side, not configured scoring arms.** Both are mandatory to compute;
only the primary drives the screen. Attention is strongly autocorrelated, so predicting future attention
from current attention is nearly free, and a Radar score that merely reproduces it has discovered nothing.

- **PRIMARY — `baseline-attention-persistence`: the trailing distinct-publisher count over `(D − 21, D]`,
  built by exactly the same construction as the outcome** (distinct third-party publishers with a resolving
  `MediaAttention` signal). Same units, same units of failure, no quantisation — it answers "recently
  covered companies keep being covered" in the outcome's own terms, which makes it the hardest honest
  baseline available and the one the screen must clear.
- **SECONDARY — `baseline-attention-score`: the stored `AttentionScore` from that same row's
  `disclosure-led-v11` snapshot.** Retained and **reported**, never screened on. It is a `[0,100]` clamped
  integer, so it is coarse and tie-prone against an unbounded count outcome; beating a quantised predictor
  is a weaker result than beating the primary, and reporting both makes that difference visible rather than
  letting a soft baseline flatter the arm.

⚠️ **`AttentionScore` here keeps its v8 meaning over the whole gated set.** Spec 157 §3 now rejects breadth
as a v11 strategy channel; it does **not** narrow this diagnostic component. Changing `AttentionScore` would
silently turn the secondary comparator into "positive-only attention persistence" — a different and weaker
predictor, and an easier one to beat.

**7 · Primary statistic and failure screen.** The primary arm is **`disclosure-led-v11`**
(one `sec-edgar` channel at 1.00, S 3; no breadth channel), paired with an identical
`disclosure-led-v10-control` budget.

> **AMENDED 2026-07-28 — the primary arm was `filings-led-v11` and is now `disclosure-led-v11`.** Spec 158
> measured the original budget (`sec-form4` .50 / `sec-13dg` .30 / breadth .20) at a **constant integer 0
> across all 43 companies**, which the degeneracy rule below would have excluded — so it could never have
> cleared or failed this screen. Its initial design suggestion B (`sec-edgar` .60 / RSS .40) had slightly
> better rank resolution, but a post-merge input-only audit found RSS configured for only **26/43** companies
> versus `sec-edgar` for **43/43**. The missing 17 would conflate no configured source with a valid quiet
> window. The adopted arm is therefore spec 158's measured option A: 13/43 non-zero, 9 distinct integers,
> largest tie-group 30 — strictly smaller and uniformly observable at the source-configuration level.
>
> Amended **before any v11 snapshot or forward outcome existed**, so the pre-commitment is intact; §6's
> secondary comparator reads the `disclosure-led-v11` snapshot. Metric, novelty rule, horizon, missing-data
> rules, comparators, statistic and failure threshold are unchanged; §4 separately moves the first eligible
> date to exclude the known predictor-resolution transition.

Comparison is paired on exactly the same eligible companies at each as-of date:

1. require at least **20 companies** with a usable forward outcome on that date;
2. compute the cross-sectional Spearman ρ of `disclosure-led-v11.OpportunityScore` against the forward
   publisher count;
3. over those same companies, compute the cross-sectional Spearman ρ of the **primary** comparator
   `baseline-attention-persistence` (the trailing publisher count) against that same outcome, and — reported
   alongside, never screened on — the same for the secondary `baseline-attention-score`;
4. form the paired daily difference `δ(D) = ρ_v11(D) − ρ_persistence(D)` against the **primary** comparator.

A date whose outcome or either predictor is constant is excluded under a named degeneracy and does not count
toward the minimum. The precommitted descriptive screen is the **median `δ(D)` over at least 20 eligible
dates**. A median `<= 0` records a **MISS** for the operationalised thesis at its declared horizon; it may
not be rescued by changing the outcome or horizon after inspection. A median `> 0` clears only this necessary
screen.

The 20 daily windows overlap and are not independent. This screen therefore makes **no significance,
confidence or efficacy claim in either direction**. A valid dependence-aware comparison remains parked in
`docs/next/deferred/155-…`, and AD-15's suspension governs every positive claim regardless of what this
screen shows.

**Status.** **Accepted · 2026-07-28** — accepted by the maintainer on the day it was proposed, and **binding
from this point**: the stealth thesis is what Radar tests, and the consequences listed above (long window,
notedness discount load-bearing, no neutral amplification, attention-arrival as the primary outcome,
benchmark-adjusted price) govern subsequent work. Superseding it requires a new AD, not a config edit.
Cross-references
AD-6 (formula structure is code), AD-9 (no advice language), AD-14 (price is validation-only), AD-15 (a
composite must beat every baseline out-of-sample), and spec 153's `CompositionRevision` mechanism, which is
how the v10 correction this AD implies must be made visible.

### AMENDMENT · 2026-07-29 — §4's first eligible primary-screen date moves for the spec-160 comparability cap

Spec 160 adds a deterministic comparability scan and confidence cap to the AI directional filing read: when
an earnings release itself declares comparability breaks ("litigation settlement", "discontinued
operations", …), the persisted confidence of the `GuidanceChange` signal is bounded by
`min(readConfidence, ComparabilityConfidenceCap)` (default 0.65). Those signals attach to `sec-edgar`
evidence — **exactly the collector `disclosure-led-v11`'s single channel consumes** — so spec 160 changes
the §7 primary arm's *input regime*.

The transition is not instantaneous, and that is why the boundary must move. Spec 160's cache rule heals
forward: a cached read with a **null** comparability policy (written pre-160) remains a cache HIT and keeps
replaying its **uncapped** confidence for as long as its filing stays in the scoring window. A 60-day
scoring window therefore mixes capped and uncapped reads until **(first post-160 baseline run date) + 60
days** — later than the current 2026-09-26 boundary for any merge after 2026-07-27. Screening across that
seam would compare v11 snapshots computed under two different confidence regimes as if they were one.

**§4 is amended as follows.** The first eligible primary-screen as-of date becomes **the later of
2026-09-26 and (first post-160 baseline run date + 60 days)**. The concrete date is left for the operator to
record here after that first run completes (expected ≈ 2026-09-28/29 for a ~2026-07-30 merge, exactly as §4
recorded the spec-145 run's timestamp after the fact). The arms may accrue before then; transitional
snapshots do not enter the primary screen. Metric (§1), novelty rule (§2), horizon (§3), missing-data rules
(§5), comparators (§6), statistic and failure screen (§7) are all unchanged.

Amended **before any v11 primary-screen outcome existed** — moving a precommitted boundary is itself a
precommitment-integrity act and must be written down before the data exists, which is now. For the avoidance
of doubt, this amendment does **not** correct any accrued signal: the CASS 0.90 read that motivated spec 160
stands untouched (heal forward, specs 142/145) and ages out of the 60-day window naturally after as-of
≈ 2026-09-21.

### AMENDMENT · 2026-07-31 — §7's eligible set excludes the spec-166 event-enriched cohort

Universe batch 4 (spec 166, eight companies) is an **event-enriched exploratory cohort**: several names were
proposed partly because of known 2026 events — current manifestations of the very predictor Radar scores,
in one case brushing the attention outcome itself. §7 pairs "exactly the same eligible companies at each
as-of date", so without this amendment those names would enter the binding primary screen as they accrue
usable forward outcomes, and a screen that clears *because* of them would prove enrichment, not
discrimination. Reporting the cohort separately (spec 166's own text) does not change §7; this amendment
does.

**§7 is amended as follows.** The eligible company set at every as-of date **excludes** the members of any
cohort declared `"excludeFromPrimaryScreen": true` in a committed cohort file under `docs/cohorts/`. The
first such file is **`docs/cohorts/event-enriched-2026-07.json`** (machine-readable — the evaluator reads
the file, never git history), listing the eight spec-166 tickers with their CIKs. These companies may
appear **only** in a separately labelled exploratory rerun of the same statistic, reported beside — never
pooled into — the primary result. Everything else in §7 (minimum-20 rule, metric, comparators, degeneracy
rule, MISS semantics) is unchanged; the minimum-20 count is taken **after** the exclusion.

Amended **before any batch-4 company holds a single snapshot or forward outcome** — the cohort exists only
as a pending spec — so the precommitment is intact. Future universe additions must either satisfy neutral
selection (sector fill + filing cadence, price and events unconsulted — the spec-125/159 standard) or ship
with their own `docs/cohorts/` exclusion file in the same PR that seeds them.

### AMENDMENT · 2026-08-03 — §4's boundary is CONCRETE, and §5's coverage dependency was factually too strong

Spec 169 makes §§1–7 executable. Making them executable exposed exactly one factual error in this AD and
left exactly one value still open. Both are settled here, **before any v11 primary-screen outcome exists**.

**A · §4's first eligible primary-screen as-of date is 2026-09-29.** The 2026-07-29 amendment left the
concrete date to be recorded once the first post-spec-160 baseline run existed. It does:

- `PipelineRunRecord` `7f28ca48-5cb3-4646-8d57-56baf1e482e1`;
- `CreatedAtUtc = 2026-07-30T08:07:19.5804397Z`.

Sixty days from that instant ends **during** 2026-09-28. Pinning **2026-09-29** — the first whole UTC
calendar day that is unambiguously past the 60-day seam — keeps eligibility from depending on the intraday
schedule of a once-daily job: a run that drifts twenty minutes earlier must not silently make a date
eligible that yesterday's identical run did not. It is conservative by **less than one day**, it is recorded
before any outcome for that date can exist, and it changes none of the metric, horizon, comparator or
failure rules. The evaluator carries this date as a code constant, not as configuration: a precommitted
boundary that an operator can tune is not a precommitment.

Twenty daily eligible dates therefore cannot exist before roughly early November 2026. Until then the
evaluator's status is `Pending`. That is **expected accrual, not a defect**, and it is deliberately
distinguished from the availability failures in C below.

**B · Prospective coverage provenance must be recording by 2026-09-08.** The first eligible date's
comparator window opens at `2026-09-29 − 21 days = 2026-09-08`, so the per-collector/per-company coverage
record described in C has to be live by then for that date to be usable at all. If it lands later, **do not
backfill success and do not move the boundary.** The affected company-dates are honestly
`IncompleteAttentionCollection`, and later as-of dates become usable as their own complete windows accrue.
Moving a precommitted boundary to rescue observations Radar failed to record is the exact
unfalsifiability failure the pre-commitment clause exists to prevent.

**C · §5's "Implementation dependency" blockquote is CORRECTED.** It claimed the coverage test was
"satisfiable from existing data — `PipelineRunRecord` carries `Collectors`, `SourcesFailed` and
`CollectionWarnings`". **That is factually too strong, and the difference is not cosmetic:**

- `SourcesFailed` is an **aggregate across every collector**. It cannot distinguish two failed RSS feeds
  (irrelevant to this metric) from one failed `newssearch` feed (fatal to it).
- Nothing in the record is **per company**. A run in which `newssearch` failed for one company and
  succeeded for forty-two is indistinguishable from one in which it failed for all forty-three.
- Nothing records that a **successful** query hit its result limit. A truncated-but-successful feed is the
  most dangerous case of all: it looks like complete coverage and silently undercounts publishers.

A recent *global* collection date is therefore **not** proof of attention coverage. The dependency is
replaced by a **prospective per-collector, per-company coverage contract** recorded on the run record:

- `PipelineRunRecord.CollectorRuns` — one row per collector that ran, in stable collector order, carrying
  that collector's own unmerged summary (sources checked / succeeded / failed, items collected, its source
  failures) plus optional per-company coverage. It is recorded **before** the collection merge, because the
  merge discards collector identity.
- `CollectorRunRecord.CompanyCoverage` — for `newssearch`, one row for **every** company in the collection
  context (not only those with feeds): expected feed count, successful feed count, whether any feed's **raw**
  reader result count reached the **effective clamped** request limit, and a stable ordinally-sorted issue
  set drawn from the closed vocabulary `CollectionHealthMismatch`, `MissingFeed`, `ResultLimitReached`,
  `SourceFailure`. An empty issue set means complete at that checkpoint.

Both fields are trailing and optional, so every existing on-disk run record still deserializes. They are
**observational run provenance only** — never an evidence, signal, score, fingerprint or
strategy-comparability input. **For coverage purposes `null` means UNPROVEN, never success**, and it is
never inferred or backfilled for records written before this contract existed (heal forward — specs
142/145).

Coverage for a company over an exact interval `(a, b]` requires a chain of *complete* checkpoints: one at or
before `a` no more than 36 hours earlier, one at or after `b` no more than 36 hours later, and no gap
greater than 36 hours between consecutive complete checkpoints spanning it. A partial collection run
(`CompanyFilter != null`), a score-only run, a legacy record without `CollectorRuns`, a failed or capped
company feed, or a `newssearch` feed-inventory health warning each break the chain, and the company-date is
dropped as `IncompleteAttentionCollection` with the more specific coverage reason reported alongside. The
rule is applied **separately** to `(T − 21d, T]` and `(T, T + 21d]`.

This is an operational statement about **Radar's configured news source**, not a claim that Google News
indexes the whole web, and every rendered artifact must say so. If an enabled collector other than
`newssearch` can emit third-party `MediaAttention`, the evaluation fails with `UnsupportedAttentionCollector`
rather than silently mixing in signals whose coverage cannot be proved.

Per article, only a **RECORDED** collector stamp (spec 146) admits it to the publisher count. Spec 151's
**inferred** attribution is treated exactly like missing attribution — `Unresolved*Provenance` — because it
re-derives which collector retrieved an article and a derivation cannot prove that article's collection was
complete. This is the per-article reading of the same rule §5 states per record — attribution that is
"never inferred or backfilled", where an unproven value means UNPROVEN, never success — and of spec 169's
"No inferred success" constraint. It also keeps the precommitted metric **invariant to
`Radar:Scoring:InferLegacyCollectorAttribution`**, a scoring-only flag an operator can flip between runs;
a metric that moves with such a knob is the unfalsifiability this AD exists to prevent. The check is on the
attribution's *source*, structurally, not on which resolver happens to be composed.

**Unchanged by this amendment:** the metric (§1), the publisher-novelty rule (§2 — still not used), the
21-day horizon with no exit tolerance (§3), the missing-data/valid-zero rule (§5's substance), both
comparators (§6), and the primary statistic, degeneracy rule, minimum-20 rule and median-δ failure screen
(§7). No scoring input, formula version, rule-set version or fingerprint moves: coverage is recorded
provenance, and the evaluator is read-only.
