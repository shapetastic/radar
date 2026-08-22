# Task: Theme Radar — versioned declarations and point-in-time topic capture

## Overview

Radar currently begins with a watched company and asks what has happened around that company. That is useful,
but often late: a filing, press release or earnings print normally appears after customers, employees,
suppliers or regulators have already begun behaving differently.

Theme Radar is a **standalone side capability**: a separate read side that measures externally observed
trends and maps declared company exposure to them, entirely outside the company evidence → signal → score
path and entirely outside the daily baseline run.

```text
external topic evidence → measured theme → versioned company exposure → later operating/price outcome
```

The motivating development example is `tattoo-removal-demand`: independent stories about tattoo regret may
precede increased searches, bookings and equipment demand, which may eventually benefit removal-service or
laser-equipment businesses. The example also shows why a headline count is not a signal by itself. One viral
story can be syndicated by 40 publishers; regret is not the same as intent to pay for removal; and even a
real demand trend may be immaterial to a diversified public company.

Theme Radar keeps two propositions separate:

1. **Theme evidence:** is a real-world behaviour or demand pattern changing?
2. **Company exposure:** does a named company benefit or suffer through a cited, material mechanism?

**This spec is the first of two.** It delivers the versioned declarations (themes and exposures) and the
point-in-time topic capture that everything downstream requires. Spec 180 delivers the semantic
classification, descriptive characterization, standalone report and exploratory evaluation over what this
spec accrues. Open-ended theme/company discovery queues are deliberately in **neither** spec — they are a
future spec of their own, if ever. Splitting this way means capture starts accruing observations while the
model/report half is still being built; a theme archive is only as good as its accrual cadence.

## Assignment

Worktree: any
Dependencies: spec 177 merged (point-in-time observation payload schema, safe optional publisher-content
reader, hydrated-index insert-only store conventions). Spec 176 is **not** a dependency — Theme Radar
consumes no strategy rows. Specs 179/180 are downstream, not prerequisites.
Estimated time: ~1.5–2 days.

## 1. Inside Radar, outside company scoring, outside the baseline run

Theme Radar shares Radar's provenance, timestamping and file-store primitives and lives in this repository.
It must not enter the existing evidence → signal → company-score path, and it must not run inside — or be
able to delay, fail or relabel — the baseline company run.

In particular:

- no `Signal` is minted from a theme observation;
- no `EvidenceItem` is attributed to a company merely because that company has a matching tag;
- no existing formula, `KeywordSignalExtractor.RuleSetVersion`, `ScoringConfigVersion`, strategy identity,
  snapshot or efficacy precommitment moves;
- no theme result changes a Radar label or current live rank;
- price and later earnings remain validation/reference outcomes, never theme inputs; and
- the baseline `full`/`collect`/`score`/`replay` runs are byte-identical in behaviour and output — the theme
  stage is not registered in any of them.

Write outputs beneath `data/theme-radar/`, not `data/evidence`, `data/signals` or `data/scores`.

## 2. Execution model: its own run mode, its own schedule

Theme Radar runs as a new `Radar:RunMode` token, **`theme`**, following the spec-144 mode pattern exactly:

- `theme` mode registers ONLY the theme components — no company collector, no scoring, no AI seam (this
  slice makes no model call), no report stage. `Assert.Empty(GetServices<IEvidenceCollector>())` holds in
  theme mode as it does in score mode.
- Every other mode registers NO theme component, so the baseline graph is unchanged by construction — a
  stronger guarantee than an `Enabled` flag, and asserted the same way spec 144 asserts its mode splits.
- Mode conflicts fail fast through the existing `RadarRunModes.Resolve` machinery (`theme` +
  `Replay:Enabled=true` fails naming both keys, mirroring the collect/score guards).
- `StrategyIdentityGuard` is NOT required first in theme mode: no scoring occurs and no snapshot can land.
  Record that exemption in the mode's doc comment so it reads as decided, not missed.

Scripts:

- `run-radar.ps1 -Mode theme` works like the existing mode passthrough and supplies
  `Radar:ThemeResearch:*Directory` beneath its output root.
- A `run-theme-radar.ps1` convenience wrapper is optional; if written, it must delegate to `run-radar.ps1`
  rather than duplicating its config plumbing.
