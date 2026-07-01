# Task: `radar-formula-v2` — make corroboration and source diversity raise scores, not lower them

## Overview

The first two-collector live run (`["rss","sec"]`, 111 signals across RSS press releases + SEC 8-K filings)
exposed a structural flaw in `radar-formula-v1`: **adding corroborating evidence *lowered* every company it
touched.** The two companies that gained no SEC signals were the only ones that didn't drop; Helios fell from
Opportunity 32 → 22 despite gaining two real 8-K signals. Mechanism (all three factors moved the wrong way):

1. **Trajectory** is a confidence/recency-weighted mean of `sign·strength`. Neutral 8-K signals contribute `0`
   to the numerator but keep their weight in the denominator, dragging the mean toward 50 (Helios 80→61).
2. **Attention** counts distinct *source names*; adding the company's own SEC feed pushed Attention 17→29, and
   the `(1 − Attention/200)` term *penalises* that — but a company's own regulatory filing is **not** market
   attention.
3. **EvidenceConfidence** uses *mean* signal confidence, so a 0.40-confidence filing averaged *down* the
   0.60 press-release confidence, cancelling the diversity gain.

Net: more evidence → lower score, which is backwards for a research tool whose whole premise is corroboration.
This slice ships **`radar-formula-v2`** (an AD-6 update, maintainer-approved) with three targeted fixes so that
corroboration and source diversity *earn* a stronger label. `OpportunityScore` and `SignalVelocityScore`
formulas are unchanged. Existing v1 snapshots keep their recorded `ScoringVersion` (provenance intact).

**Maintainer-approved design decision (confidence aggregation):** *best-anchored + diversity bonus* — anchor on
the strongest signal and reward extra source types, so corroboration can never lower confidence.

---

## Assignment

Worktree: any
Dependencies: existing trunk (scoring + SEC collector merged).
Conflicts with: None — the formula, its DI registration, its tests, a small source-type classification, and the
AD-6 ledger entry.
Estimated time: ~2.5 h (the highest-care slice — a versioned, co-designed formula)

---

## The v2 formula (precise)

Implement `RadarScoreFormulaV2 : IScoreFormula` with `Version => "radar-formula-v2"`. Keep every v1 constant and
the Opportunity/Velocity formulas identical; change only the three components below.

### 1. TrajectoryScore — exclude zero-direction signals
- `TrajectoryScore = 50 + 5·T_raw` (unchanged shape).
- `T_raw` = confidence-and-recency-weighted mean of `directionSign·strength` **computed over only the signals
  whose direction is `Positive` or `Negative`**. `Neutral` and `Mixed` signals are excluded from **both** the
  numerator and the denominator (they no longer dilute the directional read).
- If a company has **no** directional signals, `T_raw = 0` → `TrajectoryScore = 50` (neutral).
- Direction signs unchanged: `Positive +1`, `Negative −1`.

### 2. AttentionScore — count only third-party (market) sources
- `AttentionScore = 100·reach/(reach+5)` (unchanged shape).
- `reach = distinctThirdPartySourceNames + 0.5·mediaSignals`, where `distinctThirdPartySourceNames` counts
  distinct evidence **source names** ONLY among evidence whose `EvidenceSourceType` is **third-party**.
- **Third-party (market attention) source types:** `NewsArticle`, `MediaAttention`, `SocialMedia`,
  `ConferenceMention`. **First-party (own disclosure) — everything else:** `PressRelease`, `Filing`, `RssFeed`,
  `CompanyBlog`, `EarningsTranscript`, `GovernmentContract`, `JobPosting`, `Patent`, `RegulatoryAnnouncement`,
  `InsiderTransaction`, `LocalFile`, `Manual`. First-party evidence contributes nothing to `reach`.
- With only first-party collectors configured today, `reach → 0` and `AttentionScore → 0` (correct: market
  attention is unmeasurable from first-party data alone). When a media/news collector lands, Attention becomes
  meaningful automatically.

### 3. EvidenceConfidenceScore — best-anchored + diversity bonus (monotonic)
- `EvidenceConfidenceScore = 100 · bestConf · (0.6 + 0.4·bestQualWeight) · (0.7 + 0.3·divFactor)`.
- `bestConf` = **max** signal confidence among the contributing signals (anchor — adding a weaker signal can
  never lower it).
- `bestQualWeight` = **max** evidence-quality weight among the contributing evidence, using the existing v1
  quality weights (Primary 1.0 / High .85 / Med .6 / Low .35 / Unknown .4). A High-quality filing raises it; a
  lower-quality source can't drag it down.
