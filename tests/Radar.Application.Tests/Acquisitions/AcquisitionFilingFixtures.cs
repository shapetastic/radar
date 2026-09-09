namespace Radar.Application.Tests.Acquisitions;

/// <summary>
/// SPEC 217 §1 — the item-1.01 filing bodies <c>acqscan-v1</c> is exercised against.
///
/// <para>
/// <b>The two MarineMax fixtures are the slice's ground truth.</b> The accrued store holds exactly 178
/// filing evidence records whose <c>metadata.items</c> contains <c>1.01</c> (measured 2026-09-08), and
/// MarineMax (HZO) has TWO of them:
/// </para>
/// <list type="bullet">
/// <item><c>0001193125-26-290439</c>, filed 2026-06-30 (items 1.01, 1.02, 2.03, 7.01, 9.01) — a CREDIT
/// AGREEMENT. It must NOT be recognised, and it is the fail-closed case: "Entry into a Material Definitive
/// Agreement" plus the company's own name is exactly the shape a title-only rule fires on.</item>
/// <item><c>0001193125-26-341302</c>, filed 2026-08-10 (items 1.01, 7.01, 9.01) — the Safe Harbor Marinas
/// merger at $53.00 per share in cash. It is the ONE expected recognition in the whole store, and the
/// pinned regression case for "an 8-K 1.01 merger ⇒ Ignore, not Thesis improving".</item>
/// </list>
/// <para>
/// <b>What these strings ARE, stated plainly so nobody mistakes them for something they are not.</b> They
/// are REPRESENTATIVE 8-K wording written for this test suite from the publicly known facts of each filing
/// (parties, item codes, consideration, filing dates) in the standard form those items take. They are NOT
/// verbatim copies of the SEC documents, and no test here claims they are. The VERBATIM behaviour over the
/// real documents is the job of the env-gated live harness
/// (<c>AcquisitionRecognitionLiveMeasurementTests</c>), which runs <c>acqscan-v1</c> over all 178 accrued
/// item-1.01 filings through the production reader and reports the distribution — the measurement spec 217
/// §1 requires before the recognition can be trusted, and the one thing these fixtures deliberately do not
/// stand in for.
/// </para>
/// </summary>
internal static class AcquisitionFilingFixtures
{
    /// <summary>
    /// HZO accession 0001193125-26-341302, filed 2026-08-10 — the merger. Both legs are present: the
    /// company is the TARGET ("Merger Sub will merge with and into MarineMax, Inc.") and the per-share
    /// consideration is stated ("$53.00 in cash").
    /// </summary>
    public const string MarineMaxMerger =
        "Item 1.01 Entry into a Material Definitive Agreement. "
        + "On August 10, 2026, MarineMax, Inc., a Florida corporation (the \"Company\"), entered into an "
        + "Agreement and Plan of Merger (the \"Merger Agreement\") by and among Safe Harbor Marinas, LLC, a "
        + "Delaware limited liability company (\"Parent\"), Harbor Merger Sub, Inc., a Florida corporation "
        + "and a wholly owned subsidiary of Parent (\"Merger Sub\"), and the Company. "
        + "Subject to the terms and conditions of the Merger Agreement, Merger Sub will merge with and into "
        + "MarineMax, Inc., with the Company surviving the merger as a wholly owned subsidiary of Parent. "
        + "At the effective time of the merger, each share of common stock of the Company issued and "
        + "outstanding immediately prior to the effective time will be converted into the right to receive "
        + "$53.00 in cash, without interest and less any applicable withholding taxes. "
        + "The board of directors of the Company unanimously approved the Merger Agreement. Completion of "
        + "the merger is subject to customary closing conditions, including approval by the Company's "
        + "shareholders and the receipt of required regulatory approvals. "
        + "Item 7.01 Regulation FD Disclosure. On August 10, 2026, the Company issued a press release "
        + "announcing the execution of the Merger Agreement. A copy of the press release is furnished as "
        + "Exhibit 99.1 hereto. "
        + "Item 9.01 Financial Statements and Exhibits. (d) Exhibits. 2.1 Agreement and Plan of Merger. "
        + "99.1 Press release dated August 10, 2026.";

