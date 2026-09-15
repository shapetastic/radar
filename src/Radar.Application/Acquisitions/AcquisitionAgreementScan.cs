using System.Globalization;
using System.Text.RegularExpressions;

namespace Radar.Application.Acquisitions;

/// <summary>
/// Why one item-1.01 filing did or did not yield a <see cref="PendingAcquisitionRecord"/> (spec 217 §1).
/// EVERY outcome is a counted outcome: the recognition harness and the live pass both tally these, so a
/// filing is never silently skipped. Values start at 1 so a defaulted zero is an undefined member.
/// </summary>
public enum AcquisitionScanOutcome
{
    /// <summary>Both legs held verbatim: the company is the TARGET and a per-share consideration is stated.</summary>
    Recognised = 1,

    /// <summary>
    /// Leg (a) failed: the text carries no "Agreement and Plan of Merger" / "merger agreement" /
    /// "definitive agreement … to be acquired" phrase at all. The overwhelming majority of item-1.01
    /// filings (credit agreements, leases, supply contracts) land here.
    /// </summary>
    NoMergerAgreement = 2,

    /// <summary>
    /// Leg (a) failed: a merger-agreement phrase exists, but no sentence puts the SUBJECT company in the
    /// target position ("acquired by", "merge with and into", "acquisition of"). Fail-closed by design.
    /// </summary>
    CompanyNotTarget = 3,

    /// <summary>
    /// Leg (a) failed BY CONSTRUCTION because the company is the ACQUIRER (its name governs "to acquire" /
    /// "wholly owned subsidiary of {company}"). Counted separately from
    /// <see cref="CompanyNotTarget"/> because it is a different fact about the filing, not a weaker
    /// version of the same one.
    /// </summary>
    CompanyIsAcquirer = 4,

    /// <summary>
    /// Leg (a) held but the acquirer is not NAMED anywhere the scan can read it. Nothing is persisted: a
    /// record whose acquirer reads "unknown" would render an acquisition banner naming nobody, and
    /// inventing a name is worse. Counted, never coerced.
    /// </summary>
    AcquirerNotNamed = 5,

    /// <summary>
    /// Leg (b) failed: leg (a) held, but no per-share cash/stock consideration is stated (no
    /// "$X per share", no "right to receive $X", no exchange ratio).
    /// </summary>
    NoStatedConsideration = 6,

    /// <summary>
    /// The filing body was empty or implausibly short — the fetch was degenerate, so there was nothing to
    /// scan. NOT a "no acquisition" answer: a later run re-attempts it (the spec-114 precedent).
    /// </summary>
    EmptyBody = 7,

    /// <summary>
    /// BOTH legs held, but the final verbatim re-check failed: a quote the result would have carried is not
    /// an ordinal substring of the body that was scanned. It has its OWN bucket because it is neither a
    /// leg-(a) nor a leg-(b) failure — it means the scan and the verification disagree about the text,
    /// which is a DEFECT in this code (the quotes are sliced from that very body), not a fact about the
    /// filing. Folding it into either leg's count would hide a bug inside an expected number. Nothing is
    /// persisted, and a non-zero count in the live distribution is a finding to investigate.
    /// </summary>
    VerbatimCheckFailed = 8,
}

/// <summary>
/// One scan's whole answer: the outcome, and — on <see cref="AcquisitionScanOutcome.Recognised"/> — the
/// verbatim-verified facts a <see cref="PendingAcquisitionRecord"/> is built from. Every non-recognised
/// outcome carries null facts; there is no partial record.
/// </summary>
public sealed record AcquisitionScanResult(
    AcquisitionScanOutcome Outcome,
    string? AcquirerName = null,
    string? ConsiderationPerShare = null,
    string? ConsiderationCurrency = null,
    AcquisitionConsiderationKind? ConsiderationKind = null,
    string? ConsiderationQuote = null,
    string? TargetQuote = null)
{
    public bool IsRecognised => Outcome == AcquisitionScanOutcome.Recognised;

    internal static AcquisitionScanResult NotRecognised(AcquisitionScanOutcome outcome) => new(outcome);
}

