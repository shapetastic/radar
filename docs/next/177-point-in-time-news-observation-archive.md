# Task: Point-in-time news observation archive and safe content reader

## Overview

Radar currently preserves enough third-party-news provenance to show that an article existed, but not enough
content to replay a semantic read:

- `NewsArticleItem` retains the Google News landing URL, full headline, publisher and publication time;
- `HttpNewsSearchReader` discards the RSS `<description>` and the `<source url>` attribute;
- `NewsAttentionCollector` persists the landing URL as `SourceUrl`, `metadata.url` and inside a synthesized
  `RawText`; and
- Radar never follows the landing URL or stores the publisher's article body.

Fetching whatever a saved URL returns months later and pretending it was known on the publication date would
introduce content drift, survivorship/availability bias and look-ahead. This slice creates the immutable
point-in-time archive that later semantic readers and backtests require. It also supplies a bounded, opt-in
publisher-content reader, but performs no AI assessment and reads no price; those are spec 179.

## Assignment

Worktree: any  
Dependencies: spec 169 merged (per-company `newssearch` coverage).  
Estimated time: ~1–2 days.

## 1. Acquisition only — no interpretation or score change

This slice records what text and provenance Radar actually observed, when it observed it. It does not decide
whether an article is bullish, bearish, risky or relevant to a thesis.

- No AI/model call.
- No price read or efficacy output.
- No new `Signal` or change to spec 70's Neutral `MediaAttention` behaviour.
- No score, label, strategy, report rank, fingerprint or AD-15/AD-16 boundary changes.
- No article body is fetched in the baseline live run; the safe reader is a reusable opt-in seam whose shipped
  allowlist is empty.

## 2. Do not mutate existing evidence identity

Do **not** add RSS descriptions or fetched bodies to `CollectedEvidence.RawText`, `EvidenceItem.RawText`,
`Title`, `Summary`, `ContentHash` or `MetadataJson`.

Spec 145 defines evidence identity from normalized title + body, and spec 70 creates one `MediaAttention`
signal per `NewsArticle` evidence item. Changing `RawText` would give a previously seen URL a second content
hash and could count one article twice. Updating metadata cannot repair that safely either: accrued raw
evidence is insert-only and an existing content hash correctly wins.

Create a separate observational archive:

```text
data/news-observations/
  observations/{yyyy}/{MM}/{observationId}.json
  batches/{asOfUtc-file-token}.json
  boundary.json
```

It is not an `IEvidenceRepository`, is never read by extraction/resolution/review/scoring, and is excluded from
`ScoringConfigVersion`, strategy identity and collection provenance. Existing news evidence and signals remain
byte-identical.

## 3. Capture the RSS payload Radar currently discards

Extend the typed news-search read:

`NewsArticleItem` additionally carries:

- exact `<description>` element content supplied by the feed, bounded to 16 KiB;
- a deterministic plain-text rendering of that description;
- the `<source url>` attribute as `PublisherSiteUrl` when it is an absolute HTTP(S) URL; and
- retrieval time from injected `TimeProvider`.

Rules:

- `DescriptionRaw` is the bounded provider payload. `DescriptionText` strips tags, removes script/style
  content, HTML-decodes and collapses whitespace through one shared deterministic helper.
- Truncation is explicit (`DescriptionTruncated=true`); never pass a prefix off as complete content.
- An absent description is `null`, not a headline copied into a second field.
- `PublisherSiteUrl` is publisher-site provenance, not a claimed canonical article URL. `<link>` remains the
  Google News landing URL.
- Existing relevance filtering and within-feed URL dedupe run unchanged. Only surviving articles become
  observations; off-topic search results are not archived against the company.
- Existing `CollectedEvidence` mapping, including `RawText` and metadata, is byte-identical.

