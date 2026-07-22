# Project Radar - Full Pipeline Spec

## Purpose

Project Radar is a local-first investment discovery assistant for finding public companies that may be becoming more interesting before they become obvious to mainstream retail investors.

Radar is built for one first user: **Dean**.

The first goal is not to build a production SaaS platform, a trading bot, or a full institutional research terminal. The first goal is to prove this loop works:

```text
Automated collectors
  -> raw evidence
  -> interpreted signals
  -> company scoring
  -> weekly research report
  -> human investigation
```

Radar must never present outputs as financial advice. It should surface evidence-backed candidates for human review.

Core principle:

> Signals before stories. Evidence before opinions. AI assists. Humans decide.

---

## MVP Direction

The MVP is **collector-driven**.

Radar should not depend on manually copying files into an inbox. Manual files are useful only for tests and debugging. The real MVP should automatically collect public evidence from a small set of trusted sources, normalize it, extract signals, and generate a local markdown report.

The first useful question is:

> Which companies became more interesting this week, and why?

A good Radar v1 result is not:

> Buy this stock.

A good Radar v1 result is:

> Investigate this company because Radar found three new pieces of evidence: a named customer win, a strategic partnership, and increasing commercial activity.

---

## MVP Scope

The first version must be small, working, and locally runnable.

MVP must support:

1. A local watch universe of companies to monitor.
2. Automated collection from at least one real-world source type.
3. Local raw evidence storage as files.
4. Evidence normalization and deduplication.
5. Conservative company/entity resolution using aliases.
6. Deterministic signal extraction for first proof-of-concept.
7. Explainable scoring from extracted signals.
8. Weekly markdown report output.
9. Evidence links/references behind every company surfaced.

MVP should deliberately **not** require:

- PostgreSQL.
- A web UI.
- Live social media scraping.
- LinkedIn scraping.
- Complex vector search.
- Automated trading.
- Portfolio advice.
- Backtesting.
- Full SEC/RNS ingestion.
- Production-grade resilience.

These can be added later once the evidence -> signal -> report loop proves useful.

---

## MVP Success Criteria

Radar v1 is successful if it can run locally and produce a weekly report containing:

- 5-10 companies worth looking at.
- A clear explanation for why each company surfaced.
- Evidence references for each signal.
- A suggested human action: `Investigate`, `Watch`, `Ignore`, or `Needs More Evidence`.
- No unsupported recommendations.

The test is practical:

> Did Radar show Dean at least one company or development he would not otherwise have noticed?

---

## Technology Stack

- Target framework: `.NET 10`
- Language: C# 14
- Runtime model: local-first console/worker application
- Backend style: .NET Worker Service / console runner first
- Storage for MVP: file-based JSON/Markdown under `data/`
- Database: optional later; do not require it for MVP
- Data access: file repositories first; Dapper/PostgreSQL later
- Background jobs: manual local run first; hosted services later
- AI abstraction: `Microsoft.Extensions.AI` when AI is introduced
- Structured outputs: typed records / JSON schema
- Logging: `Microsoft.Extensions.Logging`
- UI: markdown report first; web UI later

All projects should target `net10.0`.

---

## Architecture

### High-Level Flow

```text
Watch Universe
  -> Collectors
  -> Raw Evidence Files
  -> Evidence Normalizer
  -> Company Resolver
  -> Signal Extractor
  -> Signal Reviewer
  -> Scoring Engine
  -> Weekly Report
  -> Human Review
```

### Important Separation

Collectors are responsible for **finding and capturing evidence**.

Radar is responsible for **interpreting evidence**.

Do not make the signal extraction pipeline responsible for finding content on the internet. That belongs to collectors.

---

## Project Structure

Recommended initial solution:

```text
Radar.sln

src/
  Radar.Domain/
    Companies/
    Evidence/
    Signals/
    Scoring/
    Reports/

  Radar.Application/
    Collectors/
    EntityResolution/
    SignalExtraction/
    SignalReview/
    Scoring/
    Reporting/
    Ai/

  Radar.Infrastructure/
    FileSystem/
    Sources/
    Rss/
    Ai/

  Radar.Worker/
    Program.cs
    Jobs/

tests/
  Radar.Domain.Tests/
  Radar.Application.Tests/
  Radar.Infrastructure.Tests/
  Radar.Worker.Tests/

data/
  watch-universe/
    companies.json

  evidence/
    raw/
      press-releases/
      news/
      test/
    normalized/

  signals/

  scores/

  reports/
    weekly/
```

