namespace Radar.TestSupport;

/// <summary>
/// SPEC 229 — CONSTRUCTED item-1.01 bodies for the <c>acqscan-v4</c> rules that no real filing in the store exercises.
/// Unlike <see cref="RealAcquisitionFilingBodies"/>, NOT ONE of these is quoted from a filing: each is the minimal
/// wording that isolates one rule, written for the test suite. Every body names the invented "Example Industries,
/// Inc." except the MarineMax ones, whose target sentence the spec itself constructed to show the rule-2 pattern (it is
/// NOT quoted from HZO's filing, which <c>acqscan-v3</c> recognises through other clauses). They live here, beside the
/// real bodies, so the Application unit tests (what v4 answers) and the IntegrationTests control tests (what the frozen
/// <c>acqscan-v3</c> control answered) read the same strings.
/// </summary>
public static class AcquisitionScanV4ConstructedCases
{
    /// <summary>The mentions the Example Industries cases are scanned with.</summary>
    public static readonly string[] ExampleMentions = ["Example Industries, Inc.", "Example Industries"];

    /// <summary>The mentions the MarineMax cases are scanned with (the seed's name and alias).</summary>
    public static readonly string[] MarineMaxMentions = ["MarineMax, Inc.", "MarineMax"];

    /// <summary>
    /// Rule 2 — the spec's constructed MarineMax sentence, word for word: the company precedes "agreed to acquire", but
    /// Parent is the verb's subject ("pursuant to which Parent"), so it is a TARGET clause — the body's ONLY one. (The
    /// buyer is named in a separate party-list sentence and the consideration in a third.)
    /// </summary>
    public static readonly string MarineMaxPursuantToWhichParentAgreedToAcquire = MarineMaxFrame(
        "On August 10, 2026, MarineMax, Inc. entered into an Agreement and Plan of Merger with Parent, pursuant to which "
        + "Parent agreed to acquire the Company.");

    /// <summary>
    /// Rule 2 — the same shape SHORT enough that the company mention sits within the 90-character subject proximity of
    /// "agreed to acquire": the case <c>acqscan-v3</c>'s proximity veto read as acquirer-side. (The spec's own sentence
    /// puts "MarineMax" 99 characters before "agreed to acquire", beyond that proximity, so v3 missed it for a different
    /// reason: no target form read "Parent agreed to acquire the Company" at all.)
    /// </summary>
    public static readonly string MarineMaxShortPursuantToWhichParentAgreedToAcquire = MarineMaxFrame(
        "MarineMax, Inc. signed a merger agreement with Parent, pursuant to which Parent agreed to acquire the Company.");

