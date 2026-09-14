using System.Text.RegularExpressions;

using Radar.Application.Acquisitions;

namespace Radar.IntegrationTests;

// ============================================================================================================
// SPEC 227 §3 — HISTORICAL CONTROL. DO NOT EDIT. DO NOT REUSE.
//
// This is a FROZEN, VERBATIM copy of the acqscan-v1 rule body exactly as it shipped in
// src/Radar.Application/Acquisitions/AcquisitionAgreementScan.cs at commit eff79eb (the last commit before spec
// 227 replaced it with acqscan-v2). The only mechanical changes are the ones needed to compile it outside the
// Application assembly: the class name; the two `internal` members' visibility (`MinPlausibleBodyLength` made
// `private`, `SplitSentences` made `public`); the internal NotRecognised factory replaced by the public record
// constructor; and the Token/AllOutcomes/Int helpers removed (the production vocabulary is reused instead). Not
// one rule, phrase, regex or threshold was altered.
//
// It exists for ONE purpose: spec 227 §3's live measurement must run acqscan-v1 and acqscan-v2 side by side on
// the SAME fetched body, and v1 no longer exists in production. It is a measurement CONTROL, not a shared
// primitive — the reuse-over-copy rule does not apply because this copy must NEVER track the production scan.
// Editing it would silently change what the "v1" column of a recorded measurement means. If a future slice
// needs a different control, add a new frozen file; never edit this one.
// ============================================================================================================
public static partial class AcqScanV1HistoricalControl
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
    private const int MinPlausibleBodyLength = 200;

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
            return new AcquisitionScanResult(AcquisitionScanOutcome.EmptyBody);
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
            return new AcquisitionScanResult(AcquisitionScanOutcome.NoMergerAgreement);
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
            return new AcquisitionScanResult(
                sawAcquirerSide ? AcquisitionScanOutcome.CompanyIsAcquirer
                : hasExplicitMergerPhrase ? AcquisitionScanOutcome.CompanyNotTarget
                : AcquisitionScanOutcome.NoMergerAgreement);
        }

        var acquirer = ExtractAcquirer(body, targetSentence, mentions);
        if (acquirer is null)
        {
            return new AcquisitionScanResult(AcquisitionScanOutcome.AcquirerNotNamed);
        }

        // Leg (b): a per-share consideration, stated in ONE sentence so the quote is a real sentence rather
        // than a stitched-together fragment.
        var consideration = ExtractConsideration(sentences);
        if (consideration is null)
        {
            return new AcquisitionScanResult(AcquisitionScanOutcome.NoStatedConsideration);
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
            return new AcquisitionScanResult(AcquisitionScanOutcome.VerbatimCheckFailed);
        }

        if (!body.Contains(consideration.Quote, StringComparison.Ordinal))
        {
            return new AcquisitionScanResult(AcquisitionScanOutcome.VerbatimCheckFailed);
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
    public static IReadOnlyList<string> SplitSentences(string text)
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

}
