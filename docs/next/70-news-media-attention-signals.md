# Task: Turn GDELT news evidence into MediaAttention signals so third-party coverage lifts AttentionScore

## Overview

Spec 67 landed the GDELT news collector: each watch-universe company's recent third-party news
coverage is now collected as `NewsArticle`-type evidence with full provenance (article `url` as
`SourceUrl`, `domain`/`seendate`/`language` metadata, `Medium` declared quality) and — critically —
`NewsArticle` is a **third-party market-attention** source (`EvidenceSourceTypes.IsThirdPartyAttentionSource(NewsArticle) == true`, AD-6). But — exactly as spec 62's USASpending contract evidence produced no
signal until spec 63 — that news evidence produces **no signal today**, so it never enters any
company's signal set and never lifts the score. This slice is the deliberate signal-extraction
follow-up to spec 67, mirroring precisely how spec 63 followed spec 62.

**The gap (this is the motivation — state it in the PR).** Confirmed by a live four-collector run on
2026-07-02 (`["rss","sec","usaspending","news"]`): **Aehr Test Systems collected 2 relevant
`NewsArticle` evidence items, yet its `AttentionScore` stayed 0** and the news never surfaced in its
scored evidence. Root cause — **Attention is signal-gated**. `RadarScoreFormulaV2` computes Attention
from the evidence behind the window's *signals*, not from raw evidence
(`src/Radar.Application/Scoring/RadarScoreFormulaV2.cs`, ~lines 139-150):

```csharp
var distinctThirdPartySources = signals
    .Where(s => EvidenceSourceTypes.IsThirdPartyAttentionSource(s.Evidence.SourceType))
    .Select(s => s.Evidence.SourceName)
    .Where(name => !string.IsNullOrWhiteSpace(name))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .Count();
var mediaCount = signals.Count(s => s.Signal.Type == SignalType.MediaAttention);
var reach = distinctThirdPartySources + MediaReachWeight * mediaCount;   // MediaReachWeight = 0.5
```

`ScoringInput.Signals` is a list of `(Signal, Evidence)` pairs — **only evidence that produced a signal
in the window enters the scoring input at all**. A `NewsArticle` evidence item that yields no signal
contributes nothing to `distinctThirdPartySources` *or* `mediaCount`. And the `KeywordSignalExtractor`
produces **no signal from a news headline** — its rules are press-release-oriented phrases. So news is
collected but never counted, and Attention stays 0 for every company.

**The domain already reserves the fix.** `SignalType.MediaAttention` **already exists** in the enum
(`src/Radar.Domain/Signals/SignalType.cs`) and the formula **already counts it** (`0.5·mediaCount`) —
nothing PRODUCES it yet. This slice closes exactly that gap: for each `NewsArticle` evidence item, emit
one `MediaAttention` signal. Its evidence is that article, so (a) the article's third-party
`SourceName` now counts toward `distinctThirdPartySources` and (b) it increments `mediaCount` — both
lift Attention. Result: Aehr's 2 articles → `mediaCount 2` + `1` distinct third-party source name →
`reach = 1 + 0.5·2 = 2` → `AttentionScore = 100·2/(2+5) = 29`, non-zero at last. No scoring-formula
change: this slice **produces the signals the AD-6 formula already consumes**.

**Direction is `Neutral`, deliberately.** Coverage itself is not directional — being written about does
not mean the thesis is improving or deteriorating. `Neutral`:
- keeps the signal OUT of `TrajectoryScore` (AD-6: `Neutral`/`Mixed` are excluded from both numerator
  and denominator), so news can never fake a `Thesis improving`/`Thesis deteriorating` label; and
- honours the output-language rule — media coverage is a fact of attention, never a view or advice.

---

## This is intentionally source-type-driven (unlike keyword matching)

Spec 63 deliberately kept the extractor **source-agnostic** (it matches phrases, never reads
`evidence.SourceType`). This category is different **on purpose**: the *existence* of third-party news
coverage is itself the signal — it is keyed on `EvidenceSourceType.NewsArticle`, not on any phrase in
the headline. There is no phrase that reliably means "this is news coverage"; the source type is the
fact. So this slice makes the extractor **source-type-aware for the `NewsArticle` attention category**,
exactly as spec 66 made it **metadata-aware** for `GovernmentContract` materiality. Both are contained,
documented, single-category refinements to the one deterministic extractor; **all existing keyword
behaviour for every other evidence type stays byte-for-byte unchanged.** (Note: spec 66 already relaxed
the strict "reads nothing but text" invariant by reading `evidence.MetadataJson`; reading
`evidence.SourceType` for this one category is the same shape of deliberate, narrow capability. No new
architecture-decisions entry is required — AD-6 already reserves `MediaAttention`; this slice merely
feeds it — but document the branch with a clear code comment, as spec 66 did for the amount tiers.)