- `divFactor = min(distinctSourceTypes, 3) / 3` — distinct `EvidenceSourceType`s among contributing evidence,
  saturating at 3 (same shape as v1). Because it multiplies a max-anchored base, more source types only ever
  *increases* confidence.
- **Invariant to preserve (and test):** for a fixed set of signals, adding another signal/evidence item never
  *decreases* `EvidenceConfidenceScore` (monotonic non-decreasing under corroboration).

### Unchanged
- `SignalVelocityScore` — unchanged (activity legitimately rises with more filings).
- `OpportunityScore = Trajectory·(EC/100)·(1 − Attention/200)` — unchanged formula (its inputs now behave
  correctly).
- The `PreviousSignals` / window / provenance rules from AD-6 — unchanged.

---

## Version, DI, and ledger

- New `RadarScoreFormulaV2` with `Version = "radar-formula-v2"`; register it in DI in place of
  `RadarScoreFormulaV1`. Per the spec-implementation checklist, **delete `RadarScoreFormulaV1`** (and port its
  tests to V2) rather than leaving it dormant — the recorded `ScoringVersion` string on existing on-disk
  snapshots is provenance and does not require the V1 code to remain.
- Add a small first-party/third-party classification for `EvidenceSourceType` (e.g. a static
  `IsThirdPartyAttentionSource(EvidenceSourceType)` helper) — place it where the reviewer prefers (a Domain
  evidence helper or an internal scoring helper); document the third-party set as the "market attention" set.
- **Update `docs/architecture-decisions.md` AD-6:** record the v2 refinement (the three changes), mark the v1
  component formulas as *superseded by radar-formula-v2* with the maintainer-approved rationale (corroboration
  and diversity must not lower scores), and keep the note that snapshots remain identified by their recorded
  `ScoringVersion`.

---

## Tests

Port `RadarScoreFormulaV1Tests` → `RadarScoreFormulaV2Tests` with recomputed expected values, and add:
- **Trajectory neutral-exclusion:** a company with one Positive strength-6 signal + two Neutral signals scores
  the *same* trajectory as the Positive signal alone (Neutrals don't dilute); a company with only Neutral
  signals → Trajectory 50.
- **Attention first-party:** evidence from first-party types only → `AttentionScore == 0`; adding a third-party
  (`NewsArticle`/`MediaAttention`) source raises Attention. Assert a company's own `PressRelease`+`Filing` give
  Attention 0.
- **Confidence monotonicity + diversity:** adding a lower-confidence signal never lowers `EvidenceConfidenceScore`
  (max-anchor); adding evidence of a *new* source type raises it (diversity bonus); a High-quality filing raises
  `bestQualWeight`.
- **Scenario (the Helios case):** a Positive strength-6 press-release GuidanceChange (conf 0.65, Medium quality)
  + Neutral 8-K signals (Filing, High quality) yields Trajectory ~80, Attention 0, EvidenceConfidence ≈ 55, and
  `OpportunityScore ≥ 40` (i.e. the policy would label it `Watch`, not `Ignore`) — the corroboration payoff.
- **Version:** `Version == "radar-formula-v2"`.
- Keep all other scoring/pipeline/report tests green (update any that asserted the v1 version string or v1
  component values).

---

## Constraints

- Target `net10.0`. Deterministic, pure formula (no clock/IO). Reproducible outputs.
- This is an AD-6 change: bump the version, update the ledger, preserve v1-snapshot provenance. Do NOT change
  Opportunity/Velocity/window/provenance rules.
- No advice language; all component scores stay within their documented ranges.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

## Acceptance criteria

- [ ] `RadarScoreFormulaV2` (`Version = "radar-formula-v2"`) implements: Trajectory excluding zero-direction
      signals; Attention counting only third-party source types; EvidenceConfidence anchored on max
      signal-confidence and max quality with a saturating diversity factor. Opportunity/Velocity unchanged.
- [ ] Corroboration is monotonic: adding a signal/evidence item never lowers EvidenceConfidence, and Neutral
      signals never lower Trajectory — asserted by tests.
- [ ] The Helios-style scenario (strong Positive PR + Neutral High-quality 8-Ks) reaches `OpportunityScore ≥ 40`.
- [ ] `RadarScoreFormulaV1` removed, DI points at V2, AD-6 updated to record the v2 refinement; all suites green.
