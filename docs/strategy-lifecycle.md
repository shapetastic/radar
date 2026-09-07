# Strategy lifecycle journal

Append-only, audit-only (spec 184 §3). **The Worker never parses this file** — the only runtime input to
call resolution is `data/strategy-operating-calls.json`. One line per event
(declared / retuned-as-new-name / retired / call-made / call-reviewed — a review that KEEPS a call and
sets its next review-by / call-overridden / call-resolved / labels-set / finding), with strategy,
date, basis, actor, and — for resolutions — the outcome and an evidence reference. Retirement is config
removal plus a line here; changing `Radar:PrimaryStrategy` (storage/series identity only) likewise. No
automation for either. **A `call-made … Lead` must land in the same change as that arm's `Labels`
(`Radar:Strategies[i].Labels`, the Investigate / Watch lines) in the live profile** — the Worker refuses
to build the report otherwise (spec 212) — and the journal line records the values and the prevalence they
were chosen at (a workload decision connected to no outcome, never a claim about opportunity).

The maintainer's guidance, quoted exactly as given (2026-08-23):

> "As a mantra, I would prefer us to take a stab and potentially make a wrong call, rather than sitting on
> the fence and doing nothing."

Uncertain evidence changes the confidence and review date of a call; it does not eliminate the obligation
to make one. Radar records wrong calls rather than avoiding falsifiable decisions.

## Events

Opening entries are a one-time, best-effort backfill from git history (commit hashes cited); everything
after 2026-08-23 is appended as it happens.

- 2026-07-26 · declared · `default` · the synthesised single strategy became the named storage-primary
  strategy when multi-strategy scoring landed (spec 137, `518d3d1`) · actor: maintainer.
- 2026-07-27 · declared · `filings-led`, `narrative-led` · first live 3-strategy run beside `default`
  (spec 137 era) · actor: maintainer.
- 2026-07-27 · retuned-as-new-name · `filings-led` → `filings-led-v2`, `narrative-led` →
  `narrative-led-v2` · spec 141's immutable-by-convention rule: a retuned strategy gets a NEW name, the
  old series is never rewritten (specs 146/149, `3993981`/`5a60e7f`) · actor: maintainer.
- 2026-07-28 · declared · `filings-led-halfnoted`, `filings-led-nonoted` · notedness-discount ablation
  arms recorded in the live profile ("the five live strategies", `168125b`) · actor: maintainer.
- 2026-07-28 · declared · `baseline-earnings-only`, `baseline-activity-only`, `baseline-media-only` ·
  deliberately-dumb comparator arms the composite must beat (spec 154, `ad36e4d`); marked
  `Purpose: Comparator` when the purpose field landed (spec 176, `1320db5`) — comparators carry no
  operating call, ever · actor: maintainer.
- 2026-07-28 · declared · `disclosure-led-v11` (research) and `disclosure-led-v10-control` (comparator) ·
  radar-formula-v11 directional-only arms (spec 157, `e94846a`) · actor: maintainer.
- 2026-08-03 · declared · `disclosure-led-v11` predeclared as the AD-15 paired-primary composite
  (`Radar:Efficacy:Comparison:PairedPrimaryStrategy`, `10c5d39`) with the precommitted claim boundary
  (`7adb4a6`; first eligible as-of later pinned to 2026-09-29) · actor: maintainer.
- 2026-08-23 · call-made · `disclosure-led-v11` · **Lead** · basis: the prospectively declared
  AD-15/AD-16 arm under test · actor: human (maintainer-directed) · review by 2026-09-05T00:00:00Z (the
  first multi-strategy ranking — a review, not a resolution; realistic gate-resolution horizon ≈2027-02-02,
  but the rule references the GATE EVENT, not a calendar date) · resolution rule: Right if the AD-15
  composite gate PASSES for this arm; Wrong if it FAILS; Unresolved until the gate evaluates. (Interim
  reviews may re-call on descriptive evidence; doing so journals THIS call as superseded, outcome
  Unresolved.)
- 2026-08-23 · call-made · `default` · **DoNotLead** · basis: oos ρ −0.05, CI spans zero at call time;
  remains the storage primary / legacy reference series (`Radar:PrimaryStrategy` untouched) · actor: human
  (maintainer-directed) · review by 2026-09-05T00:00:00Z · resolution rule: Wrong if, at the
  gate-resolution instant, default's out-of-sample ρ interval excludes zero POSITIVELY on the then-current
  leaderboard; Right otherwise.
