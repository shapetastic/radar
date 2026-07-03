# Task: SEC filing earnings-release (EX-99.1) body reader (Infrastructure, non-AI)

## Overview

The SEC EDGAR collector (spec 56, merged) fetches filing **metadata** and produces `Filing` evidence
whose `Title`/`RawText` are *synthesised from 8-K item codes* — it never fetches the filing **document
body**. For an earnings 8-K (item `2.02`), the beat/miss detail is **not** in the 8-K primary document
(that is a boilerplate cover page with no numbers); it lives in a **separate exhibit, `EX-99.1`** — the
earnings press release. So the directional-filing-signals arc needs a reader that pulls the `EX-99.1`
body text for a given filing.

This slice adds exactly that: a small, **deliberately non-AI, pure-Infrastructure HTTP reader** that,
given a filing (CIK + accession), fetches the filing's index page, finds the `EX-99.1` document, fetches
it, strips HTML to plain text (**reusing** Radar's existing HTML stripper), and returns the plain text
via a typed result. The text it returns is what the AI analyzer (spec 74) will read for beat/miss — but
**no AI, no signal, and no collector change happen here.** This is proven by fetching + selecting
`EX-99.1` + stripping to text, with graceful typed degradation, entirely offline in tests.

**Arc position (slice 2 of 4):** spec 72 (AI `IChatClient` seam, merged) → **THIS body-reader** →
spec 74 (`IFilingAnalyzer`, AI beat/miss over this text) → spec 75 (directional filing signal). AI and
signal-emission are OUT of scope here.

---

## Assignment

Worktree: any (self-contained new `Radar.Infrastructure/Sec` files + additive DI + offline tests)
Dependencies: **56 merged** (SEC collector/reader pattern, `SecCollectorOptions.UserAgent`, the
`ISecFilingReader`/`SecFilingReadResult`/`SecFilingReadOutcome`/`HttpSecFilingReader` shape to mirror, and
the SEC User-Agent + rate-limit + URL-pattern facts); **38 merged** (HTML stripping in
`EvidenceNormalizer` — the stripper to reuse).
Conflicts with: **None.** New `Radar.Infrastructure/Sec/*.cs` files + one additive DI method + new tests.
Does **not** touch the SEC collector, the RSS/GDELT/USASpending collectors, scoring, the report, or the
AI seam.
Estimated time: ~2 h

---

## Verified facts (do NOT re-research; verified live against SEC EDGAR on 2026-07-03 with a compliant User-Agent — the headless implementer CANNOT reach SEC)

Cross-reference spec 56, which already carries the same SEC User-Agent / rate-limit / URL facts and whose
`HttpSecFilingReader` builds the same `{cik}`/`{accNoNoDashes}` values.

- **User-Agent is MANDATORY (same as spec 56).** SEC returns **HTTP 403** for any request without a
  compliant declared `User-Agent` (name + contact email, e.g. `"Radar Research <email>"`). Send
  `Accept-Encoding: gzip, deflate` and enable automatic decompression. Rate limit is **10 req/s** — be
  polite and **sequential** (this reader issues at most two requests per filing: the index page, then the
  exhibit). **Reuse `SecCollectorOptions.UserAgent`** — the reader must **not** hard-code the UA, and a
  missing/blank UA is a fail-fast config error at registration, exactly as `AddSecEdgarCollector` does.
- **Filing base URL:** `https://www.sec.gov/Archives/edgar/data/{cik}/{accNoNoDashes}` where `cik` = the
  CIK with **leading zeros stripped** and `accNoNoDashes` = the accession number with **dashes removed**.
  Example: accession `0001049521-26-000021` → `accNoNoDashes = 000104952126000021`, CIK `1049521`.
  (These are exactly the values `HttpSecFilingReader.BuildIndexUrl` already computes.)
- **The filing index page** is at `{base}/{accessionWithDashes}-index.html`, e.g.
  `.../000104952126000021/0001049521-26-000021-index.html`. It contains a **document table** — one row
  per document with a **Type** column and a **Document** (filename, linked `.htm`/`.html`) column.
  **Verified real rows** for a Mercury Systems earnings 8-K:
  - Type `8-K`     → `mrcy-20260505.htm` (the primary cover page — ~38 KB boilerplate, **no** earnings numbers)
  - Type `EX-99.1` → `a2026q3earningsreleaseex.htm` (~321 KB — **the** earnings press release; issuer-specific filename)
  - Type `EX-99.2` → `q3fy26earningspresentati.htm` (the earnings-presentation slide deck)
  The reader must **parse the index page's document table, find the row whose Type is exactly `EX-99.1`,
  and take its filename.** The filename is issuer-specific and **cannot be guessed** — it MUST be
  discovered from the index.
- **Fallback order when there is no exact `EX-99.1` row:** any `EX-99.*` row (if multiple, prefer the
  **largest by the Size column**, else the first in document order); **else** return the typed
  `NoEarningsExhibit` outcome. **Never** fall back to the primary `8-K` document — it has no numbers.
- **Document URL:** `{base}/{filename}`, e.g.
  `.../000104952126000021/a2026q3earningsreleaseex.htm`.
- **The EX-99.1 body**, HTML-stripped, is exactly the beat/miss material. Verified excerpt: *"Mercury
  Systems Reports Third Quarter Fiscal 2026 Results — Record Q3 FY26 Bookings of $348 million grew 73.7%
  year-over-year … Revenue of $236 million, up 11.5% organically … adjusted EBITDA of $36 million, up
  46.2% …"* (~34 KB of plain text after stripping).
- **Index-page parsing source (recommended: the `-index.html` document table).** The index page is HTML;
  parse the document table **robustly** (rows that carry a Type cell and an `.htm`/`.html` Document cell).
  Do **not** add an HTML-parser package — use the BCL (regex / `System.Text.RegularExpressions`),
  consistent with spec 38. There is also an `index.json` at `{base}/index.json`, but its `type` field is
  only the **icon name** (useless) — do **NOT** use it for document types. An even more robust structured
  alternative is the full-submission SGML header (`{base}/{accessionWithDashes}.txt`, which carries
  `<TYPE>EX-99.1<FILENAME>…` markers). **Recommendation: implement the `-index.html` document-table parse
  (that is what was verified live and what the fixtures below model); mention the SGML `.txt` header as an
  acceptable alternative but do not implement both.**

---

## Project structure changes

```text
src/Radar.Infrastructure/Sec/
  ISecEarningsReleaseReader.cs        # NEW: internal; ReadAsync(cik, accession, ct) -> SecEarningsReleaseReadResult
  SecEarningsReleaseReadResult.cs     # NEW: internal; outcome enum + plain text + resolved doc type/filename
  HttpSecEarningsReleaseReader.cs     # NEW: internal; HttpClient (UA + gzip) -> index -> select EX-99.1 -> exhibit -> strip

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs   # MODIFIED: AddSecEarningsReleaseReader(...) additive registration + named HttpClient

tests/Radar.Infrastructure.Tests/Sec/
  HttpSecEarningsReleaseReaderTests.cs # NEW: offline (fake HttpMessageHandler + fixture index/exhibit HTML)
```

No domain change, no collector change, no scoring/report change, no DB, no AI. New types are `internal`
(mirroring `ISecFilingReader` et al.); the Infrastructure csproj already has `InternalsVisibleTo` for
`Radar.Infrastructure.Tests`.

---

## Implementation details

### Result type (`SecEarningsReleaseReadResult` / `SecEarningsReleaseReadOutcome`)

Mirror `SecFilingReadResult` / `SecFilingReadOutcome` exactly in shape and discipline:

```
internal enum SecEarningsReleaseReadOutcome
{
    Success,            // EX-99.1 (or EX-99.* fallback) fetched and stripped to plain text
    NoEarningsExhibit,  // index parsed OK but no EX-99.* document row present
    Unreachable,        // transport error (HttpRequestException)
    HttpError,          // a non-success HTTP status (other than 403) on index or exhibit
    Forbidden,          // HTTP 403 on index or exhibit — almost always a missing/invalid User-Agent
    Timeout,            // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,          // the index page could not be parsed / had no usable document table
}
```

`internal sealed record SecEarningsReleaseReadResult` carries: `Outcome`, `PlainText` (non-null on
`Success`, else empty), `DocumentType` (the resolved Type cell, e.g. `"EX-99.1"`, on `Success`),
`DocumentFileName` (the resolved filename on `Success`), and a short advice-free `Detail` (logging only).
Provide `Success(text, docType, fileName)` and `Failure(outcome, detail)` factories; `Failure` throws if
handed `Success` (as `SecFilingReadResult.Failure` does). Include an `IsSuccess` convenience.

### Reader interface (`ISecEarningsReleaseReader`)

```
internal interface ISecEarningsReleaseReader
{
    Task<SecEarningsReleaseReadResult> ReadAsync(string cik, string accession, CancellationToken ct);
}
```

- `cik` is the company CIK (the reader strips leading zeros internally, reusing the same rule as
  `HttpSecFilingReader.StripLeadingZeros`); `accession` is the **dashed** accession number
  (e.g. `0001049521-26-000021`). Keeping URL construction inside the reader means spec 74 does not need to
  know SEC URL patterns. (Taking a pre-built index URL is an acceptable alternative; pick one and document
  it. `(cik, accession)` is recommended, since spec 74's `Filing`/`SecFilingItem` already carries both.)

### Reader implementation (`HttpSecEarningsReleaseReader`)

- Inject the typed `HttpClient` (from `IHttpClientFactory` via `AddHttpClient<ISecEarningsReleaseReader,
  HttpSecEarningsReleaseReader>`), an `IEvidenceNormalizer` (the shared HTML stripper — see below), and
  `ILogger<HttpSecEarningsReleaseReader>`. Guard-clause null-check all three (mirror `HttpSecFilingReader`).
- **Build URLs** from `cik`/`accession` per the verified facts:
  `base = https://www.sec.gov/Archives/edgar/data/{cikNoZeros}/{accNoNoDashes}`,
  `indexUrl = {base}/{accession}-index.html`.
- **Fetch the index page** (`GetAsync`, `HttpCompletionOption.ResponseHeadersRead`, `ct`). Map status
  exactly as `HttpSecFilingReader` does: `403 → Forbidden` (log the UA hint), other non-success →
  `HttpError`, `HttpRequestException → Unreachable`, `TaskCanceledException` with `ct` **not** requested →
  `Timeout`, and `OperationCanceledException when ct.IsCancellationRequested` **re-throws**. Read the body
  as a string.
- **Parse the document table** with a BCL regex over the index HTML: extract rows, and from each row the
  **Type** cell text and the **Document** cell's linked `.htm`/`.html` filename (and, if present, the
  **Size** cell for the fallback tie-break). If no rows / no usable table can be parsed → `Malformed`.
  Selection:
  1. a row whose Type is exactly `EX-99.1` (case-insensitive, trimmed);
  2. else any `EX-99.*` row — if several, the largest by Size, else the first in document order;
  3. else → `NoEarningsExhibit`. **Never** select the `8-K` primary document.
- **Fetch the selected exhibit** at `{base}/{filename}` (same status→outcome mapping as the index fetch).
  Read the body as a string.
- **Strip HTML → plain text by REUSING the shared stripper.** Do **not** hand-roll a second HTML stripper.
  Spec 38 put HTML stripping inside `EvidenceNormalizer` (its private `CleanHtml`), reachable through the
  public seam **`IEvidenceNormalizer.Normalize(title: null, rawText: exhibitHtml).NormalizedText`**, which
  returns tag-stripped, entity-decoded, whitespace-collapsed **plain text**. Call that and return its
  `NormalizedText` as the result's `PlainText`. (The normalizer also computes a content hash we ignore
  here — that is fine; reuse over duplication.) Return
  `SecEarningsReleaseReadResult.Success(plainText, docType, fileName)`.
