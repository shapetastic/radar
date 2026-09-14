using System.Reflection;

namespace Radar.TestSupport;

/// <summary>
/// SPEC 227 — VERBATIM excerpts of two REAL item-1.01 8-K bodies, exactly as the production
/// <c>IAcquisitionFilingBodyReader</c> (the SEC index → primary document → EX-99.1 read, stripped by the shared
/// <c>EvidenceNormalizer</c>) returned them. Unlike the spec-217 <c>AcquisitionFilingFixtures</c>, which are
/// representative wording written for the test suite, every line here is a line of the real plain text.
/// <para>
/// <b>Provenance.</b> Fetched 2026-09-14 (19:54–19:59 UTC) through the production reader and the shared, paced
/// SEC client by the spec-227 §3 harness (<c>AcquisitionScanV2LiveMeasurementTests</c>), from the accrued
/// evidence of the main Radar store:
/// </para>
/// <list type="bullet">
/// <item><see cref="SteveMaddenQ1ResultsAndCreditAgreement"/> — Steven Madden, Ltd. (SHOO), CIK 913241,
/// accession 0001641172-25-008949, filed 2025-05-07 (items 1.01, 2.01, 2.02, 2.03, 8.01, 9.01):
/// https://www.sec.gov/Archives/edgar/data/913241/000164117225008949/0001641172-25-008949-index.htm — the Q1
/// 2025 results 8-K with an
/// amended and restated credit agreement attached. <c>acqscan-v1</c> recognised it as a pending acquisition of
/// SHOO by "Lead Borrower" at $0.21 per share (the quarterly dividend).</item>
/// <item><see cref="MarineMaxMergerAgreement"/> — MarineMax, Inc. (HZO), CIK 1057060, accession
/// 0001193125-26-341302, filed 2026-08-10 (items 1.01, 7.01, 9.01):
/// https://www.sec.gov/Archives/edgar/data/1057060/000119312526341302/0001193125-26-341302-index.htm
/// — the Agreement and Plan of Merger with SHM Holdco, LLC (Safe Harbor Marinas, a Blackstone Infrastructure
/// portfolio company) at $53.00 per share in cash. <c>acqscan-v1</c> recorded "Parent" at $0.001 (the par
/// value).</item>
/// </list>
/// <para>
/// <b>What "trimmed" means.</b> Each file is a handful of CONTIGUOUS line ranges of the real body, in body order.
/// The ranges are joined by a line holding only <c>[…]</c> with a blank line on each side, so an elided stretch
/// is also a clause boundary — no two excerpts can bind across the cut. Within a range nothing is edited: the
/// source's hard line wraps ("Steve\nMadden"), leading spaces, curly quotes and the blank lines that separate
/// blocks are the reader's own. Line endings are normalised to <c>\n</c> on load (the normalizer's own form), so
/// a CRLF checkout cannot change what the scan sees. The contiguity that matters is preserved in full: SHOO's
/// signature page → EX-99.1 heading → dateline run (the v1 false binding) and HZO's §2.01 par-value → merger
/// consideration sequence.
/// </para>
/// </summary>
public static class RealAcquisitionFilingBodies
{
    /// <summary>SHOO, accession 0001641172-25-008949 — must NOT be recognised under <c>acqscan-v2</c>.</summary>
    public static string SteveMaddenQ1ResultsAndCreditAgreement => Load("shoo-0001641172-25-008949-excerpt.txt");

    /// <summary>HZO, accession 0001193125-26-341302 — the genuine takeover, at $53.00 per share in cash.</summary>
    public static string MarineMaxMergerAgreement => Load("hzo-0001193125-26-341302-excerpt.txt");

    private static string Load(string fileName)
    {
        var name = "Radar.TestSupport.Acquisitions." + fileName;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded fixture '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
