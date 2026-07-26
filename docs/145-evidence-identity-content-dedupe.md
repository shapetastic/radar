# Task: Evidence identity and content-level dedupe — close the ~9.2× duplication before it can be scored

> ⛔ **This is a latent correctness landmine, and spec 142 armed it.** Measured on the live store: the 49,454
> accrued signals collapse under the spec-85 dedupe key to **49,454 — a 1.00× no-op, meaning that key has
> been near-vacuous all along** — but collapse by *content* to **5,368, i.e. ~9.2× real duplication**.
>
> Radar is protected from ~9× score inflation today only by an **accident**: `CollectedEvidenceMapper` minted
> a fresh evidence `Guid` every run while `FileRawEvidenceStore` keyed files on `contentHash`, so the
> duplicate evidence ids were never persisted and `ScoringEngine` silently dropped their signals (~9,500
> provenance warnings per run per strategy). **142 made the evidence repo durable and heals id resolution
> forward.** As resolution improves, those previously-dropped duplicate signals become scoreable — and the
> inflation lands in the direction that flatters every score.

## Why the spec-85 key cannot see this

The dedupe key includes **evidence identity**. Duplication lives *in* evidence identity — the same article,
filing or release collected on N runs (or by N collectors) becomes N distinct evidence records with N
distinct ids and therefore N distinct, non-colliding signal keys. A key built on identity can never dedupe
identity. That is why the measured collapse ratio is exactly 1.00×: the key is doing nothing across runs.

**Verify all of this yourself before designing.** Re-run the collapse measurement against the current store
and report both ratios (key-collapse and content-collapse) with counts. If the numbers have moved since 142,
the design follows the new numbers, not this spec's.

## Design

### 1. Establish stable, content-derived evidence identity

Evidence for the same content must resolve to the **same** evidence record across runs and across collectors.
`FileRawEvidenceStore` already keys files on `contentHash`, so the durable store has effectively been doing
this while the in-memory mint did not — **reconcile the two onto one rule** rather than adding a third
(CLAUDE.md: reuse over copy).

- Decide and document what "same content" means, and be explicit about what is deliberately excluded from
  the hash (retrieval timestamp, run id, minted ids, volatile URL query parameters, tracking tokens).
- **Same content from two different collectors is a real question, not an oversight** — decide whether that
  is one evidence record with two sources or two records, state the choice and its consequence for the
  attention/breadth components, which count distinct publishers.

### 2. Make the dedupe key see content

Once identity is content-derived, the spec-85 key collapses duplicates as it was always meant to. **Prove the
ratio moves**: a test asserting that N runs over identical source content yield one scored signal, not N.

### 3. Do not let the fix inflate scores

The whole point. Before and after this slice, scoring the **same real window** must not increase any
company's score. Assert it:

- A regression test over a fixture reproducing the duplication shape: dedupe collapses it, and the resulting
  score equals the score from the single-copy fixture.
- Report measured before/after scores for the live 30-day window (2,628 signals, 44 companies, currently
  1.031× content-distinct). **If any score rises, stop and report — do not rationalise it.**

### 4. Decide the fate of accrued history explicitly

The 49,454 accrued signals contain ~9.2× duplication that was never scored. Options — **pick one, state why,
and do not quietly do a fourth thing**:

- Leave history as-is and dedupe forward only (safest; historical series stay as they were actually scored).
- Mark superseded duplicates without deleting them (append-only respected, AD-8).
- Rebuild a deduped view for replay only, leaving the live series untouched.

**Do not delete accrued evidence or signals.** AD-8 is append-only and this data is not reproducible.

### 5. Fix the provenance-warning flood, or say why not

~9,500 warnings per run per strategy for dropped signals is real signal about this defect, not noise. If
dedupe removes the cause, show the count drops. If a residue remains, aggregate per company rather than
silencing per signal.

## Files (verify against the tree before planning)

`CollectedEvidenceMapper`, `FileRawEvidenceStore` (+ the `IEvidenceRepository` implementation added by 142),
`FileSignalStore`, the spec-85/113 dedupe key, `ScoringEngine`'s evidence-resolution/drop path, and the
attention/breadth components if §1 changes distinct-publisher counting.

## Constraints

- **Scores must not rise.** Any increase is a bug in this slice, not a benefit of it.
- **Append-only (AD-8).** Nothing is deleted or rewritten in place.
- **Provenance is sacred.** A deduped evidence record must retain every source attribution that contributed
  to it — collapsing duplicates must not collapse *provenance*.
- **No fingerprint move** unless the design genuinely requires one; if it does, say so explicitly rather than
  smuggling it (see spec 141 for how that pin discipline is being amended).
- **Layering:** persistence in `Radar.Infrastructure`; the dedupe rule where spec 85's key already lives.

## Out of scope (record, do not build)

- **Strategy identity / the fingerprint split** — spec 141. This slice is evidence identity; that one is
  strategy identity. They are different axes and must not be merged.
- **Backfilling missing evidence for the 89.5% of signals with no resolvable evidence.** Healing forward is
  this slice; reconstructing the past is not, and synthesising it would be a lie.
- **Per-strategy collector selection** (143) / **split passes** (144) / **strategy-vs-price** (140).

## Acceptance criteria

- [ ] Evidence identity is content-derived and stable across runs and collectors; the rule and its exclusions
      are documented.
- [ ] N runs over identical source content yield **one** scored signal, not N — asserted.
- [ ] Scoring the same real window produces **no score increase** versus pre-slice; before/after numbers for
      the live 30-day window are reported in the hand-back.
- [ ] The key-collapse and content-collapse ratios are re-measured and reported.
- [ ] The chosen treatment of accrued history is stated with its rationale; nothing is deleted.
- [ ] Provenance retained per contributing source on collapsed records.
- [ ] The dropped-signal warning volume is shown to fall, or the residue is explained and aggregated.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.
