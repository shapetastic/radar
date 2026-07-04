# Task: Trunk cleanup — one shared `query=<phrase>&ticker=<TICKER>` feed-token parser (`QueryFeedTarget`) for the two attention collectors

## Overview

This is a **pure cleanup / convergence slice**. It converts the single MEDIUM finding (**M-1**) from the
`radar-architecture-reviewer` checkpoint on the trunk at commit `299794d` (main), after specs 80/81/82
merged. Verdict CLEANUP: no HIGH, and this M-1 is the only finding NOT already covered by the decisions
ledger AD-1…AD-13.

**The drift.** `src/Radar.Infrastructure/Gdelt/GdeltFeedTarget.cs` and
`src/Radar.Infrastructure/News/NewsFeedTarget.cs` are **byte-for-byte identical** records:

- identical `QueryKey` (`"query="`) and `TickerKey` (`"ticker="`) constants;
- an identical `Parse(string?)` body — same `query=`/`ticker=` key-ordering branches (query-only,
  query-before-ticker, ticker-before-query), the same first-`&`-boundary split logic that preserves the
  phrase's literal spaces, the same empty-ticker → null-ticker handling, and the same blank / missing-`query=`
  / empty-phrase → `null` returns;
- identical parsed shape `(string QueryPhrase, string? Ticker)`.

Only the **type name** and the **XML doc example ticker/phrase** differ (GDELT: `Mercury Systems`/`MRCY`;
News: `Rocket Lab`/`RKLB`). The duplication extends to the tests:
`tests/Radar.Infrastructure.Tests/Gdelt/GdeltFeedTargetTests.cs` and
`tests/Radar.Infrastructure.Tests/News/NewsFeedTargetTests.cs` share the **same six cases** (five `[Fact]`
+ one `[Theory]`), differing only in the ticker/phrase literals.

**Why it matters / how it compounds.** Spec 81's `NewsAttentionCollector` needed exactly the same
`query=<phrase>&ticker=<TICKER>` feed-token parser that GDELT (spec 67) already had, and **copied it
verbatim**. There is no compile-time link between the two copies. Two identical parsers WILL diverge under
any future token-format change (a new key, URL-decoding, a different boundary rule) — only one copy gets
fixed, and the other silently keeps the old behaviour. This is precisely the "copied primitive across two
sibling readers" shape the trunk has now converged twice before (spec 76 evidence-metadata envelope reader,
spec 77 SEC EDGAR URL helper).

**The fix (recommended by the reviewer — smallest convergence).** Extract **one shared `internal`
record** — `QueryFeedTarget` — into the existing shared collector namespace
`Radar.Infrastructure.Sources` (the natural home alongside the existing shared `CollectorCompanyHints`,
which is `internal static` in `src/Radar.Infrastructure/Sources/CollectorCompanyHints.cs`). It carries the
single `Parse(string?)` and the parsed `(string QueryPhrase, string? Ticker)` shape. Route **both**
`GdeltNewsCollector` and `NewsAttentionCollector` through it, **DELETE** both per-provider copies
(`GdeltFeedTarget`, `NewsFeedTarget`), and **MERGE** the two test files into one `QueryFeedTargetTests`.

**There is NO behaviour change here.** The parsed `(QueryPhrase, Ticker)` for every token — key ordering,
whitespace trimming, empty-ticker handling, and the blank / missing-`query=` / empty-phrase → `null`
returns — must be **byte-identical** before and after this slice. The two collectors' emitted queries and
their client-side relevance decisions are unchanged. This is **Infrastructure-only plumbing** and does
**NOT** change scoring output, so per **AD-10** it does **NOT** bump `ScoringEngine.ScoringConfigVersion`
(it stays at its current value) — stated explicitly so the implementer does not wonder.

---

## Assignment

Worktree: any
Dependencies: spec 67 (`GdeltNewsCollector` / `GdeltFeedTarget`) and spec 81 (`NewsAttentionCollector` /
`NewsFeedTarget`) — both merged.
Conflicts with: touches both attention collectors (`GdeltNewsCollector`, `NewsAttentionCollector`), adds a
new shared `Radar.Infrastructure.Sources` record, deletes both per-provider feed-target records, and merges
their two test files into one. **Must NOT run in parallel with any GDELT / news-attention collector slice
or any `Radar.Infrastructure.Sources` change — sequence it.**
Estimated time: ~1–1.5 h

---

## Project structure changes

