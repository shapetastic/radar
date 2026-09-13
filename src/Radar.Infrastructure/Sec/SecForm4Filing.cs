using Radar.Application.Collectors;
using Radar.Domain.Signals;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// One parsed + classified SEC Form 4 (insider-transaction) filing. The reader aggregates the filing's
/// discretionary transactions into a single filing-level <see cref="Direction"/> and <see cref="NetValue"/>
/// per the deterministic transaction-code table (the 10b5-1 plan override forces every transaction Neutral);
/// the collector synthesizes an advice-free evidence phrase from these real fields and never fabricates
/// filing body text. <see cref="IndexUrl"/> is the stable filing landing page (provenance).
/// <see cref="ClassificationReason"/> is the stable <see cref="InsiderActivityMetadata"/> classification token
/// naming the classification branch taken (spec 156) — persisted as additive evidence metadata so the
/// WHY of an insider classification is recoverable from the store going forward.
/// <see cref="PrimaryOwnerCik"/> (spec 224) is the SAME first reporting owner's SEC CIK
/// (<c>reportingOwner/reportingOwnerId/rptOwnerCik</c>), trimmed, <c>null</c> when absent/blank — persisted
/// beside the name as additive metadata so the scoring-time same-insider collapse has a structured identity to
/// bucket on and never parses the title.
/// </summary>
internal sealed record SecForm4Filing(
    string Accession,
    string FilingDate,
    DateTimeOffset AcceptanceDateTimeUtc,
    string IndexUrl,
    string? IssuerTicker,
    string PrimaryOwnerName,
    string? PrimaryOwnerCik,
    int DistinctOwnerCount,
    SignalDirection Direction,
    decimal NetValue,
    decimal Shares,
    bool HasCluster,
    bool Is10b5Plan,
    string ClassificationReason);
