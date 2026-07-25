# Task: Fix the ODP patents reader against the LIVE API (closes spec 131's deferred verification)

> **DEPENDS ON PR #136 (spec 131) BEING MERGED FIRST.** Spec 131 repointed `HttpPatentSearchReader` off the
> dead `search.patentsview.org` onto the ODP PFW Search API, but pinned its request shape **from
> documentation** because no API key existed — live verification was explicitly deferred (spec-130
> precedent). **An ODP API key was obtained on 2026-07-25 and that verification has now been done.** It
> found **two blocking defects that make the collector return zero data on every run**, plus three lesser
> corrections. This slice applies them.
>
> **No `RuleSetVersion` / `_formula.Version` / weight / tier / enum change. No fingerprint move.** Same
> emitted phrase, same Neutral rule, still opt-in / OFF. Reader transport + parse only.

## Why this is not cosmetic

`patents` is opt-in / OFF, so the baseline is unaffected either way — but as merged, the collector is
**non-functional**: defect 1 makes every request fail with HTTP 400 before any company is ever matched.

---

## ⚠️ DEFECT 1 (BLOCKING) — `rangeFilters` requires BOTH bounds; the reader sends one

`BuildRequestBody` deliberately sends a one-sided floor:

```csharp
// One-sided grant-date range (valueFrom only — the reader receives a floor, so no valueTo is invented).
rangeFilters = new[] { new { field = GrantDateField, valueFrom = floor } },
```

**Live result: HTTP 400, unconditionally.** Verified 2026-07-25 by isolating each body element against
`api.uspto.gov` — a one-sided `rangeFilters` is rejected regardless of `q`, `fields`, `sort`, or paging:

| Body | Result |
|---|---|
| `valueFrom` + `valueTo` (both), no `fields`/`sort` | **200**, `count: 2` |
| `valueFrom` + `valueTo` + `fields` | **200**, `count: 2` |
| `valueFrom` + `valueTo` + `fields` + `sort` (`Desc` and `desc`) | **200**, `count: 2` |
| `valueFrom` + `valueTo: "9999-12-31"` | **200**, `count: 2` |
| **`valueFrom` only (as merged)** | **400** |

So every read degrades to `HttpError` → `SourceFailure`. The collector can never emit evidence.

**Fix:** send both bounds. Use the **far-future ceiling `9999-12-31`** — the reader genuinely only has a
floor, and this is the exact pattern `HttpFdaClearanceReader` already uses for openFDA
(`decision_date:[<floor> TO 9999-12-31]`, chosen so the reader needs no clock for the upper bound). Pin
the ceiling as a named constant and mirror FDA's comment explaining why it is far-future rather than "today".

## ⚠️ DEFECT 2 (BLOCKING for quiet companies) — HTTP 404 means ZERO RESULTS, not an error

**Verified live:** a query matching nothing returns **HTTP 404 with an empty body**. It is ODP's
empty-result response.

As merged the reader passes `onStatus: null` to `HttpOutcomeFetch.SendAsync`, so 404 falls through to
`onHttpError` → `PatentSearchOutcome.HttpError`. Every company with no grants in the window — which,
given how thin recent-grant volume is, is most of the seed set — would report a **source failure** rather
than an honest zero.

