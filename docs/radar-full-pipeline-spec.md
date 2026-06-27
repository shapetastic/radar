# Project Radar - Full Pipeline Spec

## Purpose

Project Radar detects public companies whose business trajectory appears to be improving before that improvement becomes obvious to mainstream retail investors.

Radar is not a stock-picking oracle and must never present outputs as financial advice. Its role is to surface evidence-backed candidates for human investigation.

Core principle:

> Signals before stories. Evidence before opinions. AI assists. Humans decide.

---

## MVP Scope

The first version must be small and working. Do not build the full future platform yet.

MVP question:

> Which public companies show meaningful positive signal activity from press releases, company news, and simple market-attention data?

The MVP should support:

1. Ingest raw evidence from a small number of source types.
2. Store raw evidence with provenance.
3. Extract structured signals from the evidence.
4. Resolve signals to public companies where possible.
5. Score companies using explainable rules.
6. Produce a weekly Radar report.
7. Allow a human to review the evidence behind every score.

Out of scope for MVP:

- Automated trading.
- Portfolio allocation advice.
- Full SEC/RNS ingestion.
- LinkedIn scraping.
- Patent ingestion.
- Social media scraping.
- Complex vector search.
- Backtesting engine.
- Real-time alerts.

These can be added later.

---

## Technology Stack

- Target framework: `.NET 10`
- Language: C# 14
- Backend: ASP.NET Core / Worker Service
- Database: PostgreSQL
- Data access: Dapper
- Background jobs: Hosted services first; Hangfire or Quartz later
- AI abstraction: `Microsoft.Extensions.AI`
- Structured outputs: JSON schema / typed records
- Logging: Microsoft.Extensions.Logging
- Observability: OpenTelemetry later
- UI: Blazor or React later; console/markdown reports acceptable for MVP

All projects should target `net10.0`.

---

## Architecture

```text
Sources
  -> Collectors
  -> Raw Evidence Store
  -> Entity Resolver
  -> Signal Extractor
  -> Signal Reviewer
  -> Scoring Engine
  -> Weekly Report
  -> Human Review
```

The system must preserve provenance at every stage. A score without evidence is invalid.

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
    Scoring/
    Reporting/
    Ai/

  Radar.Infrastructure/
    Database/
    Repositories/
    Ai/
    Sources/

  Radar.Worker/
    Program.cs
    Jobs/

  Radar.Api/                 # optional after MVP foundation

  Radar.Web/                 # optional after API exists

tests/
  Radar.Domain.Tests/
  Radar.Application.Tests/
  Radar.Infrastructure.Tests/
