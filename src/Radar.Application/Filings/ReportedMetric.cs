using Radar.Application.Identity;
using Radar.Application.Storage;

namespace Radar.Application.Filings;

/// <summary>
/// The CLOSED set of metrics the earnings-release read may keep (spec 215 §1). Guidance is deliberately
/// absent — guidance is a statement, not a measurement. Anything the model names outside this set is
/// dropped and COUNTED (<c>ReportedMetricsDroppedUnrecognised</c>), never coerced. Values start at 1 so a
/// defaulted zero is an UNDEFINED member: persistence is token-based only (<c>RadarFileStoreJson</c>
/// renders enum names and rejects integers) and the wire shape is a string parsed by the digit-rejecting
/// <c>NewsTypingTokens.TryParse</c>, so the ordinals below carry no persisted or wire meaning.
/// </summary>
public enum ReportedMetric
{
    Revenue = 1,
    NetIncome = 2,
    DilutedEps = 3,
    GrossMargin = 4,
    OperatingIncome = 5,
    Backlog = 6,
    CashAndInvestments = 7,
    TotalDebt = 8,
    FreeCashFlow = 9,
}

/// <summary>
/// How a persisted <see cref="ReportedMetricRecord"/> was verified before it was kept. Exactly one member
/// exists under <see cref="ReportedMetricsPolicy.Version"/>: a metric that fails the deterministic scan is
/// never persisted, so no weaker verification state is representable on disk. Starts at 1 so a defaulted
/// zero is undefined (the strict enum converter refuses it).
/// </summary>
public enum ReportedMetricVerification
{
    /// <summary>The value (and prior value, when stated) appears verbatim inside the quote, and the quote appears verbatim inside the body the analyzer read.</summary>
    Verbatim = 1,
}

/// <summary>
/// The ONE declaration of the reported-metrics policy token (spec 215 §1). It names, together, the closed
/// <see cref="ReportedMetric"/> enum and the verbatim verification rule (value-in-quote, unit-in-quote,
/// quote-in-truncated-body). It is stamped on every NEW <see cref="AnalyzedFilingRecord"/> as
/// <see cref="AnalyzedFilingRecord.ReportedMetricsPolicy"/> and on every <see cref="ReportedMetricRecord"/>
/// as <see cref="ReportedMetricRecord.Policy"/>; a cache record whose non-null policy differs from this
/// value is a bounded automatic MISS (the spec-160 <c>cmpscan</c> rule), while a null policy is a HIT
/// (heal-forward — the accrued cache is never mass-invalidated). Widening the enum or changing the
/// verification rule is <c>reported-metrics-v2</c>. NOT a scoring or fingerprint input.
/// </summary>
public static class ReportedMetricsPolicy
{
    public const string Version = "reported-metrics-v1";
}

/// <summary>
/// One VERIFIED metric statement as the filing read returns it (spec 215 §1) — the closed metric, the
/// value/unit/period exactly as the release states them, the prior pair ONLY when the release itself
/// states it, and the verbatim quote the verifier confirmed. Not yet persisted: it carries no identity,
/// no evidence and no reader; <see cref="ReportedMetricRecord"/> is the durable form.
/// </summary>
public sealed record ReportedMetricReading(
    ReportedMetric Metric,
    string Value,
    string Unit,
    string Period,
    string? PriorValue,
    string? PriorPeriod,
    string Quote);

/// <summary>
/// What ONE fresh filing analysis produced for the reported-metrics ledger (spec 215 §1), carried on a
/// <see cref="DirectionalFilingSignal"/> so the collection pass can write the ledger with the RESOLVED
/// company id. Present ONLY on a fresh analysis whose analyzer extracted metrics; a cache replay and a
/// read made with extraction disabled carry <c>null</c> — "not extracted this pass", never an empty list
/// meaning "none". The three drop counts and the incomplete-prior-pair count are measured whenever the model response was examined: a 0 is a
/// measured zero.
/// </summary>
/// <param name="Accession">The dashed SEC accession the release was read from (the ledger file key).</param>
/// <param name="Form">The filing form the evidence declares (e.g. <c>8-K</c>), rendered to the judge as "stated in {form}".</param>
/// <param name="ReaderIdentity">The provider:model identity that read the release (the source's <c>ModelIdentity</c>).</param>
/// <param name="Metrics">The verified metrics, the three drop counts and the incomplete-prior-pair count, exactly as the analyzer returned them.</param>
public sealed record ReportedMetricExtraction(
    string Accession,
    string Form,
    string ReaderIdentity,
    VerifiedReportedMetrics Metrics);