---

## Design decision #1 — RECOMMENDED: `NewsArticle` evidence emits ONLY the Neutral `MediaAttention` signal

The current extractor runs the keyword rule table over **all** evidence, so a headline like
`"Company wins major contract"` would today emit a `CustomerWin` Positive (if news produced any signal
at all). The design question: for `NewsArticle` evidence, should the directional keyword rules **also**
run (so a headline could still emit a `CustomerWin`), or should news evidence emit **only** the Neutral
`MediaAttention` signal?

**Recommendation: news evidence emits ONLY the Neutral `MediaAttention` signal — suppress the
directional keyword rules for `NewsArticle` evidence.** Justification:

1. **News framing ≠ the company's own disclosure.** A directional signal asserts a real business-
   trajectory event (a customer win, a guidance raise). A third-party headline is a *report about* an
   event, often re-worded, aggregated, or editorialised (the verified GDELT set included a Yahoo
   "Among the Best Mid Cap Defense stocks" listicle). Trusting a headline's wording to mint a directional
   `Positive` signal is a much weaker provenance claim than trusting a company's own press release or an
   SEC filing item.
2. **Double-counting risk.** News frequently *echoes* a press release Radar already collects first-party
   (RSS/spec 55). If both the press release AND its news echo emitted a `CustomerWin`, the same real-world
   event would be counted twice in Trajectory — inflating a directional read off one event. Emitting only
   a Neutral `MediaAttention` for the news echo lifts *Attention* (the correct axis for "the market is
   noticing") without re-inflating Trajectory.
3. **Conservative and provenance-honest for this slice.** Whether to trust third-party-framed directional
   signals is a real, riskier question best answered by a future **AI-sentiment** slice that can read the
   article body and judge framing — not by the deterministic keyword table. Deferring it keeps this slice
   small, deterministic, and defensible.

Because the extractor currently runs keyword rules on all evidence, "MediaAttention only" **must be
implemented as suppression**: for `NewsArticle` evidence the extractor bypasses the keyword rule loop
entirely and emits exactly the one `MediaAttention` signal. This is **tested** (a news headline
containing a directional cue like `"wins contract"` yields ONLY the `MediaAttention` signal, no
`CustomerWin`).

---

## Design decision #2 — where it lives: a contained `NewsArticle`-scoped branch in `KeywordSignalExtractor`

The pipeline runs a **single** `ISignalExtractor` (the deterministic `KeywordSignalExtractor`; the AI
extractor is a later slice). A separate component would need its own registration and a composition
seam that does not exist. **Recommendation: add a small, contained `EvidenceSourceType.NewsArticle`
branch at the top of `KeywordSignalExtractor.ExtractAsync`**, documented as the coverage/attention rule.
It reuses the evidence already in hand, preserves determinism, and keeps the first-match/dedupe
conventions intact for every other source type. This mirrors spec 66's contained metadata branch in the
same class.

---

## Proposed signal values (a tunable starting point)

A Neutral coverage signal should be modest — it states "the market is paying some attention," no more:

| Field | Value | Rationale |
|---|---|---|
| `SignalType` | `MediaAttention` | already in the enum; already counted by the formula |
| `Direction`  | `Neutral` | coverage is not directional (see Overview) |
| `Strength`   | `3` | modest; **at** the reviewer's `MinMaterialStrength` (strict `< 3`) so it passes materiality but claims little |
| `Novelty`    | `4` | above the reviewer's `MinNovelty` (`3`); a fresh article is mildly novel |
| `Confidence` | `0.5` | reflects news's `Medium` quality — below a first-party press release's `0.6` |

All values are within domain validation ranges (Strength/Novelty 1-10, Confidence 0-1) so the mapped
signal passes `SignalValidation`. Present these as a **tunable starting point**; a reviewer may adjust.

**Reviewer interaction (state the expected outcome).** Run the proposed signal through
`DeterministicSignalReviewer` (spec 11): `Strength 3` is NOT `< 3`; `Novelty 4` is NOT `< 3`;
`Confidence 0.5` is NOT `< 0.40`; and `NewsArticle` evidence is declared `Medium` quality, which is NOT
`Unknown`/`Low` — so **no materiality/novelty/hype/weak-source issue fires**. Assuming the company
mention resolves (the same entity-resolution precondition as every other signal), the decision is
**`Approve`** and the signal enters scoring as `Approved` — which is what lets it lift Attention. (Had
`Strength` been set below 3 it would be flagged `NeedsMoreEvidence` and never reach scoring; `3` is the
deliberate floor that passes. Note this when choosing the tunable value.)

---

## Assignment

Worktree: any
Dependencies: **67** (GDELT news collector — produces the `NewsArticle` evidence) merged. **63** (USASpending
contract-evidence → `GovernmentContract` signal) is the direct precedent for the evidence-type → signal
pattern — read it. **69** established the `ScoringEngine.ScoringConfigVersion` convention this slice bumps
— read it. Neither 63 nor 69 is a code dependency beyond the files below.
Conflicts with: touches the **shared** `KeywordSignalExtractor` **and** `ScoringEngine.ScoringConfigVersion`
and both their test files — it must **NOT** run in parallel with any other extractor-editing or
scoring-editing slice. No collector/DI/schema/Domain change.
Estimated time: ~1.5-2 h

---

## Project structure changes

```text
src/Radar.Application/SignalExtraction/
  KeywordSignalExtractor.cs           # MODIFIED: add a contained NewsArticle branch at the top of
                                      #   ExtractAsync -> emit exactly one Neutral MediaAttention signal
                                      #   and return (suppresses the keyword loop for news evidence)

src/Radar.Application/Scoring/
  ScoringEngine.cs                    # MODIFIED: bump ScoringConfigVersion -> "radar-scoring-config-v2"
                                      #   (this is a scoring-affecting change; spec-69 convention)

tests/Radar.Application.Tests/SignalExtraction/
  KeywordSignalExtractorTests.cs      # MODIFIED: NewsArticle evidence -> one Neutral MediaAttention signal
                                      #   with a real excerpt; directional headline on news -> ONLY
                                      #   MediaAttention (no CustomerWin); non-news evidence unaffected;
                                      #   determinism

tests/Radar.Application.Tests/Scoring/
  (existing scoring tests)            # MODIFIED/ADDED: a window whose only signal is a Neutral
                                      #   MediaAttention over NewsArticle evidence -> AttentionScore > 0;
                                      #   update any test asserting the old ScoringConfigVersion string
```

No production code outside `Radar.Application` changes. Collectors, DI, seed, the scoring **formula**,
signal review, policy, report, and the Domain enum are untouched.

---

## Implementation details

### Keyword extractor — the contained NewsArticle attention branch

At the top of `ExtractAsync`, after the `ct`/null guards and before the keyword rule loop, branch on
source type:

```csharp
// Third-party news coverage is inherently a source-type signal, not a phrase signal: the EXISTENCE of
// NewsArticle evidence is the attention event (spec 70). Emit exactly one Neutral MediaAttention signal
// and return, deliberately SUPPRESSING the directional keyword rules for news (news framing != the
// company's own disclosure; avoids double-counting a press release + its news echo — see spec 70).
// This is the second source-type-aware branch in this deterministic extractor (spec 66 was the first,
// metadata-aware for GovernmentContract materiality); all keyword behaviour for other sources is
// unchanged below.
if (evidence.SourceType == EvidenceSourceType.NewsArticle)
{
    var searchable = EvidenceSearchableText.Compose(evidence.Title, evidence.RawText);
    var excerpt = BuildExcerpt(searchable, matchIndex: 0, phraseLength: 0);   // verbatim provenance slice
    var signal = new ExtractedSignal(
        CompanyMention: evidence.SourceName,
        SignalType: SignalType.MediaAttention.ToString(),
        Direction: SignalDirection.Neutral.ToString(),
        Strength: 3,
        Novelty: 4,
        Confidence: 0.5m,
        SupportingExcerpt: excerpt,
        Reason: "Third-party news coverage (media attention)");
    return Task.FromResult(new ExtractSignalsOutput(
        new List<ExtractedSignal> { signal },
        "1 media-attention signal extracted from news coverage."));
}
```

- `SupportingExcerpt` must be a **real, verbatim** slice of the composed searchable text (the headline
  and any body). Reuse the existing `BuildExcerpt` (a `[start..end]` slice of the composed text) so the
  excerpt survives the mapper's excerpt-in-evidence provenance round-trip, exactly like every other
  extracted signal. Excerpting from index 0 with the existing `ExcerptWindow` yields the leading portion
  of the headline — a genuine provenance excerpt. (If the headline can be empty in edge cases, fall back
  to a bounded slice of `RawText`; `Compose` already joins Title + RawText, so index 0 covers the title.)
- `CompanyMention = evidence.SourceName` — identical placeholder convention to the keyword path; a
  company/ticker is never guessed here (entity resolution stays downstream).
- Exactly **one** signal per `NewsArticle` evidence item. Two `NewsArticle` items from the same company
  feed therefore yield two `MediaAttention` signals sharing one `SourceName` — precisely the
  `mediaCount 2` + `1` distinct source name that produces `AttentionScore = 29` in the Overview.
- All numbers stay in domain range so the mapped signal passes `SignalValidation`. `Neutral` keeps it
  out of Trajectory (AD-6).
- Everything below the branch (the `Rules` table, the metadata-aware `GovernmentContract` materiality
  tiers from spec 66, first-match-per-`SignalType` dedupe, stable ordering, verbatim excerpts) is
  **unchanged** and still runs for all non-`NewsArticle` evidence.

### Scoring — bump the generation stamp (spec-69 convention)

Raising Attention above 0 for news-covered companies is a **scoring-affecting change**: pre-70 and
post-70 snapshots are not comparable (a company gains Attention purely because Radar now extracts a
signal it did not before). Per spec 69's `ScoringConfigVersion` convention, bump it in `ScoringEngine`:

```csharp
// src/Radar.Application/Scoring/ScoringEngine.cs
private const string ScoringConfigVersion = "radar-scoring-config-v2";   // was "radar-scoring-config-v1"
```

Update the accompanying comment to record that spec 70 (news → MediaAttention signals) is the change
that ships this generation, so a cross-run delta correctly renders `(scoring updated)` instead of a
fabricated `Thesis improving`/`Thesis deteriorating` across the pre/post-70 boundary. Do **not** touch
`ScoringVersion`, `EngineVersion`, or `RadarScoreFormulaV2.Version` — the formula math is unchanged.

---

## Tests

Extend `KeywordSignalExtractorTests` (xUnit; reuse `EvidenceBuilder`/`MakeEvidence`). Use
`new EvidenceBuilder().WithSourceType(EvidenceSourceType.NewsArticle)…` to build news evidence.

1. **NewsArticle evidence → exactly one Neutral MediaAttention signal (the gap this slice closes).**
   A `NewsArticle` evidence item with a real headline (e.g. Title
   `"Aehr Test Systems , Inc . ( AEHR ): Q3 wafer-level test order momentum"`) yields `Single` signal
   with `SignalType == MediaAttention`, `Direction == "Neutral"`, `Strength == 3`, `Novelty == 4`,
   `Confidence == 0.5m`, and a `SupportingExcerpt` that is a verbatim `Contains` slice of the composed
   searchable text. Assert `CompanyMention == evidence.SourceName`.
2. **Directional headline on news → ONLY MediaAttention, no directional signal (the suppression rule).**
   A `NewsArticle` item whose Title contains a directional keyword cue (e.g. `"Acme wins contract with
   the US Navy"` — which on a press release would emit a `CustomerWin`) yields `Single` signal, and it
   is `MediaAttention` Neutral — assert NO `CustomerWin`/`GovernmentContract` signal is present.
3. **Round-trips to a valid `Signal`.** The extracted `MediaAttention` signal passes
   `ExtractedSignalMapper.ToSignal(...).IsValid` (excerpt provenance survives the mapper round-trip).
4. **Non-news evidence is completely unaffected (regression).** All existing extractor tests stay green:
   a `PressRelease`-typed `"wins contract"` still yields a `CustomerWin`; the USASpending
   `GovernmentContract` materiality-tier tests (spec 66) still pass; the `"Top Benefits Award"` negative
   case is unchanged. A `PressRelease` item never yields a `MediaAttention` signal.
5. **Determinism/reproducibility.** Two extractions over the same `NewsArticle` evidence yield equal
   signal sequences (extend or rely on the existing determinism test).

Extend the scoring tests (`tests/Radar.Application.Tests/Scoring/`):

6. **News coverage lifts Attention above 0 (the downstream payoff — ideally an end-to-end-ish scoring
   test).** Build a `ScoringInput` whose window contains one (or two) `MediaAttention` Neutral
   signal(s) each paired with a `NewsArticle` evidence item (distinct `SourceName`s or the same, per the
   case), and assert `Compute(...).Components.AttentionScore > 0` (and that `TrajectoryScore` stays at
   the neutral 50 baseline — the Neutral signal does not move it). This proves both the `mediaCount` and
   the `distinctThirdPartySources` terms fire. A first-party-only window still scores `Attention == 0`
   (unchanged).
7. **ScoringConfigVersion stamp.** Update any test that asserts the snapshot's `ScoringConfigVersion`
   (search for `"radar-scoring-config-v1"`) to expect `"radar-scoring-config-v2"`.

No advice language is introduced; all values stay in domain range.

---

## Spec-implementation checklist

1. **Code paths replaced:** the keyword rule loop is **skipped** for `NewsArticle` evidence (new early
   branch). No existing rule is removed; every non-news path is unchanged. The `ScoringConfigVersion`
   constant is updated (the only scoring change).
2. **Tests:** add the news-evidence cases (1-3), the suppression case (2), the Attention-lift scoring
   case (6), and update the `ScoringConfigVersion` assertion (7); keep all existing extractor and
   scoring tests green.
3. **Delete nothing still used:** the keyword `Rules` table and the spec-66 materiality tiers remain
   valid for all non-news evidence — do not remove them.
4. **CLAUDE.md:** no architecture rule changes (extraction stays deterministic, in `Radar.Application`,
   before AI; provenance preserved). **No CLAUDE.md update needed** — note this in the PR. (No new
   `architecture-decisions.md` entry: AD-6 already reserves `MediaAttention`; this slice feeds it.)

---

## Constraints

- Target `net10.0`, C# 14. Extraction stays deterministic and in `Radar.Application`, **before** any AI
  (prefer deterministic code first). Reuse the single `KeywordSignalExtractor` — no new extractor, no
  new DI registration.
- **No scoring-formula change** — Attention already consumes third-party source names + `mediaCount`
  (AD-6); this slice only produces the `MediaAttention` signals the formula already counts.
- **No collector change** and **no Domain enum change** — `NewsArticle` (source type) and
  `MediaAttention` (signal type) already exist.
- No provider SDK; no DB (AD-8, files-first); no AI. Provenance preserved (verbatim excerpt; evidence →
  signal via the mapper). Never emit advice language — media coverage is `Neutral`, a fact of attention,
  not a view.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Acceptance criteria

- [ ] For each `NewsArticle`-typed evidence item, `KeywordSignalExtractor` emits **exactly one**
      `MediaAttention` signal with `Direction == Neutral`, a real verbatim `SupportingExcerpt`, and
      `CompanyMention == evidence.SourceName`, via a contained `EvidenceSourceType.NewsArticle` branch.
- [ ] The directional keyword rules are **suppressed** for `NewsArticle` evidence: a news headline
      containing a directional cue (e.g. `"wins contract"`) yields ONLY the `MediaAttention` signal, no
      `CustomerWin`/directional signal (asserted by test).
- [ ] A window whose signals include a `MediaAttention` over `NewsArticle` evidence scores
      `AttentionScore > 0` (via `mediaCount` and `distinctThirdPartySources`), while `TrajectoryScore`
      stays at the neutral baseline and a first-party-only window still scores `Attention == 0`
      (asserted by a scoring test).
- [ ] The extracted signal round-trips valid via `ExtractedSignalMapper.ToSignal`; the proposed values
      (Strength 3 / Novelty 4 / Confidence 0.5) pass `DeterministicSignalReviewer` as `Approve` (not
      flagged `NeedsMoreEvidence`), given a resolved company.
- [ ] Non-news evidence is completely unaffected: all existing keyword rules, the spec-66
      `GovernmentContract` materiality tiers, and the `"Top Benefits Award"` negative case stay green; a
      `PressRelease` item never yields a `MediaAttention` signal.
- [ ] `ScoringEngine.ScoringConfigVersion` is bumped to `"radar-scoring-config-v2"` (spec-69 convention)
      and any test asserting the old value is updated; `ScoringVersion`/`EngineVersion`/formula `Version`
      are unchanged.
- [ ] No collector/DI/seed/scoring-formula/policy/report/Domain-enum change. Extraction is deterministic
      and reproducible. `build`/`test` green.