- **Cadence is the one real cost of being a side capability**: the 7-/28-day windows spec 180 characterizes
  only mean something if observations accrue regularly, and the coverage rules will (correctly) report
  sporadic capture as incomplete forever. Registering a separate scheduled task (`RadarThemeDaily -Mode
  theme`, following the `setup-baseline-task.ps1` precedent) is the maintainer-elevated step; document the
  exact commands in the script header as spec 144 did for the collect/score split. Do not touch
  `RadarBaselineDaily`.

`default.json` gains NO theme entry and enables nothing: the baseline profile records how the baseline runs,
and Theme Radar is not part of it. Theme configuration lives in its own committed inputs (§3, §4) plus the
`Radar:ThemeResearch` block (§6), read only in theme mode.

## 3. Existing `Company.Themes` are tags, not exposure

`Company.Themes` already exists and `data/companies.json` contains values such as `grid-scale storage`,
`medical devices` and `consumer staples`. These strings are useful retrieval hints, but they have no:

- stable theme identity/version;
- positive/adverse direction;
- mechanism;
- materiality;
- valid-from date;
- supporting source; or
- historical change record.

Do not reinterpret them automatically as bullish exposures. A company tagged `medical devices` is not thereby
a beneficiary of tattoo removal, and a company tagged `consumer staples` is not exposed to every consumer
trend.

The tags may seed a **candidate** exposure search, but only the explicit §5 record can place a company in the
mapped-exposure table.

## 4. Versioned theme declarations — discovery is not validation

Add a committed `data/theme-definitions.json` with a fail-closed loader. Each definition contains at least:

```text
themeId                       stable kebab-case id
definitionVersion             positive integer
name
hypothesis                    one falsifiable causal sentence
geography                     explicit region(s), or Global
language
queryPhrases[]                exact provider queries
exclusionTerms[]
behaviourOfInterest           what action would represent real demand/change
beneficiaryMechanism          how a business could benefit
adverseMechanism?             how a business could be harmed
status                        Development | Prospective
declaredAtUtc
firstProspectiveAsOfUtc?
```

Unknown keys, duplicate `(themeId, definitionVersion)`, blank queries, invalid timestamps and an unexplained
version gap fail startup with the exact path and remedy. Query/exclusion order is preserved and contributes
to a canonical definition hash written on every observation and run manifest.

Definitions are immutable by convention. Changing a query, geography, mechanism, exclusion term or (in spec
180) classifier instruction requires `definitionVersion + 1`; old observations remain attached to the old
hash. Renaming the display name alone may remain within a version only if it is excluded from the canonical
measurement identity and recorded as display metadata.

### Development versus prospective

- A theme suggested after seeing an interesting story begins as `Development`. The evidence that suggested it
  may be displayed, but cannot validate that theme.
- Promotion to `Prospective` writes `firstProspectiveAsOfUtc` before any later company outcome is available.
- An automatically proposed theme is never promoted by the proposing model in the same run.
- `tattoo-removal-demand` is explicitly a development example already known before this spec. Its historical
  stories are development data; only post-declaration observations can test whether it persists or leads.

This is lighter than an AD-15 precommitment: no success threshold is declared yet. It is strong enough to
stop the system inventing a theme and immediately reporting the same articles as proof that it found one.

Ship `tattoo-removal-demand` as the one worked Development definition, with a small literal query set and a
clear `behaviourOfInterest` distinguishing regret discussion from an action such as researching, booking or
receiving removal. Do not invent four more themes simply to make a larger demo; additional themes require an
argued hypothesis and versioned declaration.

## 5. Company exposure is a separate, cited and time-versioned claim

Add a committed `data/theme-exposures.json`. One `ThemeExposureDefinition` contains:

```text
themeId / themeDefinitionVersion
exposureVersion
companyId? / ticker / legalName
universeStatus                 Watched | OutsideUniverse
direction                      Beneficiary | AdverselyExposed
mechanism                      DirectRevenue | Enabler | InputCost | Displacement | Regulatory
materiality                    Minor | Meaningful | Core | Unknown
geography
rationale
primarySourceUrls[]
validFromUtc
declaredAtUtc
status                         Proposed | Declared
```

Rules:

- `universeStatus=Watched` requires a `companyId` that resolves against `data/companies.json`;
  `universeStatus=OutsideUniverse` must carry NO `companyId`. The loader validates both directions and fails
  startup on a mismatch — an exposure claiming to be watched while naming no seeded company is exactly the
  ambiguity this schema exists to prevent.
