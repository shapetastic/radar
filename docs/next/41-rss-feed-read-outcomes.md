# Task: Surface per-feed RSS read outcomes (success vs. failure)

## Overview

For the RSS-first MVP, the whole report depends on feeds actually being readable. Today
`HttpRssFeedReader` collapses **every** failure mode — non-success HTTP status, transport error, HTTP
timeout, malformed XML, parsed-to-null — into the same `[]` (empty list) return, with only a per-call
`LogWarning`. `RssPressReleaseCollector` therefore **cannot distinguish a genuinely quiet feed (read OK,
zero items) from a broken feed (404, DNS failure, malformed XML)**. The collector logs only an aggregate
`{FeedsRead} feed(s) read, {ItemsCollected} item(s) collected`, so a feed that silently stopped working
is invisible — Dean sees a sparse report and cannot tell "quiet week" from "dead feed". This erodes
trust in the central artefact.

This slice makes the reader return a small typed **read outcome** (success-with-items, or a failure with
a reason) instead of degrading to `[]`, and makes the collector log a **Warning per failed feed**
(feed name, URL, reason) plus an aggregate "checked N feeds, M failed". It is an Infrastructure-only
change: it does not touch the `IEvidenceCollector` Application contract, the pipeline, scoring, or the
report. Caller-requested cancellation still propagates unchanged. Slice 42 then lifts these outcomes
into a structured collection summary on the collector contract.

---

## Assignment

Worktree: any
Dependencies: None.
Conflicts with: Slice 42 (both edit `RssPressReleaseCollector.cs` and its tests) — **sequence 41 before
42**. No other conflicts.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Infrastructure/Rss/RssFeedReadResult.cs        # NEW: result record + outcome enum
src/Radar.Infrastructure/Rss/IRssFeedReader.cs           # MODIFIED: ReadAsync returns RssFeedReadResult
src/Radar.Infrastructure/Rss/HttpRssFeedReader.cs        # MODIFIED: populate outcome instead of returning []
src/Radar.Infrastructure/Rss/RssPressReleaseCollector.cs # MODIFIED: per-feed warning + aggregate summary

tests/Radar.Infrastructure.Tests/Rss/HttpRssFeedReaderTests.cs        # MODIFIED/extended
tests/Radar.Infrastructure.Tests/Rss/RssPressReleaseCollectorTests.cs # MODIFIED/extended
```

No new public API outside Infrastructure (`IRssFeedReader` and its types stay `internal`). No DI
changes, no domain changes, no Application changes.

---

## Implementation details

### `RssFeedReadResult` (new, internal)

```csharp
internal enum RssFeedReadOutcome
{
    Success,      // feed fetched and parsed; Items may still be empty (a genuinely quiet feed)
    Unreachable,  // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,    // a non-success HTTP status code
    Timeout,      // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,    // XML could not be parsed, or SyndicationFeed.Load returned null
}