API/UI projects can wait.

---

## Core Philosophy for Implementation

Radar should be useful before it is beautiful.

For the MVP:

- Prefer files over databases.
- Prefer deterministic rules over premature LLM calls.
- Prefer a working weekly report over a dashboard.
- Prefer one reliable collector over many weak collectors.
- Prefer explainable simple scoring over clever scoring.
- Prefer small specs that leave the app runnable.

---

## Watch Universe

The watch universe tells Radar which companies to monitor first.

This is intentionally narrow for the MVP. Radar is not yet scanning every listed company in the world. It is monitoring a curated set of companies and learning how to collect and interpret evidence.

Example:

```json
[
  {
    "ticker": "RKLB",
    "name": "Rocket Lab USA",
    "exchange": "NASDAQ",
    "country": "US",
    "aliases": ["Rocket Lab", "Rocket Lab USA", "Rocket Lab USA Inc"],
    "sourceFeeds": [
      {
        "type": "rss",
        "name": "Rocket Lab Investor News",
        "url": "https://example.com/rss"
      }
    ],
    "themes": ["space", "defence", "launch infrastructure"]
  }
]
```

The watch universe should be easy to edit by hand.

MVP does not need a UI for managing this.

---

## Collectors

### Purpose

Collectors gather raw public evidence and write it to local storage.

They do not score companies.
They do not decide whether a company is investable.
They do not create final recommendations.

They simply answer:

> What new public information did we find?

### Collector Interface

Recommended application interface:

```csharp
public interface IEvidenceCollector
{
    string CollectorName { get; }
    string SourceType { get; }

    Task<IReadOnlyCollection<CollectedEvidence>> CollectAsync(
        CollectionContext context,
        CancellationToken cancellationToken);
}
```

### Collected Evidence

Collectors return normalized collection results before persistence:

```csharp
public sealed record CollectedEvidence(
    string SourceType,
    string SourceName,
    string? SourceUrl,
    string Title,
    string RawText,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CollectedAt,
    IReadOnlyDictionary<string, string> Metadata);
```

### MVP Collector

The first real collector should be:

## RSS Press Release Collector

Responsibilities:

1. Read RSS source definitions from the watch universe.
2. Fetch RSS feeds.
3. Convert each new feed item into raw evidence.
4. Preserve title, URL, publication date, summary/body, source feed name, and company hint.
5. Write evidence into `data/evidence/raw/press-releases/`.
6. Avoid duplicates using URL and content hash.
7. Log what it collected.

This is the first real-world test of Radar.

### Local Test Collector

Keep or add a local deterministic collector for tests.

This can read from:

```text
data/evidence/raw/test/
```

or embedded test fixtures.

It exists so integration tests do not require the internet.

---

## Evidence Storage

MVP storage is file-based.

A raw evidence file should be written as JSON:

```text
data/evidence/raw/{sourceType}/{yyyy}/{MM}/{contentHash}.json
```

Example:

```text
data/evidence/raw/press-releases/2026/01/6AF3E9.json
```

### Raw Evidence Schema

```json
{
  "evidenceId": "ev_20260110_6af3e9",
  "sourceType": "press_release",
  "sourceName": "Rocket Lab Investor News",
  "sourceUrl": "https://...",
  "title": "Rocket Lab Announces New Multi-Launch Agreement",
  "rawText": "...",
  "normalizedText": null,
  "publishedAt": "2026-01-10T08:00:00Z",
  "collectedAt": "2026-01-10T10:15:00Z",
  "contentHash": "6AF3E9...",
  "companyHints": ["RKLB"],
  "metadata": {
    "rssFeedUrl": "https://...",
    "rssItemId": "..."
  }
}
```

### Storage Rules

- Do not overwrite raw evidence.
- If the same URL/content appears again, skip it.
- If same URL but changed content appears, write a new version with a new hash.
- Every downstream object must reference the evidence ID.

---

## Evidence Normalization

Normalization prepares evidence for signal extraction.

Responsibilities:

- Trim obvious RSS boilerplate.
- Preserve title.
- Preserve source URL.
- Preserve published date.
- Remove duplicate whitespace.
- Keep enough surrounding text for excerpts.

MVP normalization should be simple.

Do not overfit boilerplate removal early.