/// <summary>
/// SPEC 217 §1, tightened by SPEC 227 (<c>acqscan-v2</c>, the rule) and re-versioned by SPEC 228 (<c>acqscan-v3</c>,
/// the read — the rule below is v2's, unchanged; see <see cref="Version"/>): the DETERMINISTIC,
/// PURE, FAIL-CLOSED recognition of a pending acquisition OF THE SUBJECT COMPANY from an item-1.01 8-K's own
/// text. No AI, no clock, no I/O, no randomness (AD-3): the same text and the same company names always yield
/// the same answer.
/// <para>
/// <b>Why v1 was replaced (spec 227).</b> Measured on the live store, <c>acqscan-v1</c> made two
/// recognitions and one was false: Steven Madden (SHOO, accession 0001641172-25-008949 — a Q1 results 8-K
/// with a credit agreement attached) was recognised because (a) a press-release HEADING with no terminal
/// punctuation ran into the dateline, so "Steven Madden" 60-odd characters after "Acquisition of Kurt
/// Geiger ~ LONG ISLAND CITY, N.Y., May 7, 2025 –" was read as the OBJECT of "acquisition of" (SHOO was
/// the BUYER); (b) the FIRST <c>$X per share</c> sentence in the document — a quarterly DIVIDEND — was
/// taken as the consideration; and (c) the credit agreement's defined role "Lead Borrower" was accepted as
/// the acquirer. The genuine recognition (MarineMax, HZO, 0001193125-26-341302) carried the par value
/// ($0.001) as its price and the defined term "Parent" as its acquirer. v2 closes each hole; the rules
/// below say how. (⚠ AMENDED in place by spec 228: neither body was the 8-K. The reader took SHOO's EX-10.1
/// credit agreement and HZO's EX-2.1 merger agreement as "the primary document", so the credit agreement's
/// signature pages ran straight into the EX-99.1 release, and HZO's par-value recital came from the merger
/// agreement. <c>acqscan-v3</c> reads the real 8-K; see <see cref="Version"/>.)
/// </para>
/// <para>
/// <b>Why it must read the filing.</b> A title-only rule fires on every "Entry into a Material Definitive
/// Agreement" — and the accrued store held 178 item-1.01 filings on 2026-09-08 (190 on 2026-09-14), overwhelmingly credit agreements, leases
/// and supply contracts. The keyword extractor's "material definitive agreement" rule (as it stood until
/// spec 226) read MarineMax's $1.5B all-cash sale of the whole company as a Positive
/// <c>StrategicPartnership</c> and the 2026-08-10 report labelled it <b>Thesis improving</b>. (Spec 226,
/// <c>radar-keyword-rules-v9</c>, has since made every item-heading read a Neutral <c>CorporateAction</c> —
/// the title says an agreement exists, never whether it is good news — but only THIS scan can say the
/// agreement is a takeover of the company.) Recognition therefore reads the text, and it recognises only when
/// BOTH legs hold:
/// </para>
/// <list type="number">
/// <item><b>Leg (a) — the company is the TARGET.</b> A merger-agreement phrase is present, AND one CLAUSE
/// puts the company's own name (or an alias) in the target POSITION relative to "acquired by" /
/// "merge with and into" / "acquisition of … by …". Position matters: an acquirer-side 8-K contains all the
/// same words ("Merger Sub, a wholly owned subsidiary of {filer}, will merge with and into {target}"), so an
/// unordered co-occurrence test would close the wrong company's thesis.
/// <para>
/// <b>The v2 leg-(a) mechanism is BOTH candidates spec 227 named, deliberately.</b> The body is produced by
/// the shared <c>EvidenceNormalizer</c>, which KEEPS the filing's source line breaks — but those are the
/// HTML source's hard wrap, not sentence structure: the live SHOO body reads "Steve\nMadden Announces…" and
/// "LONG\nISLAND CITY", and HZO's merger-agreement sentences wrap mid-clause. Splitting on EVERY line break
/// would therefore cut real sentences (and real company names) in half, so v2 does not. It splits a clause
/// only on boundaries that are structural in that output: terminal punctuation (as v1), a BLANK line (the
/// normalizer emits one only where the source had a block boundary — a heading, a paragraph), the
/// <c>~</c> heading ornament, and a SPACED en/em dash (the press-release dateline separator, "May 7, 2025 –
/// Steven Madden"). That alone would have separated SHOO's heading from its dateline. It is not trusted
/// alone, because a heading can be laid out with none of those marks; so, in addition, a phrase the company
/// must FOLLOW ("merge with and into", "acquisition of") binds only a DIRECT object — nothing but
/// whitespace, quotation marks and the articles <c>a</c>/<c>an</c>/<c>the</c> may sit between the phrase
/// and the mention. The 90-character proximity v1 applied to those phrases is gone; it survives only for
/// the phrases the company PRECEDES as the subject ("MarineMax Enters into Definitive Agreement to be
/// Acquired by …"), where a subject legitimately sits several words from its verb.
/// </para>
/// <para>
/// "acquisition of" is the weakest phrase — it reads identically in "announces completion of acquisition of
/// X" (the filer is the buyer) and "the acquisition of the Company by Parent" — so v2 keeps it only when the
/// company is its direct object AND a capitalised <c>by {acquirer}</c> follows in the same clause. And
/// "completion of (the) acquisition of" / "completes|completed (the) acquisition of" governed by the company
/// are acquirer-side vetoes.
/// </para></item>
/// <item><b>Leg (b) — a stated per-share MERGER consideration.</b> "$53.00 per share in cash", "converted into
/// the right to receive $53.00", or a stated exchange ratio — in a sentence that carries MERGER VOCABULARY
/// (<see cref="ConsiderationVocabulary"/>). An amount that is a par value, a dividend, an exercise price, a
/// conversion price or the price of an offering is EXCLUDED (<see cref="ConsiderationExclusions"/> — the
/// one place those rules live), and scanning continues past it within the same sentence and after it. A
/// merger agreement with no qualifying per-share consideration is not recognised.</item>
/// </list>
/// <para>
/// <b>The acquirer must be a NAME.</b> A bare defined-term role ("Parent", "Lead Borrower", "Merger Sub",
/// the rest of <see cref="DefinedTermRoles"/>) is never accepted from any form; the scan keeps looking and
/// otherwise fails <see cref="AcquisitionScanOutcome.AcquirerNotNamed"/>. A banner naming a placeholder is
/// worse than one the design refuses to print.
/// </para>
/// <para>
/// <b>Fail-closed, and every failure NAMED.</b> Each way a filing can fall out is its own
/// <see cref="AcquisitionScanOutcome"/>, tallied by the caller, because a false positive here CLOSES A LIVE
/// THESIS — strictly worse than missing one. In particular an acquirer-side filing
/// (<see cref="AcquisitionScanOutcome.CompanyIsAcquirer"/>) is vetoed before any target pattern is tried.
/// </para>
/// <para>
/// <b>Verbatim verification (the spec-215/216 rule).</b> Both quotes the result carries are slices of the
/// supplied text and are re-checked with an ordinal <c>Contains</c> before the result is returned, so a
/// persisted record can never quote a sentence the filing does not contain.
/// </para>
/// </summary>
public static partial class AcquisitionAgreementScan
{
    /// <summary>
    /// The versioned identity of THIS scan. It is part of every record's content-derived id and is stamped
    /// on the record, and — since spec 217 §2 — it is hashed into <c>ScoringConfigVersion</c> through
    /// <c>SignalSourceDescriptor</c>'s <c>acq=</c> field, because a recognition changes which signals reach
    /// scoring (the <c>CorporateAction</c> supersede). Bump it when the recognition RULE changes; not when
    /// a comment does.
    /// <para>
    /// Since spec 227 it is ALSO part of recognition identity end to end: the acquisitions store keys a
    /// record's durable path by it, the recognition pass rescans a filing whose only record carries an older
    /// version, and <see cref="PendingAcquisitions"/> admits only records stamped with THIS value (an older
    /// one is counted as <see cref="PendingAcquisitions.RetiredByScanVersion"/>, never silently dropped).
    /// </para>
    /// <para>
    /// <b>Since spec 228 the version covers the READ as well as the rule.</b> A recognition is a function of WHICH
    /// text was read and of the rule that scans it, so a change to what the item-1.01 body reader supplies (which
    /// documents, in which order) changes the answer for the same accession exactly as a rule change does, and
    /// bumps this value. <c>acqscan-v3</c> changed no rule: it is the first version whose body is the filing's real
    /// primary 8-K document (see <see cref="Scan"/>'s <c>text</c>).
    /// History: <c>acqscan-v1</c> (spec 217) → <c>acqscan-v2</c> (spec 227, the rule) → <c>acqscan-v3</c> (spec 228,
    /// the read).
    /// </para>
    /// </summary>
    public const string Version = "acqscan-v3";

    /// <summary>
    /// Minimum plausible body length (chars, after trimming) for a scan to be authoritative. Shorter means
    /// the fetch was degenerate (an interstitial/error page stripped to almost nothing), so the answer is
    /// <see cref="AcquisitionScanOutcome.EmptyBody"/> and a later run re-attempts it — the spec-114
    /// precedent. It IS part of what the scan answers, so changing it is a change to the rule and
    /// must bump <see cref="Version"/> with everything else.
    /// </summary>
    internal const int MinPlausibleBodyLength = 200;

    /// <summary>
    /// How far BEFORE a phrase the company may sit and still be its SUBJECT (the company-first phrases and the
    /// acquirer-side vetoes), and how far after a "subsidiary of" veto phrase it may start. Since spec 227 it
    /// no longer governs the company-AFTER target phrases — those bind a direct object only.
    /// </summary>
    private const int ObjectProximity = 90;