    /// <summary>
    /// HZO accession 0001193125-26-290439, filed 2026-06-30 — the CREDIT AGREEMENT. It carries item 1.01
    /// and the company's own name in every sentence, and it must be counted, not recognised
    /// (<c>NoMergerAgreement</c>): there is no merger-agreement phrase and no per-share consideration.
    /// </summary>
    public const string MarineMaxCreditAgreement =
        "Item 1.01 Entry into a Material Definitive Agreement. "
        + "On June 29, 2026, MarineMax, Inc., a Florida corporation (the \"Company\"), entered into an "
        + "Amended and Restated Credit Agreement with a syndicate of lenders and Wells Fargo Bank, National "
        + "Association, as administrative agent. "
        + "The Amended and Restated Credit Agreement provides for a revolving credit facility in an "
        + "aggregate principal amount of up to $950.0 million and matures on June 29, 2031. "
        + "Borrowings under the facility bear interest at a rate based on Term SOFR plus an applicable "
        + "margin. The Company's obligations are guaranteed by certain of its subsidiaries. "
        + "Item 1.02 Termination of a Material Definitive Agreement. In connection with the entry into the "
        + "Amended and Restated Credit Agreement, the Company terminated its prior credit agreement dated "
        + "as of August 2, 2021. "
        + "Item 2.03 Creation of a Direct Financial Obligation. The description under Item 1.01 is "
        + "incorporated herein by reference. "
        + "Item 9.01 Financial Statements and Exhibits. (d) Exhibits. 10.1 Amended and Restated Credit "
        + "Agreement dated as of June 29, 2026.";

    /// <summary>
    /// An ACQUIRER-side item-1.01 8-K: the subject company is buying someone else. Every merger word is
    /// present, so an unordered co-occurrence test would recognise it and close the WRONG thesis — which is
    /// exactly why leg (a) is positional and the acquirer veto runs first.
    /// </summary>
    public const string AcquirerSideMerger =
        "Item 1.01 Entry into a Material Definitive Agreement. "
        + "On May 4, 2026, Stereotaxis, Inc., a Delaware corporation (the \"Company\"), entered into an "
        + "Agreement and Plan of Merger with Robocath SA (\"Robocath\") and Cath Merger Sub, Inc., a wholly "
        + "owned subsidiary of Stereotaxis, Inc. "
        + "Under the terms of the agreement, the Company agreed to acquire Robocath in an all-stock "
        + "transaction. "
        + "At the effective time, each share of Robocath will be converted into the right to receive "
        + "$4.25 in cash. "
        + "Item 9.01 Financial Statements and Exhibits. (d) Exhibits. 2.1 Agreement and Plan of Merger.";

    /// <summary>
    /// A merger agreement with NO stated per-share consideration — leg (a) holds, leg (b) does not. It must
    /// be counted as <c>NoStatedConsideration</c>, never recognised on the strength of the merger phrase
    /// alone.
    /// </summary>
    public const string MergerWithoutStatedConsideration =
        "Item 1.01 Entry into a Material Definitive Agreement. "
        + "On March 2, 2026, Example Industries, Inc., a Delaware corporation (the \"Company\"), entered "
        + "into an Agreement and Plan of Merger by and among Acme Holdings, Inc., a Delaware corporation "
        + "(\"Parent\"), Acme Merger Sub, Inc., a wholly owned subsidiary of Parent (\"Merger Sub\"), and "
        + "the Company. "
        + "Subject to the terms of the Merger Agreement, Merger Sub will merge with and into Example "
        + "Industries, Inc., with the Company surviving as a wholly owned subsidiary of Parent. "
        + "The consideration payable to the Company's shareholders will be determined in accordance with "
        + "the terms of the Merger Agreement and is described in the agreement filed as Exhibit 2.1. "
        + "Item 9.01 Financial Statements and Exhibits. (d) Exhibits. 2.1 Agreement and Plan of Merger.";
}