    /// <summary>Rule 2, second bullet — the company itself governs the verb inside "pursuant to which": still the BUYER.</summary>
    public static readonly string PursuantToWhichCompanyAgreedToAcquire = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement.\n\n"
        + "On March 2, 2026, Example Industries, Inc. entered into an Agreement and Plan of Merger with Widget Holdings, "
        + "Inc., pursuant to which Example Industries agreed to acquire Widget Holdings, Inc. for $12.00 per share in cash.");

    /// <summary>
    /// Rule 2, BUYER-side (review case A) — the FILER is defined as ("Parent") and the TARGET as (the "Company"), so
    /// "pursuant to which Parent agreed to acquire the Company" names the filer BUYING. Must never be a target clause.
    /// </summary>
    public static readonly string BuyerSideParentAgreedToAcquireTheDefinedCompany = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement. On March 2, 2026, Example Industries, Inc., a Delaware "
        + "corporation (\"Parent\"), entered into an Agreement and Plan of Merger (the \"Merger Agreement\") with Widget "
        + "Holdings, Inc., a Nevada corporation (the \"Company\"), and Example Merger Sub, pursuant to which Parent agreed "
        + "to acquire the Company. Each share of the Company will be converted into the right to receive $12.00 in cash.");

    /// <summary>
    /// Rule 2, BUYER-side (review case D) — the TARGET is defined as (the "Company") and an undefined "Buyer" is the
    /// subject: "the Company" is not the filer, so this is not a target clause.
    /// </summary>
    public static readonly string BuyerSideUndefinedBuyerAgreedToAcquireTheDefinedCompany = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement. On March 2, 2026, Example Industries, Inc. and Example "
        + "Acquisition Corp., its wholly owned subsidiary, entered into an Agreement and Plan of Merger with Widget "
        + "Holdings, Inc. (the \"Company\"), pursuant to which Buyer agreed to acquire the Company. Each share of the "
        + "Company will be converted into the right to receive $12.00 in cash.");

    /// <summary>
    /// Rule 2, BUYER-side — a subsidiary defined as (the "Purchaser") buys the TARGET defined as (the "Company").
    /// </summary>
    public static readonly string BuyerSideSubsidiaryPurchaserAgreedToAcquireTheDefinedCompany = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement. On March 2, 2026, Example Industries, Inc. and Example "
        + "Acquisition Corp., its wholly owned subsidiary (the \"Purchaser\"), entered into an Agreement and Plan of "
        + "Merger with Widget Holdings, Inc. (the \"Company\"), pursuant to which Purchaser agreed to acquire the "
        + "Company. Each share of the Company will be converted into the right to receive $12.00 in cash.");

    /// <summary>
    /// Rule 2, BUYER-side with NO (the "Company") definition anywhere — only the filer's own role definition ("Parent"),
    /// well over 90 characters before the verb, says the subject is the filer: acquirer-side, never target.
    /// </summary>
    public static readonly string BuyerSideParentFarFromTheVerb = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement. On March 2, 2026, Example Industries, Inc., a Delaware "
        + "corporation (\"Parent\"), entered into an Agreement and Plan of Merger with Widget Holdings, Inc. and Example "
        + "Merger Sub, a Delaware corporation, pursuant to which Parent agreed to acquire the Company. Each share will be "
        + "converted into the right to receive $12.00 in cash.");

    /// <summary>
    /// Rule 2, BUYER-side (review probe M3) — the filer mention sits right before the TARGET's (the "Company")
    /// definition, but a VERB stands between them ("Example Industries, Inc. agreed to acquire Widget Holdings, Inc.
    /// (the "Company")"): the definition is not the filer's, so "Purchaser will acquire the Company" is not a target clause.
    /// </summary>
    public static readonly string BuyerSideVerbBeforeTheDefinedCompany = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement.\n\n"
        + "On March 2, 2026, Example Industries, Inc. agreed to acquire Widget Holdings, Inc. (the \"Company\") under an "
        + "Agreement and Plan of Merger by and among Widget Holdings, Inc., a Nevada corporation, Example Industries and "
        + "Example Purchaser LLC (\"Purchaser\"). Example Industries will fund Purchaser, and Purchaser will acquire the "
        + "Company. Each share of the Company will be converted into the right to receive $12.00 in cash.");

    /// <summary>
    /// Rule 2, BUYER-side (review probe M2) — a heading "Example Industries Acquires Widget Holdings, Inc. (the
    /// "Company")", then "Example Industries will cause its subsidiary Buyer to acquire the Company".
    /// </summary>
    public static readonly string BuyerSideHeadingAcquiresTheDefinedCompany = Pad(
        "Example Industries Acquires Widget Holdings, Inc. (the \"Company\") in Agreement and Plan of Merger\n\n"
        + "Example Industries will cause its subsidiary Buyer to acquire the Company. Each share of the Company will be "
        + "converted into the right to receive $12.00 in cash.");

    /// <summary>
    /// Rule 2, BUYER-side (review probe O) — probe M3 with an UNQUOTED "(the Company)" definition.
    /// </summary>
    public static readonly string BuyerSideVerbBeforeTheUnquotedCompany = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement.\n\n"
        + "On March 2, 2026, Example Industries, Inc. agreed to acquire Widget Holdings, Inc. (the Company) under an "
        + "Agreement and Plan of Merger by and among Widget Holdings, Inc., a Nevada corporation, Example Industries and "
        + "Example Purchaser LLC (\"Purchaser\"). Example Industries will fund Purchaser, and Purchaser will acquire the "
        + "Company. Each share of the Company will be converted into the right to receive $12.00 in cash.");

    /// <summary>
    /// Rule 2, TARGET-side (must stay recognised) — the filer's own (the "Company") definition directly after its name.
    /// </summary>
    public static readonly string TargetSideCompanyDefinedRightAfterTheFilersName = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement.\n\n"
        + "Example Industries, Inc. (the \"Company\") entered into an Agreement and Plan of Merger by and among Acme "
        + "Holdings, LLC (\"Parent\") and the Company, pursuant to which Parent agreed to acquire the Company. Each share "
        + "of the Company will be converted into the right to receive $12.00 in cash.");

    /// <summary>
    /// Rule 2, TARGET-side (must stay recognised) — an EX-2.1 party list: "…, and Example Industries, Inc., a Florida
    /// corporation (the "Company")", the filer's definition behind an entity apposition.
    /// </summary>
    public static readonly string TargetSidePartyListWithApposition = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement.\n\n"
        + "This Agreement and Plan of Merger is by and among Acme Holdings, LLC, a Delaware limited liability company "
        + "(\"Parent\"), and Example Industries, Inc., a Florida corporation (the \"Company\"), pursuant to which Parent "
        + "agreed to acquire the Company. Each share of the Company will be converted into the right to receive $12.00 in "
        + "cash.");

    /// <summary>
    /// Rule 2, TARGET-side (must stay recognised) — "Company Parties" is defined beside the filer's name; it is not a
    /// (the "Company") definition, so "the Company" is still the filer.
    /// </summary>
    public static readonly string TargetSideCompanyPartiesBesideTheFilersName = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement.\n\n"
        + "Example Industries, Inc. and its subsidiaries (the \"Company Parties\") entered into an Agreement and Plan of "
        + "Merger with Acme Holdings, LLC (\"Parent\"), pursuant to which Parent agreed to acquire the Company. Each share "
        + "of the Company will be converted into the right to receive $12.00 in cash.");

    /// <summary>
    /// Rule 4 — the acquirer is named ONLY in a title-case headline ("Acquired By"); no defined party, no "wholly owned
    /// subsidiary of {X}", no "by and among {X}" names it.
    /// </summary>
    public static readonly string TitleCaseAcquiredByHeadline = Pad(
        "Example Industries, Inc. Enters into Merger Agreement to be Acquired By Acme Holdings, Inc. for $12.00 Per Share\n\n"
        + "Under the merger agreement each outstanding share will be converted into the right to receive $12.00 in cash.");

    /// <summary>Rule 4 — the same headline in capitals ("ACQUIRED BY"); the captured name keeps the filing's case.</summary>
    public static readonly string UpperCaseAcquiredByHeadline = Pad(
        "EXAMPLE INDUSTRIES, INC. ENTERS INTO MERGER AGREEMENT TO BE ACQUIRED BY ACME HOLDINGS, INC. FOR $12.00 PER SHARE\n\n"
        + "Under the merger agreement each outstanding share will be converted into the right to receive $12.00 in cash.");

    /// <summary>Rule 5 — "dividend" in one sentence, the consideration in the NEXT: the consideration is kept.</summary>
    public static readonly string DividendSentenceThenConsiderationSentence = MergerFrame(
        "Pending the merger, the regular quarterly dividend will be suspended. Holders will receive $53.00 per share in cash "
        + "in the merger.");

    /// <summary>
    /// Rule 5 — "dividend" in the SAME clause but not governing the amount (spec 227's fixed 60-character look-behind
    /// excluded it): the consideration is kept.
    /// </summary>
    public static readonly string DividendInClauseNotGoverningTheAmount = MergerFrame(
        "Pending the merger the regular quarterly dividend is suspended and holders will receive $53.00 per share in cash "
        + "in the merger.");

    /// <summary>Rule 3 — the filer GOVERNS "completion of the acquisition of" through its alias's possessive: the BUYER.</summary>
    public static readonly string GovernedCompletionOfTheAcquisitionOf = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement.\n\n"
        + "Example Industries, Inc. (the \"Company\") signed a merger agreement and announced the Company's completion of the "
        + "acquisition of Widget Holdings, Inc.");

    /// <summary>
    /// Rule 3 — ANOTHER party governs "completion of the acquisition of", and its object is the company: not the buyer.
    /// Under <c>acqscan-v3</c> the company mention 72 characters before the phrase made this acquirer-side.
    /// </summary>
    public static readonly string UngovernedCompletionOfTheAcquisitionOf = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement.\n\n"
        + "Example Industries, Inc. signed a merger agreement and expects Parent's completion of the acquisition of the "
        + "Company in 2027.");

    /// <summary>
    /// Rule 1 — a target clause with NO explicit merger phrase and no "definitive agreement … acquire" in that clause
    /// (only the heading's "Material Definitive Agreement" elsewhere): not a merger filing.
    /// </summary>
    public static readonly string TargetClauseWithoutMergerContext = Pad(
        "Item 1.01 Entry into a Material Definitive Agreement.\n\n"
        + "On March 2, 2026, Acme Holdings, Inc., a Delaware corporation (\"Parent\"), and Example Industries, Inc. signed a "
        + "support agreement. Acme Sub will merge with and into Example Industries, Inc., and each share will be converted "
        + "into the right to receive $12.00 in cash.");

    /// <summary>The MarineMax frame: a party list naming the buyer, then <paramref name="targetSentence"/>, then the consideration.</summary>
    private static string MarineMaxFrame(string targetSentence) =>
        Pad(
            "Item 1.01 Entry into a Material Definitive Agreement.\n\n"
            + "The Merger Agreement (the \"Agreement\") is by and among Safe Harbor Marinas, LLC (\"Parent\") and the Company.\n\n"
            + targetSentence
            + " Each share of common stock of the Company will be converted into the right to receive $53.00 in cash (the "
            + "\"Merger Consideration\").");

    /// <summary>A named, target-side merger agreement around <paramref name="considerationSentence"/>.</summary>
    private static string MergerFrame(string considerationSentence) =>
        Pad(
            "Item 1.01 Entry into a Material Definitive Agreement. On March 2, 2026, Example Industries, Inc. (the "
            + "\"Company\") entered into an Agreement and Plan of Merger with Acme Holdings, Inc., a Delaware "
            + "corporation (\"Parent\"). Subject to its terms, Acme Merger Sub will merge with and into Example "
            + "Industries, Inc., with the Company surviving as a wholly owned subsidiary of Parent. "
            + considerationSentence);

    /// <summary>Pads a short constructed body past the scan's degenerate-fetch floor without adding any rule-relevant word.</summary>
    private static string Pad(string text) =>
        text + "\n\nItem 9.01 Financial Statements and Exhibits. (d) Exhibits. 99.1 Press release, furnished herewith "
        + "and not filed for purposes of Section 18 of the Securities Exchange Act of 1934.";
}
