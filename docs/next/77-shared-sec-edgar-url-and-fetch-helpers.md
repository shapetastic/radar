# Task: Trunk cleanup — one shared SEC EDGAR URL helper (`SecEdgarUrls`) and shared HTTP-fetch ladder for the two SEC readers

## Overview

This is a **pure cleanup / convergence slice**. It converts the single MEDIUM finding (**M-L2**) from the
`radar-architecture-reviewer` checkpoint on the trunk after the AI arc (origin/main `043038d`; verdict
CLEANUP, no HIGH, and this is the only finding NOT already covered by the decisions ledger AD-1…AD-11).
M-L2 was a LOW item last checkpoint; the AI arc (spec 73's `HttpSecEarningsReleaseReader`) turned the SEC
EDGAR primitives into a **three-place duplication**, promoting it to MEDIUM.

**The drift.** The two SEC HTTP readers in `src/Radar.Infrastructure/Sec/` independently re-implement the
same SEC EDGAR primitives — the archive URL scheme, the CIK leading-zero stripping, and the SEC HTTP-403 /
non-success / transport / timeout / cancellation fetch ladder:

1. `src/Radar.Infrastructure/Sec/HttpSecFilingReader.cs` is the **reference implementation**:
   - `BuildIndexUrl(string cik, string accession)` (~lines 177–181) produces
     `https://www.sec.gov/Archives/edgar/data/{cik}/{accNoNoDashes}/{accession}-index.htm`, where
     `accNoNoDashes = accession.Replace("-", "")` (~line 179). This URL is surfaced on
     `SecFilingItem.IndexUrl` (~line 167).
   - `StripLeadingZeros(string cik)` (~lines 226–229) — `TrimStart('0')`, empty → `"0"`.
   - The fetch ladder in `ReadAsync` (~lines 40–77): `403 → Forbidden` (with User-Agent guidance),
     non-success `→ HttpError`, `HttpRequestException → Unreachable`,
     `OperationCanceledException when ct.IsCancellationRequested → re-throw`,
     `TaskCanceledException → Timeout`.

2. `src/Radar.Infrastructure/Sec/HttpSecEarningsReleaseReader.cs` **re-derives the same base path** from a
   raw CIK + accession:
   - `StripLeadingZeros(cik.Trim())` (~line 50), `dashedAccession.Replace("-", "")` (~line 52), the
     `https://www.sec.gov/Archives/edgar/data/{cikNoZeros}/{accNoNoDashes}` base string (~lines 53–54), and
     its own **byte-identical** private `StripLeadingZeros` (~lines 313–317). (Note this reader builds the
     index page with the `.html` extension and appends selected exhibit filenames onto the same base.)
   - A **near-identical** fetch ladder in `FetchAsync` (~lines 109–162) that differs from the filing
     reader's only in the typed result record it returns (`SecEarningsReleaseReadResult` vs
     `SecFilingReadResult`) and in reading the body as a **string** rather than bytes.

**Why it matters / how it compounds.** `DirectionalFilingSignalSource.TryResolveFiling`
(`src/Radar.Infrastructure/Filings/DirectionalFilingSignalSource.cs`, `ParseCikAndAccession` ~lines
252–273 + `IndexUrlRegex` ~lines 278–281) **re-parses** CIK + accession out of the index `SourceUrl` that
`HttpSecFilingReader` itself authored, then hands them to the earnings reader, which **rebuilds the same
base path** — a parse/rebuild round-trip of a URL Radar itself wrote. With the SEC path scheme, the
CIK zero-stripping, and the 403/timeout handling each living in **three** places (two readers + the
regex mirror), any change to SEC's path scheme or its HTTP handling now needs coordinated edits in
several files with no compile-time link between the writer and the readers.

**The fix (recommended by the reviewer — smallest convergence).** Extract a single **internal** helper
type in `Radar.Infrastructure/Sec`, e.g. `SecEdgarUrls`, owning `StripLeadingZeros` and the archive
base-path / index-URL builders. Optionally extract a small shared `SecHttpFetch` helper that maps SEC
HTTP outcomes (403 → User-Agent guidance, non-success, transport failure, timeout, **genuine**
cancellation re-thrown) to a **typed failure via a callback/generic**, so each reader keeps returning its
own typed result record. `HttpSecFilingReader` is the reference and stays the **source** of the extracted
logic; `HttpSecEarningsReleaseReader` moves onto the shared helpers. Every reader's external behaviour and
typed outcomes stay **byte-identical**: same URLs produced, same 403/non-success/transport/timeout mapping,
genuine `ct`-cancellation still re-thrown.