**Fix:** handle 404 → `Success` with 0 grants via the **`HttpOutcomeFetch.onStatus` hook**. This is
**exactly** the problem spec 129 already solved for openFDA ("empty-search HTTP 404 ⇒ Success 0 per
endpoint via reference-identity sentinel through `HttpOutcomeFetch.onStatus`"). **Reuse that mechanism —
do not invent a second one.** If the sentinel plumbing in `HttpFdaClearanceReader` can be lifted into a
shared helper without contorting either call site, do so (CLAUDE.md reuse-over-copy); if not, follow the
same shape and say why in a comment.

Every *other* non-2xx (400, 401, 5xx) must still map to `HttpError`.

## DEFECT 3 — applicant matching needs client-side normalisation

`firstApplicantName` phrase matching is **token-based, not exact**. Both failure directions are live-verified:

- **False positives** — `firstApplicantName:"Energy Recovery"` returns **280** raw matches including
  `General Energy Recovery Inc.`, `CiTech Energy Recovery System Malaysia Sdn. Bhd.`, and
  `CORE Energy Recovery Solutions Inc.` — unrelated companies whose grants would inflate ERII's count.
- **False negatives** — Mercury Systems files under at least four spellings:
  `Mercury Systems, Inc.` / `Mercury Systems Inc.` / `MERCURY  SYSTEMS, INC.` (double space) /
  `MERCURY SYSTEMS, INC`. Strict equality would drop most of a company's own rows.

**Fix:** after parsing, filter rows by a **normalised** comparison of
`applicationMetaData.firstApplicantName` against the seed token — upper-case, strip all non-alphanumeric
characters (punctuation **and** whitespace), then prefix-match. Verified effective: reduces Energy
Recovery's 280 raw hits to 239 genuine ones while retaining all 20 Mercury spelling variants.

The emitted grant count **must** be the post-normalisation count. (The reader already ignores the root
`count` for the emitted total — keep it that way; `count` is pre-normalisation and is provenance-only.)

## DEFECT 4 — drop the MRCY seed

Live check on 2026-07-25, normalised:

| Seed | Normalised records | Grants ≤180d | Most recent grant | Verdict |
|---|---|---|---|---|
| **ERII** (`assignee=Energy Recovery`) | 239 | **2** | 2026-05-12 | **KEEP** — verified working |
| **EOSE** (`assignee=Eos Energy`) | 77 | 0 | 2026-01-13 | **KEEP** — real history, just outside window |
| **MRCY** (`assignee=Mercury Systems`) | 20 | 0 | **2021-07-06** | **DROP** |

Remove the **MRCY** `patents` feed token from `data/companies.json`. Mercury Systems has zero filings,
publications, or grants in the last two years on any clock. It is a permanently empty feed producing only
log noise. (Consistent with the dataset caveat below — Mercury grows largely by acquisition, and acquired
IP is invisible to an applicant-keyed API.) Data-only; no fingerprint impact.

## DEFECT 5 — remove the phantom `totalNumFound` constant

The reader pins `TotalNumFoundProperty = "totalNumFound"`. The live envelope has **no such property**; the
root is `{ count, patentFileWrapperDataBag, requestIdentifier }`. Remove the dead constant and any fallback
branch that reads it, so the pinned names reflect reality.

---

## VERIFIED CORRECT — do NOT change these

Live-confirmed on 2026-07-25. Changing any of them would be a regression:

- Endpoint `POST https://api.uspto.gov/api/v1/patent/applications/search`; configurable `BaseUrl`. ✅
- Auth header `X-Api-Key` (HTTP header names are case-insensitive; the live probe used `X-API-Key`). ✅
- Field constants — all four exactly right as merged: `applicationMetaData.firstApplicantName`,
  `.grantDate`, `.patentNumber`, `.inventionTitle`. ✅
- Response container `patentFileWrapperDataBag`; root total `count`; metadata object
  `applicationMetaData`. ✅
- `fields` projection — accepted. ✅
- `sort` with `order` `"Desc"` (and `"desc"`) — accepted. ✅
- `pagination.limit = 100` — accepted (`limit: 200` fails; keep 100 as a hard ceiling). ✅
- Assignee quote-escaping in `q` — already applied on the branch, keep it. ✅
- `grantDate` / `patentNumber` are **absent keys** on non-granted rows; the existing skip-don't-coerce
  parse is correct. ✅

> **Dataset caveat (record, do not act on):** ODP PFW is an **applications** dataset keyed on **applicant**.
> There is **no assignee field**; IP acquired by assignment is invisible. Accepted limitation of the Neutral
> v1 signal.

## Verified sample response — build fixtures from this

Request: `q = applicationMetaData.firstApplicantName:"Energy Recovery"`,
`rangeFilters = [{field: applicationMetaData.grantDate, valueFrom: "2025-01-01", valueTo: "2026-07-25"}]`,
`pagination = {offset: 0, limit: 3}`. Trimmed to the fields the reader reads — the live payload also
carries `eventDataBag`, `parentContinuityBag`, `recordAttorney`, `applicantBag`, `inventorBag`,
`cpcClassificationBag` and more, so fixtures **must tolerate unknown properties**.

```json
{
  "count": 11,
  "requestIdentifier": "<redacted-guid>",
  "patentFileWrapperDataBag": [
    {
      "applicationNumberText": "18867137",
      "applicationMetaData": {
        "firstApplicantName": "Energy Recovery, Inc.",
        "inventionTitle": "GEOTHERMAL POWER GENERATION SYSTEMS WITH PRESSURE EXCHANGERS",
        "grantDate": "2026-05-12",
        "patentNumber": "12624681",
        "filingDate": "2024-11-19",
        "earliestPublicationDate": "2025-07-03",
        "applicationStatusDescriptionText": "Patented Case"
      }
    },
    {
      "applicationNumberText": "18849446",
      "applicationMetaData": {
        "firstApplicantName": "Energy Recovery, Inc.",
        "inventionTitle": "PRESSURE EXCHANGERS WITH FOULING AND PARTICLE HANDLING CAPABILITIES",
        "grantDate": "2025-09-02",
        "patentNumber": "12404877",
        "filingDate": "2024-09-20",
        "earliestPublicationDate": "2025-06-12",
        "applicationStatusDescriptionText": "Patented Case"
      }
    }
  ]
}
```

A **non-granted** row is the same shape with `grantDate` and `patentNumber` **keys absent entirely**. A
**false-positive** row uses `firstApplicantName: "General Energy Recovery Inc."`; **spelling-variant** rows
use `"MERCURY  SYSTEMS, INC."` (double space), `"Mercury Systems Inc."`, `"MERCURY SYSTEMS, INC"`.

## Assignment

Worktree: any. Files: `Radar.Infrastructure/Patents/HttpPatentSearchReader.cs` (+ its tests/fixtures),
possibly a shared 404-sentinel helper alongside `Radar.Infrastructure/Fda/HttpFdaClearanceReader.cs`, and
`data/companies.json` (MRCY token removal).
Dependencies: **PR #136 (spec 131) MERGED.** Independent of 132 and 133 — shares no files.
Estimated time: ~1–1.5 h.

## Tests

- **`rangeFilters` carries BOTH `valueFrom` and `valueTo`** — a POST-body-shape assertion pinning the
  request JSON (this also closes the spec-131 reviewer's non-blocking note about locking the body shape).
  Assert the ceiling constant is used, and that a one-sided body is never produced.
- **HTTP 404 ⇒ `Success` with 0 grants, no failure recorded** — explicit regression test.
- **400 / 401 / 5xx still ⇒ `HttpError`** — so the 404 special-case can't over-broaden.
- **Normalisation** — one fixture containing a false positive (`General Energy Recovery Inc.`) **and**
  punctuation/whitespace variants of the seed asserts the false positive is excluded and every genuine
  variant retained; and that the emitted count is the post-normalisation count.
- Existing behaviour regression-locked: absent/unparseable `grantDate` row skipped; blank key ⇒
  `MissingApiKey` with no HTTP call; malformed ⇒ `Malformed`; timeout ⇒ `Timeout`; `HttpRequestException`
  ⇒ `Unreachable`; cancellation re-throws.
- `data/companies.json` seed test (if one exists) updated for the MRCY removal.
- **Fingerprint guard:** `ScoringConfigFingerprintTests` and siblings green **unmodified, with no pin edit**.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Constraints

- **Secret handling:** the key is read at runtime from the env var named in config; never logged,
  committed, or written to evidence/provenance. Committed fixtures contain no key and a redacted
  `requestIdentifier`.
- **Reuse over copy:** the 404-as-empty handling must go through the same `HttpOutcomeFetch.onStatus`
  mechanism spec 129 established — do not paste a parallel implementation.
- **Scope:** reader transport/parse + one seed token. If this slice touches `SignalType.cs`,
  `EvidenceSourceType.cs`, `KeywordSignalExtractor.cs`, `ScoringWeights`, or any fingerprint pin, it has
  leaked scope and is wrong.

## Acceptance criteria

- [ ] `rangeFilters` sends **both** `valueFrom` and a far-future `valueTo` constant; a POST-body-shape test
      pins it; a live-equivalent fixture proves the 400 is gone.
- [ ] **HTTP 404 ⇒ `Success` 0 grants** via `HttpOutcomeFetch.onStatus` (reusing the spec-129 mechanism);
      400/401/5xx still ⇒ `HttpError`; both tested.
- [ ] Applicant matching is **normalised client-side**; emitted count is post-normalisation; tested against
      both a false positive and spelling variants.
- [ ] The **MRCY** `patents` feed token is removed from `data/companies.json`; ERII and EOSE unchanged.
- [ ] The phantom `totalNumFound` constant and any fallback reading it are removed.
- [ ] Every item in **VERIFIED CORRECT** is left unchanged.
- [ ] **No `_formula.Version` / `RuleSetVersion` / weight / tier / enum change; fingerprints byte-identical**
      — `ScoringConfigFingerprintTests` green **without any pin edit**.
- [ ] `patents` remains opt-in / OFF; `scripts/run-profiles/default.json` untouched by this slice.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
