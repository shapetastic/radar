using Radar.Application.NewsTyping;

namespace Radar.Application.Efficacy.FilingReads;

/// <summary>One declared adverse phrase and the reason it is in the set. Rendered verbatim in the artifact.</summary>
public sealed record FilingReadNegativePhrase(string Phrase, string Justification);

/// <summary>One declared adverse taxonomy member and the reason it is in the set.</summary>
public sealed record FilingReadNegativeEventType(NewsEventType EventType, string Justification);

/// <summary>What matched one typed fact: an event type, a phrase, or both (each named).</summary>
public sealed record FilingReadNegativeMatch(
    string Statement, IReadOnlyList<string> MatchedEventTypes, IReadOnlyList<string> MatchedPhrases);

/// <summary>
/// The CLOSED, versioned adverse vocabulary the spec-218 section-3 disagreement read uses, and nothing else
/// uses.
/// <para>
/// <b>Why it has to be declared here.</b> <see cref="NewsEventType"/> carries no polarity — the taxonomy was
/// built to say WHAT a fact is about, deliberately not whether it is good or bad — so "a typed fact that
/// disagrees with a Positive filing read" is not expressible over the stored data without a declaration.
/// This is that declaration, kept small, explicit and closed so a reader can see exactly what could match:
/// the artifact renders every member VERBATIM beside the counts, and reports per-phrase match counts so a
/// single over-broad member cannot silently drive the whole rate.
/// </para>
/// <para>
/// <b>It is a coarse lexical proxy, not a verdict.</b> A match means "Radar's own typed record contained
/// adverse-sounding language in the same window", never "the filing read was wrong". Some members are known
/// to over-match in ordinary financial prose (a narrowing net loss still contains "loss"); that is why the
/// per-phrase counts ship beside the rate, and why nothing is promoted, demoted, gated or thresholded from
/// it.
/// </para>
/// <para>
/// <b>NOT a fingerprint input, structurally.</b> This vocabulary is read only by the read-side reporter that
/// writes <c>data/efficacy/directional-filing-reads.*</c>. It enters no signal, no score, no
/// <c>SignalSourceDescriptor</c> field and no <c>ScoringConfigVersion</c> segment; editing it changes an
/// artifact and nothing else. <see cref="Version"/> exists so a future edit is a NAMED change to a published
/// measurement, not a silent one.
/// </para>
/// </summary>
public static class FilingReadDisagreementVocabulary
{
    /// <summary>The vocabulary's version token, stamped on the artifact. An edit here earns a new token.</summary>
    public const string Version = "filing-read-disagreement-v1";

    /// <summary>
    /// The ONE adverse taxonomy member. Every other member of <see cref="NewsEventType"/> is deliberately
    /// EXCLUDED because none of them carries polarity: a <c>RegulatoryOrLegal</c> event can be an approval, a
    /// <c>MarketReaction</c> can be a rise, a <c>FinancingOrDilution</c> event can be a well-received raise,
    /// and an <c>AnalystOrRatingAction</c> can be an upgrade. Including an ambiguous member would manufacture
    /// disagreement out of neutral coverage.
    /// </summary>
    public static readonly IReadOnlyList<FilingReadNegativeEventType> NegativeEventTypes =
    [
        new(
            NewsEventType.ShortSellerOrCritique,
            "Definitionally adverse: the taxonomy member exists to mark a published critique of the company. "
                + "It is the only member whose meaning is one-directional."),
    ];