```

Start with Domain, Application, Infrastructure, Worker, and tests. API/UI can wait.

---

## Core Concepts

### Evidence

Evidence is a raw piece of source material.

Examples:

- Press release
- Company blog post
- News article
- Earnings transcript excerpt
- Government contract notice
- Job advert

Evidence must store:

- source type
- source name
- URL or identifier
- publication date
- collected date
- raw text or normalized text
- content hash
- collection metadata

Evidence is immutable once stored. If re-collected, create a new evidence record only if content changed.

### Signal

A signal is a structured interpretation of evidence.

Examples:

- customer win
- strategic partnership
- executive hire
- hiring expansion
- guidance raise
- government contract
- patent activity
- insider buying
- product launch

Signals must link to evidence.

A signal has:

- type
- direction: positive / negative / neutral
- strength: 1-10
- novelty: 1-10
- confidence: 0-1
- company reference
- reason
- evidence reference

### Company

A company is a public entity that may be investable.

Company records include:

- legal/common name
- ticker
- exchange
- country
- sector
- industry
- market cap when available
- aliases

Entity resolution must be conservative. If uncertain, create an unresolved company mention rather than guessing.

### Score

A company score is a snapshot derived from recent signals.

Initial MVP scores:

- `TrajectoryScore`: how much the company appears to be improving
- `AttentionScore`: how widely noticed the company appears to be
- `OpportunityScore`: trajectory adjusted by attention and evidence confidence
- `EvidenceConfidence`: how strong and diverse the supporting evidence is
- `SignalVelocity`: recent acceleration in signals

Scores must be explainable and reproducible from stored signals.

---

## MVP Signal Types

Start with these only:

1. `CustomerWin`
   - Named customer, contract, deployment, expansion.
2. `StrategicPartnership`
   - Named partnership with credible partner.
3. `ExecutiveHire`
   - Senior hire from notable organisation.
4. `ProductLaunch`
   - New commercially relevant product/platform.
5. `CapitalRaise`
   - Funding or debt raise, positive or negative depending context.
6. `GuidanceChange`
   - Raised or reduced guidance.

Avoid social signals in the first implementation. Add later once evidence quality is strong.

---

## AI Usage

Use AI only behind provider-independent application interfaces.

No class outside the AI infrastructure should call a specific provider SDK directly.

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

AI outputs must be typed records and validated before persistence.

If AI confidence is low, persist the evidence but do not create high-confidence signals.

---

## Pipeline Stages

### Stage 1 - Source Collection

Input: configured source definitions.

Output: raw evidence records.

MVP source options:

- manual URL input
- RSS feed collector
- local test file collector

The MVP should include at least one deterministic local/test collector so the pipeline can run in tests without the internet.

### Stage 2 - Evidence Normalization

Normalize raw material into text suitable for extraction.

Responsibilities:

- remove obvious boilerplate
- preserve title and date
- preserve source URL
- compute content hash
- reject duplicates

### Stage 3 - Entity Resolution

Detect company mentions and resolve to known company records.

MVP approach:

- start with a seed company table/watch universe
- alias matching
- conservative matching only
- unresolved mentions stored separately

Do not hallucinate tickers.

### Stage 4 - Signal Extraction

Use AI structured output to extract candidate signals from evidence.

The extractor must produce:

- signal type
- company mention
- direction
- strength
- novelty estimate
- confidence
- supporting excerpt
- reasoning

### Stage 5 - Signal Review

Apply deterministic and AI-assisted checks:

- Is this repeated PR?
- Is the source primary or secondary?
- Is the signal material?
- Is the company match reliable?
- Is the signal hype rather than evidence?

For MVP, implement deterministic checks first and leave AI reviewer as interface/stub if necessary.

### Stage 6 - Scoring

Calculate company scores from recent signals.

MVP scoring can be simple and explainable:

```text
TrajectoryScore = weighted average of recent positive/negative signal strength
SignalVelocity = count and strength acceleration over 30 days vs previous 30 days
EvidenceConfidence = confidence adjusted by source diversity and primary-source weight
AttentionScore = article/source count or explicit attention signals
OpportunityScore = TrajectoryScore + EvidenceConfidence - AttentionPenalty
```

The exact formula should be simple, visible, and versioned.

### Stage 7 - Weekly Report

Generate a markdown report:

- Top improving companies
- New signals
- Highest opportunity score
- Signals needing review
- Companies with deteriorating signals
- Evidence links for each recommendation to investigate

The report must include disclaimers:

- Not financial advice.
- For research only.
- Human review required.

---

## Data Persistence Rules

- Never overwrite raw evidence.
- Signals must reference evidence IDs.
- Scores must reference scoring version.
- Reports must reference score snapshot IDs.
- Store timestamps in UTC.
- Use database migrations if available; otherwise clearly version schema SQL.

---

## Backtesting and Time Travel

Not required in MVP, but design for it.

Do not mutate historical signals or scores in a way that prevents replay.

Later goal:

> Show what Radar knew on a given date without hindsight contamination.

---

## Human Review Workflow

Radar should not say “buy”.

Allowed labels:

- Investigate
- Watch
- Ignore
- Needs more evidence
- Thesis improving
- Thesis deteriorating

Human review should be recorded separately from system scores.

---

## Implementation Approach

The work planner must split this master spec into small implementation specs of roughly 1-2 hours.

Suggested first chunks:

1. Solution skeleton targeting .NET 10.
2. Domain model records for Company, Evidence, Signal, Score, Report.
3. PostgreSQL schema and repositories for evidence/signals.
4. Local file collector for deterministic test evidence.
5. Signal extraction interface and fake extractor.
6. Simple scoring engine.
7. Markdown weekly report generator.
8. Replace fake extractor with Microsoft.Extensions.AI implementation.

Every task must leave the solution buildable and testable.

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

---

## Acceptance Criteria for MVP

- [ ] Solution builds on .NET 10.
- [ ] Evidence can be ingested from at least one deterministic source.
- [ ] Evidence is stored with provenance.
- [ ] Signals can be extracted or simulated with typed outputs.
- [ ] Signals link back to evidence.
- [ ] Companies can be scored from signals.
- [ ] Weekly markdown report is generated.
- [ ] Every reported company includes evidence references.
- [ ] Tests cover core scoring and extraction validation.
- [ ] No provider-specific AI SDK leaks outside infrastructure.
