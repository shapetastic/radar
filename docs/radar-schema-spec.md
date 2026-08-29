# Project Radar - Schema Spec

## Purpose

This document defines the initial domain and persistence schema for Project Radar.

The schema must support an evidence-first, replayable pipeline. It should be simple enough for MVP but extensible for future source types, agents, and scoring versions.

---

## Naming

Use PascalCase for C# records/classes and snake_case for PostgreSQL tables/columns.

All IDs should be UUIDs unless there is a strong reason otherwise.

All timestamps should be UTC.

---

## Domain Records

### Company

```csharp
public sealed record Company(
    Guid Id,
    string Name,
    string? LegalName,
    string? Ticker,
    string? Exchange,
    string? CountryCode,
    string? Sector,
    string? Industry,
    CompanyStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<string> Themes,
    FollowingTier FollowingTier = FollowingTier.Small);

// FollowingTier is a SCORING INPUT (the notedness discount). It is curated from
// following/coverage evidence ONLY and is NEVER derived from price, market cap or
// volume - see AD-14.
public enum FollowingTier { Small = 0, Mid, Large, Mega }
```

```csharp
public enum CompanyStatus
{
    Active,
    Delisted,
    WatchOnly,
    Unresolved
}
```

### CompanyAlias

```csharp
public sealed record CompanyAlias(
    Guid Id,
    Guid CompanyId,
    string Alias,
    string AliasType,
    DateTimeOffset CreatedAtUtc);
```

Alias types: `Name`, `FormerName`, `Ticker`, `Subsidiary`, `Brand`, `CommonMisspelling`.

---

## Evidence

### EvidenceItem

```csharp
public sealed record EvidenceItem(
    Guid Id,
    EvidenceSourceType SourceType,
    string SourceName,
    string? SourceUrl,
    string Title,
    string? Summary,
    string RawText,
    string ContentHash,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset CollectedAtUtc,
    EvidenceQuality Quality,
    string? MetadataJson);
```

```csharp
public enum EvidenceSourceType
{
    Manual,
    LocalFile,
    RssFeed,
    PressRelease,
    NewsArticle,
    CompanyBlog,
    Filing,
    EarningsTranscript,
    GovernmentContract,
    JobPosting,
    Patent,
    SocialMedia,
    RegulatoryAnnouncement,
    InsiderTransaction,
    ConferenceMention,
    RegulatoryApproval,
    Trademark
}
```

```csharp
public enum EvidenceQuality
{
    Unknown,
    Low,
    Medium,
    High,
    PrimarySource
}
```

### EvidenceMention

```csharp
public sealed record EvidenceMention(
    Guid Id,
    Guid EvidenceId,
    string MentionText,
    Guid? ResolvedCompanyId,
    decimal ResolutionConfidence,
    string? ResolutionReason,
    DateTimeOffset CreatedAtUtc);
```

---

## Signals

### Signal

```csharp
public sealed record Signal(
    Guid Id,
    Guid EvidenceId,
    Guid? CompanyId,
    string CompanyMention,
    SignalType Type,
    SignalDirection Direction,
    int Strength,
    int Novelty,
    decimal Confidence,
    string SupportingExcerpt,
    string Reason,
    SignalReviewStatus ReviewStatus,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset CreatedAtUtc,
    string? MetadataJson = null);

// MetadataJson carries the provenance envelope for a judgment-derived news signal
// (newsJudgmentSignalVersion / newsJudgmentId / newsJudgmentCohortKey / observation
// and evidence ids). Trailing + nullable: a legacy signal hydrates it as null.
```

```csharp
public enum SignalType
{
    CustomerWin,
    StrategicPartnership,
    ExecutiveHire,
    ProductLaunch,
    CapitalRaise,
    GuidanceChange,
    GovernmentContract,
    HiringExpansion,
    InsiderBuying,
    PatentActivity,
    DeveloperAdoption,
    MediaAttention,
    HiringActivity,
    InstitutionalOwnership,
    RegulatoryApproval,
    TrademarkActivity,
    Other
}
```