internal sealed record RssFeedReadResult(
    RssFeedReadOutcome Outcome,
    IReadOnlyList<RssFeedItem> Items,
    string? Detail)
{
    public bool IsSuccess => Outcome == RssFeedReadOutcome.Success;

    public static RssFeedReadResult Success(IReadOnlyList<RssFeedItem> items) =>
        new(RssFeedReadOutcome.Success, items, Detail: null);

    public static RssFeedReadResult Failure(RssFeedReadOutcome outcome, string detail) =>
        new(outcome, [], detail);
}
```

`Detail` is a short human-readable reason (e.g. `"HTTP 404"`, `"malformed XML"`, `"request timed out"`)
used only for logging — keep it stable and free of advice language.

### `IRssFeedReader`

Change the signature to `Task<RssFeedReadResult> ReadAsync(string feedUrl, CancellationToken ct)`. Update
the XML doc so it states the reader **reports** failures via the result rather than swallowing them, and
that caller-requested cancellation still throws.

### `HttpRssFeedReader`

Replace each existing `return [];` failure path with the matching `RssFeedReadResult.Failure(...)`:

- non-success status → `HttpError`, detail `$"HTTP {(int)response.StatusCode}"`;
- `HttpRequestException` → `Unreachable`;
- `TaskCanceledException` (the existing timeout branch, ct not requested) → `Timeout`;
- `XmlException` and the `feed is null` branch → `Malformed`.

The success path returns `RssFeedReadResult.Success(items)` (items may be empty). Keep the existing
`LogWarning` calls as-is (they already log the URL + reason) — the reader still logs, and now also
reports. **Do not change** the XXE hardening (`DtdProcessing.Prohibit`, `XmlResolver = null`), the
`HttpCompletionOption.ResponseHeadersRead` + materialize-before-parse flow, the `content:encoded`
extraction, or the `OperationCanceledException when (ct.IsCancellationRequested) → throw` rethrow.

### `RssPressReleaseCollector`

`CollectAsync` already loops feeds in deterministic order. Per feed:

- call the reader, then branch on `result.IsSuccess`;
- on success, process `result.Items` exactly as today (title guard, within-batch dedupe, map to
  `CollectedEvidence`) — **behaviour for successful feeds is unchanged**;
- on failure, `LogWarning` once with the feed **name**, **URL**, and `result.Detail` (e.g.
  `"RSS feed '{FeedName}' ({FeedUrl}) could not be read: {Detail}; skipping."`), and count it as a
  failed feed.

Replace the current aggregate log with one that distinguishes outcomes, e.g.
`"RSS press-release collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, {ItemsCollected} item(s) collected."`
Failed feeds contribute zero items (the result already carries an empty list). Determinism, ordering,
the shared within-batch `seen` dedupe set, and the company-hint logic are all unchanged.

---

## Tests

### `HttpRssFeedReaderTests` (extended)

- **Success with items:** a valid RSS fixture → `Outcome == Success`, items populated.
- **Success but empty:** a valid feed with no items → `Outcome == Success`, `Items` empty (regression:
  a quiet feed is NOT a failure).
- **HTTP error:** stubbed 404 → `Outcome == HttpError`, `Detail` contains the status, `Items` empty.
- **Unreachable:** handler throws `HttpRequestException` → `Outcome == Unreachable`, empty items.
- **Timeout:** handler throws `TaskCanceledException` with the token **not** cancelled → `Outcome ==
  Timeout`.
- **Malformed XML:** body is not valid XML → `Outcome == Malformed`.
- **Cancellation still propagates:** a cancelled `CancellationToken` → `OperationCanceledException`
  thrown (not reported as a failure result). (Keep/adapt the existing cancellation test.)

### `RssPressReleaseCollectorTests` (extended)

- **Failed feed logged + skipped:** a fake reader returning a `Failure(HttpError, …)` for one feed and
  `Success` for another → only the successful feed's items appear; the run does not throw; the failed
  feed produces zero evidence. Assert a Warning was logged for the failed feed (use the existing
  test-logger pattern in the suite).
- **All-success regression:** existing successful-collection assertions (ordering, dedupe, hints,
  metadata) still hold against the new `RssFeedReadResult`-returning fake reader.

Update the in-test fake `IRssFeedReader` to return `RssFeedReadResult` (helper factories for success and
each failure outcome).

---

## Constraints

- Target .NET 10; C# 14.
- Infrastructure-only: keep all HTTP/XML/Syndication code in Infrastructure (AD-5). `IRssFeedReader`,
  `RssFeedReadResult`, and `RssFeedReadOutcome` stay `internal`.
- Preserve provenance and determinism: successful-feed behaviour, ordering, dedupe, hints, and the XXE
  hardening are unchanged. Caller-requested cancellation still throws.
- No advice language in any log/detail string. Do not touch scoring, the report, the pipeline, DI, or
  the `IEvidenceCollector` Application contract.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `IRssFeedReader.ReadAsync` returns `RssFeedReadResult`; `HttpRssFeedReader` reports every failure
      mode (`HttpError`/`Unreachable`/`Timeout`/`Malformed`) instead of returning `[]`, and a quiet but
      valid feed returns `Success` with empty items.
- [ ] Caller-requested cancellation still throws `OperationCanceledException` (not a failure result).
- [ ] `RssPressReleaseCollector` logs a Warning per failed feed (name, URL, reason) and an aggregate
      "checked / failed / collected" summary; successful-feed behaviour, ordering, dedupe, and hints are
      unchanged.
- [ ] New/updated tests cover each outcome, the quiet-feed regression, cancellation, and per-feed
      failure logging; build/test green.
