namespace Radar.Application.Acquisitions;

/// <summary>Why one item-1.01 body read ended (spec 217 §1). Every value is counted by the caller.</summary>
public enum AcquisitionBodyReadOutcome
{
    /// <summary>
    /// The primary document (and its EX-99.1 exhibit when present) were fetched and stripped. (⚠ AMENDED in place by
    /// spec 228: and, after EX-99.1, the EX-2.1 merger agreement when the index carries one — the primary 8-K always
    /// comes first; no EX-10.* contract is ever read.)
    /// </summary>
    Success = 1,

    /// <summary>
    /// The fetch failed — 403/429/timeout/transport/unparseable index, or (spec 228) no authoritative primary
    /// document: "no primary document row" (neither the declared primary nor a row typed 8-K / 8-K/A) or
    /// "ambiguous primary document rows" (more than one row typed as the form). NOT "no acquisition": nothing is
    /// persisted and a later run re-attempts the filing, so a transient block can never permanently close
    /// or permanently open a thesis.
    /// </summary>
    FetchFailed = 2,
}

/// <summary>
/// One item-1.01 body read: the concatenated plain text on success, or the named failure. The
/// <see cref="Detail"/> is advice-free and used only for logging.
/// </summary>
public sealed record AcquisitionFilingBody(
    AcquisitionBodyReadOutcome Outcome,
    string PlainText,
    string? Detail)
{
    public bool IsSuccess => Outcome == AcquisitionBodyReadOutcome.Success;

    public static AcquisitionFilingBody Success(string plainText) =>
        new(AcquisitionBodyReadOutcome.Success, plainText, Detail: null);

    public static AcquisitionFilingBody Failed(string detail) =>
        new(AcquisitionBodyReadOutcome.FetchFailed, string.Empty, detail);
}

/// <summary>
/// The Application seam over "fetch the item-1.01 filing's own text" (spec 217 §1). The SEC/HTTP/HTML
/// specifics live behind it in Infrastructure (AD-5), so the scan (<c>AcquisitionAgreementScan.Version</c>) stays a pure function of text.
/// <para>
/// One BOUNDED fetch per item-1.01 filing, cached like the earnings read, and routed through the shared
/// global SEC pacer — the recognition pass never issues an unpaced burst.
/// </para>
/// </summary>
public interface IAcquisitionFilingBodyReader
{
    /// <param name="cik">The company's CIK (leading zeros optional).</param>
    /// <param name="accession">The DASHED accession of the 8-K.</param>
    /// <param name="primaryDocument">
    /// The filing's primary document file name as the evidence metadata declares it, or null. Supplied so
    /// the reader can fetch the 8-K body itself (the item-1.01 narrative lives there, not in EX-99.1).
    /// (⚠ AMENDED in place by spec 228: this used to say "a null falls back to whatever the filing index names
    /// first", and in practice that fallback took an exhibit. A null — or a declared name the index does not carry
    /// — now falls back ONLY to the one index row typed as the form itself (<c>8-K</c> / <c>8-K/A</c>); with no
    /// such row the read is a named <see cref="AcquisitionBodyReadOutcome.FetchFailed"/>.)
    /// </param>
    Task<AcquisitionFilingBody> ReadAsync(
        string cik, string accession, string? primaryDocument, CancellationToken ct);
}