Carry surviving observations as a trailing optional observational sidecar on `CollectionResult` (the same
compatibility shape as spec 169's `CompanyCoverage`) or through an equivalently explicit Application-owned
seam. Capture the sidecar per collector before `CollectionResultMerger` discards attribution. The collection
orchestration writes it even when the corresponding evidence is an accrued `AddIfNewAsync` duplicate:
evidence dedupe and observation capture answer different questions. `NewsAttentionCollector` must not reach
into a filesystem store directly.

## 4. Observation schema, identity and cross-partition dedupe

Persist `NewsObservationRecord` with at least:

```text
schemaVersion                 news-observation-v1
observationId
companyId / ticker
collector                     newssearch
queryPhrase / feedId / feedName
googleLandingUrl
publisher / publisherSiteUrl?
headline
descriptionRaw? / descriptionText? / descriptionTruncated
publishedAtUtc?
retrievedAtUtc
firstObservedAtUtc
payloadHash
captureMode                   ProspectiveRss | LegacyHeadlineOnly | RetrospectiveUrlFetch
articleFetch                  null or §6 result
```

`payloadHash` is SHA-256 over a versioned canonical encoding of the exact bounded provider fields, including
landing URL, headline, publisher and raw description. `observationId` is deterministic from normalized landing
URL + payload hash.

The path partition derives from the record's immutable **`firstObservedAtUtc`**. A path check alone is not a
dedupe mechanism: observing the same id in a later month would otherwise create a second file under a new
partition. Follow `FileRawEvidenceStore`'s spec-142 mechanism:

- lazily hydrate every observation file into a process-wide `observationId → canonical record/path` index;
- enumerate paths deterministically and keep the ordinal-first identical record if legacy duplicate files
  exist, while retaining/reporting the duplicate files;
- treat one id carrying a different payload hash as an unreadable/conflicting record, never as a dedupe;
- use atomic `TryAdd` plus `FileMode.CreateNew` so concurrent writers cannot overwrite; and
- consult the hydrated index before deriving/writing a new path, so identical observations dedupe across all
  year/month partitions and preserve the original earliest `firstObservedAtUtc`.

The same URL with changed provider content has a different payload hash/id and creates a later observation.
JSON ordering/formatting is invariant.

## 5. Batch manifest and prospective boundary

Each collection pass writes a batch manifest recording:

- batch id and exact pipeline as-of/run association;
- archive schema version;
- company/query and spec-169 `newssearch` coverage status;
- observations attempted, written, cross-run deduped and failed; and
- provider cap, malformed, unreachable and rate-limit outcomes.

A write error is logged as Warning and recorded. It does not abort company scoring, but makes that
company/run **unproven capture**, never a clean zero for a later semantic reader. Associate the manifest with
the resulting `PipelineRunRecord` by explicit batch id or exact run id; do not use a nearest-time join.

`boundary.json` is created once on the first successful post-spec-177 full-universe batch and never
overwritten. `firstProspectiveCaptureAsOfUtc` comes from that run, not a date guessed in this document. A
company-filtered collect pass may capture observations but cannot establish the whole-universe boundary.

## 6. Safe optional publisher-content reader

Add an `INewsArticleContentReader` behind configuration. Its shipped posture is disabled with an empty
allowlist. A later reader (spec 179/178) may invoke it only for a bounded candidate set.

Safety contract:

- `Radar:NewsResearch:ArticleFetch:Enabled=true` requires a non-empty exact/suffix domain allowlist; the
  allowlist is the operator's explicit assertion that retrieval/storage is permitted for those domains.
- No authentication, cookies, subscriptions, paywall bypass, browser automation or anti-bot circumvention.
- HTTP(S) only; reject user-info, loopback, private, link-local and other non-public destinations before every
  request and redirect. Disable automatic redirects and permit at most five explicit hops.
- Require a contact-bearing User-Agent, honor `robots.txt`, pace sequentially per host and cache robots
  decisions for the run.
- Bound timeout and response bytes; accept only supported textual content types.
- Use versioned deterministic visible-text extraction with script/style/navigation removed and a declared
  character cap.

Closed outcomes include:

```text
Fetched | DomainNotAllowed | RobotsDisallowed | UnsafeUrl | RedirectLimit |
UnresolvedLandingUrl | Paywalled | UnsupportedContentType | TooLarge | HttpError |
RateLimited | Timeout | ExtractionEmpty
```

Every attempt records actual retrieval time, public redirect hops, resolved publisher URL when known, HTTP
status, content type, truncation flag, extractor version and content hash. Persist extracted body text only
for an allowlisted source under the operator's storage permission. Otherwise preserve RSS text plus outcome;
do not retain a transient full body or imply that it was read.

Changing allowlist, extractor or fetch policy creates a new retrieval-policy identity and observation/content
version. It never edits an existing record.

## 7. Honest migration of existing URLs/headlines

Provide an idempotent migration over accrued raw `NewsArticle` evidence:

- copy preserved headline, publisher, Google URL, `PublishedAtUtc` and original `CollectedAtUtc` into
  `LegacyHeadlineOnly` observations;
- use original `CollectedAtUtc` as `firstObservedAtUtc` because that headline/URL really was persisted then;
- leave description/body null; and
- never rewrite source evidence.

An explicit `--retrospective-fetch` mode may revisit saved URLs through §6. Resulting text is
`RetrospectiveUrlFetch` with `retrievedAtUtc = actual retrieval time`. It never inherits publication or old
collection time as its knowledge cutoff. Disappeared, changed or inaccessible pages are durable outcomes.

Retrospectively fetched content cannot establish what was knowable historically; it is useful for prompt
development and source-availability measurement only. The three capture modes remain distinguishable in
every downstream query.

## 8. Configuration and shipped posture

Add a fail-closed `Radar:NewsResearch` block:

```json
{
  "CaptureRss": true,
  "ObservationDirectory": "data/news-observations",
  "ArticleFetch": {
    "Enabled": false,
    "AllowedDomains": []
  }
}
```

`run-radar.ps1` supplies the directory beneath its output root; no absolute machine path is committed.
Capture is independent of AI and enabled in live `default.json`. Unknown keys/invalid limits fail startup.
These are observation/safety controls, not score weights and are hashed into no scoring fingerprint.

## Files to inspect

- `src/Radar.Infrastructure/News/NewsArticleItem.cs`
- `src/Radar.Infrastructure/News/HttpNewsSearchReader.cs`
- `src/Radar.Infrastructure/News/NewsAttentionCollector.cs`
- `src/Radar.Application/Collectors/CollectionResult.cs`
- `src/Radar.Application/Pipeline/CollectionPass.cs`
- `src/Radar.Application/Pipeline/PipelineRunRecord.cs`
- `src/Radar.Infrastructure/FileSystem/FileRawEvidenceStore.cs` (hydrated-index/insert-only precedent)
- `src/Radar.Worker/RadarWorkerOptions.cs`
- `src/Radar.Worker/RadarWorkerServices.cs`
- `scripts/run-radar.ps1`
- `scripts/run-profiles/default.json`

## Tests

- RSS parsing preserves exact bounded description, deterministic plain text, truncation and source URL;
  absent/malformed fields degrade honestly.
- Existing `CollectedEvidence`/`EvidenceItem` fields and content hash remain byte-identical for old fixtures.
- An accrued evidence duplicate still reaches observation capture but creates no second evidence/signal.
- Same URL+payload re-observed in a later month resolves through the hydrated id index to the original record;
  no second partition file is written and earliest first-observed time survives.
- Changed payload creates a later record; concurrent writes never overwrite; id/hash conflicts fail closed.
- Batch failures/incomplete company coverage cannot represent clean capture.
- First successful full-universe prospective boundary is create-once.
- Redirect hops and destinations are validated; private/loopback/link-local/user-info/non-HTTP targets fail.
- Empty allowlist, robots denial, paywall, 429, timeout, oversize and unsupported content type produce exact
  outcomes and no stored body.
- Legacy migration is idempotent; retrospective fetch uses actual retrieval time and cannot backdate content.
- Architecture guard: no type in `Radar.Application.Scoring` or the evidence/signal pipeline references the
  news-observation archive/content reader.

Do not run tests concurrently with another agent's solution-wide test run. At handoff,
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass in one coordinated
session.

## Acceptance criteria

- [ ] Every relevance-kept Google News result can be replayed from immutable point-in-time headline/RSS text,
      with company, publisher, URLs, publication/retrieval times and payload identity.
- [ ] Existing news evidence, signals, scores, fingerprints and strategy identities are byte-identical.
- [ ] Observation identity dedupes across all date partitions through a hydrated index and preserves the
      earliest first-observed instant.
- [ ] Capture/coverage failures are durable and cannot become quiet or clean observations downstream.
- [ ] Full publisher text is fetched/stored only through the explicit safe allowlisted path.
- [ ] Existing headlines migrate honestly; later URL fetches are visibly retrospective and never backdated.
- [ ] No AI, price, efficacy or scoring integration is included; spec 179 owns the shadow reader/evaluator.
- [ ] Build and coordinated tests green.
