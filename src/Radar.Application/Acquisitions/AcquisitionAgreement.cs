using Radar.Application.Identity;

namespace Radar.Application.Acquisitions;

/// <summary>
/// What the stated per-share consideration is paid IN (spec 217 §1). Values start at 1 so a defaulted zero
/// is an UNDEFINED member — persistence is token-based (<c>RadarFileStoreJson</c> renders enum names and
/// rejects integers), so the ordinals carry no persisted meaning.
/// </summary>
public enum AcquisitionConsiderationKind
{
    /// <summary>Cash only ("$53.00 per share in cash").</summary>
    Cash = 1,

    /// <summary>Stock only (an exchange ratio, or shares of the acquirer per target share).</summary>
    Stock = 2,

    /// <summary>Cash AND stock stated together in the same consideration sentence.</summary>
    Mixed = 3,
}

/// <summary>
/// How a persisted <see cref="PendingAcquisitionRecord"/> was verified before it was kept (spec 217 §1).
/// Exactly one member exists under <see cref="AcquisitionAgreementScan.Version"/>: a scan that cannot find
/// BOTH legs verbatim in the filing text persists nothing, so no weaker verification state is representable
/// on disk. Starts at 1 so a defaulted zero is undefined — the same rule
/// <c>ReportedMetricVerification</c> follows (spec 215/216).
/// </summary>
public enum AcquisitionVerification
{
    /// <summary>
    /// The target quote and the consideration quote both appear VERBATIM inside the filing body the reader
    /// fetched, and each quote contains the token the record claims it does.
    /// </summary>
    Verbatim = 1,
}

/// <summary>
/// One durably persisted, deterministically recognised PENDING ACQUISITION OF THE COMPANY (spec 217 §1) —
/// a fact the company itself stated in an item-1.01 8-K Radar read, kept verbatim with the filing it came
/// from, the evidence it was recognised on, the scan version it was produced under and how it was verified.
/// Append-only, content-identified, never rewritten (AD-8).
/// <para>
/// <b>Not a scoring input, and not a prediction.</b> The record closes a thesis: it forces the report label
/// (<c>Ignore</c>, policy rule 0), supersedes the extractor's <c>StrategicPartnership</c> read of the same
/// evidence with a Neutral <c>CorporateAction</c>, derives
/// <see cref="Radar.Domain.Companies.CompanyStatus.PendingAcquisition"/> at run time, and excludes the
/// company's observations from the forward efficacy series. It never adds a positive signal, never scores
/// a deal spread, and Radar performs NO arithmetic on the consideration — it is stored and stated as the
/// filing words it (AD-9: no advice, no M&amp;A-arbitrage reading).
/// </para>
/// </summary>
/// <param name="Id">Content-derived (<see cref="IdentityFor"/>): the same (company, accession) re-read is the same record.</param>
/// <param name="CompanyId">The RESOLVED company the item-1.01 filing evidence belongs to — the TARGET.</param>
/// <param name="Accession">The dashed SEC accession of the 8-K that announced the agreement.</param>
/// <param name="EvidenceId">The item-1.01 filing evidence the recognition was made on (provenance).</param>
/// <param name="AnnouncedOnUtc">The filing date — when the agreement became public. The date the state starts.</param>
/// <param name="AcquirerName">The acquirer exactly as the filing names it (a string; never normalised into an identity).</param>
/// <param name="ConsiderationPerShare">The per-share figure exactly as stated (a string; no parsing, no arithmetic).</param>
/// <param name="ConsiderationCurrency">The currency token exactly as stated (empty when the filing states none).</param>
/// <param name="ConsiderationKind">Cash / stock / mixed, as the consideration sentence states it.</param>
/// <param name="ConsiderationQuote">The verbatim filing sentence the consideration was verified inside.</param>
/// <param name="TargetQuote">The verbatim filing sentence proving the COMPANY is the target, not the acquirer.</param>
/// <param name="ScanVersion">The <see cref="AcquisitionAgreementScan.Version"/> that recognised it.</param>
/// <param name="Verification">How the record was verified (<see cref="AcquisitionVerification.Verbatim"/>).</param>
public sealed record PendingAcquisitionRecord(
    Guid Id,
    Guid CompanyId,
    string Accession,
    Guid EvidenceId,
    DateTimeOffset AnnouncedOnUtc,
    string AcquirerName,
    string ConsiderationPerShare,
    string ConsiderationCurrency,
    AcquisitionConsiderationKind ConsiderationKind,
    string ConsiderationQuote,
    string TargetQuote,
    string ScanVersion,
    AcquisitionVerification Verification)
{
    /// <summary>
    /// The content-derived identity (spec 145's pattern through the shared <see cref="DeterministicGuid"/>):
    /// ONE record per (scan version, company, accession), so re-reading the same filing on a later run is a
    /// durable no-op and a later scan version writes a NEW record beside the old one rather than colliding
    /// with it (the spec-216 §5 policy-in-the-key rule).
    /// </summary>
    public static Guid IdentityFor(string scanVersion, Guid companyId, string accession) =>
        DeterministicGuid.FromCanonicalString(
            $"radar:pending-acquisition:{scanVersion}:{companyId:D}:{accession}");

    /// <summary>
    /// The one-line, advice-free description the report and the policy rationale both print — so the banner,
    /// the <c>## Acquisitions pending</c> row and the label rationale can never word the same fact
    /// differently. States what the filing says and nothing more.
    /// </summary>
    public string DescribeConsideration() =>
        string.IsNullOrWhiteSpace(ConsiderationPerShare)
            ? DescribeKind()
            : $"{ConsiderationCurrency}{ConsiderationPerShare} per share {DescribeKind()}";

    private string DescribeKind() => ConsiderationKind switch
    {
        AcquisitionConsiderationKind.Cash => "in cash",
        AcquisitionConsiderationKind.Stock => "in stock",
        AcquisitionConsiderationKind.Mixed => "in cash and stock",
        _ => "as stated in the filing",
    };
}
