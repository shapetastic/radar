namespace Radar.Infrastructure.UsaSpending;

/// <summary>
/// A single parsed federal contract award from a USASpending <c>spending_by_award</c> response (one
/// record per <c>results[]</c> row). Raw metadata only — the collector synthesizes evidence Title/RawText
/// from these real fields and never fabricates award body text. <see cref="AwardUrl"/> is the stable award
/// landing page (<c>https://www.usaspending.gov/award/{generated_internal_id}</c>) for provenance.
/// <see cref="RecipientId"/> is the precise recipient key the collector client-side-filters on (the
/// <c>recipient_search_text</c> query is fuzzy and can match subsidiaries/unrelated entities).
/// <see cref="LastModifiedDate"/> is the award's most-recent activity instant (<c>yyyy-MM-dd HH:mm:ss</c>,
/// UTC) — the collector stamps evidence <c>PublishedAt</c> from it so recently-active awards land in the
/// scoring window; <see cref="StartDate"/> is the period-of-performance start (often years old for
/// multi-year vehicles) and is kept for display only.
/// </summary>
internal sealed record UsaSpendingAwardItem(
    string AwardId,
    string RecipientName,
    decimal AwardAmount,
    string AwardingAgency,
    string StartDate,
    string? EndDate,
    string? LastModifiedDate,
    string? Description,
    string RecipientId,
    string GeneratedInternalId,
    string AwardUrl);