---

## Company Resolution

MVP company resolution should be conservative and alias-driven.

Inputs:

- watch universe company name
- aliases
- ticker
- company hints from collector
- evidence title/text

Rules:

1. If a collector collected evidence from a company-specific feed, use that company as a high-confidence hint.
2. If an alias appears in title or body, resolve to that company.
3. If multiple companies match, mark as ambiguous.
4. If no company matches, store unresolved mention.
5. Never hallucinate tickers.

MVP can avoid global company databases.

---

## Signal Extraction

Signal extraction turns evidence into structured business signals.

For MVP, start deterministic.

Do not wait for LLM integration before proving the pipeline.

### MVP Signal Types

Start with:

1. `CustomerWin`
   - Named customer, contract, deployment, expansion, renewal.
2. `StrategicPartnership`
   - Named partnership, collaboration, integration, ecosystem relationship.
3. `GovernmentContract`
   - Government, defence, NASA, MOD, DoD, public procurement, grant.
4. `ProductLaunch`
   - Commercially relevant new product, platform, capability, service.
5. `CapitalRaise`
   - Equity raise, debt facility, convertible note, refinancing. Can be positive or negative depending context.
6. `GuidanceChange`
   - Raised/reduced outlook, backlog increase, major revenue expectation change.

Later signal types:

- ExecutiveHire
- HiringMomentum
- InsiderBuying
- InstitutionalOwnership
- PatentActivity
- DeveloperAdoption
- ConferenceMentions
- SocialAttention

### Deterministic Extractor

First extractor can be keyword/rule based.

Examples:

- Words like `contract`, `selected by`, `awarded`, `multi-year agreement` -> possible `CustomerWin` or `GovernmentContract`.
- Words like `partnership`, `collaboration`, `integrates with` -> possible `StrategicPartnership`.
- Words like `launches`, `introduces`, `new platform` -> possible `ProductLaunch`.
- Words like `raises`, `offering`, `credit facility`, `debt financing` -> possible `CapitalRaise`.

The deterministic extractor should produce low/medium confidence signals and supporting excerpts.

### Future AI Extractor

When introduced, AI extraction must sit behind an application abstraction and use typed outputs.

No provider SDK should leak into application code.

Recommended interface:

```csharp
public interface IAiStructuredOutputService
{
    Task<TOutput> GenerateAsync<TOutput>(
        string systemPrompt,
        object input,
        CancellationToken cancellationToken);
}
```

Implementation can use `Microsoft.Extensions.AI`.

---

## Signal Model

A signal is a structured interpretation of evidence.

A signal must include:

- signal ID
- evidence ID
- company ID/ticker if resolved
- signal type
- direction: positive / negative / neutral
- strength: 1-10
- confidence: 0-1
- novelty: 1-10 where possible
- supporting excerpt
- reason
- extractor name/version
- created timestamp

Example:

```json
{
  "signalId": "sig_20260110_001",
  "evidenceId": "ev_20260110_6af3e9",
  "companyTicker": "RKLB",
  "signalType": "CustomerWin",
  "direction": "Positive",
  "strength": 7,
  "confidence": 0.74,
  "novelty": 5,
  "supportingExcerpt": "Rocket Lab announced a new multi-launch agreement with...",
  "reason": "Named commercial agreement suggests additional customer traction.",
  "extractor": "DeterministicKeywordSignalExtractorV1",
  "createdAt": "2026-01-10T10:20:00Z"
}
```

---

## Signal Review

MVP signal review should be simple and deterministic.

Checks:

- Does the signal have evidence?
- Does the signal have a resolved company or explicit unresolved status?
- Is the supporting excerpt present?
- Is the strength within range?
- Is the confidence within range?
- Is this clearly promotional boilerplate with no specific event?

Allowed statuses:

- `Accepted`
- `NeedsReview`
- `Rejected`

For MVP, do not block the whole report because some signals need review. Include them separately.

---

## Scoring

Scoring should be explainable and deliberately simple.

Initial scores:

- `TrajectoryScore`: positive/negative signal strength over the current window.
- `SignalVelocity`: number and strength of recent signals compared with a prior window.
- `EvidenceConfidence`: source quality and signal confidence.
- `AttentionScore`: simple count of evidence items for now.
- `OpportunityScore`: trajectory adjusted by attention and confidence.

### MVP Formula Guidance

Keep formula visible and versioned.

