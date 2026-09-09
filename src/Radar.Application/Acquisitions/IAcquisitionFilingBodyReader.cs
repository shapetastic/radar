namespace Radar.Application.Acquisitions;

/// <summary>Why one item-1.01 body read ended (spec 217 §1). Every value is counted by the caller.</summary>
public enum AcquisitionBodyReadOutcome
{
    /// <summary>The primary document (and its EX-99.1 exhibit when present) were fetched and stripped.</summary>
    Success = 1,

    /// <summary>
    /// The fetch failed — 403/429/timeout/transport/unparseable index. NOT "no acquisition": nothing is
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
/// specifics live behind it in Infrastructure (AD-5), so <c>acqscan-v1</c> stays a pure function of text.
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
    /// the reader can fetch the 8-K body itself (the item-1.01 narrative lives there, not in EX-99.1);
    /// a null falls back to whatever the filing index names first.
    /// </param>
    Task<AcquisitionFilingBody> ReadAsync(
        string cik, string accession, string? primaryDocument, CancellationToken ct);
}
