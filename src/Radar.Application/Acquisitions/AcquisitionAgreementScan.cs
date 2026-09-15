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
    /// Leg (a) failed: the text carries no "Agreement and Plan of Merger" / "merger agreement" / "plan of merger"
    /// phrase, no target clause that itself says "definitive agreement … acquire", and no clause in which the company
    /// acquires a NAMED party. The overwhelming majority of item-1.01 filings (credit agreements, leases, supply
    /// contracts, note purchases) land here. (⚠ AMENDED in place by spec 229: under <c>acqscan-v1</c>–<c>v3</c> the
    /// phrase list said the same, but an acquirer-side sentence OUTRANKED this outcome — so a note purchase by "a
    /// wholly owned subsidiary of {company}" read company-is-acquirer — and any target clause counted because every
    /// item-1.01 body carries "Material Definitive Agreement" in its heading. Since <c>acqscan-v4</c> this is the
    /// answer whenever there is no merger context, whatever a proximity veto said.)
    /// </summary>
    NoMergerAgreement = 2,

    /// <summary>
    /// Leg (a) failed: an explicit merger-agreement phrase exists, but no clause the scan reads puts the SUBJECT
    /// company in the target position ("acquired by", "merge with and into", "acquisition of … by", and since spec
    /// 229 "… pursuant to which Parent agreed to acquire the Company") and none names it the buyer. Fail-closed by
    /// design: it says what the scan could not read, not that the company is not being acquired (spec 229 §2 found a
    /// real takeover — WTRG — in this bucket).
    /// </summary>
    CompanyNotTarget = 3,

    /// <summary>
    /// Leg (a) failed BY CONSTRUCTION because the company is the ACQUIRER: it is the SUBJECT of an acquisition verb
    /// ("to acquire", "agreed to acquire", "completes acquisition of", …) with no other party the subject in between,
    /// or it governs "completion of the acquisition of"; or — only beside an explicit merger phrase — it is the direct
    /// object of "wholly owned subsidiary of {company}". WITHOUT a merger phrase the acquisition must be of a NAMED
    /// party ("Aehr Test Systems to Acquire Incal Technology"). Counted separately from <see cref="CompanyNotTarget"/>
    /// because it is a different fact about the filing, not a weaker version of the same one. (⚠ AMENDED in place by
    /// spec 229: under <c>acqscan-v1</c>–<c>v3</c> it was a 90-character proximity test, and the label was false more
    /// often than true — of the 45 filings <c>acqscan-v3</c> labelled with it on the live store, a hand check found 15
    /// in which the filer is acquiring something; the rest were debt issues, leases, supply contracts, divestitures and
    /// one filer that is itself the TARGET. Under <c>acqscan-v4</c> all 14 labels were true. Nothing downstream reads it.)
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

    /// <summary>
    /// SPEC 229 — the verbatim clause that DECIDED a not-recognised outcome, when a clause decided it: the
    /// acquirer-side clause for <see cref="AcquisitionScanOutcome.CompanyIsAcquirer"/>, the first clause holding an
    /// explicit merger phrase for <see cref="AcquisitionScanOutcome.CompanyNotTarget"/>, and the target clause for
    /// <see cref="AcquisitionScanOutcome.AcquirerNotNamed"/> / <see cref="AcquisitionScanOutcome.NoStatedConsideration"/>.
    /// Null when the outcome is decided by an ABSENCE (<see cref="AcquisitionScanOutcome.NoMergerAgreement"/>,
    /// <see cref="AcquisitionScanOutcome.EmptyBody"/>) and on a recognition (whose clauses are
    /// <see cref="TargetQuote"/> and <see cref="ConsiderationQuote"/>). A DIAGNOSTIC, so a reader can check WHY
    /// against the filing: it is not persisted, not cached and read by nothing downstream (spec 229 §4).
    /// </summary>
    public string? DecidingQuote { get; init; }

    internal static AcquisitionScanResult NotRecognised(AcquisitionScanOutcome outcome, string? decidingQuote = null) =>
        new(outcome) { DecidingQuote = decidingQuote };
}