Example:

```text
TrajectoryScore = weighted average of accepted signal strength
SignalVelocity = current 30-day signal score - previous 30-day signal score
EvidenceConfidence = average confidence adjusted by source count
OpportunityScore = TrajectoryScore + EvidenceConfidence + SignalVelocity - AttentionPenalty
```

The exact formula can evolve, but every score must explain which signals contributed.

---

## Weekly Report

The weekly report is the MVP user interface.

Output path:

```text
data/reports/weekly/radar-weekly-{yyyy-MM-dd}.md
```

The report should include:

```markdown
# Radar Weekly Report

Generated: 2026-01-10

> Research assistant output only. Not financial advice. Human review required.

## Top Companies To Investigate

### 1. Rocket Lab USA (RKLB)

Action: Investigate
Opportunity Score: 78
Trajectory Score: 72
Evidence Confidence: 81

Why Radar noticed:
- CustomerWin: Multi-launch agreement announced.
- GovernmentContract: NASA-related contract evidence found.

Evidence:
- [Rocket Lab Announces ...](source-url-or-local-evidence-id)

Notes:
- Needs human review for contract size and financial materiality.

## Watch

## Ignore / Low Signal

## Signals Needing Review
```

Allowed action labels:

- `Investigate`
- `Watch`
- `Ignore`
- `Needs More Evidence`

Forbidden labels:

- `Buy`
- `Sell`
- `Strong Buy`
- `Price Target`

---

## Human Review

Human review can be lightweight for MVP.

A simple markdown or JSON note is enough.

Example:

```json
{
  "ticker": "RKLB",
  "reviewedAt": "2026-01-10T18:00:00Z",
  "decision": "Investigate",
  "notes": "Worth reviewing latest investor presentation and balance sheet."
}
```

Do not build a full review UI yet.

---

## Data Persistence Roadmap

MVP uses files.

PostgreSQL comes later when:

- evidence volume becomes annoying in files;
- query/report performance suffers;
- the data model stabilizes;
- UI/API work begins.

When database persistence is introduced, preserve the same conceptual model:

- Evidence
- Signals
- Scores
- Reports
- Reviews

Do not let database design drive the MVP.

---

## AI Usage Roadmap

AI is not required for the first proof.

Recommended progression:

### Level 1 - Deterministic

- keyword extraction
- simple rules
- file-based evidence
- explainable scoring

### Level 2 - AI-Assisted Extraction

- LLM reads evidence and proposes signals
- typed structured output
- deterministic validation
- confidence thresholds

### Level 3 - Review Agents

- signal reviewer
- skeptic reviewer
- thesis builder
- thesis challenger

### Level 4 - Discovery Agents

- broader search/discovery
- related company discovery
- cross-source relationship building

Do not start at Level 4.

---

## Collector Roadmap

Build collectors incrementally.

### First Collector

1. RSS Press Release Collector

### Next Candidates

2. SEC company filings collector.
3. UK RNS collector.
4. Insider transaction collector.
5. Government contract collector.
6. Company careers/hiring collector.
7. Conference agenda collector.
8. Article/media collector.
9. Patent collector.
10. Social attention collector.

Only add a collector when the current pipeline can turn its evidence into useful report output.

### Additional Candidate Collectors (evaluated 2026-07-22)

Beyond the original ten, the following official / structured / free-feed sources fit Radar's
"signals before stories, evidence before opinions" thesis — they are **leading**, machine-readable,
and resolvable to a US registrant. Listed strongest-fit first for the current US-registrant,
EDGAR-resolvable, hardware- and health-skewed watch universe. Each would follow the proven
collector template (specs 103/127): an Infrastructure reader seam, a feed-token parser, one
deterministic extractor rule, a new `SignalType`/`EvidenceSourceType`, opt-in OFF by default, and a
`RuleSetVersion` + fingerprint re-pin.

- **FCC Equipment Authorization** — a company must obtain FCC certification *before* it may sell a
  wireless/electronic device in the US, so the authorization record leads product shipment by weeks
  to months. The sharpest "before the market notices" signal for the hardware names (Rocket Lab,
  Mercury Systems, Bel Fuse, Energy Recovery). Free public grantee database, resolvable by grantee
  name. Positive, count/new-grant based (same anti-misfire shape as the patent collector).