    /// <summary>
    /// The closed adverse phrase set. Matched case-insensitively against a typed fact's <c>Statement</c> with
    /// WORD boundaries on both sides, so "miss" cannot match inside "mission". Inflections are listed
    /// explicitly rather than stemmed — a stemmer is a second, invisible rule set, and this one has to be
    /// readable in the artifact.
    /// </summary>
    public static readonly IReadOnlyList<FilingReadNegativePhrase> NegativePhrases =
    [
        new("miss", "A reported figure below expectation; the spec-218 worked example's own coverage says 'Earnings Miss'."),
        new("missed", "Inflection of 'miss'."),
        new("misses", "Inflection of 'miss'."),
        new("shortfall", "An explicit gap against a stated expectation or commitment."),
        new("decline", "A stated fall in a business quantity (revenue, orders, margin)."),
        new("declined", "Inflection of 'decline'."),
        new("declines", "Inflection of 'decline'."),
        new("fell", "A stated fall, past tense — the most common form in typed coverage."),
        new("falls", "Inflection of 'fall'."),
        new("drop", "A stated fall; the worked example's 'Drops 6.3%'."),
        new("drops", "Inflection of 'drop'."),
        new("dropped", "Inflection of 'drop'."),
        new("slides", "A stated fall; the worked example's 'slides as valuation pressure ... weigh on sentiment'."),
        new("plunge", "A stated large fall."),
        new("plunged", "Inflection of 'plunge'."),
        new("downgrade", "An analyst action in the adverse direction; the upgrade case is excluded by naming only this form."),
        new("downgraded", "Inflection of 'downgrade'."),
        new("cuts guidance", "A company lowering its own forward outlook — the direct contradiction of a Positive GuidanceChange read."),
        new("cut guidance", "Inflection of 'cuts guidance'."),
        new("lowered guidance", "A company lowering its own forward outlook, alternative phrasing."),
        new("guidance cut", "A company lowering its own forward outlook, noun phrasing."),
        new("loss", "A reported loss is adverse on its face. KNOWN over-matcher (a narrowing net loss still contains it) — which is why per-phrase counts ship beside the rate."),
        new("losses", "Inflection of 'loss'; same over-match caveat."),
        new("impairment", "A written-down asset value."),
        new("write-down", "A written-down asset value, alternative phrasing."),
        new("writedown", "A written-down asset value, unhyphenated."),
        new("restatement", "Previously reported figures withdrawn — directly undermines a read taken from them."),
        new("restated", "Inflection of 'restatement'."),
        new("going concern", "The auditor's substantial-doubt language."),
        new("bankruptcy", "An insolvency process."),
        new("delisting", "Loss of an exchange listing."),
        new("lawsuit", "Litigation against the company."),
        new("class action", "Shareholder or consumer litigation."),
        new("investigation", "A regulatory or law-enforcement enquiry."),
        new("subpoena", "A compelled production of records."),
        new("recall", "A product withdrawal."),
        new("layoffs", "A workforce reduction."),
        new("layoff", "Inflection of 'layoffs'."),
        new("resigns", "An unplanned executive departure."),
        new("resigned", "Inflection of 'resigns'."),
        new("stepped down", "An unplanned executive departure, alternative phrasing."),
        new("dilution", "Existing holders' claims reduced."),
        new("dilutive", "Inflection of 'dilution'."),
        new("short seller", "A published adverse position; pairs with the ShortSellerOrCritique event type."),
        new("fraud", "An allegation or finding of deception."),
        new("underperform", "An explicit adverse relative-performance claim."),
        new("disappointing", "An explicit adverse judgment of a reported result."),
        new("warning letter", "An adverse regulatory finding (FDA and analogues)."),
        new("halted", "Trading or an operation stopped."),
    ];

    /// <summary>
    /// Whether <paramref name="fact"/> matches the adverse vocabulary, and exactly what matched. Returns
    /// <c>null</c> when nothing matched — never a match record with empty lists, so a caller cannot count a
    /// non-match as a match.
    /// </summary>
    public static FilingReadNegativeMatch? TryMatch(NewsTypingValidatedFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        var matchedTypes = new List<string>();
        foreach (var declared in NegativeEventTypes)
        {
            if (fact.EventTypes.Contains(declared.EventType))
            {
                matchedTypes.Add(declared.EventType.ToString());
            }
        }

        var matchedPhrases = new List<string>();
        var statement = fact.Statement ?? string.Empty;
        var lowered = statement.ToLowerInvariant();
        foreach (var declared in NegativePhrases)
        {
            if (ContainsWholeWord(lowered, declared.Phrase))
            {
                matchedPhrases.Add(declared.Phrase);
            }
        }

        return matchedTypes.Count == 0 && matchedPhrases.Count == 0
            ? null
            : new FilingReadNegativeMatch(statement, matchedTypes, matchedPhrases);
    }

    /// <summary>
    /// Ordinal, word-boundary-anchored containment over an ALREADY-lowercased haystack (the caller lowercases
    /// once per statement rather than once per phrase). A boundary is any position not adjacent to a letter or
    /// a digit, so hyphenated members such as "write-down" match as written.
    /// </summary>
    private static bool ContainsWholeWord(string loweredHaystack, string loweredPhrase)
    {
        if (loweredPhrase.Length == 0 || loweredHaystack.Length < loweredPhrase.Length)
        {
            return false;
        }

        var index = 0;
        while (index <= loweredHaystack.Length - loweredPhrase.Length)
        {
            index = loweredHaystack.IndexOf(loweredPhrase, index, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var end = index + loweredPhrase.Length;
            var startOk = index == 0 || !char.IsLetterOrDigit(loweredHaystack[index - 1]);
            var endOk = end == loweredHaystack.Length || !char.IsLetterOrDigit(loweredHaystack[end]);
            if (startOk && endOk)
            {
                return true;
            }

            index++;
        }

        return false;
    }
}