    /// <summary>
    /// SPEC 227 — how far after the company mention the <c>by {acquirer}</c> that qualifies an
    /// "acquisition of {company}" clause may start ("the acquisition of MarineMax, Inc., a Florida
    /// corporation, by Parent").
    /// </summary>
    private const int AcquisitionOfByProximity = 60;

    /// <summary>The phrases that make a filing a merger-agreement filing at all (leg (a), part 1).</summary>
    private static readonly string[] MergerAgreementPhrases =
    [
        "agreement and plan of merger",
        "merger agreement",
        "plan of merger",
    ];

    /// <summary>
    /// The phrases that, together with a merger-agreement phrase, make a "definitive agreement" filing a
    /// merger filing. "Definitive agreement" alone is not enough — it is boilerplate on supply and
    /// licensing deals too — so it counts only beside an explicit target phrase.
    /// </summary>
    private const string DefinitiveAgreementPhrase = "definitive agreement";

    /// <summary>
    /// Ordered acquirer-side vetoes evaluated against a sentence BEFORE any target pattern. Each is a
    /// format string whose <c>{0}</c> is the company mention; a hit means the company governs the verb (it
    /// is the buyer), so the sentence can never establish that it is the target.
    /// </summary>
    private static readonly string[] AcquirerVetoAfterCompany =
    [
        "to acquire",
        "will acquire",
        "has acquired",
        "have acquired",
        "agreed to acquire",
        "completed the acquisition of",

        // SPEC 227: the SHOO shape — "Steve Madden … Announces Completion of Acquisition of Kurt Geiger". The
        // filer completing an acquisition is the BUYER, however the heading words it.
        "completed acquisition of",
        "completes acquisition of",
        "completes the acquisition of",
        "completion of acquisition of",
        "completion of the acquisition of",
    ];

    private static readonly string[] AcquirerVetoBeforeCompany =
    [
        "wholly owned subsidiary of",
        "wholly-owned subsidiary of",
        "subsidiary of",
    ];

    /// <summary>Target phrases the company must appear BEFORE (the company is the thing acquired).</summary>
    private static readonly string[] TargetPhrasesAfterCompany =
    [
        "to be acquired by",
        "will be acquired by",
        "would be acquired by",
        "is being acquired by",
        "acquired by",
        "will merge with and into",
        "shall merge with and into",
    ];

    /// <summary>
    /// Target phrases the company must appear AFTER as the phrase's DIRECT object (spec 227: nothing but
    /// whitespace, quotation marks and articles between them — see <see cref="IsDirectObject"/>).
    /// </summary>
    private static readonly string[] TargetPhrasesBeforeCompany =
    [
        "merge with and into",
        "merged with and into",
    ];

    /// <summary>
    /// SPEC 227 — the weakest target phrase, held to a stricter rule than
    /// <see cref="TargetPhrasesBeforeCompany"/>: the company must be its direct object AND a capitalised
    /// <c>by {acquirer}</c> must follow in the same clause ("the acquisition of MarineMax by Safe Harbor").
    /// Without the <c>by</c>, "Announces Completion of Acquisition of X" reads identically whether the filer
    /// is the buyer or the bought.
    /// </summary>
    private const string AcquisitionOfPhrase = "acquisition of";

    /// <summary>
    /// SPEC 227 — the words a DIRECT object may be separated from its phrase by: articles only (quotation
    /// marks and whitespace are also allowed; see <see cref="IsDirectObject"/>).
    /// </summary>
    private static readonly string[] DirectObjectFillerWords = ["a", "an", "the"];

    /// <summary>
    /// SPEC 227 — the clause-boundary separators BEYOND terminal punctuation (see the class remarks for why
    /// a single line break is deliberately NOT one): the <c>~</c> heading ornament and a spaced en/em dash.
    /// A blank line is the third boundary and is detected structurally in <see cref="SplitSentences"/>.
    /// </summary>
    private static readonly char[] HeadingSeparatorChars = ['~'];

    /// <inheritdoc cref="HeadingSeparatorChars"/>
    private static readonly char[] SpacedDashChars = ['–', '—'];

    /// <summary>
    /// SPEC 227 — defined-term ROLES a filing gives its parties. A candidate acquirer that is only one of
    /// these (case-insensitive, after a leading "the") is never a name: SHOO's v1 record named
    /// "Lead Borrower" and HZO's named "Parent". Data, in ONE place; a candidate that merely CONTAINS
    /// "merger sub" is also refused (the acquisition vehicle, not the acquirer — the v1 rule, kept).
    /// </summary>
    private static readonly string[] DefinedTermRoles =
    [
        "acquiror", "acquirer", "administrative agent", "agent", "borrower", "borrowers", "buyer", "buyers",
        "collateral agent", "company", "guarantor", "guarantors", "holdings", "investor", "investors",
        "issuer", "lead borrower", "lender", "lenders", "merger sub", "merger subsidiary", "parent",
        "purchaser", "purchasers", "seller", "sellers", "sponsor", "surviving corporation", "target",
        "trustee",
    ];

    /// <summary>Where, relative to a <c>$X per share</c> amount, an exclusion phrase must sit to exclude it.</summary>
    private enum ExclusionPlacement
    {
        /// <summary>In the text leading up to the amount ("par value $0.001 per share").</summary>
        Before = 1,

        /// <summary>Before OR just after the amount ("a dividend of $0.21 per share" / "$0.21 per share dividend").</summary>
        BeforeOrAfter = 2,
    }

    /// <summary>
    /// One named reason a per-share amount is NOT a merger consideration (spec 227 leg (b)).
    /// <paramref name="RequiresSentenceTerm"/>, when non-empty, further requires the SENTENCE to contain one
    /// of those terms — "purchase price" is only an offering's price beside an offering.
    /// </summary>
    private sealed record ConsiderationExclusion(
        string Name, string Phrase, ExclusionPlacement Placement, string[] RequiresSentenceTerm);

    /// <summary>
    /// SPEC 227 — the ONE place the per-share amounts that are not a merger consideration are named. v1 took
    /// the FIRST <c>$X per share</c> in the document, and so recorded SHOO's quarterly dividend ($0.21) and
    /// HZO's par value ($0.001) as deal prices.
    /// </summary>
    private static readonly ConsiderationExclusion[] ConsiderationExclusions =
    [
        new("par-value", "par value", ExclusionPlacement.Before, []),
        new("dividend", "dividend", ExclusionPlacement.BeforeOrAfter, []),
        new("exercise-price", "exercise price", ExclusionPlacement.Before, []),
        new("conversion-price", "conversion price", ExclusionPlacement.Before, []),
        new("offering-price", "offering price", ExclusionPlacement.Before, []),
        new("offering-price", "price to the public", ExclusionPlacement.Before, []),
        new("offering-purchase-price", "purchase price", ExclusionPlacement.Before, ["offering", "private placement"]),
    ];

