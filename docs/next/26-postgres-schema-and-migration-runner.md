# Task: PostgreSQL schema, connection factory, and migration runner

## Overview

Persistence today is in-memory only (`Radar.Infrastructure.Persistence.InMemory.*`). This slice lays the
**foundation** for a real PostgreSQL store behind the existing Application repository interfaces: the
relational schema (DDL), a connection factory, the Dapper configuration, and an idempotent migration
runner that applies the schema at startup. **No repository implementations land in this slice** — they
arrive in specs 27 and 28 against this foundation.

The schema honours the settled architecture decisions:

- **AD-1** — `evidence_items` carries a `UNIQUE (content_hash)` so insert-only/immutable dedupe is
  enforced *at the database*. The other aggregates are keyed by `id` so the later repositories can
  upsert-by-Id (last-write-wins).
- **AD-3** — every table carries the column(s) the deterministic ordering sorts on (evidence
  `collected_at_utc`, companies/aliases `created_at_utc`, signals `observed_at_utc`, snapshots
  `created_at_utc`, links `id`, report items `rank`), each with `id` as the tiebreaker.
- All timestamps are `timestamptz` (UTC). All ids are `uuid`.

Npgsql and Dapper are concrete drivers and live **only** in `Radar.Infrastructure` (AD-5). The single new
Application-layer addition is a tiny provider-agnostic `IPersistenceInitializer` seam so the host can ask
"prepare the store" without knowing about Postgres.

---

## Assignment

Worktree: pending
Dependencies: None (foundation slice; specs 27/28/29/30/31 build on it)
Conflicts with: None for the new files. Touches `src/Radar.Infrastructure/Radar.Infrastructure.csproj`
(adds packages) and adds one Application interface file — sequence before 27/28/29.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Abstractions/Persistence/
  IPersistenceInitializer.cs            # NEW: provider-agnostic "prepare the store" seam

src/Radar.Infrastructure/
  Radar.Infrastructure.csproj           # MODIFIED: add Npgsql + Dapper package references
  Persistence/Postgres/
    NpgsqlConnectionFactory.cs          # NEW: opens NpgsqlConnection from a shared NpgsqlDataSource
    PostgresDapperConfiguration.cs      # NEW: one-time Dapper setup (underscore mapping + enum handlers)
    PostgresSchemaInitializer.cs        # NEW: IPersistenceInitializer; idempotent migration runner
    Migrations/
      001_initial_schema.sql            # NEW (embedded resource): the 8 MVP tables + indexes
```

Namespace: `Radar.Infrastructure.Persistence.Postgres`.

---

## Implementation details

### `IPersistenceInitializer` (Application)

```csharp
namespace Radar.Application.Abstractions.Persistence;

