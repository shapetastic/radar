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
    DateTimeOffset UpdatedAtUtc);
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
    SocialMedia
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
    DateTimeOffset CreatedAtUtc);
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
    DateTimeOffset CreatedAtUtc);
```

Scores are 0-100.

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

### ReviewSignalsOutput

```csharp
public sealed record ReviewSignalsOutput(
    string Status,
    IReadOnlyList<ReviewedSignal> ReviewedSignals,
    string Summary);

public sealed record ReviewedSignal(
    Guid? SignalId,
    string Decision,
    decimal AdjustedConfidence,
    string Reason,
    IReadOnlyList<string> Issues);
```

---

## PostgreSQL Tables

Initial table list:

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

Scoring formulas must have explicit versions, e.g. `mvp-v1`.

AI prompts should also be versioned in code or database metadata.

If a prompt changes significantly, future outputs should record the new prompt version.

---

## Acceptance Criteria

- [ ] Domain records compile under .NET 10.
- [ ] Database schema supports evidence -> signal -> score -> report traceability.
- [ ] Signals cannot exist without evidence.
- [ ] Scores can be traced back to contributing signals and evidence.
- [ ] AI outputs are typed and validated before persistence.