    /// <summary>How far before a <c>$</c> an exclusion phrase may start (bounded also by the previous amount).</summary>
    private const int ExclusionLookBehind = 60;

    /// <summary>How far after an amount match an <see cref="ExclusionPlacement.BeforeOrAfter"/> phrase may start.</summary>
    private const int ExclusionLookAhead = 25;

    /// <summary>
    /// SPEC 227 — the merger vocabulary a consideration sentence must carry. Any ONE of: an explicit merger
    /// term in this list ("exchange ratio" is here because a stated ratio is the stock-deal form of the same
    /// fact); "right to receive $X" (matched structurally by <c>RightToReceiveCashRegex</c> itself); or
    /// <see cref="PerShareInCashTerm"/> together with a deal word (<see cref="PerShareInCashDealWords"/>).
    /// </summary>
    private static readonly string[] ConsiderationVocabulary = ["merger consideration", "offer price", "exchange ratio"];

    /// <inheritdoc cref="ConsiderationVocabulary"/>
    private const string PerShareInCashTerm = "per share in cash";

    /// <inheritdoc cref="ConsiderationVocabulary"/>
    private static readonly string[] PerShareInCashDealWords = ["acquired", "acquire", "merger", "transaction"];

    /// <summary>
    /// Runs the current scan (<see cref="Version"/>) over one item-1.01 filing's text.
    /// </summary>
    /// <param name="text">
    /// The filing body: the primary 8-K document and its EX-99.1 exhibit, stripped to plain text by the
    /// shared normalizer and concatenated. Never null. (⚠ AMENDED in place by spec 228: under <c>acqscan-v1</c>
    /// and <c>acqscan-v2</c> that was the DESIGN, not the practice — the reader dropped the iXBRL primary's index
    /// row, so 153 of 154 live bodies were an EX-10.1 / EX-2.1 / EX-1.1 / EX-4.1 / EX-5.1 / EX-3.1 exhibit plus
    /// EX-99.1 when present, and only 1 began with the 8-K cover. Since <c>acqscan-v3</c> the body is the declared or
    /// form-typed primary 8-K document, then EX-99.1 and then the EX-2.1 merger agreement, each when the index
    /// shows one; no EX-10.* material contract is ever appended.)
    /// </param>
    /// <param name="companyMentions">
    /// The subject company's own name and aliases (and its ticker), as the seed records them. A mention is
    /// matched case-insensitively as a whole phrase; an empty/blank mention is ignored, and an empty LIST
    /// means the company cannot be located in the text at all, which fails leg (a) closed.
    /// </param>
    public static AcquisitionScanResult Scan(string text, IReadOnlyList<string> companyMentions)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(companyMentions);