/// <summary>
/// Provider-agnostic startup hook: prepare the backing store before the pipeline runs
/// (e.g. apply schema migrations). The in-memory store registers a no-op; the relational
/// store applies its schema. Concrete database concerns stay in Infrastructure (AD-5).
/// </summary>
public interface IPersistenceInitializer
{
    Task InitializeAsync(CancellationToken ct);
}
```

### Packages (`Radar.Infrastructure.csproj`)

Add the concrete drivers (latest stable for `net10.0`):

```xml
<PackageReference Include="Npgsql" Version="10.0.0" />
<PackageReference Include="Dapper" Version="2.1.66" />
```

(If a `net10.0`-compatible Npgsql 10.x is not yet published at implementation time, use the latest 9.x
that restores cleanly under `net10.0` — do not pin to a version that fails restore.)

### `NpgsqlConnectionFactory`

A small internal factory built from a singleton `NpgsqlDataSource` (the data source itself is registered
by `AddPostgresRadarPersistence` in spec 29; this slice just defines the factory type and its contract):

```csharp
internal sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource)
{
    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
        => dataSource.OpenConnectionAsync(ct);
}
```

All repository methods (specs 27/28) and the schema initializer open connections through this factory and
pass `ct` into every Npgsql/Dapper call (AD-2 — the real implementations DO observe the token).

### `PostgresDapperConfiguration`

A static, idempotent one-time setup invoked by both the schema initializer and the integration-test
fixtures, so record-by-constructor mapping works and enums round-trip as their **names** (text):

- `Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;` so `content_hash` maps to the `ContentHash`
  constructor parameter, etc.
- Register a small `EnumStringTypeHandler<T> : SqlMapper.TypeHandler<T>` (parse/format `T.ToString()`)
  for every domain enum stored as text: `EvidenceSourceType`, `EvidenceQuality`, `CompanyStatus`,
  `SignalType`, `SignalDirection`, `SignalReviewStatus`, `RadarReportAction`. (Nullable handling: store
  `null` for absent values; the columns that hold enums are all NOT NULL in this schema.)
- Guard with a `static bool _configured` + lock so it runs exactly once per process.

> Enums are stored as **text (enum name)**, not ints — it keeps the database inspectable and replayable
> and is forward-compatible if enum members are reordered. The `*Json` columns are stored as **`text`**
> (not `jsonb`) to preserve the exact serialized bytes the pipeline produced (determinism/provenance) —
> do not let Postgres reformat them.

### `001_initial_schema.sql` (embedded resource)

Mark as `<EmbeddedResource>` and load via `Assembly.GetManifestResourceStream`. Snake_case table/column
names per the schema spec. Columns map 1:1 to the domain records (`string?` → nullable, `DateTimeOffset` →
`timestamptz NOT NULL`, `DateTimeOffset?` → `timestamptz NULL`, `Guid` → `uuid`, `decimal` → `numeric`,
`int` → `integer`, enum → `text NOT NULL`):

```sql
CREATE TABLE IF NOT EXISTS companies (
    id            uuid PRIMARY KEY,
    name          text NOT NULL,
    legal_name    text NULL,
    ticker        text NULL,
    exchange      text NULL,
    country_code  text NULL,
    sector        text NULL,
    industry      text NULL,
    status        text NOT NULL,
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS company_aliases (
    id            uuid PRIMARY KEY,
    company_id    uuid NOT NULL REFERENCES companies(id),
    alias         text NOT NULL,
    alias_type    text NOT NULL,
    created_at_utc timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_company_aliases_alias ON company_aliases(alias);

CREATE TABLE IF NOT EXISTS evidence_items (
    id             uuid PRIMARY KEY,
    source_type    text NOT NULL,
    source_name    text NOT NULL,
    source_url     text NULL,
    title          text NOT NULL,
    summary        text NULL,
    raw_text       text NOT NULL,
    content_hash   text NOT NULL,
    published_at_utc timestamptz NULL,
    collected_at_utc timestamptz NOT NULL,
    quality        text NOT NULL,
    metadata_json  text NULL,
    CONSTRAINT ux_evidence_items_content_hash UNIQUE (content_hash)
);
CREATE INDEX IF NOT EXISTS ix_evidence_items_published_at ON evidence_items(published_at_utc);

CREATE TABLE IF NOT EXISTS signals (
    id              uuid PRIMARY KEY,
    evidence_id     uuid NOT NULL REFERENCES evidence_items(id),
    company_id      uuid NULL REFERENCES companies(id),
    company_mention text NOT NULL,
    type            text NOT NULL,
    direction       text NOT NULL,
    strength        integer NOT NULL,
    novelty         integer NOT NULL,
    confidence      numeric NOT NULL,
    supporting_excerpt text NOT NULL,
    reason          text NOT NULL,
    review_status   text NOT NULL,
    observed_at_utc timestamptz NOT NULL,
    created_at_utc  timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_signals_company_observed ON signals(company_id, observed_at_utc);
CREATE INDEX IF NOT EXISTS ix_signals_type_observed ON signals(type, observed_at_utc);

CREATE TABLE IF NOT EXISTS company_score_snapshots (
    id              uuid PRIMARY KEY,
    company_id      uuid NOT NULL REFERENCES companies(id),
    scoring_version text NOT NULL,
    trajectory_score integer NOT NULL,
    opportunity_score integer NOT NULL,
    attention_score  integer NOT NULL,
    evidence_confidence_score integer NOT NULL,
    signal_velocity_score integer NOT NULL,
    explanation     text NOT NULL,
    component_json  text NOT NULL,
    window_start_utc timestamptz NOT NULL,
    window_end_utc  timestamptz NOT NULL,
    created_at_utc  timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_score_snapshots_company_created
    ON company_score_snapshots(company_id, created_at_utc);

CREATE TABLE IF NOT EXISTS score_evidence_links (
    id               uuid PRIMARY KEY,
    score_snapshot_id uuid NOT NULL REFERENCES company_score_snapshots(id),
    signal_id        uuid NOT NULL REFERENCES signals(id),
    evidence_id      uuid NOT NULL REFERENCES evidence_items(id),
    contribution_reason text NOT NULL,
    contribution_weight integer NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_score_evidence_links_snapshot
    ON score_evidence_links(score_snapshot_id);

CREATE TABLE IF NOT EXISTS radar_reports (
    id              uuid PRIMARY KEY,
    report_type     text NOT NULL,
    title           text NOT NULL,
    period_start_utc timestamptz NOT NULL,
    period_end_utc  timestamptz NOT NULL,
    markdown_content text NOT NULL,
    created_at_utc  timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS radar_report_items (
    id              uuid PRIMARY KEY,
    report_id       uuid NOT NULL REFERENCES radar_reports(id),
    company_id      uuid NOT NULL REFERENCES companies(id),
    score_snapshot_id uuid NOT NULL REFERENCES company_score_snapshots(id),
    suggested_action text NOT NULL,
    summary         text NOT NULL,
    rank            integer NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_radar_report_items_report ON radar_report_items(report_id);
```

> `signal_reviews` and `evidence_mentions` are intentionally **not** created here — `signal_reviews`
> arrives with its repository in spec 31 (migration `002`); `evidence_mentions` has no repository in the
> MVP and is out of scope.

### `PostgresSchemaInitializer` (the migration runner)

`IPersistenceInitializer` implementation. Lightweight, ordered, tracked, idempotent:

1. Open a connection via `NpgsqlConnectionFactory`.
2. `CREATE TABLE IF NOT EXISTS schema_migrations (migration_id text PRIMARY KEY, applied_at_utc timestamptz NOT NULL);`
3. Enumerate embedded `Migrations/*.sql` resources **sorted by name** (`001_…`, then `002_…` from spec 31).
4. For each not already present in `schema_migrations`, run the script and insert its row **inside one
   transaction** (so a partial migration rolls back), passing `ct` to every command.
5. Calling `InitializeAsync` repeatedly is safe (already-applied migrations are skipped; the DDL is
   `IF NOT EXISTS` as a belt-and-braces).

Call `PostgresDapperConfiguration.EnsureConfigured()` at the top of `InitializeAsync` so a host that runs
the initializer before any repository use is fully configured.

---

## Tests

No live-database tests in this slice (those land, gated, in spec 30). This slice must:

- **Compile and restore** under `net10.0` with the new packages.
- Keep `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release --no-build` green
  (existing in-memory tests untouched).

Optionally add a fast unit test (no database) asserting the embedded `001_initial_schema.sql` resource is
present and non-empty and that `PostgresDapperConfiguration.EnsureConfigured()` is idempotent.

---

## Constraints

- Target .NET 10. Npgsql/Dapper appear **only** in `Radar.Infrastructure` (AD-5) — never in
  `Radar.Application` or `Radar.Domain`. The only Application addition is `IPersistenceInitializer`.
- Preserve provenance: schema keeps every FK (`signals.evidence_id`, `score_evidence_links.*`,
  `radar_report_items.score_snapshot_id`) so evidence → signal → score → report stays traceable.
- `UNIQUE (content_hash)` on evidence is mandatory (AD-1). All timestamps `timestamptz`; all ids `uuid`.
- Keep changes scoped: define the foundation only — do not implement repositories here.
- Do not modify the in-memory repositories, their tests, or DI registration in this slice.

---

## Acceptance criteria

- [ ] `IPersistenceInitializer` exists in `Radar.Application.Abstractions.Persistence`.
- [ ] `Radar.Infrastructure` references Npgsql and Dapper; no other project does.
- [ ] `001_initial_schema.sql` is an embedded resource defining all 8 MVP tables with the listed indexes
      and `UNIQUE (content_hash)`; every timestamp is `timestamptz`, every id `uuid`.
- [ ] `PostgresDapperConfiguration` enables underscore name matching and registers enum-as-text handlers,
      idempotently.
- [ ] `PostgresSchemaInitializer : IPersistenceInitializer` applies tracked, ordered migrations inside a
      transaction, observing `ct`, and is safe to call repeatedly.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.