/// <summary>
/// One durably persisted company-reported metric (spec 215 §1): a figure the company itself STATED in a
/// filing Radar read, kept verbatim with its period, its prior pair when the release stated one, the
/// quote it was verified against, the evidence it came from, the filing date, the reader that read it and
/// the policy it was verified under. Append-only, content-identified, never rewritten (AD-8).
/// <para>
/// <b>Not a scoring input.</b> The ledger feeds the stage-2 judge (as citable reference values, spec 215
/// §2) and the weekly report's evidence line (§4). It changes no signal, score, weight or fingerprint on
/// its own, and Radar performs NO arithmetic on it — the judge compares; Radar states.
/// </para>
/// </summary>
/// <param name="Id">Content-derived (<see cref="IdentityFor"/>): the same (accession, metric, period) re-read is the same record.</param>
/// <param name="CompanyId">The RESOLVED company the filing evidence belongs to.</param>
/// <param name="Accession">The dashed SEC accession of the filing whose release stated the figure.</param>
/// <param name="EvidenceId">The earnings-8-K evidence item the read was made for (provenance).</param>
/// <param name="FilingDateUtc">The evidence's <c>PublishedAtUtc ?? CollectedAtUtc</c> — when the company stated it.</param>
/// <param name="Form">The filing form (e.g. <c>8-K</c>), as the evidence metadata declares it.</param>
/// <param name="Metric">The closed metric.</param>
/// <param name="Value">The figure exactly as stated (a string; no parsing, no arithmetic).</param>
/// <param name="Unit">The unit token exactly as stated (may be empty when the release states none).</param>
/// <param name="Period">The period the figure is stated FOR (<c>Q2 FY27</c>, <c>as of 2026-07-31</c>), as stated.</param>
/// <param name="PriorValue">The prior-period figure ONLY when the release itself states it; else null.</param>
/// <param name="PriorPeriod">The prior period ONLY when the release itself states it; else null.</param>
/// <param name="Quote">The verbatim release sentence the value was verified inside.</param>
/// <param name="ReaderIdentity">The provider:model that read the release.</param>
/// <param name="Verification">How the record was verified (<see cref="ReportedMetricVerification.Verbatim"/>).</param>
/// <param name="Policy">The <see cref="ReportedMetricsPolicy.Version"/> the record was produced under.</param>
public sealed record ReportedMetricRecord(
    Guid Id,
    Guid CompanyId,
    string Accession,
    Guid EvidenceId,
    DateTimeOffset FilingDateUtc,
    string Form,
    ReportedMetric Metric,
    string Value,
    string Unit,
    string Period,
    string? PriorValue,
    string? PriorPeriod,
    string Quote,
    string ReaderIdentity,
    ReportedMetricVerification Verification,
    string Policy)
{
    /// <summary>
    /// The content-derived identity (spec 145's pattern through the shared <see cref="DeterministicGuid"/>):
    /// one record per (accession, metric, period), so a re-read of the same accession is a durable no-op
    /// and a second period of the same metric in one release (current + prior stated as its own row) is a
    /// distinct record. The period is compared as stated (ordinal); a re-worded period is a new record,
    /// which is honest — Radar cannot know two wordings mean one period without arithmetic it refuses.
    /// </summary>
    public static Guid IdentityFor(string accession, ReportedMetric metric, string period) =>
        DeterministicGuid.FromCanonicalString(
            $"radar:reported-metric:{accession}:{metric}:{period}");
}

/// <summary>
/// The report-facing display name of a <see cref="ReportedMetric"/> — lower-case words, ONE definition so
/// the weekly report's <c>— reported: revenue …, backlog …</c> clause and any future renderer agree. No
/// direction word can be produced here by construction.
/// </summary>
public static class ReportedMetricDisplay
{
    public static string NameOf(ReportedMetric metric) => metric switch
    {
        ReportedMetric.Revenue => "revenue",
        ReportedMetric.NetIncome => "net income",
        ReportedMetric.DilutedEps => "diluted eps",
        ReportedMetric.GrossMargin => "gross margin",
        ReportedMetric.OperatingIncome => "operating income",
        ReportedMetric.Backlog => "backlog",
        ReportedMetric.CashAndInvestments => "cash and investments",
        ReportedMetric.TotalDebt => "total debt",
        ReportedMetric.FreeCashFlow => "free cash flow",
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Undefined reported metric."),
    };
}

/// <summary>
/// The append-only reported-metrics ledger (spec 215 §1), implemented in Infrastructure at
/// <c>{root}/{companyId}/{accession}.json</c> — one file per (company, accession) holding that release's
/// verified record LIST. Insert-if-new (the spec-206 raw-evidence pattern): an existing file is
/// <see cref="DurableWriteOutcome.AlreadyAvailable"/> and is never overwritten; a disk failure is
/// <see cref="DurableWriteOutcome.Failed"/> and never throws; only caller cancellation propagates. An
/// AD-14 analogue on the WRITE side (reference data the pipeline produces) and a judge/report input on the
/// READ side — never evidence, never a signal source, never a scoring or fingerprint input.
/// </summary>
public interface IReportedMetricStore
{
    /// <summary>
    /// Writes <paramref name="records"/> as the ledger file for (<paramref name="companyId"/>,
    /// <paramref name="accession"/>) if none exists. An empty list still claims the file (a release that
    /// verified nothing is a recorded fact, not an absence). Never throws for a disk failure.
    /// </summary>
    Task<DurableWriteResult> WriteIfNewAsync(
        Guid companyId, string accession, IReadOnlyList<ReportedMetricRecord> records, CancellationToken ct);

    /// <summary>
    /// Every ledger record for one company in deterministic order (<c>FilingDateUtc</c> descending, then
    /// <c>Metric</c>, <c>Period</c>, <c>Id</c> — AD-3). An unreadable file is logged and skipped; an
    /// absent company folder is an empty list.
    /// </summary>
    Task<IReadOnlyList<ReportedMetricRecord>> GetForCompanyAsync(Guid companyId, CancellationToken ct);
}