- **FDA / openFDA** — drug & device approvals, 510(k) clearances, PMA, and recalls. Free structured
  API, clear valence (approval = Positive, recall = Negative). High leading value for medtech in the
  universe (AxoGen, TransMedics) and future health names.
- **USPTO Trademark filings** — new brand/product names filed *before* launch; sits closer to
  go-to-market than patents and reuses almost all of the patent collector's plumbing. Free TSDR/bulk
  API, Neutral count-based rule.
- **WARN Act layoff notices** — the downside counterpart Radar mostly lacks: a leading *contraction*
  signal feeding "Thesis deteriorating," adding symmetry to a positive-signal-heavy pipeline. Lower
  feasibility — state-fragmented (~50 sources, uneven formats).
- **DOL H-1B / LCA disclosure data** — hiring intent + role + wage as structured bulk data (unlike
  careers-page scraping); a stronger cousin of the careers/hiring collector.
- **ClinicalTrials.gov** — trial phase-transitions/results; leading for health names, free API. Worth
  building once the universe carries more biotech.
- **GitHub / open-source activity** — developer-adoption velocity for software names; free API. Low
  fit while the universe is hardware/industrial-skewed, high fit if it adds SaaS.
- **SEC 13F institutional holdings** — smart-money accumulation, free from EDGAR, but 45-day lagged
  and closer to following the story than leading it.

**Deliberately excluded** (they cut against the philosophy and AD-14 — market opinion, not business
evidence): short interest, options flow, social/Reddit sentiment, price/volume momentum. Import
bill-of-lading demand data is conceptually strong but effectively paywalled (Panjiva/ImportGenius),
so feasibility rules it out.

Suggested expansion arc: **Patent (spec 127, queued) → FCC Equipment Authorization → FDA → USPTO
Trademark**, sequenced (not parallelized) because each bumps `RuleSetVersion` and re-pins the same
fingerprint.

---

## Work Planner Guidance

The work planner must treat this document as a reference/master spec.

It must not generate one giant implementation task.

It should create small implementation specs, each approximately 1-2 hours.

### Preferred Next Specs From Current Direction

The planner should prioritize collector-driven proof of concept work:

1. Collector abstraction and collection context.
2. Watch universe source feed configuration.
3. RSS press release collector.
4. Raw evidence file writer and deduplication.
5. Pipeline command to run collection -> extraction -> scoring -> report.
6. Report improvements to show collector evidence clearly.

### Planner Rules

- Keep Radar locally runnable after every task.
- Prefer file-based storage until the concept proves useful.
- Do not introduce PostgreSQL unless a spec explicitly requires it.
- Do not introduce a web UI yet.
- Do not introduce AI extraction until deterministic end-to-end collection works.
- Every generated spec must preserve evidence provenance.
- Every generated spec must include tests.
- Every generated spec must avoid trading advice wording.

---

## Non-Goals

Radar must not:

- Execute trades.
- Recommend position sizes as commands.
- Present speculative outputs as certainty.
- Hide evidence behind scores.
- Use black-box scoring with no explanation.
- Scrape sites in violation of terms.
- Store API keys or secrets in source control.
- Become a productized SaaS before it is useful locally.

---

## Acceptance Criteria for MVP

- [ ] Solution builds on .NET 10.
- [ ] A watch universe file defines companies and RSS feeds.
- [ ] RSS collector fetches at least one configured feed.
- [ ] Collector writes raw evidence files locally.
- [ ] Duplicate evidence is skipped by URL/hash.
- [ ] Evidence is normalized.
- [ ] Company resolver links evidence to watch universe companies conservatively.
- [ ] Deterministic extractor creates typed signals from evidence.
- [ ] Signals reference evidence IDs.
- [ ] Scores are produced from signals.
- [ ] Weekly markdown report is generated locally.
- [ ] Report includes evidence references for every surfaced company.
- [ ] Report uses only research labels: Investigate, Watch, Ignore, Needs More Evidence.
- [ ] Tests cover collector, evidence writing, duplicate handling, signal extraction, scoring, and report generation.
- [ ] The whole pipeline can run locally without a database.

---

## Final MVP Definition

Radar v1 is complete when Dean can:

1. Add companies and RSS feeds to a local watch universe file.
2. Run one local command.
3. Have Radar automatically collect new evidence.
4. See which watched companies produced meaningful signals.
5. Read the evidence behind each surfaced company.
6. Decide what to investigate next.

That is the product for now.