**There is NO behaviour change here.** The URLs the filing reader surfaces on `SecFilingItem.IndexUrl`,
the URLs the earnings reader fetches, and every reader's typed outcome (Forbidden/HttpError/Unreachable/
Timeout/Malformed/NoEarningsExhibit/Success) must be **byte-identical** before and after this slice. This
is **Infrastructure-only plumbing** and does **not** change scoring output, so per **AD-10** it does
**NOT** bump `ScoringEngine.ScoringConfigVersion` (it stays at its current value) — stated explicitly so
the implementer does not wonder.

---

## Assignment

Worktree: any
Dependencies: spec 73 (`HttpSecEarningsReleaseReader`) and spec 75 (`DirectionalFilingSignalSource`) — all
merged.
Conflicts with: touches both SEC readers (`HttpSecFilingReader`, `HttpSecEarningsReleaseReader`), a new
shared `Radar.Infrastructure.Sec` helper (and optionally a shared fetch helper), plus tests. **Must NOT
run in parallel with any SEC-collector / filing / directional-signal slice — sequence it.**
Estimated time: ~1.5–2 h

---

## Project structure changes

```text
src/Radar.Infrastructure/Sec/
  SecEdgarUrls.cs                                   # NEW: single owner of StripLeadingZeros + archive URL builders
  SecHttpFetch.cs                                   # NEW (optional): shared SEC HTTP outcome→typed-failure mapper
  HttpSecFilingReader.cs                            # MODIFIED: uses SecEdgarUrls; (optionally) SecHttpFetch — reference source
  HttpSecEarningsReleaseReader.cs                   # MODIFIED: uses SecEdgarUrls; (optionally) SecHttpFetch — moved onto shared logic

tests/Radar.Infrastructure.Tests/Sec/
  SecEdgarUrlsTests.cs                              # NEW: focused unit tests for the URL helper
  HttpSecFilingReaderTests.cs                       # UNCHANGED (behaviour-preserving proof)
  HttpSecEarningsReleaseReaderTests.cs              # UNCHANGED (behaviour-preserving proof)
```

`Radar.Domain` and `Radar.Application` are unchanged. No DB (AD-8). No new package references.

---

## Implementation details

### 1 — Add `SecEdgarUrls` (Infrastructure/Sec) — single owner of the SEC URL primitives

