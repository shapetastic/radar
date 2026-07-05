using Radar.Domain.Signals;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// One parsed + classified SEC Form 4 (insider-transaction) filing. The reader aggregates the filing's
/// discretionary transactions into a single filing-level <see cref="Direction"/> and <see cref="NetValue"/>
/// per the deterministic transaction-code table (the 10b5-1 plan override forces every transaction Neutral);
/// the collector synthesizes an advice-free evidence phrase from these real fields and never fabricates
/// filing body text. <see cref="IndexUrl"/> is the stable filing landing page (provenance).
/// </summary>
internal sealed record SecForm4Filing(
    string Accession,
    string FilingDate,
    DateTimeOffset AcceptanceDateTimeUtc,
    string IndexUrl,
    string? IssuerTicker,
    string PrimaryOwnerName,
    int DistinctOwnerCount,
    SignalDirection Direction,
    decimal NetValue,
    decimal Shares,
    bool HasCluster,
    bool Is10b5Plan);