        var body = text.Trim();
        if (body.Length < MinPlausibleBodyLength)
        {
            return AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.EmptyBody);
        }

        var lower = body.ToLowerInvariant();
        var mentions = companyMentions
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim().ToLowerInvariant())
            // A one/two-character "alias" would match inside ordinary words; require a real token.
            .Where(m => m.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Leg (a), part 1. "Agreement and Plan of Merger" / "merger agreement" / "plan of merger" is a
        // merger filing ON ITS OWN. "Definitive agreement" is NOT: it is the boilerplate title of EVERY
        // item-1.01 8-K ("Entry into a Material Definitive Agreement"), so on its own it recognises credit
        // agreements, leases and supply contracts alike — which is the entire reason this scan exists. It
        // therefore counts only BESIDE an explicit target sentence (spec 217 §1's
        // "definitive agreement … to be acquired"), which is why the decision is deferred until after the
        // sentence walk below.
        var hasExplicitMergerPhrase =
            MergerAgreementPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal));
        var hasDefinitiveAgreementPhrase =
            lower.Contains(DefinitiveAgreementPhrase, StringComparison.Ordinal);
        if (!hasExplicitMergerPhrase && !hasDefinitiveAgreementPhrase)
        {
            return AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.NoMergerAgreement);
        }

        var sentences = SplitSentences(body);

        // Leg (a). Walk the sentences ONCE, classifying each as acquirer-side, target-side or neither.
        // The acquirer-side veto is evaluated first WITHIN each sentence, so a sentence that names the
        // company as the buyer can never also be read as naming it as the target.
        string? targetSentence = null;
        var sawAcquirerSide = false;
        foreach (var sentence in sentences)
        {
            var sentenceLower = sentence.ToLowerInvariant();
            var positions = MentionPositions(sentenceLower, mentions);
            if (positions.Count == 0)
            {
                continue;
            }

            if (IsAcquirerSide(sentenceLower, positions))
            {
                sawAcquirerSide = true;
                continue;
            }

            if (targetSentence is null && IsTargetSide(sentence, sentenceLower, positions, mentions))
            {
                targetSentence = sentence;
            }
        }

        if (targetSentence is null)
        {
            // The acquirer-side finding is reported ahead of the generic one: "this filing is the company
            // BUYING someone" is a different fact about the filing, not a weaker version of "no target
            // sentence". Without an explicit merger phrase the honest answer is that this is not a merger
            // filing at all — the credit-agreement / lease / supply-contract population.
            return AcquisitionScanResult.NotRecognised(
                sawAcquirerSide ? AcquisitionScanOutcome.CompanyIsAcquirer
                : hasExplicitMergerPhrase ? AcquisitionScanOutcome.CompanyNotTarget
                : AcquisitionScanOutcome.NoMergerAgreement);
        }

        var acquirer = ExtractAcquirer(body, targetSentence, mentions);
        if (acquirer is null)
        {
            return AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.AcquirerNotNamed);
        }

        // Leg (b): a per-share consideration, stated in ONE sentence so the quote is a real sentence rather
        // than a stitched-together fragment.
        var consideration = ExtractConsideration(sentences);
        if (consideration is null)
        {
            return AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.NoStatedConsideration);
        }

        // Verbatim verification (spec 215/216 rule), re-checked rather than assumed: EACH quote must be an
        // ordinal substring of the body the reader actually fetched.
        //
        // The two checks are SPLIT into separate branches, and that is not pedantry. The not-recognised
        // tally must be split by WHICH leg failed (spec 217 §1), and a single combined `||` returned the
        // leg-(b) bucket (`NoStatedConsideration`) even when it was the leg-(a) TARGET quote that failed —
        // a leg-(a) failure counted as a leg-(b) one, in the tally whose whole purpose is that split.
        //
        // Both branches report VerbatimCheckFailed, which is its OWN bucket rather than either leg's,
        // because a failure here is a DEFECT IN THIS SCAN and not a fact about the filing: every quote is a
        // slice of the very body being checked, so neither branch is reachable while the slicing is
        // correct. Attributing it to a leg would hide a bug inside an expected number; a non-zero count in
        // the live distribution is a finding to investigate.
        if (!body.Contains(targetSentence, StringComparison.Ordinal))
        {
            return AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.VerbatimCheckFailed);
        }

        if (!body.Contains(consideration.Quote, StringComparison.Ordinal))
        {
            return AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.VerbatimCheckFailed);
        }

        return new AcquisitionScanResult(
            AcquisitionScanOutcome.Recognised,
            acquirer,
            consideration.Amount,
            consideration.Currency,
            consideration.Kind,
            consideration.Quote,
            targetSentence);
    }

    /// <summary>
    /// Every start index at which one of <paramref name="mentions"/> occurs in the (already lower-cased)
    /// sentence, as a whole phrase (bounded by non-letter/digit on both sides so "hzo" cannot match inside
    /// a longer word).
    /// </summary>
    private static List<int> MentionPositions(string sentenceLower, IReadOnlyList<string> mentions)
    {
        var positions = new List<int>();
        foreach (var mention in mentions)
        {
            var from = 0;
            while (from <= sentenceLower.Length - mention.Length)
            {
                var at = sentenceLower.IndexOf(mention, from, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                if (IsWholePhrase(sentenceLower, at, mention.Length))
                {
                    positions.Add(at);
                }

                from = at + 1;
            }
        }

        positions.Sort();
        return positions;
    }

    private static bool IsWholePhrase(string text, int start, int length)
    {
        var before = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
        var endIndex = start + length;
        var after = endIndex >= text.Length || !char.IsLetterOrDigit(text[endIndex]);
        return before && after;
    }

    /// <summary>
    /// True when this sentence names the company as the BUYER: it governs an acquire verb, or a merger sub
    /// is described as its subsidiary. Evaluated before any target pattern (spec 217 §1).
    /// </summary>
    private static bool IsAcquirerSide(string sentenceLower, List<int> companyPositions)
    {
        foreach (var phrase in AcquirerVetoAfterCompany)
        {
            var at = sentenceLower.IndexOf(phrase, StringComparison.Ordinal);
            while (at >= 0)
            {
                // The company must be the SUBJECT: it appears before the verb and close enough to govern it.
                if (companyPositions.Any(p => p < at && at - p <= ObjectProximity))
                {
                    return true;
                }

                at = sentenceLower.IndexOf(phrase, at + 1, StringComparison.Ordinal);
            }
        }

        foreach (var phrase in AcquirerVetoBeforeCompany)
        {
            var at = sentenceLower.IndexOf(phrase, StringComparison.Ordinal);
            while (at >= 0)
            {
                var end = at + phrase.Length;
                if (companyPositions.Any(p => p >= end && p - end <= ObjectProximity))
                {
                    return true;
                }

                at = sentenceLower.IndexOf(phrase, at + 1, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>
    /// True when this clause puts the company in the TARGET position (spec 217 §1 leg (a), tightened by spec
    /// 227): the company PRECEDES a company-first phrase within <see cref="ObjectProximity"/>, or it is the
    /// DIRECT object of a company-after phrase, or it is the direct object of "acquisition of" with a
    /// capitalised <c>by {acquirer}</c> following it.
    /// </summary>
    /// <param name="sentence">The clause in its original case (the <c>by {Acquirer}</c> capital check reads it).</param>
    /// <param name="sentenceLower">The same clause lower-cased (same length; every index is shared).</param>
    /// <param name="companyPositions">Start indices of whole-phrase company mentions in the clause.</param>
    /// <param name="mentions">The lower-cased mentions, so a mention's END can be located.</param>
    private static bool IsTargetSide(
        string sentence, string sentenceLower, List<int> companyPositions, IReadOnlyList<string> mentions)
    {
        foreach (var phrase in TargetPhrasesAfterCompany)
        {
            var at = sentenceLower.IndexOf(phrase, StringComparison.Ordinal);
            while (at >= 0)
            {
                if (companyPositions.Any(p => p < at && at - p <= ObjectProximity))
                {
                    return true;
                }

                at = sentenceLower.IndexOf(phrase, at + 1, StringComparison.Ordinal);
            }
        }

        foreach (var phrase in TargetPhrasesBeforeCompany)
        {
            var at = sentenceLower.IndexOf(phrase, StringComparison.Ordinal);
            while (at >= 0)
            {
                var end = at + phrase.Length;
                if (companyPositions.Any(p => p >= end && IsDirectObject(sentenceLower, end, p)))
                {
                    return true;
                }

                at = sentenceLower.IndexOf(phrase, at + 1, StringComparison.Ordinal);
            }
        }

        var acquisitionAt = sentenceLower.IndexOf(AcquisitionOfPhrase, StringComparison.Ordinal);
        while (acquisitionAt >= 0)
        {
            var end = acquisitionAt + AcquisitionOfPhrase.Length;
            foreach (var p in companyPositions)
            {
                if (p >= end
                    && IsDirectObject(sentenceLower, end, p)
                    && HasCapitalisedByAfter(sentence, MentionEnd(sentenceLower, p, mentions)))
                {
                    return true;
                }
            }

            acquisitionAt = sentenceLower.IndexOf(AcquisitionOfPhrase, acquisitionAt + 1, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// SPEC 227 — true when only whitespace, quotation marks and the articles in
    /// <see cref="DirectObjectFillerWords"/> sit between a phrase's end and a company mention's start, i.e.
    /// the company is the phrase's DIRECT object. "acquisition of Kurt Geiger ~ LONG ISLAND CITY, N.Y., May 7,
    /// 2025 – Steven Madden" fails it by construction, whatever the clause split did.
    /// </summary>
    private static bool IsDirectObject(string sentenceLower, int phraseEnd, int mentionStart)
    {
        if (mentionStart < phraseEnd)
        {
            return false;
        }

        var gap = sentenceLower[phraseEnd..mentionStart];
        var tokens = gap.Split(
            [' ', '\t', '\n', '\r', '"', '\'', '“', '”', '‘', '’'],
            StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(t => Array.IndexOf(DirectObjectFillerWords, t) >= 0);
    }

    /// <summary>The end index of the LONGEST mention that starts at <paramref name="start"/>.</summary>
    private static int MentionEnd(string sentenceLower, int start, IReadOnlyList<string> mentions)
    {
        var end = start;
        foreach (var mention in mentions)
        {
            if (string.CompareOrdinal(sentenceLower, start, mention, 0, mention.Length) == 0
                && start + mention.Length <= sentenceLower.Length)
            {
                end = Math.Max(end, start + mention.Length);
            }
        }

        return end;
    }

    /// <summary>
    /// SPEC 227 — true when <c>by</c> followed by a capitalised word (or an opening quote) starts within
    /// <see cref="AcquisitionOfByProximity"/> characters after <paramref name="from"/>, in the same clause.
    /// </summary>
    private static bool HasCapitalisedByAfter(string sentence, int from)
    {
        var window = sentence[from..Math.Min(sentence.Length, from + AcquisitionOfByProximity + 4)];
        var match = AcquisitionByPartyRegex().Match(window);
        return match.Success && match.Index <= AcquisitionOfByProximity;
    }

    /// <summary>
    /// The acquirer's name as the filing states it, or null when the scan cannot read one (counted as
    /// <see cref="AcquisitionScanOutcome.AcquirerNotNamed"/> — never "unknown", never invented). Four
    /// ordered forms, first hit wins, so the answer is deterministic:
    /// <list type="number">
    /// <item>the target sentence's own <c>acquired by {X}</c>;</item>
    /// <item>the defined-term form <c>{X} … ("Parent")</c> / <c>("Purchaser")</c> / <c>("Buyer")</c>;</item>
    /// <item><c>wholly owned subsidiary of {X}</c> anywhere in the body;</item>
    /// <item>the first party after <c>by and among</c> that is not the company itself.</item>
    /// </list>
    /// Since spec 227 a candidate that is only a defined-term ROLE (<see cref="DefinedTermRoles"/>) is refused
    /// in EVERY form and the walk continues — within the form (the next match) and then to the next form —
    /// so the text before <c>("Parent")</c> is reached when it names the entity, and otherwise the answer is
    /// null (<see cref="AcquisitionScanOutcome.AcquirerNotNamed"/>), never the role word. A defined-party capture
    /// is also cut after its last party-list introducer (<see cref="PartyListIntroducers"/>), so "This Agreement
    /// … is by and among Acme, LLC (“ Parent ”)" yields "Acme, LLC", not the sentence before it.
    /// </summary>
    private static string? ExtractAcquirer(string body, string targetSentence, IReadOnlyList<string> mentions)
    {
        var fromTarget = AcquiredByRegex().Match(targetSentence);
        if (fromTarget.Success)
        {
            var name = CleanEntityName(fromTarget.Groups["name"].Value);
            if (IsUsableEntityName(name, mentions))
            {
                return name;
            }
        }

        foreach (Match match in DefinedPartyRegex().Matches(body))
        {
            var name = CleanEntityName(AfterPartyListIntroducer(match.Groups["name"].Value));
            if (IsUsableEntityName(name, mentions))
            {
                return name;
            }
        }

        foreach (Match match in SubsidiaryOfRegex().Matches(body))
        {
            var name = CleanEntityName(match.Groups["name"].Value);
            if (IsUsableEntityName(name, mentions))
            {
                return name;
            }
        }

        foreach (Match match in ByAndAmongRegex().Matches(body))
        {
            var name = CleanEntityName(match.Groups["name"].Value);
            if (IsUsableEntityName(name, mentions))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// SPEC 227 — the party-list introducers a defined-party capture can run back across when no parenthesis
    /// stops it ("This Agreement … is by and among Acme Holdings, LLC, a Delaware limited liability company
    /// (“ Parent ”)"). The capture is cut after the LAST one, so the candidate starts at the party itself.
    /// </summary>
    private static readonly string[] PartyListIntroducers = [" by and among ", " by and between ", " among ", " between ", " with "];

    /// <summary>The text after the last <see cref="PartyListIntroducers"/> occurrence, or the input unchanged.</summary>
    private static string AfterPartyListIntroducer(string rawCapture)
    {
        var capture = WhitespaceRegex().Replace(rawCapture, " ");
        var cut = -1;
        foreach (var introducer in PartyListIntroducers)
        {
            var at = capture.LastIndexOf(introducer, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                cut = Math.Max(cut, at + introducer.Length);
            }
        }

        return cut >= 0 ? capture[cut..] : capture;
    }

    /// <summary>
    /// Trims the trailing legal apposition a filing appends to a party name ("…, a Delaware corporation")
    /// and any stray punctuation, then collapses inner whitespace. Never invents or expands a name.
    /// </summary>
    private static string CleanEntityName(string raw)
    {
        var name = raw.Trim();

        var comma = name.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0)
        {
            // Keep the ", Inc." / ", LLC" / ", L.P." style suffix — it IS part of the name — but drop a
            // descriptive apposition that starts with an article.
            var tail = name[(comma + 1)..].TrimStart();
            var isLegalSuffix = LegalSuffixRegex().IsMatch(tail);
            if (!isLegalSuffix)
            {
                name = name[..comma];
            }
            else
            {
                var secondComma = name.IndexOf(',', comma + 1);
                if (secondComma > 0)
                {
                    name = name[..secondComma];
                }
            }
        }

        name = name.Trim().Trim('"', '“', '”', '(', ')', '.', ';', ':');
        return WhitespaceRegex().Replace(name, " ").Trim();
    }

    /// <summary>
    /// A candidate acquirer name is usable when it is a plausible entity name and is NOT the subject
    /// company itself (a filing names the company in "by and among" and in "Parent" definitions too).
    /// </summary>
    private static bool IsUsableEntityName(string name, IReadOnlyList<string> mentions)
    {
        if (name.Length < 3 || name.Length > 120)
        {
            return false;
        }

        if (!name.Any(char.IsLetter))
        {
            return false;
        }

        var lower = name.ToLowerInvariant();

        // "Merger Sub" is the acquisition vehicle, not the acquirer.
        if (lower.Contains("merger sub", StringComparison.Ordinal))
        {
            return false;
        }

        if (IsDefinedTermRole(lower))
        {
            return false;
        }

        return !mentions.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>
    /// SPEC 227 — true when a (lower-cased) candidate is nothing but a defined-term role from
    /// <see cref="DefinedTermRoles"/>, optionally preceded by "the".
    /// </summary>
    internal static bool IsDefinedTermRole(string candidateLower)
    {
        var bare = candidateLower.Trim();
        if (bare.StartsWith("the ", StringComparison.Ordinal))
        {
            bare = bare[4..].TrimStart();
        }

        return Array.IndexOf(DefinedTermRoles, bare) >= 0;
    }

    private sealed record ConsiderationFacts(
        string Amount, string Currency, AcquisitionConsiderationKind Kind, string Quote);

    /// <summary>One per-share amount found in a sentence, keyed by the index of its <c>$</c>.</summary>
    private sealed record AmountCandidate(int DollarIndex, int End, string Amount, bool FromRightToReceive);

    /// <summary>
    /// Leg (b): the FIRST qualifying per-share MERGER consideration in document order (deterministic), or
    /// null. The amount is kept exactly as stated (a string); Radar performs no arithmetic on it.
    /// <para>
    /// SPEC 227: within each sentence every <c>$X per share</c> / <c>right to receive $X</c> amount is
    /// considered IN POSITION ORDER. An amount an entry of <see cref="ConsiderationExclusions"/> names (par
    /// value, dividend, exercise/conversion price, an offering's price) is skipped and the scan CONTINUES —
    /// to the next amount in the same sentence, then to later sentences — so HZO's "par value $0.001 per
    /// share" no longer stops the scan before the merger consideration. A surviving amount qualifies only
    /// when it is a "right to receive $X" or its sentence carries merger vocabulary
    /// (<see cref="ConsiderationVocabulary"/>, or "per share in cash" beside a deal word); SHOO's dividend
    /// sentence carries neither.
    /// </para>
    /// </summary>
    private static ConsiderationFacts? ExtractConsideration(IReadOnlyList<string> sentences)
    {
        foreach (var sentence in sentences)
        {
            var lower = sentence.ToLowerInvariant();

            // A sentence must actually be ABOUT a per-share figure: it must mention a share (the v1 rule).
            if (!lower.Contains("per share", StringComparison.Ordinal)
                && !lower.Contains("each share", StringComparison.Ordinal)
                && !lower.Contains("right to receive", StringComparison.Ordinal))
            {
                continue;
            }

            var hasVocabulary = HasMergerVocabulary(lower);
            var ratio = ExchangeRatioRegex().Match(sentence);
            var mentionsStock = ratio.Success
                || lower.Contains("exchange ratio", StringComparison.Ordinal)
                || lower.Contains("shares of common stock of parent", StringComparison.Ordinal)
                || lower.Contains("validly issued", StringComparison.Ordinal);

            var candidates = AmountCandidates(sentence);
            var previousEnd = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var nextStart = i + 1 < candidates.Count ? candidates[i + 1].DollarIndex : lower.Length;
                var excluded = ExcludedBy(lower, candidate, previousEnd, nextStart) is not null;
                previousEnd = candidate.End;

                if (excluded || candidate.Amount.Length == 0)
                {
                    continue;
                }

                if (!hasVocabulary && !candidate.FromRightToReceive)
                {
                    continue;
                }

                var kind = mentionsStock ? AcquisitionConsiderationKind.Mixed : AcquisitionConsiderationKind.Cash;
                return new ConsiderationFacts(candidate.Amount, "$", kind, sentence);
            }

            // A stock deal states a ratio instead of a cash amount. "exchange ratio" is itself merger
            // vocabulary, so the ratio form needs no further qualifier.
            if (ratio.Success)
            {
                var amount = ratio.Groups["ratio"].Value.Trim();
                if (amount.Length > 0)
                {
                    var kind = lower.Contains("in cash", StringComparison.Ordinal)
                        ? AcquisitionConsiderationKind.Mixed
                        : AcquisitionConsiderationKind.Stock;
                    return new ConsiderationFacts(amount, string.Empty, kind, sentence);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every per-share amount in the sentence from either amount form, merged on the <c>$</c> index (the two
    /// forms often match the SAME amount — "right to receive an amount in cash equal to $53.00 per share"),
    /// in position order.
    /// </summary>
    private static List<AmountCandidate> AmountCandidates(string sentence)
    {
        var byDollar = new SortedDictionary<int, AmountCandidate>();

        foreach (Match match in PerShareCashRegex().Matches(sentence))
        {
            byDollar[match.Index] = new AmountCandidate(
                match.Index,
                match.Index + match.Length,
                match.Groups["amount"].Value.Trim(),
                FromRightToReceive: false);
        }

        foreach (Match match in RightToReceiveCashRegex().Matches(sentence))
        {
            var amount = match.Groups["amount"];
            var dollar = sentence.LastIndexOf('$', amount.Index);
            var end = match.Index + match.Length;
            byDollar[dollar] = byDollar.TryGetValue(dollar, out var existing)
                ? existing with { End = Math.Max(existing.End, end), FromRightToReceive = true }
                : new AmountCandidate(dollar, end, amount.Value.Trim(), FromRightToReceive: true);
        }

        return [.. byDollar.Values];
    }

    /// <summary>
    /// The <see cref="ConsiderationExclusions"/> entry that disqualifies this amount, or null. The look-behind
    /// never reaches back past the PREVIOUS amount in the sentence, and the look-ahead never reaches the next
    /// one, so an exclusion that belongs to one amount can never disqualify its neighbour.
    /// </summary>
    private static ConsiderationExclusion? ExcludedBy(
        string sentenceLower, AmountCandidate candidate, int previousEnd, int nextStart)
    {
        var behindStart = Math.Max(previousEnd, candidate.DollarIndex - ExclusionLookBehind);
        var behind = behindStart < candidate.DollarIndex
            ? sentenceLower[behindStart..candidate.DollarIndex]
            : string.Empty;
        var aheadEnd = Math.Min(nextStart, Math.Min(sentenceLower.Length, candidate.End + ExclusionLookAhead));
        var ahead = candidate.End < aheadEnd ? sentenceLower[candidate.End..aheadEnd] : string.Empty;

        foreach (var exclusion in ConsiderationExclusions)
        {
            var hit = behind.Contains(exclusion.Phrase, StringComparison.Ordinal)
                || (exclusion.Placement == ExclusionPlacement.BeforeOrAfter
                    && ahead.Contains(exclusion.Phrase, StringComparison.Ordinal));
            if (!hit)
            {
                continue;
            }

            if (exclusion.RequiresSentenceTerm.Length == 0
                || exclusion.RequiresSentenceTerm.Any(t => sentenceLower.Contains(t, StringComparison.Ordinal)))
            {
                return exclusion;
            }
        }

        return null;
    }

    /// <summary>SPEC 227 — whether a (lower-cased) sentence carries merger vocabulary (see <see cref="ConsiderationVocabulary"/>).</summary>
    private static bool HasMergerVocabulary(string sentenceLower) =>
        ConsiderationVocabulary.Any(t => sentenceLower.Contains(t, StringComparison.Ordinal))
        || (sentenceLower.Contains(PerShareInCashTerm, StringComparison.Ordinal)
            && PerShareInCashDealWords.Any(w => sentenceLower.Contains(w, StringComparison.Ordinal)));

    /// <summary>
    /// Abbreviations whose trailing period is NOT a sentence boundary. Filings are dense with them
    /// ("MarineMax, Inc. entered into…"), and splitting there would cut the target relation in half.
    /// </summary>
    private static readonly string[] NonTerminalAbbreviations =
    [
        "inc", "corp", "co", "ltd", "llc", "lp", "plc", "no", "mr", "mrs", "ms", "dr", "jr", "sr",
        "st", "u.s", "approx", "est", "fig", "vs", "etc", "al",
    ];

    /// <summary>
    /// Splits plain text into sentences. A period/exclamation/question mark ends a sentence only when it is
    /// followed by whitespace and is not (a) a DECIMAL POINT between digits — the spec-216 rule, without
    /// which "$53.00" would split the consideration sentence in two — or (b) the period of a known
    /// abbreviation. Deterministic and allocation-bounded; every returned sentence is an ordinal substring
    /// of the input, which is what makes the verbatim check below meaningful.
    /// <para>
    /// SPEC 227 adds three CLAUSE boundaries that carry no terminal punctuation, each chosen because it is
    /// structural in the normalizer's output (the class remarks say why a single line break is NOT): a BLANK
    /// line (a line holding only spaces/tabs counts as blank), the <c>~</c> heading ornament, and an en/em
    /// dash with whitespace on both sides. The separator character itself belongs to neither clause.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch == '\n')
            {
                var j = i + 1;
                while (j < text.Length && text[j] is ' ' or '\t' or '\r')
                {
                    j++;
                }

                if (j < text.Length && text[j] == '\n')
                {
                    AddClause(sentences, text, start, i);
                    start = j;
                    i = j - 1;
                }

                continue;
            }

            if (Array.IndexOf(HeadingSeparatorChars, ch) >= 0
                || (Array.IndexOf(SpacedDashChars, ch) >= 0
                    && i > 0
                    && char.IsWhiteSpace(text[i - 1])
                    && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))))
            {
                AddClause(sentences, text, start, i);
                start = i + 1;
                continue;
            }

            if (ch is not ('.' or '!' or '?'))
            {
                continue;
            }

            // Decimal point: digit '.' digit is never a boundary.
            if (ch == '.'
                && i > 0
                && i + 1 < text.Length
                && char.IsDigit(text[i - 1])
                && char.IsDigit(text[i + 1]))
            {
                continue;
            }

            // Must be followed by whitespace (or end of text).
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                continue;
            }

            if (ch == '.' && EndsWithAbbreviation(text, i))
            {
                continue;
            }

            var sentence = text[start..(i + 1)].Trim();
            if (sentence.Length > 0)
            {
                sentences.Add(sentence);
            }

            start = i + 1;
        }

        var tail = start < text.Length ? text[start..].Trim() : string.Empty;
        if (tail.Length > 0)
        {
            sentences.Add(tail);
        }

        return sentences;
    }

    /// <summary>Adds the trimmed clause <c>text[start..end)</c> when it is non-empty (a substring, so verbatim).</summary>
    private static void AddClause(List<string> sentences, string text, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        var clause = text[start..end].Trim();
        if (clause.Length > 0)
        {
            sentences.Add(clause);
        }
    }

    private static bool EndsWithAbbreviation(string text, int periodIndex)
    {
        var wordStart = periodIndex;
        while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '.'))
        {
            wordStart--;
        }

        if (wordStart >= periodIndex)
        {
            return false;
        }

        var word = text[wordStart..periodIndex].ToLowerInvariant().TrimEnd('.');
        return Array.IndexOf(NonTerminalAbbreviations, word) >= 0;
    }

    [GeneratedRegex(
        @"acquired\s+by\s+(?<name>[A-Z][^;()]{2,120}?)(?=\s*(?:,\s*(?:a|an|the)\s|\.\s|\.$|;|\(|$))",
        RegexOptions.CultureInvariant)]
    private static partial Regex AcquiredByRegex();

    // The name group deliberately ADMITS commas: a party is written "Safe Harbor Marinas, LLC, a Delaware
    // limited liability company (\"Parent\")", so a comma-free group would match only the trailing "LLC".
    // CleanEntityName then trims the descriptive apposition while KEEPING the legal suffix, which is part of
    // the name. SPEC 227: whitespace is admitted INSIDE the quotation marks — the live HZO body reads
    // "SHM Holdco, LLC, a Delaware limited liability company (“ Parent ”)", which the v1 pattern could not
    // match, so v1 fell through to "wholly-owned subsidiary of Parent" and recorded the role as the name.
    [GeneratedRegex(
        "(?<name>[A-Z][^;()]{2,120}?)\\s*\\(\\s*[“\"']?\\s*(?:Parent|Purchaser|Buyer|Acquiror|Acquirer)\\s*[”\"']?\\s*\\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DefinedPartyRegex();

    // SPEC 227: the "by {Acquirer}" that qualifies an "acquisition of {company}" clause — a lower-case "by"
    // followed by a capitalised party (optionally quoted).
    [GeneratedRegex("\\bby\\s+[“\"']?\\s*[A-Z]", RegexOptions.CultureInvariant)]
    private static partial Regex AcquisitionByPartyRegex();

    [GeneratedRegex(
        @"wholly[\s-]owned\s+subsidiary\s+of\s+(?<name>[A-Z][^;()]{2,120}?)(?=\s*(?:,\s*(?:a|an|the)\s|\.\s|\.$|;|\(|$))",
        RegexOptions.CultureInvariant)]
    private static partial Regex SubsidiaryOfRegex();

    [GeneratedRegex(
        @"by\s+and\s+among\s+(?<name>[A-Z][^;()]{2,120}?)(?=\s*(?:,\s*(?:a|an|the)\s|\.\s|\.$|;|\(|$))",
        RegexOptions.CultureInvariant)]
    private static partial Regex ByAndAmongRegex();

    [GeneratedRegex(
        @"\$\s?(?<amount>[0-9][0-9,]*(?:\.[0-9]+)?)(?:\s+in\s+cash)?\s*,?\s*(?:per\s+share|for\s+each\s+share)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PerShareCashRegex();

    [GeneratedRegex(
        @"right\s+to\s+receive\s+(?:an\s+amount\s+in\s+cash\s+equal\s+to\s+)?\$\s?(?<amount>[0-9][0-9,]*(?:\.[0-9]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RightToReceiveCashRegex();

    [GeneratedRegex(
        @"exchange\s+ratio\s+of\s+(?<ratio>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExchangeRatioRegex();

    [GeneratedRegex(
        @"^(?:Inc|Corp|Co|Ltd|LLC|L\.L\.C|L\.P|LP|PLC|N\.V|S\.A|GmbH)\b\.?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegalSuffixRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// The stable machine token for one outcome — the enum NAME lower-kebab-cased, so artifacts and log
    /// lines share one vocabulary and a renamed member breaks loudly rather than silently.
    /// </summary>
    public static string Token(AcquisitionScanOutcome outcome) => outcome switch
    {
        AcquisitionScanOutcome.Recognised => "recognised",
        AcquisitionScanOutcome.NoMergerAgreement => "no-merger-agreement",
        AcquisitionScanOutcome.CompanyNotTarget => "company-not-target",
        AcquisitionScanOutcome.CompanyIsAcquirer => "company-is-acquirer",
        AcquisitionScanOutcome.AcquirerNotNamed => "acquirer-not-named",
        AcquisitionScanOutcome.NoStatedConsideration => "no-stated-consideration",
        AcquisitionScanOutcome.EmptyBody => "empty-body",
        AcquisitionScanOutcome.VerbatimCheckFailed => "verbatim-check-failed",
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome, "Undefined acquisition scan outcome."),
    };

    /// <summary>Every defined outcome, in declaration order — the tally's column order (AD-3).</summary>
    public static IReadOnlyList<AcquisitionScanOutcome> AllOutcomes { get; } =
        [.. Enum.GetValues<AcquisitionScanOutcome>()];

    /// <summary>Invariant integer rendering, so tallies format identically on every machine.</summary>
    internal static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