- Every `Declared` mapping has at least one primary company/regulatory source supporting the product,
  service or segment connection. An article saying a stock is a "tattoo-removal play" is not sufficient.
- `materiality=Unknown` remains visible but is excluded from the investable-exposure table; unknown does not
  silently become a weak positive.
- `validFromUtc` is the earliest time the mapping is allowed to join a theme snapshot. A mapping added today
  cannot be projected backward over old theme observations.
- Direction and materiality are never inferred from `Company.Themes`, sector, price performance or a model's
  general knowledge.
- A mapping edit increments `exposureVersion`; old snapshots retain the old record/hash.
- Outside-universe issuers may appear in a research queue, but are never added to `companies.json`,
  collected, scored or ranked automatically. Adding one remains a separately reviewed universe change.
- A model may (in a future discovery spec) propose mappings, stored as `Proposed` with citations; a
  `Proposed` mapping can never participate as `Declared` in any claim. No human review is required for a
  theme run to complete: an unreviewed mapping stays proposed/diagnostic rather than failing open into a
  company candidate.

It is valid — and important — for a theme report to conclude:

> The theme may be real, but no material public-company exposure is established in Radar's current universe.

## 6. Collect topic evidence without pretending it is company evidence

Add a `themesearch` read side that reuses spec 177's provider reader (`HttpNewsSearchReader.ReadAsync` is
already query-driven), its point-in-time payload schema, its safe URL policy and its hydrated-index
insert-only store mechanism (spec 177 §4). It is driven by `ThemeDefinition.QueryPhrases`, not
`CompanySourceFeed`, and it does not apply `NewsAttentionCollector`'s company-title relevance filter.

Persist under:

```text
data/theme-radar/
  observations/{themeId}/v{definitionVersion}/{yyyy}/{MM}/{observationId}.json
  runs/{asOfUtc-file-token}.json
```