- **Never throw on a bad response** — degrade to the typed outcome. Re-throw only genuine caller
  cancellation (`OperationCanceledException when ct.IsCancellationRequested`), exactly like the spec-56
  reader. All HTTP/HTML/SEC specifics stay in `Radar.Infrastructure` (AD-5).

### DI registration (`AddSecEarningsReleaseReader`)

Add one additive method, mirroring `AddSecEdgarCollector`'s HttpClient + fail-fast UA setup:

```
public static IServiceCollection AddSecEarningsReleaseReader(
    this IServiceCollection services, SecCollectorOptions options)
```

- `ArgumentNullException.ThrowIfNull(options)`; **fail fast** with the same clear message pattern as
  `AddSecEdgarCollector` when `options.UserAgent` is null/blank (SEC 403s without it).
- Register the named typed client exactly like the collector's:
  `AddHttpClient<ISecEarningsReleaseReader, HttpSecEarningsReleaseReader>(client => { TryAddWithoutValidation
  "User-Agent" = options.UserAgent; TryAddWithoutValidation "Accept-Encoding" = "gzip, deflate"; })
  .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AutomaticDecompression = GZip | Deflate })`.
- `services.TryAddSingleton(options)` (so it coexists with `AddSecEdgarCollector`'s
  `AddSingleton(options)` without a double-registration conflict when both are wired), and
  `services.TryAddSingleton<IEvidenceNormalizer, EvidenceNormalizer>()` so the reader's stripper dependency
  resolves even when registered standalone (matches the `TryAdd` pattern already used for the mapper/collectors).