Add `src/Radar.Infrastructure/Sec/SecEdgarUrls.cs` in namespace `Radar.Infrastructure.Sec`,
`internal static`. It is the single place that knows SEC's CIK canonicalisation and archive path scheme.
Move the **verbatim** logic out of `HttpSecFilingReader` (the reference), and fold `HttpSecEarningsReleaseReader`
onto it. Suggested surface (match the maintainer's naming preference; the shapes below are load-bearing):

```csharp
namespace Radar.Infrastructure.Sec;

/// <summary>
/// Single owner of SEC EDGAR URL construction and CIK canonicalisation for the SEC readers. Consolidates
/// the byte-identical StripLeadingZeros + Archives/edgar/data path building previously copied into
/// HttpSecFilingReader (the reference) and HttpSecEarningsReleaseReader. Pure string logic, no HTTP.
/// </summary>
internal static class SecEdgarUrls
{
    /// <summary>
    /// Canonical CIK for a URL path: leading zeros stripped; an all-zero/blank CIK collapses to "0".
    /// (Callers that receive a raw CIK trim it first — this method does not trim surrounding whitespace.)
    /// </summary>
    public static string StripLeadingZeros(string cik) { /* TrimStart('0'); "" -> "0" */ }

    /// <summary>
    /// The archive base for a filing: <c>https://www.sec.gov/Archives/edgar/data/{cikNoZeros}/{accNoNoDashes}</c>,
    /// where the CIK has leading zeros stripped and the accession has its dashes removed.
    /// </summary>
    public static string BuildArchiveBaseUrl(string cik, string accession) { ... }

    /// <summary>
    /// The filing index landing page. HttpSecFilingReader surfaces the <c>.htm</c> form on
    /// <c>SecFilingItem.IndexUrl</c>; HttpSecEarningsReleaseReader fetches the <c>.html</c> form. Preserve
    /// BOTH exactly — do not unify the extension. Expose whichever surface keeps each caller byte-identical
    /// (e.g. a single builder taking the extension, or two named builders).
    /// </summary>
    public static string BuildIndexUrl(string cik, string accession, ...) { ... }
}
```

Implementation notes — preserve **every** existing external string byte-for-byte:

- `StripLeadingZeros` is the verbatim `HttpSecFilingReader` body (`TrimStart('0')`; empty → `"0"`). Both
  current copies are identical; this becomes the one copy. **Do not** add trimming of surrounding
  whitespace inside it — `HttpSecEarningsReleaseReader` currently calls `StripLeadingZeros(cik.Trim())`, so
  keep the `.Trim()` at that call site (or document the moved responsibility) so its output is unchanged.
- Use `StringComparison.Ordinal` for the `accession.Replace("-", "")` (matches both current call sites,
  ~`HttpSecFilingReader` line 179 and ~`HttpSecEarningsReleaseReader` line 52).
- **The two index-URL extensions differ and MUST stay different**: `HttpSecFilingReader.BuildIndexUrl`
  produces `…-index.htm` (line ~180) surfaced on `SecFilingItem.IndexUrl`; `HttpSecEarningsReleaseReader`
  builds `…-index.html` (line ~54) that it fetches. Do **not** unify them — expose the helper so each
  caller reproduces its exact current string. The `DirectionalFilingSignalSource.IndexUrlRegex` accepts
  both (`-index\.html?`), so the round-trip is unaffected either way, but the filing reader's surfaced
  `.htm` value and the earnings reader's fetched `.html` value are each behaviour and must not move.
- `HttpSecEarningsReleaseReader` also composes the exhibit URL as `{baseUrl}/{selected.FileName}` (~line
  82). Having it build `baseUrl` via `SecEdgarUrls.BuildArchiveBaseUrl` covers this — the exhibit URL is
  just the shared base plus the selected filename; keep that concatenation in the reader.

> **Layering (AD-5) check.** `SecEdgarUrls` is a pure `internal` Infrastructure type (BCL strings only, no
> HTTP, no provider SDK). It lives in `Radar.Infrastructure.Sec` alongside the readers. No Application/Domain/
> Worker type references it. No new package reference.

### 2 — (Optional but recommended) Add `SecHttpFetch` — shared HTTP outcome mapping

The two fetch ladders (`HttpSecFilingReader.ReadAsync` ~lines 40–77; `HttpSecEarningsReleaseReader.FetchAsync`
~lines 109–162) map the **same** SEC HTTP outcomes to a typed failure and differ only in (a) the typed
result record returned and (b) reading the body as bytes vs string. Extract a small shared helper that
performs the GET + status/exception mapping and lets each reader keep its own typed result via a small
callback / generic. Suggested shape (the failure-projection callbacks are the load-bearing part):

```csharp
internal static class SecHttpFetch
{
    /// <summary>
    /// GETs <paramref name="url"/> and maps SEC's HTTP outcomes to the caller's typed failure via the
    /// supplied projections — 403 → forbidden (with the standard User-Agent guidance log), non-success →
    /// http error, HttpRequestException → unreachable, TaskCanceledException → timeout. Genuine
    /// caller-requested cancellation (OperationCanceledException when ct.IsCancellationRequested) is
    /// re-thrown, never mapped. On success returns (default failure, body). TBody lets the filing reader read
    /// bytes and the earnings reader read a string.
    /// </summary>
    public static Task<(TFailure? Failure, TBody Body)> GetAsync<TFailure, TBody>(
        HttpClient httpClient,
        string url,
        ILogger logger,
        Func<HttpContent, CancellationToken, Task<TBody>> readBody,
        Func<string> onForbidden,     // -> TFailure "HTTP 403 (User-Agent)"
        Func<int, string> onHttpError,
        Func<TFailure> onUnreachable,
        Func<TFailure> onTimeout,
        CancellationToken ct)
        where TFailure : class
    { /* the reference ladder from HttpSecFilingReader.ReadAsync, generalised */ }
}
```

Guidance:

- Keep the **exact** log messages and the 403 User-Agent guidance text each reader currently emits (the
  two messages differ slightly — filing reader logs "SEC submissions {SubmissionsUrl}…", earnings reader
  logs "SEC {Url}…"). Preserve each reader's message wording; pass the `ILogger` and message-forming
  detail through so no log string changes. If preserving both message shapes complicates the generic
  helper, it is acceptable to share **only** `SecEdgarUrls` (part 1) and leave the two fetch ladders as-is
  — the URL/CIK duplication is the load-bearing MEDIUM; the fetch-ladder share is the reviewer's
  "optional" second half. State in the PR which you did.
- The genuine-cancellation re-throw (`catch (OperationCanceledException) when (ct.IsCancellationRequested)
  throw;`) MUST remain a re-throw, never mapped to a Timeout/failure. Verify with the existing readers'
  cancellation tests.
- `HttpSecEarningsReleaseReader.FetchAsync` also calls `ct.ThrowIfCancellationRequested()` **before** each
  request (~line 107). Preserve that pre-check (the earnings reader issues two sequential requests); if it
  is not part of the shared helper, keep it at the earnings-reader call sites.

### 3 — Move `HttpSecEarningsReleaseReader` onto the shared helpers; keep `HttpSecFilingReader` as the reference source

- `HttpSecEarningsReleaseReader.ReadAsync` (~lines 43–96): replace the inline
  `StripLeadingZeros(cik.Trim())` / `dashedAccession.Replace("-", "")` / `baseUrl` / `indexUrl` derivation
  (~lines 50–54) with `SecEdgarUrls` calls that reproduce the **exact** current strings (`.html` index,
  same `baseUrl`). Delete its private `StripLeadingZeros` (~lines 313–317).
- `HttpSecFilingReader`: replace its private `BuildIndexUrl` (~177–181) and `StripLeadingZeros` (~226–229)
  bodies with `SecEdgarUrls` calls so the reference logic now lives in one place; the `.htm` index string on
  `SecFilingItem.IndexUrl` (~line 167) stays byte-identical. Its other private JSON helpers
  (`GetString`, `GetArray`, `At`, `TryParseAcceptance`, `NullIfBlank`, `ParseFilings`) are unrelated and stay.
- If part 2 is done: both readers' fetch ladders call `SecHttpFetch.GetAsync(...)` with their own
  failure projections; if part 2 is skipped, both ladders stay as-is.
- Remove any `using` directives that become unused **only if** actually unused after the edits (verify;
  both files use `System.Globalization`, `System.Text.Json`/regex elsewhere).

---

## Tests

- **New — `SecEdgarUrlsTests` (`tests/Radar.Infrastructure.Tests/Sec/`):** focused unit tests for the URL
  helper:
  1. `StripLeadingZeros` edge cases: `"0000320193"` → `"320193"`; `"320193"` → `"320193"` (no change);
     `"0000000000"` → `"0"`; `"0"` → `"0"`; `""` → `"0"`.
  2. `BuildArchiveBaseUrl` from CIK + dashed accession strips the CIK zeros and removes the accession
     dashes: e.g. CIK `"0000320193"`, accession `"0000320193-23-000106"` →
     `https://www.sec.gov/Archives/edgar/data/320193/000032019323000106`.
  3. `BuildIndexUrl` reproduces the filing reader's `…-index.htm` form and the earnings reader's
     `…-index.html` form exactly (whichever surface you exposed) — the dashed accession stays in the
     filename, the de-dashed accession is the path segment.
  4. A round-trip guard (nice-to-have): the `.html` index URL the helper builds matches
     `DirectionalFilingSignalSource.IndexUrlRegex` and re-parses back to the input CIK-no-zeros +
     dashed accession — pinning that Radar can round-trip a URL it authored.
- **Regression (must stay green UNCHANGED — this is the proof the refactor is behaviour-preserving):**
  - `HttpSecFilingReaderTests` — the submissions-JSON parse, the surfaced `SecFilingItem.IndexUrl`, and the
    403 / non-success / transport / timeout / genuine-cancellation outcomes.
  - `HttpSecEarningsReleaseReaderTests` — the index/exhibit fetch, EX-99.* selection, and the same
    403 / non-success / transport / timeout / genuine-cancellation outcomes.
  These pass **unchanged** as the behaviour-preserving gate. If either test referenced a now-moved private
  method reflectively (it should not — both are exercised through the public `ReadAsync`), update the call
  path; otherwise no edits to the existing tests.

---

## Constraints

- Target `net10.0`, C# 14.
- **This is a CLEANUP slice — NO new feature behaviour and NO scoring-output change.** The filing reader's
  surfaced index URL, the earnings reader's fetched URLs, and every reader's typed outcome
  (Forbidden / HttpError / Unreachable / Timeout / Malformed / NoEarningsExhibit / Success) must be
  **byte-identical** before and after. It therefore does **NOT** bump
  `ScoringEngine.ScoringConfigVersion` (stays at its current value) — no AD-10 obligation is triggered
  (Infrastructure plumbing only, no formula / extractor-rule / `ScoringOptions` change).
- `HttpSecFilingReader` is the **reference**; the extracted logic is its logic. `HttpSecEarningsReleaseReader`
  moves onto the shared helper(s).
- **The two index-URL extensions (`.htm` vs `.html`) MUST stay distinct** — do not unify them.
- Genuine caller cancellation (`OperationCanceledException when ct.IsCancellationRequested`) MUST still
  re-throw, never map to a failure/timeout.
- **Layering (AD-5):** the shared helper(s) stay `internal` in `Radar.Infrastructure.Sec` (BCL only, no
  provider SDK); Domain/Application/Worker are untouched. No new package references. Files-first (AD-8),
  no AI, no DB. No advice language; AD-9 labels unchanged.
- **Provenance preserved** — the evidence → signal → score chain is untouched; only SEC URL/HTTP plumbing
  is consolidated.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Out of scope / future (informational — do NOT plan or implement this round)

- **L3 — Domain `FilingSentiment` doubling as the AI structured-output DTO.** The Domain `FilingSentiment`
  record is currently reused as the `IChatClient.GetResponseAsync<FilingSentiment>` structured-output DTO
  in `ChatFilingAnalyzer`. The reviewer recommends **RECORDING this as an accepted decision (a new AD) in
  `docs/architecture-decisions.md`** rather than reworking it — but that is a **maintainer decision**, so
  it is only flagged here, not actioned. Do not separate the DTO from the Domain record this round.
- **Residual documented `JsonElement` clone in `FileRawEvidenceStore`.** The store keeps a minimal local
  `JsonElement` clone by design (to keep the on-disk `RawEvidenceFile` JSON byte-identical). This is **not**
  drift and needs **no action** (accepted in spec 76).

These are recorded only so the next planner has context; no work is planned for them here.

---

## Acceptance criteria

- [ ] A single `internal` `SecEdgarUrls` type exists in `Radar.Infrastructure.Sec`, owning
      `StripLeadingZeros`, the `Archives/edgar/data/{cik}/{accNoNoDashes}` base-path builder, and the index-URL
      builder(s). Both SEC readers construct all SEC URLs / canonicalise the CIK **through** it — neither
      retains its own `StripLeadingZeros` or inline `Archives/edgar/data/...` string.
- [ ] `HttpSecFilingReader` is the reference source of the extracted logic; its surfaced
      `SecFilingItem.IndexUrl` (`…-index.htm`) is byte-identical. `HttpSecEarningsReleaseReader` builds its
      base URL, `.html` index URL, and exhibit URL through `SecEdgarUrls`, byte-identical, and its private
      `StripLeadingZeros` is deleted.
- [ ] Either the shared `SecHttpFetch` helper maps the SEC HTTP outcomes for **both** readers (each keeping
      its own typed result record and log wording), **or** the fetch ladders are left as-is and the PR states
      that only `SecEdgarUrls` was shared this round. In all cases, every reader's 403 / non-success /
      transport / timeout mapping is byte-identical, and genuine `ct`-cancellation still re-throws.
- [ ] **Behaviour byte-identical:** the URLs produced, the fetched URLs, and every typed outcome are
      unchanged — proven by `HttpSecFilingReaderTests` and `HttpSecEarningsReleaseReaderTests` passing
      **unchanged**.
- [ ] New `SecEdgarUrlsTests` cover `StripLeadingZeros` edge cases and URL building from CIK + accession
      (both index extensions), and ideally the round-trip against `DirectionalFilingSignalSource.IndexUrlRegex`.
- [ ] `ScoringEngine.ScoringConfigVersion` is **NOT** bumped; no AD-10 obligation applies (no scoring-output
      change). Layering (AD-5), determinism, and provenance preserved.
- [ ] `dotnet build` / `dotnet test` on `Radar.sln -c Release` green.
