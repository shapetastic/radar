# Task: Dapper repositories — Evidence and Company (+ aliases)

## Overview

Implements the first two Postgres-backed repositories behind the **existing, unchanged** Application
interfaces — `IEvidenceRepository` and `ICompanyRepository` — using Dapper on the schema and connection
factory from spec 26. The in-memory implementations stay (default for unit tests); these are an
additional implementation selectable via DI in spec 29.

Semantics must match the in-memory repositories **exactly**:

- **AD-1 (evidence is immutable/insert-only).** `AddIfNewAsync` inserts; a duplicate `ContentHash` is
  rejected and returns `false`; an existing evidence row is never overwritten.
- **AD-1 (everything else is upsert-by-Id, last-write-wins).** `Company.AddAsync` and
  `AddAliasAsync` upsert by `id`.
- **AD-3 (deterministic ordering).** `GetAllAsync` evidence orders by `collected_at_utc, id`; companies
  by `created_at_utc, id`; aliases by `created_at_utc, id`.
- **AD-2 (real impls observe the token).** Every Npgsql/Dapper call receives `ct` via
  `CommandDefinition(..., cancellationToken: ct)`.

---

## Assignment

Worktree: pending
Dependencies: 26-postgres-schema-and-migration-runner (`NpgsqlConnectionFactory`,
`PostgresDapperConfiguration`, the schema)
Conflicts with: None — adds only new files under `Persistence/Postgres/`. May run in parallel with
spec 28 (disjoint files); both are wired together by spec 29.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Infrastructure/Persistence/Postgres/
  PostgresEvidenceRepository.cs   # NEW: IEvidenceRepository
  PostgresCompanyRepository.cs    # NEW: ICompanyRepository
```

Namespace: `Radar.Infrastructure.Persistence.Postgres`.

---

## Implementation details

Each repository takes the `NpgsqlConnectionFactory` (constructor injection), opens a connection per call,
and uses Dapper with `CommandDefinition(sql, param, cancellationToken: ct)`. Map enums as text and
underscores-to-PascalCase via `PostgresDapperConfiguration` (registered in spec 29 before use; tests in
spec 30 call `EnsureConfigured()` in their fixture). Normalise every `DateTimeOffset` to UTC
(`.ToUniversalTime()`) before binding so Npgsql's `timestamptz` accepts it.

### `PostgresEvidenceRepository : IEvidenceRepository`

- `AddIfNewAsync(EvidenceItem item, CancellationToken ct)`:
  ```sql
  INSERT INTO evidence_items (id, source_type, source_name, source_url, title, summary,
      raw_text, content_hash, published_at_utc, collected_at_utc, quality, metadata_json)
  VALUES (@Id, @SourceType, @SourceName, @SourceUrl, @Title, @Summary,
      @RawText, @ContentHash, @PublishedAtUtc, @CollectedAtUtc, @Quality, @MetadataJson)
  ON CONFLICT (content_hash) DO NOTHING;
  ```
  Return `rowsAffected == 1`. A duplicate `content_hash` yields `0` → `false` and leaves the existing
  immutable row untouched (AD-1). (`ON CONFLICT` names the `ux_evidence_items_content_hash` constraint;
  do not add `DO UPDATE` — evidence is never overwritten.)
- `GetByIdAsync` — `SELECT * ... WHERE id = @id`, single-or-default.
- `GetByContentHashAsync` — `SELECT * ... WHERE content_hash = @hash`, single-or-default.
- `GetAllAsync` — `SELECT * ... ORDER BY collected_at_utc, id`.

### `PostgresCompanyRepository : ICompanyRepository`

- `AddAsync(Company company, CancellationToken ct)` — upsert by id:
  ```sql
  INSERT INTO companies (id, name, legal_name, ticker, exchange, country_code, sector, industry,
      status, created_at_utc, updated_at_utc)
  VALUES (@Id, @Name, @LegalName, @Ticker, @Exchange, @CountryCode, @Sector, @Industry,
      @Status, @CreatedAtUtc, @UpdatedAtUtc)
  ON CONFLICT (id) DO UPDATE SET
      name = EXCLUDED.name, legal_name = EXCLUDED.legal_name, ticker = EXCLUDED.ticker,
      exchange = EXCLUDED.exchange, country_code = EXCLUDED.country_code, sector = EXCLUDED.sector,
      industry = EXCLUDED.industry, status = EXCLUDED.status,
      created_at_utc = EXCLUDED.created_at_utc, updated_at_utc = EXCLUDED.updated_at_utc;
  ```
  (Last-write-wins: every column is overwritten with the incoming record, matching the in-memory
  `_companies[company.Id] = company` replace.)
- `GetByIdAsync` — `WHERE id = @id`, single-or-default.
- `GetAllAsync` — `ORDER BY created_at_utc, id`.
- `AddAliasAsync(CompanyAlias alias, CancellationToken ct)` — upsert by id into `company_aliases`
  (`ON CONFLICT (id) DO UPDATE SET company_id, alias, alias_type, created_at_utc = EXCLUDED.*`).
- `GetAliasesAsync` — `SELECT * FROM company_aliases ORDER BY created_at_utc, id`.

> Dapper maps each `SELECT *` row straight onto the domain record's constructor (underscore matching +
> enum handlers from spec 26). If constructor mapping proves fussy for any record, fall back to a private
> `Row` DTO with primitive columns plus a `ToDomain()` projection — but keep the column/ordering contract
> identical.

---

## Tests

No live-database tests in this slice — the gated Testcontainers suite covering these repositories lands in
spec 30 (it asserts the AD-1 duplicate-hash rejection and AD-3 ordering against a real Postgres). This
slice must compile and keep `dotnet build`/`dotnet test Radar.sln` green. Do not weaken or duplicate the
existing in-memory repository tests.

---

## Constraints

- Target .NET 10. Npgsql/Dapper stay in `Radar.Infrastructure` (AD-5).
- Match the in-memory semantics exactly: evidence insert-only with duplicate-hash → `false` (AD-1);
  company/alias upsert-by-Id last-write-wins (AD-1); the documented orderings (AD-3).
- Observe `ct` on every database call (AD-2 — non-observance is in-memory-only).
- Store/emit UTC only; normalise `DateTimeOffset` to UTC before binding.
- Keep changes scoped to the two new repository files. Do not touch DI, the Worker, or the in-memory code.

---

## Acceptance criteria

- [ ] `PostgresEvidenceRepository` and `PostgresCompanyRepository` implement their interfaces with the
      exact method signatures.
- [ ] Evidence `AddIfNewAsync` uses `ON CONFLICT (content_hash) DO NOTHING` and returns `false` on a
      duplicate hash without mutating the existing row.
- [ ] Company and alias writes upsert by `id` (last-write-wins); evidence is never upserted.
- [ ] All collection queries carry the AD-3 `ORDER BY` (`collected_at_utc,id` / `created_at_utc,id`).
- [ ] Every Npgsql/Dapper call passes `ct`.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.