- 2026-08-23 · call-made · `filings-led-v2`, `filings-led-halfnoted`, `filings-led-nonoted`,
  `narrative-led-v2` · **Trial** · basis: research arms still accruing outcome evidence; no descriptive
  result distinguishes them yet · actor: human (maintainer-directed) · review by 2026-09-05T00:00:00Z ·
  resolution rule: resolved by supersession — promoted or stopped by a later journaled call.
- 2026-08-23 · schema-migrated · `data/strategy-operating-calls.json` ·
  `strategy-operating-calls-v1` → **`strategy-operating-calls-v2`** (spec 186 §3) · a gate override now
  BINDS to the verdict it overrides by name (`overridesVerdictId` = the paired artifact's `gateVerdictId`),
  replacing the pre-186 "the call post-dates the verdict" rule whose verdict instant was the artifact's
  filesystem mtime — which the daily efficacy re-write advanced, so a valid override silently expired after
  one run. No call in the committed file carries an override, so no call's meaning changed; v1 stays
  readable (it simply cannot express an override) · actor: maintainer.
- 2026-08-29 · call-made · `filings-led-v2`, `filings-led-halfnoted`, `filings-led-nonoted` · **Stop**
  (supersedes the 2026-08-23 Trial calls, outcome Unresolved) · basis: on the 2026-08-29
  excess-vs-universe-v1 leaderboard (872 obs, 74 companies, 12 oos dates 2026-07-30..08-10) all three oos
  ρ are NEGATIVE with the 95 % interval excluding zero (−0.159 [−0.223, −0.093] / −0.149 [−0.214, −0.084] /
  −0.139 [−0.204, −0.074]); the halfnoted/nonoted ablations move nothing, so the notedness discount is not
  the cause — the sec-form4 + sec-13dg channel hypothesis is what fails. Descriptive, pooled, retrospective
  to the freeze; still the strongest negative result Radar holds, and declining to act on it would be data
  fence-sitting. Scoring continues (a Stop is prominence only; the arms render in the diagnostic appendix)
  · actor: human (maintainer-directed: "make a call on the data we have") · review by 2026-10-30 ·
  resolution rule: Wrong (re-call Trial) if a later leaderboard with ≥ 24 oos dates shows the interval upper
  bound above zero; Right otherwise.
- 2026-08-29 · declared · `default-noattn` (research, implicit Trial) · `default` profile with
  `OpportunityAttentionDiscountWeight` = `FollowingTierDiscountWeight` = 0, declared inline (spec 149) under
  a NEW name (spec 141) · basis: `baseline-media-only` — a pure media-count comparator — is the ONLY arm whose
  oos interval clearly excludes zero positively (+0.137 [0.071, 0.202]) while `default` sits at −0.026 and
  inverts attention; hypothesis: the inverse-attention discount subtracts the component that predicts.
  Alternative explanation, recorded so it is not forgotten: a size/regime effect over 12 dates that the
  universe-mean excess does not remove. Falsified if `default-noattn` oos ρ ≤ `default` oos ρ at review ·
  actor: human (maintainer-directed) · review by 2026-09-05 with the other calls.
- 2026-08-29 · finding · `disclosure-led-v11` vs `disclosure-led-v10-control` · the Lead and its formula
  control are INDISTINGUISHABLE — in-sample 0.1693 vs 0.1694, oos 0.0652 vs 0.0687 — so the v11
  breadth-rejection changed nothing measurable. Lead stays (precommitted; the gate, not this note, resolves
  it). Entry condition for a call: if the two remain within 0.01 at the 2026-09-05 review, journal v11's
  structural change as null and consider retiring the control · actor: human (maintainer-directed).
- 2026-09-07 · call-reviewed · `disclosure-led-v11` · **Lead KEPT** (review-by 2026-09-05 passed; reviewed
  2026-09-07 against the 2026-09-06 23:58Z run's `data/efficacy/strategy-leaderboard.md` and
  `strategy-paired-comparison.md`) · evidence: descriptive oos ρ 0.0324 [−0.031, 0.095] over 962 obs
  (74 × 13 oos dates 2026-08-03..08-18), ranked #2 of 10 in-sample — no discrimination shown, none refuted;
  the gate CANNOT evaluate: eligible joint support 0 (precommitted boundary 2026-09-29 not reached), AD-16
  screen pending, no gate verdict id. Recorded because it is uncomfortable: all three deliberately-dumb
  comparators' oos intervals exclude zero POSITIVELY (media-only +0.144 [0.082, 0.206], activity-only
  +0.093 [0.030, 0.156], earnings-only +0.077 [0.013, 0.139]) while the Lead's spans zero on the same 962
  obs. Basis for keeping: the resolution rule references the GATE event; superseding a precommitted claim on
  13 pre-boundary development dates is exactly the move the claim discipline forbids, and 13 pooled dates in
  one regime are not a stronger result than the one the arm was declared on. Precommitted trigger for the
  next review, so this is a decision rather than a deferral: if at the 2026-10-06 review the leaderboard
  shows ≥ 20 oos dates AND the Lead's oos interval still spans zero AND at least two comparators' intervals
  still exclude zero positively, journal Lead → Trial (this call superseded, outcome Unresolved) whether or
  not the gate has evaluated · actor: human (maintainer-directed: "re-review the lead call") · review by
  2026-10-06T00:00:00Z (the first review after the 2026-09-29 boundary can admit an eligible joint date) ·
  resolution rule unchanged.