```text
src/Radar.Infrastructure/Sources/
  QueryFeedTarget.cs                                  # NEW: single shared feed-token parser + parsed shape
  CollectorCompanyHints.cs                            # UNCHANGED (shown for context — the sibling shared helper)

src/Radar.Infrastructure/Gdelt/
  GdeltFeedTarget.cs                                  # DELETED (copy 1)
  GdeltNewsCollector.cs                               # MODIFIED: uses QueryFeedTarget (Parse / QueryPhrase / Ticker)

src/Radar.Infrastructure/News/
  NewsFeedTarget.cs                                   # DELETED (copy 2)
  NewsAttentionCollector.cs                           # MODIFIED: uses QueryFeedTarget (Parse / QueryPhrase / Ticker)

tests/Radar.Infrastructure.Tests/Sources/
  QueryFeedTargetTests.cs                             # NEW: the merged, deduplicated parser tests

tests/Radar.Infrastructure.Tests/Gdelt/
  GdeltFeedTargetTests.cs                             # DELETED (merged into QueryFeedTargetTests)

tests/Radar.Infrastructure.Tests/News/
  NewsFeedTargetTests.cs                              # DELETED (merged into QueryFeedTargetTests)
```

`Radar.Domain` and `Radar.Application` are unchanged. No DB (AD-8). No new package references.

> Before choosing the final type name / namespace, **confirm the current shape and home of
> `CollectorCompanyHints`** — it is `internal static class CollectorCompanyHints` in namespace
> `Radar.Infrastructure.Sources` (`src/Radar.Infrastructure/Sources/CollectorCompanyHints.cs`). Match that
> maintainer convention: `internal`, in `Radar.Infrastructure.Sources`. `QueryFeedTarget` is a record (it
> carries the parsed `(QueryPhrase, Ticker)` shape) rather than a static class, but it lives in the same
> shared namespace for the same reason.

---

## Implementation details

### 1 — Add the shared `QueryFeedTarget` record (Infrastructure/Sources)

Add `src/Radar.Infrastructure/Sources/QueryFeedTarget.cs` in namespace `Radar.Infrastructure.Sources`, as
`internal sealed record QueryFeedTarget(string QueryPhrase, string? Ticker)`. Move the **verbatim** parse
body from the two identical copies (they agree byte-for-byte, so there is a single canonical body). Suggested
surface (the shape and `Parse` signature are load-bearing — both collectors call `Parse(feed.Url)` and read
`.QueryPhrase` / `.Ticker`):

```csharp
namespace Radar.Infrastructure.Sources;

/// <summary>
/// The per-company inputs a query-driven attention feed carries in its single <c>Url</c> field, parsed
/// from the documented token <c>query=&lt;company phrase&gt;&amp;ticker=&lt;TICKER&gt;</c>
/// (e.g. <c>query=Mercury Systems&amp;ticker=MRCY</c>). Shared by the GDELT and Google-News attention
/// collectors — the same token shape both readers consume — so the shared
/// <see cref="Radar.Domain.Companies.CompanySourceFeed"/> record stays unchanged. <see cref="QueryPhrase"/>
/// is the precise company name sent to the search API; <see cref="Ticker"/> is an optional explicit ticker
/// token the collector also matches against in its client-side title relevance filter (phrase search has no
/// exact-entity key). An unparsable/empty token — or one missing the required <c>query=</c> key — yields
/// <see langword="null"/> so the collector can degrade it to a source failure rather than throwing.
/// </summary>
internal sealed record QueryFeedTarget(string QueryPhrase, string? Ticker)
{
    private const string QueryKey = "query=";
    private const string TickerKey = "ticker=";

    /// <summary>
    /// Parses a <c>query=...&amp;ticker=...</c> token. Robust to key ordering and surrounding whitespace;
    /// the phrase's literal spaces are preserved (the token is NOT URL-decoded). The <c>ticker=</c> key is
    /// optional — a bare <c>query=&lt;phrase&gt;</c> token parses with a null ticker. Returns
    /// <see langword="null"/> when the token is blank, the <c>query=</c> key is missing, or the phrase is empty.
    /// </summary>
    public static QueryFeedTarget? Parse(string? token) { /* the verbatim shared body */ }
}
```

