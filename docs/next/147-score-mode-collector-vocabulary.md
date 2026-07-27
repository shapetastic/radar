# Task: `score` mode must know the collector vocabulary — and must never record false provenance

> Specs 144 and 146 are each correct and do not compose. 144 registers **zero collectors** in
> `RunMode=score` (correctly — a score pass must not be able to collect). 146 and 141 both read the
> registered collector set for things that still matter when nothing is being collected. **This is a small
> composition slice, not a redesign of either.**

## The three symptoms, one cause

`SignalSourceDescriptor` derives everything from the injected `IEnumerable<IEvidenceCollector>` — it reads
only `CollectorName` and never calls `CollectAsync`. In `score` mode that sequence is empty, so:

1. **v9 collector-channel strategies fail startup.** `ScoringStrategyFactory.ValidateChannelCollectors`
   checks each channel's collectors against `sourceDescriptor.EnabledCollectors()` and throws with
   `registered collectors: (none)`. Any `radar-formula-v9` strategy naming a collector cannot be scored
   standalone — which is exactly how spec 140 will want to run them.
2. ⛔ **Every snapshot records false provenance.** `ScoringEngine` captures
   `_collectionProvenance = sourceDescriptor.CollectionProvenance()` and stamps it onto
   `CompanyScoreSnapshot.CollectionProvenance`. In score mode that is the **empty** collector set — so a
   snapshot scoring signals that a `collect` pass genuinely gathered from seven collectors claims none ran.
   **This affects v8 strategies too**, not just v9. CLAUDE.md: *provenance is sacred* — a record that says
   "no collectors" when seven collected the data is a lie, and 141 introduced this field precisely so the
   collector set stayed truthfully recorded once it left the fingerprint.
3. **v9's ran-vs-quiet distinction inverts.** `ScoringInput.EnabledCollectors` is empty, so every channel
   reports "declared collector did not run" for collectors that demonstrably did.

Symptom 2 is the serious one and is **live today** — it does not need a v9 strategy to trigger, only
`RunMode=score`.

## Design

### 1. Separate "can collect" from "is a known collector"

A score pass must not be able to *collect*, but it still needs the collector **vocabulary** — the names, for
validation and provenance. Those are different capabilities welded into one interface today.

Introduce a name-only source of the enabled-collector set that both modes share, derived from the same
configuration a collect pass builds its collectors from, and have `SignalSourceDescriptor` consume that
instead of reaching into `IEvidenceCollector` instances. **Reuse over copy** (CLAUDE.md) — the descriptor
already treats collectors as name-only by contract (*"reads ONLY CollectorName — never CollectAsync"*), so
this makes an existing invariant structural rather than adding a parallel concept.

**Verify how `RunMode=collect|full` builds its collector set before designing**, and derive both paths from
one place so they cannot disagree. A vocabulary that drifts from the registered collectors would make
validation pass on a collector that cannot run — worse than today's failure.

### 2. Provenance must distinguish three states, not two

`CollectionProvenance` currently conflates "no collectors configured" with "no collection happened in this
pass". Those are different facts and only one of them is ever true in score mode.

**Decide explicitly and document it** — options, with a recommendation:

- **(A) Stamp the configured collector set.** Simple and makes score-mode snapshots comparable with
  full-run ones — but it slightly implies collection occurred in this pass.
- **(B) Stamp an explicit score-pass marker** distinguishable from an empty set (e.g. a
  `no-collection-this-pass` form carrying the configured vocabulary). **Recommended** — it is the only
  option that cannot be misread, and provenance's job is to be unambiguous later.
- **(C) Derive it from the collectors actually recorded on the scored signals' evidence** (146 stamps the
  producing collector). Most honest, most work, and it answers a different question — *what produced this
  data* rather than *what ran this pass*.

**Whatever is chosen, an empty `CollectionProvenance` must never be produced by a score pass**, and existing
full-run behaviour must be byte-identical.

### 3. Keep the typo guard exactly as strong

Do **not** weaken or skip `ValidateChannelCollectors` in score mode. Its value — a channel over a
non-existent collector scores 0 forever and silently costs its whole share — is *highest* in score mode,
which is where strategies will actually be iterated on. With §1 it validates against the same vocabulary in
every mode.

### 4. Ran-vs-quiet in a score pass

With the vocabulary restored, `ScoringInput.EnabledCollectors` is correct again and 146's distinction works.
**State plainly in the hand-back what "did not run" means in a score pass**, since no collection happened in
it — if the honest answer is that it degenerates to "this window holds no signals from that collector",
document that rather than letting it read as an outage signal.

## Files (verify against the tree before planning)

`SignalSourceDescriptor` / `ISignalSourceDescriptor`, `ScoringStrategyFactory.ValidateChannelCollectors`,
`ScoringEngine` (provenance + `EnabledCollectors` capture), `RadarWorkerServices` / the `RunMode` wiring from
144, `InfrastructureServiceCollectionExtensions`, and the collector-registration path.

## Constraints

- **No fingerprint move.** After 141 the collector set is provenance, not identity — so this must be
  provable, not assumed. Pins `2ce20f8fc497` / `3457da53489d` hold.
- **`RunMode=full` and `collect` are byte-identical to today**, including the provenance string.
- **A score pass still constructs and invokes no collector and performs no AI read** — 144's asserted
  guarantee must survive. Adding a name-only vocabulary must not smuggle a fetch capability back in.
- **Provenance is sacred**: no snapshot may record a collector set that misrepresents what produced its data.
- **Layering:** no `IConfiguration` in `Radar.Application`.

## Out of scope (record, do not build)

- **Strategy-vs-price comparison** — spec 140, which this unblocks.
- **`ScoringWeights.TrajectoryCorroborationK` not being a fingerprint field** — real, pre-existing, and it
  moves both pins; its own spec.
- **Backfilling `CollectionProvenance` on existing snapshots.** Append-only (AD-8); fix forward.

## Acceptance criteria

- [ ] A `radar-formula-v9` collector-channel strategy starts and scores under `RunMode=score`.
- [ ] `ValidateChannelCollectors` still rejects an unknown/mis-cased collector name in **every** mode —
      asserted in score mode specifically.
- [ ] A score-pass snapshot never records an empty `CollectionProvenance`; the chosen representation is
      documented and distinguishable from "no collectors configured".
- [ ] `RunMode=full`/`collect` output is byte-identical to today, provenance string included.
- [ ] A score pass still constructs no collector and performs no AI read — existing assertions still pass.
- [ ] Pins `2ce20f8fc497` / `3457da53489d` unmoved.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.