(`snapshots/`, `assessments/` and `reports/` are spec 180's paths.)

Every observation records:

- theme id/version/definition hash and the query phrases that matched **in the writing run**;
- provider, landing URL, publisher and publisher-site URL when supplied;
- exact bounded headline/RSS description (and, if ever enabled, permitted publisher body via spec 177's
  reader);
- publication, retrieval and first-observed instants;
- payload/body hash and capture mode;
- collection/fetch outcome;
- exclusion marking (below); and
- the run id that proves coverage.

Rules:

- **Cross-partition and cross-run dedupe follow spec 177 §4 verbatim**: deterministic
  `observationId` from normalized landing URL + payload hash, path partition from the immutable
  `firstObservedAtUtc`, lazy hydrated id index consulted before every write, `TryAdd` +
  `FileMode.CreateNew`, earliest first-observed instant preserved.
- **Cross-run query associations respect insert-only.** The observation file carries the query phrases that
  matched when it was FIRST written and is never edited. When a later run matches an existing observation
  under a query phrase not in that record, the association is recorded in that run's manifest
  (`observationId → queryPhrase` pairs), and downstream readers derive the full association set as the
  record's phrases ∪ all manifest associations. An association is data about a run's retrieval, so the run
  manifest is its honest home.
- **Exclusion terms mark; they do not erase.** Every relevance-surviving provider item is archived — it was
  observed, and a definition v+1 must be able to reinterpret it. An item matching an `exclusionTerms` entry
  (deterministic, ordinal case-insensitive containment over headline + description text; the rule is part of
  the definition hash) is stored with the matching term recorded, counted per query in the run manifest, and
  excluded from spec 180's classification input. "Archived but excluded" and "not returned" are different
  facts and stay different.
- The same article may support multiple themes: one observation per (theme, version) path — each association
  is explicit and will be separately assessed. Cross-theme storage is deliberately not shared; themes are
  independent measurement contexts and their definition hashes differ.

Coverage fails closed. Per theme/query/run, the run manifest records request success, raw item count,
retained item count, exclusion-marked count, provider cap contact, archive failures and
malformed/unreachable/rate-limited outcomes, plus the effective config values and definition/exposure file
hashes. A capped or failed query cannot be interpreted as a quiet theme. A theme's first successful complete
capture run writes its per-(theme, version) prospective boundary marker (create-once, mirroring spec 177's
`boundary.json`).

Do not add unofficial search scraping in this slice. Search-intent, bookings, card spending, social posts,
jobs and equipment orders are desirable future provider types, but each needs its own lawful, stable access
characterization and point-in-time schema. The observation model is deliberately provider-neutral so those
sources can later join without being disguised as news articles.

## 7. Configuration and shipped posture

Add a fail-closed `Radar:ThemeResearch` block, read only in theme mode:

```json
{
  "DefinitionsFile": "data/theme-definitions.json",
  "ExposuresFile": "data/theme-exposures.json",
  "OutputDirectory": "data/theme-radar",
  "MaxThemesPerRun": 5,
  "MaxQueriesPerTheme": 4,
  "MaxArticlesPerQuery": 50,
  "InterRequestDelaySeconds": 1
}
```

Unknown keys and invalid bounds fail startup with the exact path (the spec-174 `ConfigSectionGuards`
pattern). `run-radar.ps1` supplies the output directory beneath its existing output root; the committed
definitions/exposures remain repository-relative inputs. There is no `Enabled` flag — the run mode is the
gate. `run-radar.ps1 -Mode theme -WhatIf` prints the resolved directories, theme/exposure counts and both
file hashes.

These controls are observational/cost inputs, not scoring weights, and are hashed into no scoring
fingerprint.

## Files to inspect

- `src/Radar.Domain/Companies/Company.cs`
- `src/Radar.Infrastructure/Sources/LocalFileCompanySeedSource.cs`
- `src/Radar.Infrastructure/News/HttpNewsSearchReader.cs`
- `src/Radar.Infrastructure/News/NewsArticleItem.cs`
- spec-177 observation/content-reader/store primitives
- `src/Radar.Worker/RadarWorkerOptions.cs`
- `src/Radar.Worker/RadarWorkerServices.cs` (RunMode registration pattern)
- `src/Radar.Worker/WorkerRunOptions.cs`
- `scripts/run-radar.ps1`
- `scripts/setup-baseline-task.ps1` (scheduled-task precedent)
- `data/companies.json`

## Tests

### Mode and posture

- `theme` mode registers no collector, no scoring, no report stage; every non-theme mode registers no theme
  component; the baseline graphs are unchanged (mirror the spec-144 mode assertions).
- Mode conflicts (`theme` + replay) fail fast naming both keys.
- No type in `Radar.Application.Scoring` or the evidence/signal pipeline references any theme type
  (architecture guard, mirroring spec 177's).

### Definitions and exposures

- Unknown/malformed/duplicate/version-gap theme entries fail with exact paths; hashes are deterministic and
  query/exclusion order-sensitive.
- `Company.Themes` alone cannot create a declared exposure.
- Watched-without-companyId and OutsideUniverse-with-companyId both fail the loader.
- Proposed, Unknown-materiality and future-valid mappings stay out of the declared current-exposure set.
- An exposure edit creates a new version/hash; prior records are retained.
- Outside-universe candidates never mutate `companies.json`.

### Capture

- Theme queries reuse the point-in-time capture path without company-title filtering and mint no company
  evidence or signal.
- Same URL+payload across queries and across runs stores once (hydrated index, across partitions), retaining
  first-writer query phrases plus manifest associations for later runs.
- Exclusion-marked items are archived, counted and distinguishable from unreturned items.
- Failed/capped/archive-incomplete queries cannot become a quiet zero; the per-(theme, version) boundary
  marker is create-once.
- Changed payload creates a later observation; concurrent writes never overwrite.

Do not run tests concurrently with another agent's solution-wide test run. At handoff,
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass in one coordinated
session.

## Acceptance criteria

- [ ] Theme Radar is a standalone `theme` run mode with its own schedule; no baseline mode registers or runs
      any theme component, and the baseline output is byte-identical.
- [ ] Radar can collect immutable, point-in-time evidence about a declared external theme without attributing
      it to a company or changing any company score.
- [ ] Theme and exposure declarations are versioned, fail-closed, hash-stamped and immutable by convention;
      existing `Company.Themes` remain hints only.
- [ ] Observation identity dedupes across partitions and runs through the spec-177 hydrated-index mechanism;
      cross-run query associations accrue append-only.
- [ ] Exclusion terms mark and count; they never silently erase an observed item.
- [ ] Capture/coverage failures are durable and cannot read as a quiet theme.
- [ ] No AI call, no price read, no report, no snapshot: those are spec 180.
- [ ] Build and coordinated tests green.