- 2026-09-07 · finding · `disclosure-led-v11` vs `disclosure-led-v10-control` · the 2026-08-29 entry
  condition ("within 0.01 at the 2026-09-05 review") is MET: in-sample 0.1052 vs 0.1096 (Δ 0.004), oos
  0.0324 vs 0.0353 (Δ 0.003), identical support (346 / 962). v11's breadth-rejection is journalled as
  measured NULL to date — the control edges it on both windows. Decision: the control is NOT retired. It is
  the only direct test of v11's one structural change, and removing it before the boundary would make that
  change unfalsifiable at exactly the point it becomes testable; its scoring cost is negligible. Retire
  condition: still within 0.01 at the 2026-10-06 review → journal `retired` and remove it from the live
  profile (spec 141: its accrued series is never rewritten) · actor: human (maintainer-directed).
- 2026-09-07 · call-reviewed · `default` · **DoNotLead KEPT** · oos ρ −0.0205 [−0.081, 0.041] over 1036
  obs (74 × 14), spans zero; the rule keys to the gate instant and nothing has happened · review by
  2026-10-06T00:00:00Z · actor: human (maintainer-directed).
- 2026-09-07 · call-reviewed · `narrative-led-v2` · **Trial KEPT** · oos ρ 0.0144 [−0.049, 0.078] over
  962 obs, ranked #4; nothing distinguishes it in either direction · review by 2026-10-06T00:00:00Z · actor:
  human (maintainer-directed).
- 2026-09-07 · finding · `default-noattn` · the 2026-08-29 falsification test (noattn oos ρ ≤ default oos ρ)
  is UNMEASURABLE on the leaderboard: the arm is dropped (`insufficient-in-sample-observations`, 0 in-sample
  obs) because its series starts 2026-08-29 while the chronological split's in-sample window ends
  2026-08-02 and the oos window ends 2026-08-18 (nothing later has a resolved 21-day forward window yet). It
  cannot be ranked until the split advances past 2026-08-29 — at one as-of date per run, roughly mid-October.
  No call on an unmeasured hypothesis; entry condition: the first leaderboard that ranks it. If it is still
  dropped at the 2026-10-06 review, the open question is whether a spec-139 replay of `default-noattn` over
  2026-06-30..08-18 (replay ⊆ forward, field-for-field) is an admissible comparison — a spec decision, not a
  call · actor: human (maintainer-directed).
- 2026-09-07 · finding · `filings-led-v2`, `filings-led-halfnoted`, `filings-led-nonoted` · NOT due (review
  by 2026-10-30) but recorded so it is not forgotten: the Stop basis has weakened — on 14 oos dates all three
  intervals now SPAN zero (−0.055 [−0.116, 0.006] / −0.024 [−0.085, 0.037] / −0.027 [−0.088, 0.035]) where
  they excluded it on 12. The Wrong condition (upper bound above zero at ≥ 24 oos dates) is not evaluable
  yet; v2's upper bound is 0.006. Stop stands · actor: human (maintainer-directed).
- 2026-09-07 · labels-set · `disclosure-led-v11` (Lead) · `Labels { Investigate 20, Watch 15 }` in
  `scripts/run-profiles/default.json` (spec 212). Prevalence chosen at: Watch 15 ≈ the v8 primary's ≈ 2 %
  ≥ 40 share on v11's 3,471 accrued snapshots (implementation-time re-measure by nearest rank: exactly 15
  over the spec's 2026-07-29 → window, 16 over the whole store — both within the spec's ±1 tolerance, 15
  stood as pinned; at 15 the v11 share is 3.8 %); Investigate 20 = the top
  ≈ 0.3 % (max 21), a stated workload judgement since v8 has no ≥ 60 prevalence to match. Fixed operating
  (triage) thresholds, connected to no outcome; report-layer only, no fingerprint moved. Until this change
  every `Watch` since 2026-08-23 came from the corroboration floor and `Investigate` never fired on the
  Lead · actor: maintainer (spec-pinned).