/// <summary>
/// SPEC 217 §1, tightened by SPEC 227 (<c>acqscan-v2</c>, the rule), re-versioned by SPEC 228 (<c>acqscan-v3</c>, the
/// read) and tightened again by SPEC 229 (<c>acqscan-v4</c>, the rule — every not-recognised outcome a TRUE stated
/// reason; see <see cref="Version"/> and "What spec 229 changed" below): the DETERMINISTIC,
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
/// are acquirer-side vetoes. (⚠ AMENDED in place by spec 229: under v2/v3 "governed" meant "a company mention within
/// 90 characters BEFORE the phrase", whoever the grammar said completed it. See "What spec 229 changed".)
/// </para></item>
/// <item><b>Leg (b) — a stated per-share MERGER consideration.</b> "$53.00 per share in cash", "converted into
/// the right to receive $53.00", or a stated exchange ratio — in a sentence that carries MERGER VOCABULARY
/// (<see cref="ConsiderationVocabulary"/>). An amount that is a par value, a dividend, an exercise price, a
/// conversion price or the price of an offering is EXCLUDED (<see cref="ConsiderationExclusions"/> — the
/// one place those rules live), and scanning continues past it within the same sentence and after it. A
/// merger agreement with no qualifying per-share consideration is not recognised. (Since spec 229 the exclusion
/// must GOVERN the amount — see "What spec 229 changed".)</item>
/// </list>
/// <para>
/// <b>The acquirer must be a NAME.</b> A bare defined-term role ("Parent", "Lead Borrower", "Merger Sub",
/// the rest of <see cref="DefinedTermRoles"/>) is never accepted from any form; the scan keeps looking and
/// otherwise fails <see cref="AcquisitionScanOutcome.AcquirerNotNamed"/>. A banner naming a placeholder is
/// worse than one the design refuses to print.
/// </para>
/// <para>
/// <b>What spec 229 changed (<c>acqscan-v4</c>).</b> Spec 228 made the body the real 8-K, and every item-1.01 8-K
/// carries "Entry into a Material Definitive Agreement", so the v2 proximity rules began to fire on ordinary 8-K
/// narrative: company-is-acquirer rose from 13 to 45 labels, most of them false. v4 keeps fail-closed and every named
/// outcome, and changes five rules, each measured on the live store (spec 229 §2):
/// </para>
/// <list type="number">
/// <item><b>Honest precedence.</b> Merger context is an explicit merger phrase, or a target clause that itself says
/// "definitive agreement … acquire". Without it the answer is <see cref="AcquisitionScanOutcome.NoMergerAgreement"/>
/// unless the company acquires a NAMED party (<see cref="HasNamedObject"/>); "subsidiary of {company}" vetoes only beside
/// an explicit merger phrase and binds a DIRECT object.</item>
/// <item><b>The acquirer veto binds to the verb's SUBJECT</b> (<see cref="GovernsVerb"/>): a defined
/// <see cref="InterveningSubjectRoles"/> role or a "pursuant to which {X}" clause between the company and the verb makes
/// X the subject, and "… pursuant to which Parent agreed to acquire the Company" is a TARGET clause. "pursuant to which
/// {company} agreed to acquire" still vetoes, and so does a role the company is itself defined as, wherever in the clause
/// (<see cref="SubjectIsFilerRole"/>). Fail-closed on buyer-side filings: "the Company" is read as the filer only when
/// every (the “Company”) definition in the body directly follows a company mention with nothing but whitespace or an
/// entity apposition (", Inc.", ", a Florida corporation") between; a definition after a verb or another party's name,
/// or one whose ownership is ambiguous, belongs to another party (<see cref="DefinesCompanyAliasForAnotherParty"/>).</item>
/// <item><b>Governed completion</b> (<see cref="GovernsCompletion"/>): "completion of the acquisition of" is acquirer-side
/// only when the filer governs it ("the Company's", "{company} announces completion of") and the company is not its
/// object.</item>
/// <item><b>Case-insensitive acquirer keywords</b> ("Acquired By", "ACQUIRED BY"); the captured name keeps its case.</item>
/// <item><b>Clause-governed consideration exclusions</b> (<see cref="ExcludedBy"/>): "dividend", "par value", … exclude an
/// amount only when they GOVERN it, with only <see cref="ExclusionConnectorWords"/> (or a defined-term parenthetical)
/// between.</item>
/// </list>
/// <para>
/// A not-recognised result also carries <see cref="AcquisitionScanResult.DecidingQuote"/>, the clause that decided it,
/// so the stated reason can be checked against the filing.
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
    /// the read) → <c>acqscan-v4</c> (spec 229, the rule: truthful not-recognised reasons; the read is v3's, with the
    /// EX-2.1 append kept because the spec-229 §2 measurement found HZO still unnamed without it).
    /// </para>
    /// </summary>
    public const string Version = "acqscan-v4";

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
    /// acquirer-side verbs), and how far after an acquisition verb its NAMED object may start (spec 229 rule 1).
    /// Since spec 227 it no longer governs the company-AFTER target phrases — those bind a direct object only —
    /// and since spec 229 it no longer governs "subsidiary of {company}" either (a direct object too).
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
    /// The phrase that, in ONE clause beside "acquire", makes a filing with no explicit merger phrase a merger
    /// filing (spec 217's "definitive agreement … to be acquired"). "Definitive agreement" alone is not enough —
    /// since spec 228 every body carries it, in the heading "Entry into a Material Definitive Agreement" — so
    /// since spec 229 it counts only INSIDE the target clause itself (<see cref="IsDefinitiveAgreementAcquisitionClause"/>).
    /// </summary>
    private const string DefinitiveAgreementPhrase = "definitive agreement";

    /// <summary>
    /// Acquirer-side VERB phrases evaluated against a clause BEFORE any target pattern. A hit means the company
    /// is the verb's SUBJECT (<see cref="GovernsVerb"/>: it precedes the verb within <see cref="ObjectProximity"/>
    /// and no other party is the subject in between — spec 229 rule 2) and the company is not the verb's object,
    /// so the clause names the company as the BUYER and can never establish that it is the target.
    /// </summary>
    private static readonly string[] AcquirerVerbPhrases =
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
    ];

    /// <summary>
    /// SPEC 229 rule 3 — the COMPLETION noun phrases. They read identically whoever completes the acquisition
    /// ("Parent's completion of the acquisition of the Company" / "the Company's completion of the acquisition of
    /// X"), so they are acquirer-side only when the filer GOVERNS them (<see cref="GovernsCompletion"/>) and the
    /// company is not their object.
    /// </summary>
    private static readonly string[] CompletionNounPhrases =
    [
        "completion of acquisition of",
        "completion of the acquisition of",
    ];

    /// <summary>
    /// SPEC 229 rule 3 — the only words that may sit between a company mention and a completion noun phrase it
    /// governs ("{company} Announces Completion of Acquisition of …", "{company} today announced the completion
    /// of the acquisition of …"). A parenthetical (a ticker, a defined term) may sit there too.
    /// </summary>
    private static readonly string[] CompletionGovernorWords =
        ["announces", "announced", "announce", "today", "has", "have", "completed", "completes", "complete", "the", "a", "an"];

    /// <summary>
    /// SPEC 229 rule 2 — defined party ROLES that, standing between a company mention and an acquisition verb, are
    /// that verb's SUBJECT instead ("MarineMax, Inc. entered into an Agreement and Plan of Merger with Parent,
    /// pursuant to which Parent agreed to acquire the Company"). Matched as a capitalised whole word outside a
    /// parenthetical. A role the company ITSELF is defined as (its first parenthetical, "Esquire (“Parent”)") is
    /// the company, not another party.
    /// </summary>
    private static readonly string[] InterveningSubjectRoles = ["Parent", "Purchaser", "Buyer", "Acquiror", "Acquirer"];

    /// <summary>
    /// SPEC 229 rule 2 — "pursuant to which {X}" between a company mention and the verb makes X the subject; it
    /// still vetoes when X is the company (a mention, "the Company", or the role the company is defined as).
    /// </summary>
    private const string PursuantToWhichPhrase = "pursuant to which";

    /// <summary>
    /// SPEC 229 rule 2 — the pending acquisition verbs whose DIRECT object may be the company, making the clause a
    /// TARGET clause when the company is not their subject ("… pursuant to which Parent agreed to acquire the
    /// Company").
    /// </summary>
    private static readonly string[] PendingAcquireVerbPhrases = ["to acquire", "will acquire", "agreed to acquire"];

    /// <summary>
    /// SPEC 229 — the defined alias an 8-K gives its registrant ("the Company"). Accepted as the company only as
    /// the DIRECT object of an acquisition phrase, or as the subject after "pursuant to which", in a clause that
    /// also names the company.
    /// </summary>
    private const string FilerAliasWord = "company";

    /// <summary>
    /// SPEC 229 rule 1 — words that, as the first capitalised word after an acquisition verb, do NOT make its
    /// object a NAMED party: securities and asset classes ("to acquire 599,808 shares of Common Stock"). Data, in
    /// ONE place, beside <see cref="DefinedTermRoles"/> (which are also refused).
    /// </summary>
    private static readonly string[] UnnamedObjectWords =
    [
        "all", "assets", "class", "common", "equipment", "interest", "interests", "note", "notes", "preferred",
        "securities", "series", "share", "shares", "stock", "units", "warrants",
    ];

    /// <summary>
    /// The merger-STRUCTURE veto: "Merger Sub, a wholly owned subsidiary of {company}". Since spec 229 it binds a
    /// DIRECT object only and applies only inside a filing that carries an explicit merger phrase — outside a
    /// merger it describes corporate structure ("Otter Tail Power Company (the “Company”), a wholly owned
    /// subsidiary of Otter Tail Corporation (“OTC”), entered into a Note Purchase Agreement"), not an acquisition.
    /// </summary>
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

    /// <summary>
    /// Where, relative to a <c>$X per share</c> amount, an exclusion phrase must sit to exclude it. Since spec 229 it
    /// must also GOVERN the amount: only <see cref="ExclusionConnectorWords"/> may stand between them.
    /// </summary>
    private enum ExclusionPlacement
    {
        /// <summary>Leading up to the amount ("par value $0.001 per share", "an exercise price per share of $9.00").</summary>
        Before = 1,

        /// <summary>Before OR just after the amount ("a dividend of $0.21 per share" / "$0.21 per share quarterly dividend").</summary>
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

    /// <summary>
    /// SPEC 229 rule 5 — the only words that may stand between an exclusion phrase and the amount it GOVERNS
    /// ("dividend of $0.21", "exercise price per share of $9.00", "$0.21 per share quarterly cash dividend",
    /// "par value $0.001"). Any other word — "the regular quarterly dividend will be suspended and holders will
    /// receive $53.00 per share" — means the phrase belongs to another part of the clause and the amount is not
    /// excluded. This replaces spec 227's fixed 60-character look-behind / 25-character look-ahead, which excluded
    /// an amount merely NEAR the word. Data, in ONE place, beside <see cref="ConsiderationExclusions"/>.
    /// </summary>
    private static readonly string[] ExclusionConnectorWords =
    [
        "a", "amount", "an", "annual", "approximately", "at", "be", "cash", "equal", "in", "initial", "is", "of", "per",
        "quarterly", "regular", "share", "special", "the", "to", "was", "will",
    ];

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

        // Leg (a), part 1 — the merger CONTEXT. "Agreement and Plan of Merger" / "merger agreement" / "plan of
        // merger" is a merger filing ON ITS OWN. "Definitive agreement" is NOT: it is the boilerplate title of
        // EVERY item-1.01 8-K ("Entry into a Material Definitive Agreement"), so since spec 229 it counts only
        // INSIDE a target clause that also says "acquire" (spec 217 §1's "definitive agreement … to be acquired").
        var hasExplicitMergerPhrase =
            MergerAgreementPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal));

        var sentences = SplitSentences(body);

        // SPEC 229 rule 2 (fail-closed): "the Company" reads as the FILER only when every (the “Company”) definition in
        // the body directly follows a filer mention, separated by nothing but whitespace or an entity apposition
        // (", a Florida corporation"); any other definition — or none provably the filer's — makes it another party. A buyer-side merger 8-K defines the TARGET that way ("Widget Holdings,
        // Inc. (the “Company”) … pursuant to which Parent agreed to acquire the Company"), and reading the alias as the
        // filer there would close the BUYER's thesis.
        var aliasIsFiler = !DefinesCompanyAliasForAnotherParty(body, lower, mentions);

        // Leg (a). Walk the clauses ONCE, classifying each as acquirer-side, target-side or neither. The
        // acquirer-side readings are evaluated first WITHIN each clause, so a clause that names the company as the
        // buyer can never also be read as naming it as the target.
        string? targetSentence = null;
        string? firstAcquirerClauseInMergerContext = null;
        string? firstAcquisitionOverNamedObjectClause = null;
        foreach (var sentence in sentences)
        {
            var sentenceLower = sentence.ToLowerInvariant();
            var positions = MentionPositions(sentenceLower, mentions);
            if (positions.Count == 0)
            {
                continue;
            }

            // SPEC 229 rules 2 + 3: the company is the SUBJECT of an acquisition verb, or governs a completion
            // noun phrase, and is not its object.
            var acquisition = ReadAcquirerSide(sentence, sentenceLower, positions, mentions, aliasIsFiler);
            if (acquisition != AcquirerSideReading.None)
            {
                firstAcquirerClauseInMergerContext ??= sentence;
                if (acquisition == AcquirerSideReading.OverNamedObject)
                {
                    firstAcquisitionOverNamedObjectClause ??= sentence;
                }

                continue;
            }

            // SPEC 229 rule 1: "subsidiary of {company}" is a merger-STRUCTURE fact, so it vetoes only in a filing
            // with an explicit merger phrase. Elsewhere the clause is left to the target test like any other.
            if (hasExplicitMergerPhrase && IsSubsidiaryOfCompany(sentenceLower, positions))
            {
                firstAcquirerClauseInMergerContext ??= sentence;
                continue;
            }

            if (targetSentence is null
                && IsTargetSide(sentence, sentenceLower, positions, mentions, aliasIsFiler)
                && (hasExplicitMergerPhrase || IsDefinitiveAgreementAcquisitionClause(sentenceLower)))
            {
                targetSentence = sentence;
            }
        }

        if (targetSentence is null)
        {
            // SPEC 229 rule 1 — the outcome precedence is honest. With an explicit merger phrase, an acquirer-side
            // clause (a governed acquisition verb or "subsidiary of {company}") is reported ahead of the generic
            // "no target clause": the company BUYING someone is a different fact, not a weaker version of it.
            // WITHOUT a merger phrase the answer is "not a merger filing" whatever an acquirer-side clause said,
            // UNLESS the company governs an acquisition verb over a NAMED object ("Aehr Test Systems to Acquire
            // Incal Technology") — that clause is acquisition context in itself. Measured on the live store
            // (spec 229 §2): OTTR's note purchase ("Otter Tail Power Company …, a wholly owned subsidiary of
            // Otter Tail Corporation, entered into a Note Purchase Agreement") is no-merger-agreement; AEHR's
            // purchase of Incal is company-is-acquirer.
            if (hasExplicitMergerPhrase)
            {
                return firstAcquirerClauseInMergerContext is not null
                    ? AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.CompanyIsAcquirer, firstAcquirerClauseInMergerContext)
                    : AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.CompanyNotTarget, FirstClauseWithMergerPhrase(sentences));
            }

            return firstAcquisitionOverNamedObjectClause is not null
                ? AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.CompanyIsAcquirer, firstAcquisitionOverNamedObjectClause)
                : AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.NoMergerAgreement);
        }

        var acquirer = ExtractAcquirer(body, targetSentence, mentions);
        if (acquirer is null)
        {
            return AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.AcquirerNotNamed, targetSentence);
        }

        // Leg (b): a per-share consideration, stated in ONE sentence so the quote is a real sentence rather
        // than a stitched-together fragment.
        var consideration = ExtractConsideration(sentences);
        if (consideration is null)
        {
            return AcquisitionScanResult.NotRecognised(AcquisitionScanOutcome.NoStatedConsideration, targetSentence);
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

    /// <summary>What an acquirer-side reading of one clause found (spec 229 rules 1–3).</summary>
    private enum AcquirerSideReading
    {
        /// <summary>The company governs no acquisition verb or completion phrase in this clause.</summary>
        None = 0,

        /// <summary>The company governs one, but its object is not a NAMED party (acquirer-side only beside a merger phrase).</summary>
        Governed = 1,

        /// <summary>The company governs one over a NAMED object — acquisition context in itself (spec 229 rule 1).</summary>
        OverNamedObject = 2,
    }

    /// <summary>
    /// SPEC 229 rules 2 + 3 — whether this clause names the company as the BUYER: the company is the SUBJECT of an
    /// <see cref="AcquirerVerbPhrases"/> verb (<see cref="GovernsVerb"/>) or GOVERNS a
    /// <see cref="CompletionNounPhrases"/> phrase (<see cref="GovernsCompletion"/>), and the company is not that
    /// phrase's object. Evaluated before any target pattern (spec 217 §1).
    /// </summary>
    private static AcquirerSideReading ReadAcquirerSide(
        string sentence, string sentenceLower, List<int> companyPositions, IReadOnlyList<string> mentions, bool aliasIsFiler)
    {
        var reading = AcquirerSideReading.None;
        var filerRoles = FilerDefinedRoles(sentence, sentenceLower, companyPositions, mentions);

        foreach (var phrase in AcquirerVerbPhrases)
        {
            for (var at = sentenceLower.IndexOf(phrase, StringComparison.Ordinal);
                 at >= 0;
                 at = sentenceLower.IndexOf(phrase, at + 1, StringComparison.Ordinal))
            {
                var end = at + phrase.Length;
                if (!IsWholePhrase(sentenceLower, at, phrase.Length)
                    || !(companyPositions.Any(p => GovernsVerb(sentence, sentenceLower, p, mentions, at, aliasIsFiler))
                         || SubjectIsFilerRole(sentence, at, filerRoles))
                    || ObjectIsCompany(sentenceLower, end, companyPositions, includeAlias: false))
                {
                    continue;
                }

                if (HasNamedObject(sentence, sentenceLower, end, companyPositions, mentions))
                {
                    return AcquirerSideReading.OverNamedObject;
                }

                reading = AcquirerSideReading.Governed;
            }
        }

        foreach (var phrase in CompletionNounPhrases)
        {
            for (var at = sentenceLower.IndexOf(phrase, StringComparison.Ordinal);
                 at >= 0;
                 at = sentenceLower.IndexOf(phrase, at + 1, StringComparison.Ordinal))
            {
                var end = at + phrase.Length;
                if (!GovernsCompletion(sentenceLower, at, companyPositions, mentions)
                    || ObjectIsCompany(sentenceLower, end, companyPositions, includeAlias: false))
                {
                    continue;
                }

                if (HasNamedObject(sentence, sentenceLower, end, companyPositions, mentions))
                {
                    return AcquirerSideReading.OverNamedObject;
                }

                reading = AcquirerSideReading.Governed;
            }
        }

        return reading;
    }

    /// <summary>
    /// SPEC 229 rule 2 — true when the company mention starting at <paramref name="mentionStart"/> is the SUBJECT of
    /// the verb at <paramref name="verbStart"/>: it precedes the verb within <see cref="ObjectProximity"/>, and no
    /// OTHER party is the subject in between. Another party is: a capitalised <see cref="InterveningSubjectRoles"/>
    /// word outside a parenthetical that is not the role the company itself is defined as; or a
    /// <see cref="PursuantToWhichPhrase"/> clause whose subject is not the company. Deterministic, no parser.
    /// </summary>
    private static bool GovernsVerb(
        string sentence, string sentenceLower, int mentionStart, IReadOnlyList<string> mentions, int verbStart, bool aliasIsFiler)
    {
        var mentionEnd = MentionEnd(sentenceLower, mentionStart, mentions);
        if (mentionStart >= verbStart || verbStart - mentionStart > ObjectProximity || mentionEnd > verbStart)
        {
            return false;
        }

        var gap = sentence[mentionEnd..verbStart];
        var gapLower = sentenceLower[mentionEnd..verbStart];
        var ownRole = OwnDefinedRole(gap);

        // Another party's defined ROLE as a bare (capitalised, whole-word, outside any parenthetical) word in between.
        foreach (var word in Words(StripParentheticals(gap)))
        {
            if (Array.IndexOf(InterveningSubjectRoles, word) >= 0 && !string.Equals(word, ownRole, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var pursuant = gapLower.LastIndexOf(PursuantToWhichPhrase, StringComparison.Ordinal);
        if (pursuant >= 0)
        {
            // "… pursuant to which {X} agreed to acquire": X is the subject. A mention of the company AFTER the phrase
            // is its own (closer) candidate subject and is judged on its own gap; for THIS mention the phrase vetoes
            // only when X is the company's alias ("the Company") or the role the company is defined as.
            var subject = Words(gapLower[(pursuant + PursuantToWhichPhrase.Length)..])
                .Where(w => Array.IndexOf(DirectObjectFillerWords, w) < 0 && Array.IndexOf(SubjectAuxiliaryWords, w) < 0)
                .ToList();
            return subject.Count == 1
                && ((aliasIsFiler && subject[0] == FilerAliasWord)
                    || (ownRole is not null && string.Equals(subject[0], ownRole, StringComparison.OrdinalIgnoreCase)));
        }

        return true;
    }

    /// <summary>
    /// SPEC 229 rule 2 — how far after a company mention its OWN defining parenthetical may start ("Essential Utilities,
    /// Inc., a Pennsylvania corporation (the “Company”)"): only an apposition may sit between them, never another party.
    /// </summary>
    private const int OwnDefinitionProximity = 60;

    /// <summary>SPEC 229 rule 2 — words that, between a mention and a parenthetical, show the definition belongs to another party.</summary>
    private static readonly string[] OtherPartySeparators = [" and ", " with ", " among ", " between ", ";"];

    /// <summary>
    /// SPEC 229 rule 2 — the lower-case words an ENTITY APPOSITION between a company's name and its own defining
    /// parenthetical may use (", a Delaware limited liability company", ", a corporation organized under the laws of
    /// Ontario"). Any other lower-case word — a verb such as "agreed to acquire", "acquires", "will fund" — means the
    /// parenthetical defines something else. Data, in ONE place.
    /// </summary>
    private static readonly string[] EntityAppositionWords =
    [
        "a", "an", "association", "bank", "banking", "benefit", "business", "chartered", "company", "cooperative",
        "corporation", "entity", "estate", "exempted", "existing", "federal", "federally", "general", "holding",
        "incorporated", "insurance", "investment", "laws", "liability", "limited", "mutual", "national", "of",
        "organized", "partnership", "public", "real", "savings", "state", "statutory", "the", "trust", "under",
    ];

    /// <summary>
    /// SPEC 229 rule 2 (fail-closed) — true when <paramref name="between"/>, the ORIGINAL-case text between the end of a
    /// company mention and a parenthetical, lets that parenthetical be the company's OWN definition: it is empty
    /// (whitespace), or it is a comma-led apposition made only of legal-suffix segments (", Inc.", ", LLC") and at most
    /// one final article segment (", a Florida corporation") whose lower-case words are all
    /// <see cref="EntityAppositionWords"/> and which holds at most three capitalised words (a jurisdiction), none of them
    /// a legal suffix. Anything else — a verb, another party's name, a second article segment — is NOT an apposition.
    /// </summary>
    private static bool IsOwnEntityApposition(string between)
    {
        if (string.IsNullOrWhiteSpace(between))
        {
            return true;
        }

        var trimmed = between.Trim();
        if (!trimmed.StartsWith(',') || trimmed.EndsWith(','))
        {
            return false;
        }

        var segments = trimmed[1..].Split(',').Select(s => s.Trim()).ToList();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
            {
                return false;
            }

            var match = LegalSuffixRegex().Match(segment);
            if (match.Success && match.Length == segment.Length)
            {
                continue;
            }

            // The article segment must be the LAST one.
            if (i != segments.Count - 1)
            {
                return false;
            }

            var words = Words(segment).ToList();
            if (words.Count < 2 || words[0] is not ("a" or "an"))
            {
                return false;
            }

            var capitalised = 0;
            foreach (var word in words.Skip(1))
            {
                if (char.IsUpper(word[0]))
                {
                    var suffix = LegalSuffixRegex().Match(word);
                    if ((suffix.Success && suffix.Length == word.Length) || ++capitalised > 3)
                    {
                        return false;
                    }
                }
                else if (Array.IndexOf(EntityAppositionWords, word) < 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// SPEC 229 rule 2 — true when the parenthetical starting at <paramref name="openParen"/> in <paramref name="lower"/>
    /// DEFINES a company mention: a whole mention ends at most <see cref="OwnDefinitionProximity"/> characters before it,
    /// with no parenthesis and no <see cref="OtherPartySeparators"/> in between. When <paramref name="original"/> is
    /// supplied (the ALIAS ownership test) the stricter <see cref="IsOwnEntityApposition"/> must also hold over that gap:
    /// nothing but whitespace or an entity apposition. The ROLE test (<see cref="FilerDefinedRoles"/>) keeps the looser
    /// form, because there a wider reading only adds vetoes — the fail-closed direction.
    /// </summary>
    private static bool DefinesAMention(string lower, int openParen, IReadOnlyList<string> mentions, string? original = null)
    {
        var windowStart = Math.Max(0, openParen - OwnDefinitionProximity - 120);
        foreach (var mention in mentions)
        {
            var at = openParen > 0 ? lower.LastIndexOf(mention, openParen - 1, openParen - windowStart, StringComparison.Ordinal) : -1;
            for (; at >= windowStart; at = at > 0 ? lower.LastIndexOf(mention, at - 1, at - windowStart, StringComparison.Ordinal) : -1)
            {
                if (!IsWholePhrase(lower, at, mention.Length))
                {
                    continue;
                }

                var end = at + mention.Length;
                if (end > openParen)
                {
                    continue;
                }

                var between = lower[end..openParen];
                if (between.Length <= OwnDefinitionProximity
                    && between.IndexOfAny(['(', ')']) < 0
                    && !OtherPartySeparators.Any(s => between.Contains(s, StringComparison.Ordinal))
                    && (original is null || IsOwnEntityApposition(original[end..openParen])))
                {
                    return true;
                }

                break;
            }
        }

        return false;
    }

    /// <summary>
    /// SPEC 229 rule 2 (fail-closed) — true unless EVERY (the “Company”) definition in the body (quoted, or the bare
    /// "(the Company)") is provably the filer's own: a whole company mention ends at most
    /// <see cref="OwnDefinitionProximity"/> characters before it, and only whitespace or an entity apposition
    /// (<see cref="IsOwnEntityApposition"/>: ", Inc.", ", a Florida corporation") sits between. A definition after a verb
    /// ("Example Industries, Inc. agreed to acquire Widget Holdings, Inc. (the “Company”)") or after another party's name
    /// belongs to another party, and an ambiguous one is treated the same way. When this is true "the Company" is never
    /// read as the filer anywhere in the body.
    /// </summary>
    private static bool DefinesCompanyAliasForAnotherParty(string body, string lower, IReadOnlyList<string> mentions)
    {
        foreach (Match definition in CompanyAliasDefinitionRegex().Matches(body))
        {
            if (!DefinesAMention(lower, definition.Index, mentions, original: body))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// SPEC 229 rule 2 — every <see cref="InterveningSubjectRoles"/> role a company mention in this clause is ITSELF defined
    /// as ("Example Industries, Inc., a Delaware corporation (“Parent”)"), wherever in the clause the definition sits.
    /// </summary>
    private static HashSet<string> FilerDefinedRoles(
        string sentence, string sentenceLower, List<int> companyPositions, IReadOnlyList<string> mentions)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in companyPositions)
        {
            var end = MentionEnd(sentenceLower, p, mentions);
            var open = sentence.IndexOf('(', end);
            if (open < 0 || !DefinesAMention(sentenceLower, open, mentions))
            {
                continue;
            }

            var close = sentence.IndexOf(')', open);
            if (close < 0)
            {
                continue;
            }

            if (OwnDefinedRole(sentence[end..(close + 1)]) is { } role)
            {
                roles.Add(role);
            }
        }

        return roles;
    }

    /// <summary>
    /// SPEC 229 rule 2 — true when the word immediately before the verb at <paramref name="verbStart"/> (auxiliaries and
    /// articles aside) is a role the filer is defined as in this clause ("… pursuant to which Parent agreed to acquire"
    /// where the filer is "Parent"). Unlike <see cref="GovernsVerb"/> it is NOT bounded by <see cref="ObjectProximity"/>:
    /// the definition names the subject, however far back it sits.
    /// </summary>
    private static bool SubjectIsFilerRole(string sentence, int verbStart, HashSet<string> filerRoles)
    {
        if (filerRoles.Count == 0)
        {
            return false;
        }

        var subject = Words(sentence[..verbStart])
            .Reverse()
            .FirstOrDefault(w => Array.IndexOf(SubjectAuxiliaryWords, w.ToLowerInvariant()) < 0
                && Array.IndexOf(DirectObjectFillerWords, w.ToLowerInvariant()) < 0);
        return subject is not null && filerRoles.Contains(subject);
    }

    /// <summary>SPEC 229 rule 2 — the auxiliaries between a "pursuant to which" subject and its verb ("… which Parent has agreed to acquire").</summary>
    private static readonly string[] SubjectAuxiliaryWords = ["agreed", "has", "have", "shall", "will", "would"];

    /// <summary>The words of <paramref name="text"/>: maximal runs of letters, digits and apostrophes, in order.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '\'' or '’');
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                yield return text[start..i].Trim('\'', '’');
                start = -1;
            }
        }
    }

    /// <summary>
    /// SPEC 229 rule 2 — the role the company mention is itself defined as, when the FIRST parenthetical after it is
    /// a role definition ("Esquire Financial Holdings, Inc., a Maryland corporation (“ Parent ”)"); otherwise null.
    /// </summary>
    private static string? OwnDefinedRole(string gap)
    {
        var open = gap.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        var close = gap.IndexOf(')', open);
        if (close < 0)
        {
            return null;
        }

        var inner = gap[(open + 1)..close].Trim(' ', '“', '”', '"', '\'', '‘', '’');
        return Array.Find(InterveningSubjectRoles, r => string.Equals(r, inner, StringComparison.Ordinal));
    }

    /// <summary>The text with every parenthetical segment (and its parentheses) replaced by spaces, same length.</summary>
    private static string StripParentheticals(string text)
    {
        var chars = text.ToCharArray();
        var depth = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '(')
            {
                depth++;
            }

            var inside = depth > 0;
            if (chars[i] == ')' && depth > 0)
            {
                depth--;
            }

            if (inside)
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// SPEC 229 rule 3 — true when the filer GOVERNS the completion noun phrase starting at
    /// <paramref name="phraseStart"/>: it is immediately preceded (articles aside) by a possessive of the company or
    /// its alias ("the Company's completion of the acquisition of …"), or by a company mention followed only by
    /// <see cref="CompletionGovernorWords"/> and parentheticals ("{company} Announces Completion of …").
    /// </summary>
    private static bool GovernsCompletion(
        string sentenceLower, int phraseStart, List<int> companyPositions, IReadOnlyList<string> mentions)
    {
        var before = sentenceLower[..phraseStart].TrimEnd();
        foreach (var article in DirectObjectFillerWords)
        {
            if (before.EndsWith(" " + article, StringComparison.Ordinal))
            {
                before = before[..^article.Length].TrimEnd();
                break;
            }
        }

        foreach (var possessive in new[] { "’s", "'s" })
        {
            if (!before.EndsWith(possessive, StringComparison.Ordinal))
            {
                continue;
            }

            var owner = before[..^possessive.Length];
            if (owner.EndsWith(" " + FilerAliasWord, StringComparison.Ordinal) || owner == FilerAliasWord)
            {
                return true;
            }

            if (companyPositions.Any(p => MentionEnd(sentenceLower, p, mentions) == owner.Length))
            {
                return true;
            }
        }

        foreach (var p in companyPositions)
        {
            var mentionEnd = MentionEnd(sentenceLower, p, mentions);
            if (mentionEnd > phraseStart)
            {
                continue;
            }

            var words = StripParentheticals(sentenceLower[mentionEnd..phraseStart])
                .Split([' ', '\t', '\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries);
            if (words.All(w => Array.IndexOf(CompletionGovernorWords, w) >= 0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// SPEC 229 rules 2 + 3 — true when the company is the DIRECT object of the phrase ending at
    /// <paramref name="phraseEnd"/>: a company mention, or (<paramref name="includeAlias"/>) the alias "the Company"
    /// (not its possessive). The alias is read as the filer only when the company is NOT the verb's subject — the
    /// target reading — and only when every (the “Company”) definition in the body is provably the filer's own
    /// (<see cref="DefinesCompanyAliasForAnotherParty"/>). When the company governs the verb, "the Company" is another party's defined term ("Esquire
    /// (“Parent”) … pursuant to which Parent agreed to acquire the Company"), so the acquirer reading ignores it.
    /// </summary>
    private static bool ObjectIsCompany(string sentenceLower, int phraseEnd, List<int> companyPositions, bool includeAlias)
    {
        if (companyPositions.Any(p => p >= phraseEnd && IsDirectObject(sentenceLower, phraseEnd, p)))
        {
            return true;
        }

        if (!includeAlias)
        {
            return false;
        }

        var alias = sentenceLower.IndexOf(FilerAliasWord, phraseEnd, StringComparison.Ordinal);
        if (alias < 0 || !IsDirectObject(sentenceLower, phraseEnd, alias) || !IsWholePhrase(sentenceLower, alias, FilerAliasWord.Length))
        {
            return false;
        }

        var after = sentenceLower[(alias + FilerAliasWord.Length)..];
        return !after.StartsWith("’s", StringComparison.Ordinal) && !after.StartsWith("'s", StringComparison.Ordinal);
    }

    /// <summary>
    /// SPEC 229 rule 1 — true when the object of the acquisition phrase ending at <paramref name="phraseEnd"/> is a
    /// NAMED party: within <see cref="ObjectProximity"/> characters, outside parentheticals, a capitalised word that is
    /// not an article, not a <see cref="DefinedTermRoles"/> role, not a <see cref="UnnamedObjectWords"/> word and not
    /// part of a company mention ("Aehr Test Systems to Acquire Incal Technology" — yes; "to acquire 599,808 shares
    /// (5%) of Graham common stock" — no).
    /// </summary>
    private static bool HasNamedObject(
        string sentence, string sentenceLower, int phraseEnd, List<int> companyPositions, IReadOnlyList<string> mentions)
    {
        var windowEnd = Math.Min(sentence.Length, phraseEnd + ObjectProximity);
        var window = StripParentheticals(sentence[phraseEnd..windowEnd]);
        var i = 0;
        while (i < window.Length)
        {
            while (i < window.Length && !char.IsLetter(window[i]))
            {
                i++;
            }

            var start = i;
            while (i < window.Length && (char.IsLetterOrDigit(window[i]) || window[i] is '-' or '&' or '.'))
            {
                i++;
            }

            if (start >= window.Length)
            {
                break;
            }

            var word = window[start..i].TrimEnd('.');
            if (word.Length == 0 || !char.IsUpper(word[0]))
            {
                continue;
            }

            var wordLower = word.ToLowerInvariant();
            var absolute = phraseEnd + start;
            if (Array.IndexOf(DirectObjectFillerWords, wordLower) >= 0
                || Array.IndexOf(UnnamedObjectWords, wordLower) >= 0
                || IsDefinedTermRole(wordLower)
                || companyPositions.Any(p => p <= absolute && absolute < MentionEnd(sentenceLower, p, mentions)))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// SPEC 229 rule 1 — the merger-STRUCTURE veto: the company is the DIRECT object of a
    /// <see cref="AcquirerVetoBeforeCompany"/> phrase ("Merger Sub, a wholly owned subsidiary of {company}"). The
    /// caller applies it only in a filing with an explicit merger phrase.
    /// </summary>
    private static bool IsSubsidiaryOfCompany(string sentenceLower, List<int> companyPositions)
    {
        foreach (var phrase in AcquirerVetoBeforeCompany)
        {
            for (var at = sentenceLower.IndexOf(phrase, StringComparison.Ordinal);
                 at >= 0;
                 at = sentenceLower.IndexOf(phrase, at + 1, StringComparison.Ordinal))
            {
                var end = at + phrase.Length;
                if (companyPositions.Any(p => p >= end && IsDirectObject(sentenceLower, end, p)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// SPEC 229 rule 1 — a clause that is merger context on its own: it carries "definitive agreement" AND an
    /// acquisition word ("MarineMax Enters into Definitive Agreement to be Acquired by …").
    /// </summary>
    private static bool IsDefinitiveAgreementAcquisitionClause(string sentenceLower) =>
        sentenceLower.Contains(DefinitiveAgreementPhrase, StringComparison.Ordinal)
        && sentenceLower.Contains("acquire", StringComparison.Ordinal);

    /// <summary>The first clause carrying an explicit merger phrase — the deciding clause of a company-not-target outcome.</summary>
    private static string? FirstClauseWithMergerPhrase(IReadOnlyList<string> sentences) =>
        sentences.FirstOrDefault(s =>
        {
            var lower = s.ToLowerInvariant();
            return MergerAgreementPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal));
        });

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
        string sentence, string sentenceLower, List<int> companyPositions, IReadOnlyList<string> mentions, bool aliasIsFiler)
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

        // SPEC 229 rule 2: "… pursuant to which Parent agreed to acquire the Company" — a pending acquisition verb
        // whose DIRECT object is the company (a mention, or — only when every (the “Company”) definition in the body is
        // provably the filer's own — the alias "the Company"), and whose subject is NOT the company (neither a governing
        // mention nor a role the filer is defined as). (ReadAcquirerSide already ran on this clause: a verb the company
        // governs never reaches here as a target.)
        var filerRoles = FilerDefinedRoles(sentence, sentenceLower, companyPositions, mentions);
        foreach (var phrase in PendingAcquireVerbPhrases)
        {
            for (var at = sentenceLower.IndexOf(phrase, StringComparison.Ordinal);
                 at >= 0;
                 at = sentenceLower.IndexOf(phrase, at + 1, StringComparison.Ordinal))
            {
                var end = at + phrase.Length;
                if (IsWholePhrase(sentenceLower, at, phrase.Length)
                    && ObjectIsCompany(sentenceLower, end, companyPositions, includeAlias: aliasIsFiler)
                    && !companyPositions.Any(p => GovernsVerb(sentence, sentenceLower, p, mentions, at, aliasIsFiler))
                    && !SubjectIsFilerRole(sentence, at, filerRoles))
                {
                    return true;
                }
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

        // SPEC 229 (defence in depth): a capture that runs across a sentence boundary ("Item 1.01 Entry into a Material
        // Definitive Agreement. On March 2") is a fragment of text, not a name.
        if (SpansSentenceBoundary(name))
        {
            return false;
        }

        return !mentions.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>
    /// SPEC 229 — true when <paramref name="name"/> holds a period followed by whitespace that ends a WORD rather than an
    /// abbreviation or initialism ("Agreement. On" — yes; "Inc. and", "U.S. Steel", "N.V. Holdings" — no), reusing
    /// <see cref="NonTerminalAbbreviations"/>.
    /// </summary>
    private static bool SpansSentenceBoundary(string name)
    {
        for (var i = 1; i < name.Length - 1; i++)
        {
            if (name[i] != '.' || !char.IsWhiteSpace(name[i + 1]))
            {
                continue;
            }

            var start = i;
            while (start > 0 && (char.IsLetterOrDigit(name[start - 1]) || name[start - 1] == '.'))
            {
                start--;
            }

            var word = name[start..i];
            if (word.Length > 2
                && !word.Contains('.', StringComparison.Ordinal)
                && Array.IndexOf(NonTerminalAbbreviations, word.ToLowerInvariant()) < 0)
            {
                return true;
            }
        }

        return false;
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
    /// The <see cref="ConsiderationExclusions"/> entry that disqualifies this amount, or null. SPEC 229 rule 5: the
    /// phrase must GOVERN the amount in the same clause — it must end immediately before the amount, or (for
    /// <see cref="ExclusionPlacement.BeforeOrAfter"/>) start immediately after it, with only
    /// <see cref="ExclusionConnectorWords"/> between. The text read never reaches back past the PREVIOUS amount in
    /// the clause nor forward past the next one, so an exclusion that belongs to one amount can never disqualify its
    /// neighbour; and a clause never reaches another sentence (<see cref="SplitSentences"/>).
    /// </summary>
    private static ConsiderationExclusion? ExcludedBy(
        string sentenceLower, AmountCandidate candidate, int previousEnd, int nextStart)
    {
        var behind = previousEnd < candidate.DollarIndex
            ? StripConnectors(sentenceLower[previousEnd..candidate.DollarIndex], fromEnd: true)
            : string.Empty;
        var ahead = candidate.End < nextStart
            ? StripConnectors(sentenceLower[candidate.End..nextStart], fromEnd: false)
            : string.Empty;

        foreach (var exclusion in ConsiderationExclusions)
        {
            var hit = behind.EndsWith(exclusion.Phrase, StringComparison.Ordinal)
                || behind.EndsWith(exclusion.Phrase + "s", StringComparison.Ordinal)
                || (exclusion.Placement == ExclusionPlacement.BeforeOrAfter
                    && ahead.StartsWith(exclusion.Phrase, StringComparison.Ordinal));
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

    /// <summary>
    /// SPEC 229 rule 5 — <paramref name="text"/> with its <see cref="ExclusionConnectorWords"/> and whitespace removed
    /// from the end nearest the amount (<paramref name="fromEnd"/>: the text BEFORE the amount; otherwise the text
    /// after it), so what remains ends / starts with whatever governs the amount.
    /// </summary>
    private static string StripConnectors(string text, bool fromEnd)
    {
        var s = text;
        while (true)
        {
            s = fromEnd ? s.TrimEnd() : s.TrimStart();
            var stripped = false;

            // A defined-term parenthetical ("exercise price per share (the “Exercise Price”) of $9.00") is a connector.
            if (fromEnd && s.EndsWith(')') && s.LastIndexOf('(') is var open and >= 0 && IsDefinedTermParenthetical(s[open..]))
            {
                s = s[..open];
                continue;
            }

            if (!fromEnd && s.StartsWith('(') && s.IndexOf(')') is var close and > 0 && IsDefinedTermParenthetical(s[..(close + 1)]))
            {
                s = s[(close + 1)..];
                continue;
            }

            foreach (var word in ExclusionConnectorWords)
            {
                var matches = fromEnd
                    ? s.EndsWith(word, StringComparison.Ordinal) && (s.Length == word.Length || !char.IsLetterOrDigit(s[^(word.Length + 1)]))
                    : s.StartsWith(word, StringComparison.Ordinal) && (s.Length == word.Length || !char.IsLetterOrDigit(s[word.Length]));
                if (matches)
                {
                    s = fromEnd ? s[..^word.Length] : s[word.Length..];
                    stripped = true;
                    break;
                }
            }

            if (!stripped)
            {
                return s;
            }
        }
    }

    /// <summary>SPEC 229 rule 5 — a parenthetical holding a quoted defined term and nothing else structural ("(the “Exercise Price”)").</summary>
    private static bool IsDefinedTermParenthetical(string parenthetical) =>
        parenthetical.Length >= 4
        && parenthetical.IndexOf('(', 1) < 0
        && parenthetical.IndexOfAny(['“', '"', '‘']) >= 0
        && !parenthetical.Contains('$', StringComparison.Ordinal);

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

    // SPEC 229 rule 4: the KEYWORDS of every acquirer pattern match in any case ("acquired by", "Acquired By",
    // "ACQUIRED BY") through an inline (?i:…) group, while the name group stays case-SENSITIVE — it must start with a
    // capital, and the captured name keeps the filing's own case. (A pattern-wide IgnoreCase would let [A-Z] match
    // "the Company".)
    [GeneratedRegex(
        @"(?i:acquired\s+by)\s+(?<name>[A-Z][^;()]{2,120}?)(?=\s*(?:,\s*(?:a|an|the)\s|\.\s|\.$|;|\(|$))",
        RegexOptions.CultureInvariant)]
    private static partial Regex AcquiredByRegex();

    // The name group deliberately ADMITS commas: a party is written "Safe Harbor Marinas, LLC, a Delaware
    // limited liability company (\"Parent\")", so a comma-free group would match only the trailing "LLC".
    // CleanEntityName then trims the descriptive apposition while KEEPING the legal suffix, which is part of
    // the name. SPEC 227: whitespace is admitted INSIDE the quotation marks — the live HZO body reads
    // "SHM Holdco, LLC, a Delaware limited liability company (“ Parent ”)", which the v1 pattern could not
    // match, so v1 fell through to "wholly-owned subsidiary of Parent" and recorded the role as the name.
    [GeneratedRegex(
        "(?<name>[A-Z][^;()]{2,120}?)\\s*\\(\\s*[“\"']?\\s*(?i:Parent|Purchaser|Buyer|Acquiror|Acquirer)\\s*[”\"']?\\s*\\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DefinedPartyRegex();

    // SPEC 227: the "by {Acquirer}" that qualifies an "acquisition of {company}" clause, followed by a capitalised party
    // (optionally quoted). Since spec 229 rule 4 "by" matches in any case; the party must still start with a capital.
    [GeneratedRegex("\\b(?i:by)\\s+[“\"']?\\s*[A-Z]", RegexOptions.CultureInvariant)]
    private static partial Regex AcquisitionByPartyRegex();

    [GeneratedRegex(
        @"(?i:wholly[\s-]owned\s+subsidiary\s+of)\s+(?<name>[A-Z][^;()]{2,120}?)(?=\s*(?:,\s*(?:a|an|the)\s|\.\s|\.$|;|\(|$))",
        RegexOptions.CultureInvariant)]
    private static partial Regex SubsidiaryOfRegex();

    [GeneratedRegex(
        @"(?i:by\s+and\s+among)\s+(?<name>[A-Z][^;()]{2,120}?)(?=\s*(?:,\s*(?:a|an|the)\s|\.\s|\.$|;|\(|$))",
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

    // SPEC 229 rule 2: a parenthetical that DEFINES the alias “Company” ("(the “Company”)", "(“Essential” or the
    // “Company”)", "(the “ Company ”, “we”)", and the unquoted "(the Company)"). "Company" must stand alone inside its
    // quotation marks, so a defined term such as “Company Common Stock” is not one. Widening this can only make "the
    // Company" LESS likely to read as the filer — the fail-closed direction.
    [GeneratedRegex("\\((?:[^()]*[“\"‘']\\s*Company\\s*[”\"’'][^()]*|\\s*the\\s+Company\\s*)\\)", RegexOptions.CultureInvariant)]
    private static partial Regex CompanyAliasDefinitionRegex();

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