- This reader is **not** an `IEvidenceCollector` and is **not** added to `Radar:Collectors`; it is a
  standalone service that spec 74 will inject. No `RadarWorkerServices`/`appsettings` change is required in
  this slice.

---

## Tests

`HttpSecEarningsReleaseReaderTests` — **offline only**: a fake `HttpMessageHandler` routes by request URL,
serving fixture index HTML for the `-index.html` URL and fixture exhibit HTML for the document URL. No
network. Deterministic. Use the same fake-handler style as `HttpSecFilingReaderTests`. Construct the reader
with a real `EvidenceNormalizer` (so the assertions exercise the **shared** stripper) and a
`NullLogger<HttpSecEarningsReleaseReader>`.

Cases:

- **Selects EX-99.1 and returns stripped plain text.** Fixture index with `8-K` / `EX-99.1` / `EX-99.2`
  rows → the reader requests the `EX-99.1` filename's URL, and returns `Success` with `DocumentType ==
  "EX-99.1"`, `DocumentFileName ==` the fixture filename, and `PlainText` containing the release text
  (e.g. `"Record Q3 FY26 Bookings"`) with **no HTML tags** (assert `PlainText` does not contain `"<"`).
- **No EX-99 row → `NoEarningsExhibit`.** Index with only an `8-K` (and e.g. an `EX-101` XBRL) row →
  outcome `NoEarningsExhibit`, and the reader does **not** fetch the primary 8-K document as a fallback.
- **EX-99.* fallback.** Index with no `EX-99.1` but an `EX-99` (or `EX-99.2`) row → the reader selects that
  row and returns `Success` from it (assert the resolved `DocumentType`/filename is the EX-99.* one).
- **403 → `Forbidden`.** Index (or exhibit) responds 403 → outcome `Forbidden`; never throws.
- **Malformed/empty index → `Malformed`.** Empty body or HTML with no parseable document table → `Malformed`.
- **Timeout → `Timeout`.** Handler throws `TaskCanceledException` (with `ct` not requested) → `Timeout`.
- **Caller cancellation re-throws.** An already-cancelled `CancellationToken` (or a handler that throws
  `OperationCanceledException` with the token cancelled) → the call **throws**
  `OperationCanceledException`, not a failure result.

Assert throughout that the reader reused the shared stripper (plain-text output, tags removed) and that no
production scoring/extraction/report/collector path changed. Existing tests stay green.

---

## Constraints

- Target `net10.0`, C# 14.
- All HTTP/HTML/SEC specifics confined to `Radar.Infrastructure` (**AD-5**); no provider SDK; **no DB**
  (AD-8); **no AI** in this slice (spec 74 adds the analyzer).
- **Reuse** the existing HTML stripper via `IEvidenceNormalizer.Normalize(...).NormalizedText`; do **not**
  hand-roll a second HTML stripper, and do **not** add an HTML-parser package (BCL regex only, per spec 38).
- Honour the SEC User-Agent (compliant UA required; sourced from `SecCollectorOptions.UserAgent`, never
  hard-coded; missing UA = fail-fast) and rate rules (sequential/polite; gzip + auto-decompress).
- Deterministic; **never throw** on a bad response — typed graceful degradation; re-throw only genuine
  caller cancellation.
- Never emit advice language (this reader returns raw filing text only; it produces no labels).
- Keep changes scoped to the new reader + its DI + tests. Do not modify the collector, scoring formula,
  extractor, resolver, or report.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `ISecEarningsReleaseReader.ReadAsync(cik, accession, ct)` (internal) fetches the filing's
      `{accession}-index.html`, parses the document table, and selects the `EX-99.1` row's issuer-specific
      filename (with the `EX-99.*`-then-`NoEarningsExhibit` fallback; never the primary 8-K).
- [ ] It fetches `{base}/{filename}` and returns the exhibit body as **plain text** produced by the
      **shared** HTML stripper (`IEvidenceNormalizer.Normalize(null, html).NormalizedText`) — no
      hand-rolled stripper, no HTML-parser package.
- [ ] The named `HttpClient` sends the configured, **non-hard-coded** `User-Agent` (from
      `SecCollectorOptions.UserAgent`) plus `Accept-Encoding: gzip, deflate` with automatic decompression;
      `AddSecEarningsReleaseReader` **fails fast** on a blank UA.
- [ ] `SecEarningsReleaseReadResult` reports typed outcomes — `Success` (+ plain text + resolved
      `DocumentType`/`DocumentFileName`), `NoEarningsExhibit`, `Forbidden` (403), `HttpError`,
      `Unreachable`, `Timeout`, `Malformed` — and the reader **never throws** on a bad response; genuine
      caller cancellation (`ct` requested) propagates.
- [ ] The reader is registered **additively** (`AddSecEarningsReleaseReader`) without touching the SEC
      collector, `Radar:Collectors`, scoring, or the report; default pipeline behaviour is unchanged.
- [ ] Offline tests (fake `HttpMessageHandler` + fixture index/exhibit HTML) cover: EX-99.1 selection +
      stripped-text output, `NoEarningsExhibit`, EX-99.* fallback, 403→`Forbidden`, malformed index, timeout,
      and caller-cancellation re-throw. No AI, no signal, no collector/scoring/report change.
- [ ] `dotnet build`/`dotnet test` on `Radar.sln -c Release` green. This is **slice 2** of the
      directional-filing arc (seam → **this body-reader** → `IFilingAnalyzer` → beat/miss signal); AI and
      signal-emission are out of scope.