Implementation notes — the body is the **exact** union (which is byte-identical, so "union" = "the one
body"):

- `string.IsNullOrWhiteSpace(token)` → `null`; then `token.Trim()`.
- Mandatory `query=` key: `IndexOf(QueryKey, StringComparison.Ordinal)` < 0 → `null`.
- Optional `ticker=` key: `IndexOf(TickerKey, StringComparison.Ordinal)`.
- Three branches, preserved exactly: (a) ticker key absent → phrase is everything after `query=`; (b)
  `query` before `ticker` → split the phrase on the FIRST `&` at/after the phrase start, requiring
  `0 <= boundary < tickerKeyIndex` (else `null`), ticker is everything after `ticker=`; (c) `ticker` before
  `query` → split the ticker on the FIRST `&` at/after the ticker start, requiring
  `0 <= boundary < queryKeyIndex` (else `null`), phrase is everything after `query=`. All uses `.Trim()` on
  the extracted spans exactly as today.
- `string.IsNullOrEmpty(phrase)` → `null`.
- `string.IsNullOrEmpty(ticker)` → treat as `null` ticker (NOT a hard failure).
- Return `new QueryFeedTarget(phrase, ticker)`.

> **Layering (AD-5) check.** `QueryFeedTarget` is a pure `internal` Infrastructure type (BCL strings only,
> no HTTP, no provider SDK). It lives in `Radar.Infrastructure.Sources` alongside `CollectorCompanyHints`.
> No Application/Domain/Worker type references it. No new package reference. Domain stays pure.

### 2 — `GdeltNewsCollector` (Infrastructure/Gdelt) consumes the shared record

- Replace `GdeltFeedTarget.Parse(feed.Url)` (~line 83) with `QueryFeedTarget.Parse(feed.Url)`.
- Change `BuildQuery(GdeltFeedTarget target)` (~line 172) and `IsRelevant(string? title, GdeltFeedTarget
  target)` (~line 190) parameter types to `QueryFeedTarget`. Their **bodies are unchanged** — they read only
  `target.QueryPhrase` and `target.Ticker`, which the shared record exposes identically.
- **Do NOT touch `GdeltNewsCollector.IsRelevant`'s body** beyond the parameter type. GDELT titles have no
  Google-News `" - Publisher"` suffix, so this collector deliberately does **not** strip one — see the
  out-of-scope guard (L-1) below. The shared work is the **feed-token parser only**, not the `IsRelevant`
  logic.
- Add `using Radar.Infrastructure.Sources;`. Delete `src/Radar.Infrastructure/Gdelt/GdeltFeedTarget.cs`.

### 3 — `NewsAttentionCollector` (Infrastructure/News) consumes the shared record

- Replace `NewsFeedTarget.Parse(feed.Url)` (~line 87) with `QueryFeedTarget.Parse(feed.Url)`.
- Change `BuildQuery(NewsFeedTarget target)` (~line 177) and `IsRelevant(string? title, NewsFeedTarget
  target)` (~line 189) parameter types to `QueryFeedTarget`. Their **bodies are unchanged** — they read only
  `target.QueryPhrase` and `target.Ticker`.
- **Do NOT touch `NewsAttentionCollector.IsRelevant`'s body** beyond the parameter type. In particular the
  `StripPublisherSuffix(title)` call (~line 191) and the `StripPublisherSuffix` / `TitleSuffixSeparator`
  (`" - "`) members (~lines 38, 210–…) are a **deliberate per-source hook** and MUST remain — see L-1 below.
- Add `using Radar.Infrastructure.Sources;`. Delete `src/Radar.Infrastructure/News/NewsFeedTarget.cs`.

### 4 — Merge the two test files into one `QueryFeedTargetTests`

- Add `tests/Radar.Infrastructure.Tests/Sources/QueryFeedTargetTests.cs` in namespace
  `Radar.Infrastructure.Tests.Sources`, `using Radar.Infrastructure.Sources;`.
- Port the six shared cases **once** (they are identical between the two old files apart from literals):
  valid `query=<phrase>&ticker=<TICKER>` preserving the phrase's space and ticker; ticker-before-query key
  ordering; query-only → null ticker; empty ticker value → null ticker; surrounding whitespace trimmed; and
  the `[Theory]` malformed/missing-query → `null` set (`null`, `""`, `"   "`,
  `"https://example.com/rss"`, `"ticker=<T>"`, `"query="`, `"query=&ticker=<T>"`). Keep the two literal
  worlds represented in the merged file (e.g. keep at least one `Mercury Systems`/`MRCY` case and one
  `Rocket Lab`/`RKLB` case) so the merge does not silently drop the coverage either collector relied on;
  the parser is source-agnostic so a single set exercises both.
- **Delete** `tests/Radar.Infrastructure.Tests/Gdelt/GdeltFeedTargetTests.cs` and
  `tests/Radar.Infrastructure.Tests/News/NewsFeedTargetTests.cs` (do not leave them dormant — spec-checklist
  step 4).

---

## Tests

- **New — `QueryFeedTargetTests` (`tests/Radar.Infrastructure.Tests/Sources/`):** the merged, deduplicated
  parser tests described in step 4 (the six cases, once, retaining a literal from each former world).
- **Regression (must stay green — the proof the refactor is behaviour-preserving):** the existing
  `GdeltNewsCollector` and `NewsAttentionCollector` tests that exercise feed-token parsing and relevance
  filtering. These change **only** in that any reference to the renamed/moved type
  (`GdeltFeedTarget`/`NewsFeedTarget` → `QueryFeedTarget`) is updated; their assertions are unchanged. Most
  such tests drive the collectors through their public `CollectAsync` and never name the record directly —
  in that case they need **no** edit at all. Confirm the two collectors' relevance tests (including the
  News collector's `" - Publisher"`-suffix behaviour) stay green **unchanged**.

---

## Constraints

- Target `net10.0`, C# 14.
- **This is a CLEANUP slice — NO new feature behaviour and NO scoring-output change.** The parsed
  `(QueryPhrase, Ticker)` for every token, the two collectors' emitted queries, and their client-side
  relevance decisions must be **byte-identical** before and after. It therefore does **NOT** bump
  `ScoringEngine.ScoringConfigVersion` (stays at its current value) — no AD-10 obligation is triggered
  (Infrastructure plumbing only; no formula / extractor-rule / `ScoringOptions` change).
- **Share ONLY the feed-token parser.** Do NOT collapse the collector bodies or the two `IsRelevant`
  methods — the per-source `" - Publisher"` suffix strip is a deliberate difference and stays a per-collector
  hook (see L-1).
- **Delete both per-provider copies** (`GdeltFeedTarget`, `NewsFeedTarget`) and **merge** both test files —
  do not leave deprecated code dormant (spec-checklist step 4).
- **Layering (AD-5):** `QueryFeedTarget` stays `internal` in `Radar.Infrastructure.Sources` (BCL only, no
  provider SDK); Domain/Application/Worker are untouched. No new package references. Files-first (AD-8), no
  AI, no DB. No advice language; AD-9 labels unchanged.
- **Provenance preserved** — the evidence → signal → score chain is untouched; only the feed-token parsing
  plumbing is consolidated.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Out of scope / future (informational — do NOT plan or implement this round)

- **L-1 — the two collectors' `IsRelevant` methods differ DELIBERATELY.**
  `NewsAttentionCollector.IsRelevant` strips the Google News `" - Publisher"` title suffix (via
  `StripPublisherSuffix` / `TitleSuffixSeparator = " - "`) before matching, so an outlet name that happens
  to contain the ticker/phrase cannot produce a false match; `GdeltNewsCollector.IsRelevant` does **not**
  strip a suffix (GDELT titles carry no such suffix). This is intentional per-source behaviour. This
  cleanup shares **only** the feed-token parser, **NOT** the collector body / `IsRelevant`. The
  per-source suffix-strip must remain a per-collector hook and must **NOT** be collapsed. Recorded here as
  an informational out-of-scope note so the next planner has context; no work is planned for it.

---

## Acceptance criteria

- [ ] A single `internal sealed record QueryFeedTarget(string QueryPhrase, string? Ticker)` exists in
      `Radar.Infrastructure.Sources` (alongside `CollectorCompanyHints`), owning the one
      `Parse(string? token)` — byte-identical to the deleted copies' body (key-ordering branches, first-`&`
      boundary logic, whitespace trimming, empty-ticker → null, blank / missing-`query=` / empty-phrase →
      `null`).
