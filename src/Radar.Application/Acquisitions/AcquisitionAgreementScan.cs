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
/// SPEC 217 §1 — <c>acqscan-v1</c>: the DETERMINISTIC, PURE, FAIL-CLOSED recognition of a pending
/// acquisition OF THE SUBJECT COMPANY from an item-1.01 8-K's own text. No AI, no clock, no I/O, no
/// randomness (AD-3): the same text and the same company names always yield the same answer.
///
/// <para>
/// <b>Why it must read the filing.</b> A title-only rule fires on every "Entry into a Material Definitive
/// Agreement" — and the accrued store holds 178 item-1.01 filings, overwhelmingly credit agreements, leases
/// and supply contracts. The keyword extractor's "material definitive agreement" rule read MarineMax's
/// $1.5B all-cash sale of the whole company as a <c>StrategicPartnership</c> and the 2026-08-10 report
/// labelled it <b>Thesis improving</b>. Recognition therefore reads the text, and it recognises only when
/// BOTH legs hold:
/// </para>
/// <list type="number">
/// <item><b>Leg (a) — the company is the TARGET.</b> A merger-agreement phrase is present, AND one sentence
/// puts the company's own name (or an alias) in the target POSITION relative to "acquired by" /
/// "merge with and into" / "acquisition of". Position matters: an acquirer-side 8-K contains all the same
/// words ("Merger Sub, a wholly owned subsidiary of {filer}, will merge with and into {target}"), so an
/// unordered co-occurrence test would close the wrong company's thesis.</item>
/// <item><b>Leg (b) — a stated per-share consideration.</b> "$53.00 per share in cash", "converted into the
/// right to receive $53.00", or a stated exchange ratio. A merger agreement with no stated per-share
/// consideration is not recognised.</item>
/// </list>
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
    /// </summary>
    public const string Version = "acqscan-v1";

    /// <summary>
    /// Minimum plausible body length (chars, after trimming) for a scan to be authoritative. Shorter means
    /// the fetch was degenerate (an interstitial/error page stripped to almost nothing), so the answer is
    /// <see cref="AcquisitionScanOutcome.EmptyBody"/> and a later run re-attempts it — the spec-114
    /// precedent. It IS part of what <c>acqscan-v1</c> answers, so changing it is a change to the rule and
    /// must bump <see cref="Version"/> with everything else.
    /// </summary>
    internal const int MinPlausibleBodyLength = 200;

    /// <summary>How far after a phrase a company mention may start and still be that phrase's object.</summary>
    private const int ObjectProximity = 90;

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

    /// <summary>Target phrases the company must appear AFTER (the company is the phrase's object).</summary>
    private static readonly string[] TargetPhrasesBeforeCompany =
    [
        "merge with and into",
        "merged with and into",
        "acquisition of",
    ];

    /// <summary>
    /// Runs <c>acqscan-v1</c> over one item-1.01 filing's text.
    /// </summary>
    /// <param name="text">
    /// The filing body: the primary 8-K document and its EX-99.1 exhibit, stripped to plain text by the
    /// shared normalizer and concatenated. Never null.
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

            if (targetSentence is null && IsTargetSide(sentenceLower, positions))
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

    /// <summary>True when this sentence puts the company in the TARGET position (spec 217 §1 leg (a)).</summary>
    private static bool IsTargetSide(string sentenceLower, List<int> companyPositions)
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
    /// The acquirer's name as the filing states it, or null when the scan cannot read one (counted as
    /// <see cref="AcquisitionScanOutcome.AcquirerNotNamed"/> — never "unknown", never invented). Four
    /// ordered forms, first hit wins, so the answer is deterministic:
    /// <list type="number">
    /// <item>the target sentence's own <c>acquired by {X}</c>;</item>
    /// <item>the defined-term form <c>{X} … ("Parent")</c> / <c>("Purchaser")</c> / <c>("Buyer")</c>;</item>
    /// <item><c>wholly owned subsidiary of {X}</c> anywhere in the body;</item>
    /// <item>the first party after <c>by and among</c> that is not the company itself.</item>
    /// </list>
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
            var name = CleanEntityName(match.Groups["name"].Value);
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

        return !mentions.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    private sealed record ConsiderationFacts(
        string Amount, string Currency, AcquisitionConsiderationKind Kind, string Quote);

    /// <summary>
    /// Leg (b): the FIRST sentence (document order — deterministic) stating a per-share cash and/or stock
    /// consideration, or null. The amount is kept exactly as stated (a string); Radar performs no
    /// arithmetic on it.
    /// </summary>
    private static ConsiderationFacts? ExtractConsideration(IReadOnlyList<string> sentences)
    {
        foreach (var sentence in sentences)
        {
            var lower = sentence.ToLowerInvariant();

            var cash = PerShareCashRegex().Match(sentence);
            if (!cash.Success)
            {
                cash = RightToReceiveCashRegex().Match(sentence);
            }

            var ratio = ExchangeRatioRegex().Match(sentence);

            if (!cash.Success && !ratio.Success)
            {
                continue;
            }

            // A sentence must actually be ABOUT the per-share consideration: it must mention a share.
            if (!lower.Contains("per share", StringComparison.Ordinal)
                && !lower.Contains("each share", StringComparison.Ordinal)
                && !lower.Contains("right to receive", StringComparison.Ordinal))
            {
                continue;
            }

            var mentionsStock = lower.Contains("exchange ratio", StringComparison.Ordinal)
                || lower.Contains("shares of common stock of parent", StringComparison.Ordinal)
                || lower.Contains("validly issued", StringComparison.Ordinal)
                || ratio.Success;
            var mentionsCash = cash.Success || lower.Contains("in cash", StringComparison.Ordinal);

            var kind = (mentionsCash, mentionsStock) switch
            {
                (true, true) => AcquisitionConsiderationKind.Mixed,
                (true, false) => AcquisitionConsiderationKind.Cash,
                (false, true) => AcquisitionConsiderationKind.Stock,
                _ => (AcquisitionConsiderationKind?)null,
            };
            if (kind is null)
            {
                continue;
            }

            var amount = cash.Success
                ? cash.Groups["amount"].Value.Trim()
                : ratio.Groups["ratio"].Value.Trim();
            var currency = cash.Success ? "$" : string.Empty;

            if (amount.Length == 0)
            {
                continue;
            }

            return new ConsiderationFacts(amount, currency, kind.Value, sentence);
        }

        return null;
    }

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
    /// </summary>
    internal static IReadOnlyList<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
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
    // the name.
    [GeneratedRegex(
        "(?<name>[A-Z][^;()]{2,120}?)\\s*\\(\\s*[“\"']?(?:Parent|Purchaser|Buyer|Acquiror|Acquirer)[”\"']?\\s*\\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DefinedPartyRegex();

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
