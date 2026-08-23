# Strategy lifecycle journal

Append-only, audit-only (spec 184 §3). **The Worker never parses this file** — the only runtime input to
call resolution is `data/strategy-operating-calls.json`. One line per event
(declared / retuned-as-new-name / retired / call-made / call-overridden / call-resolved), with strategy,
date, basis, actor, and — for resolutions — the outcome and an evidence reference. Retirement is config
removal plus a line here; changing `Radar:PrimaryStrategy` (storage/series identity only) likewise. No
automation for either.

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