```csharp
public enum SignalDirection
{
    Positive,
    Neutral,
    Negative,
    Mixed
}
```

```csharp
public enum SignalReviewStatus
{
    Pending,
    Approved,
    Rejected,
    NeedsHumanReview
}
```

Validation rules:

- Strength must be 1-10.
- Novelty must be 1-10.
- Confidence must be 0-1.
- Supporting excerpt must not be empty.
- Every signal must reference evidence.

---

## Signal Review

### SignalReview

```csharp
public sealed record SignalReview(
    Guid Id,
    Guid SignalId,
    string ReviewerName,
    SignalReviewDecision Decision,
    string Summary,
    string? IssuesJson,
    DateTimeOffset ReviewedAtUtc);
```

```csharp
public enum SignalReviewDecision
{
    Approve,
    Reject,
    NeedsMoreEvidence,
    ReduceConfidence,
    EscalateToHuman
}
```

---

## Scoring

### CompanyScoreSnapshot

```csharp
public sealed record CompanyScoreSnapshot(
    Guid Id,
    Guid CompanyId,
    string ScoringVersion,
    int TrajectoryScore,
    int OpportunityScore,
    int AttentionScore,
    int EvidenceConfidenceScore,
    int SignalVelocityScore,
    string Explanation,
    string ComponentJson,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    DateTimeOffset CreatedAtUtc,
    string? ScoringConfigVersion = null,
    string? StrategyName = null,
    string? CollectionProvenance = null);
```

Scores are 0-100.

**The three trailing fields are load-bearing and all are nullable for legacy records.**

- `ScoringConfigVersion` — the content fingerprint of everything that could change the
  number (`radar-scoring-fp-…`). Two snapshots with different stamps are NOT comparable.
- `StrategyName` — **the series key.** Radar scores N strategies over ONE collection pass,
  so a company has one snapshot *per strategy per run*. `null`/blank means the primary
  series; `ScoreSeriesKey` is the single definition and compares case-insensitively.
  Without this field the schema cannot represent what Radar actually does.
- `CollectionProvenance` — which collectors ran (`collectors=…;`), recorded verbatim and
  **hashed into nothing**, so enabling a collector re-stamps no strategy.

### ScoreEvidenceLink

```csharp
public sealed record ScoreEvidenceLink(
    Guid Id,
    Guid ScoreSnapshotId,
    Guid SignalId,
    Guid EvidenceId,
    string ContributionReason,
    int ContributionWeight);
```

---

## Reports

### RadarReport

```csharp
public sealed record RadarReport(
    Guid Id,
    string ReportType,
    string Title,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    string MarkdownContent,
    DateTimeOffset CreatedAtUtc);
```

### RadarReportItem

```csharp
public sealed record RadarReportItem(
    Guid Id,
    Guid ReportId,
    Guid CompanyId,
    Guid ScoreSnapshotId,
    RadarReportAction SuggestedAction,
    string Summary,
    int Rank);
```

```csharp
public enum RadarReportAction
{
    Investigate,
    Watch,
    Ignore,
    NeedsMoreEvidence,
    ThesisImproving,
    ThesisDeteriorating
}
```

---

## AI Structured Output Schemas

### ExtractSignalsOutput

```csharp
public sealed record ExtractSignalsOutput(
    IReadOnlyList<ExtractedSignal> Signals,
    string OverallSummary);

public sealed record ExtractedSignal(
    string CompanyMention,
    string SignalType,
    string Direction,
    int Strength,
    int Novelty,
    decimal Confidence,
    string SupportingExcerpt,
    string Reason);
```

Rules:

- If no meaningful signal exists, return empty `Signals`.
- Do not invent company names.
- Do not infer ticker unless explicit in evidence.
- Use direct evidence excerpts.

### Signal review is DETERMINISTIC — there is no AI review schema