- [ ] Both `GdeltFeedTarget` (`src/Radar.Infrastructure/Gdelt/GdeltFeedTarget.cs`) and `NewsFeedTarget`
      (`src/Radar.Infrastructure/News/NewsFeedTarget.cs`) are **DELETED** — neither collector retains its
      own copy.
- [ ] `GdeltNewsCollector` and `NewsAttentionCollector` both parse their feed token through
      `QueryFeedTarget.Parse(feed.Url)` and read `.QueryPhrase` / `.Ticker`; their `BuildQuery` /
      `IsRelevant` signatures take `QueryFeedTarget`. No collector body logic changes.
- [ ] **L-1 preserved:** `NewsAttentionCollector.IsRelevant` still strips the `" - Publisher"` suffix and
      `GdeltNewsCollector.IsRelevant` still does not — the per-source suffix-strip is NOT collapsed.
- [ ] The two per-provider test files (`GdeltFeedTargetTests`, `NewsFeedTargetTests`) are **DELETED** and
      their six cases are merged **once** into `QueryFeedTargetTests`
      (`tests/Radar.Infrastructure.Tests/Sources/`), retaining a literal from each former world.
- [ ] **Behaviour byte-identical:** the parsed `(QueryPhrase, Ticker)` for every token and the two
      collectors' emitted queries / relevance decisions are unchanged — proven by the existing GDELT and
      News collector tests passing (references to the renamed type updated only where a test names it
      directly).
- [ ] `ScoringEngine.ScoringConfigVersion` is **NOT** bumped (no scoring-output change); no AD-10
      obligation applies. Layering (AD-5), determinism, and provenance preserved.
- [ ] `dotnet build` / `dotnet test` on `Radar.sln -c Release` green.
