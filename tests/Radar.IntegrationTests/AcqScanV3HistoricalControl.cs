using System.Text.RegularExpressions;

using Radar.Application.Acquisitions;

namespace Radar.IntegrationTests;

// ============================================================================================================
// SPEC 229 §2 — HISTORICAL CONTROL. DO NOT EDIT. DO NOT REUSE.
//
// This is a FROZEN, VERBATIM copy of the acqscan-v3 rule body exactly as it shipped in
// src/Radar.Application/Acquisitions/AcquisitionAgreementScan.cs at commit b6ba462 (spec 228; unchanged through
// 0faf6ce, the last commit before spec 229 replaced it with acqscan-v4). The mechanical changes are the ones needed
// to compile it outside the Application assembly — the class name; the `internal` members' visibility
// (`MinPlausibleBodyLength` and `IsDefinedTermRole` made `private`, `SplitSentences` made `public`); the internal
// NotRecognised factory replaced by a private one over the public record constructor; the Token/AllOutcomes/Int
// helpers removed (the production vocabulary is reused instead) — plus ONE instrumentation addition, marked
// "SPEC 229 INSTRUMENTATION" where it occurs: a not-recognised result carries the clause that decided it in
// `AcquisitionScanResult.DecidingQuote` (the first acquirer-side clause for company-is-acquirer, the first clause
// holding an explicit merger phrase for company-not-target, the target clause for acquirer-not-named /
// no-stated-consideration). The instrumentation only RECORDS a clause the walk already visited; not one rule,
// phrase, regex, threshold or branch was altered, so every outcome is v3's.
//
// It exists for ONE purpose: spec 229 §2's live measurement must run acqscan-v3 and acqscan-v4 side by side on the
// SAME cached documents, and v3 no longer exists in production. It is a measurement CONTROL, not a shared
// primitive — the reuse-over-copy rule does not apply because this copy must NEVER track the production scan.
// Editing it would silently change what the "v3" column of a recorded measurement means. If a future slice needs
// a different control, add a new frozen file; never edit this one.
// ============================================================================================================
public static partial class AcqScanV3HistoricalControl
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
    private const int MinPlausibleBodyLength = 200;

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
            return NotRecognised(AcquisitionScanOutcome.EmptyBody);
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
            return NotRecognised(AcquisitionScanOutcome.NoMergerAgreement);
        }

        var sentences = SplitSentences(body);

        // Leg (a). Walk the sentences ONCE, classifying each as acquirer-side, target-side or neither.
        // The acquirer-side veto is evaluated first WITHIN each sentence, so a sentence that names the
        // company as the buyer can never also be read as naming it as the target.
        string? targetSentence = null;
        var sawAcquirerSide = false;
        string? firstAcquirerSideClause = null; // SPEC 229 INSTRUMENTATION (records only)
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
                firstAcquirerSideClause ??= sentence; // SPEC 229 INSTRUMENTATION (records only)
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
            var outcome =
                sawAcquirerSide ? AcquisitionScanOutcome.CompanyIsAcquirer
                : hasExplicitMergerPhrase ? AcquisitionScanOutcome.CompanyNotTarget
                : AcquisitionScanOutcome.NoMergerAgreement;

            // SPEC 229 INSTRUMENTATION (records only): the clause that decided the outcome.
            var deciding = outcome switch
            {
                AcquisitionScanOutcome.CompanyIsAcquirer => firstAcquirerSideClause,
                AcquisitionScanOutcome.CompanyNotTarget => sentences.FirstOrDefault(s =>
                    MergerAgreementPhrases.Any(p => s.ToLowerInvariant().Contains(p, StringComparison.Ordinal))),
                _ => null,
            };
            return NotRecognised(outcome, deciding);
        }

        var acquirer = ExtractAcquirer(body, targetSentence, mentions);
        if (acquirer is null)
        {
            return NotRecognised(AcquisitionScanOutcome.AcquirerNotNamed, targetSentence); // SPEC 229 INSTRUMENTATION
        }

        // Leg (b): a per-share consideration, stated in ONE sentence so the quote is a real sentence rather
        // than a stitched-together fragment.
        var consideration = ExtractConsideration(sentences);
        if (consideration is null)
        {
            return NotRecognised(AcquisitionScanOutcome.NoStatedConsideration, targetSentence); // SPEC 229 INSTRUMENTATION
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
            return NotRecognised(AcquisitionScanOutcome.VerbatimCheckFailed);
        }

        if (!body.Contains(consideration.Quote, StringComparison.Ordinal))
        {
            return NotRecognised(AcquisitionScanOutcome.VerbatimCheckFailed);
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
    private static bool IsDefinedTermRole(string candidateLower)
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
    public static IReadOnlyList<string> SplitSentences(string text)
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

    private static AcquisitionScanResult NotRecognised(AcquisitionScanOutcome outcome, string? decidingQuote = null) =>
        new(outcome) { DecidingQuote = decidingQuote };
}