⚠ Earlier revisions of this document specified `ReviewSignalsOutput` / `ReviewedSignal`.
**Neither has ever existed.** Review is performed by `DeterministicSignalReviewer`
(`ISignalReviewer`), which returns a `SignalReviewOutcome`; no model is called on the
review path. Do not design against an AI review schema.

The one shipped AI structured output on the extraction path is `ExtractSignalsOutput`
above. The other model-facing contracts live in their own subsystems and are versioned
independently: the directional filing read, stage-1 news typing
(`news-typing-prompt-*` / `news-typing-schema-*`), and the stage-2 news judge
(`news-judgment-prompt-*` / `news-judgment-schema-*`).

---

## Persistence

⚠ **Radar is FILE-BASED today. PostgreSQL is not wired** — there is no Dapper or Npgsql
dependency in `src/`. The table list below is the original conceptual model, retained as
intent, not as a description of storage.

It is also no longer the whole model. The durable stores that actually exist include
scoring configs, pipeline run records, price bars, efficacy/leaderboard artifacts, news
observations, typing records, fact-family snapshots, judgments, news-risk assessments and
operating calls — none of which appear below.

Two rules matter more than the table shapes:

- **The repository IS the file store** (spec 142): `FileSignalStore` and
  `FileRawEvidenceStore` implement both the file-store and repository interfaces, so
  scoring reads accrued history rather than an empty in-memory singleton.
- **Evidence identity is content-derived** (spec 145): the id comes from the normalized
  title+body hash ALONE. Source URL, collector, timestamps and metadata are excluded, so
  the same content from two collectors is ONE evidence record.

Original conceptual table list:

```text
companies
company_aliases
evidence_items
evidence_mentions
signals
signal_reviews
company_score_snapshots
score_evidence_links
radar_reports
radar_report_items
```

Indexes:

- `evidence_items(content_hash)` unique
- `evidence_items(published_at_utc)`
- `signals(company_id, observed_at_utc)`
- `signals(type, observed_at_utc)`
- `company_score_snapshots(company_id, created_at_utc)`
- `company_aliases(alias)`

---

## Seed Data

MVP can start with a small manually curated universe:

- known company name
- ticker
- exchange
- aliases

Do not rely on live ticker resolution in the first implementation.

---

## Versioning

Scoring identity is a **composite**, not one version string. Four independent things move:

- **The formula** — a `radar-formula-vN` class. Shipped set:
  `{ v8, v9, v10, v11, radar-baseline-activity-v1 }`. A new *structure* earns the next free
  `vN` (**v12**); an in-place change to an existing formula's composition instead bumps
  `IScoreFormula.CompositionRevision`.
- **The extractor rule set** — `KeywordSignalExtractor.RuleSetVersion` (a rule-STRUCTURE
  change; magnitudes are config).
- **`ScoringConfigVersion`** — the content fingerprint over everything that can change a
  number: weights, scoring window, formula identity, channel budget, signal-type filter,
  attention tier map, the news-judgment identity and the news query window. Tunable
  magnitudes live in config and re-stamp automatically.
- **Model contracts** — prompt and result schema versions, versioned separately per
  subsystem, and folded into their cohort keys so two contracts never pool.

**A strategy is IMMUTABLE BY CONVENTION.** To change one, add a new name
(`momentum` → `momentum-v2`); `StrategyIdentityGuard` fails the run before collection if a
named strategy's fingerprint moves. Pins are also **window-dependent** — the unit-test pin
is computed at a 30-day window the Worker never uses, so reconcile a real run against the
pair for the window it actually ran.

---

## Acceptance Criteria

- [ ] Domain records compile under .NET 10.
- [ ] Database schema supports evidence -> signal -> score -> report traceability.
- [ ] Signals cannot exist without evidence.
- [ ] Scores can be traced back to contributing signals and evidence.
- [ ] AI outputs are typed and validated before persistence.
