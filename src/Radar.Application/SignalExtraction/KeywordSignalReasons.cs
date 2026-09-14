namespace Radar.Application.SignalExtraction;

/// <summary>
/// The ONE definition of the <c>Reason</c> texts <see cref="KeywordSignalExtractor"/> writes. Extracted by
/// the spec-225 follow-up (the strings moved verbatim; every emitted Reason is byte-identical) so a READER that
/// classifies a persisted signal's producer — the EvidenceConfidence measurement's
/// <c>SignalProducerRule</c> — recognises the extractor's output through the extractor's own constants rather
/// than a second copy of its wording, which would drift silently on the next edit.
/// <para>
/// Reason text is provenance/audit text only: no scoring component reads it, it is not a
/// <c>RuleSetVersion</c> input and it is not hashed into any fingerprint.
/// </para>
/// <para>
/// <b>Spec 226 adds the item-heading Reason</b> (<see cref="ItemHeading"/>). It still BEGINS with
/// <see cref="MatchedPhrasePrefix"/> — deliberately, so every reader that recognises a keyword phrase rule by
/// that prefix (<c>SignalProducerRule</c>) keeps classifying the rewritten rules without a second predicate —
/// and then names the SEC 8-K item whose heading the phrase is, and says that a heading is an event type and
/// not a direction.
/// </para>
/// </summary>
public static class KeywordSignalReasons
{
    /// <summary>The prefix of every phrase-rule Reason: <c>Matched phrase '{phrase}'</c>.</summary>
    public const string MatchedPhrasePrefix = "Matched phrase '";

    /// <summary>The exact Reason of the spec-70 Neutral <c>MediaAttention</c> event for news coverage.</summary>
    public const string MediaAttention = "Third-party news coverage (media attention)";

    /// <summary>
    /// The closing clause of every item-heading Reason (spec 226). A heading records that an event of a given
    /// type happened; it carries nothing about whether that event is good or bad for the business.
    /// </summary>
    public const string ItemHeadingIsNotADirection = " — an item heading is an event type, not a direction";

    /// <summary>The phrase-rule Reason for <paramref name="phrase"/>: <c>Matched phrase '{phrase}'</c>.</summary>
    public static string MatchedPhrase(string phrase) => MatchedPhrasePrefix + phrase + "'";

    /// <summary>
    /// The item-heading marker for one heading: <c>(the SEC 8-K Item {item} heading phrase)</c>. It describes
    /// the PHRASE, never the evidence: the phrase rules are source-agnostic, so a press release that uses the
    /// same words gets the same, still-true text — the Reason never asserts that the evidence IS an 8-K.
    /// </summary>
    public static string ItemHeadingMarker(SecItemHeadingPhrase heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        return $" (the SEC 8-K Item {heading.Item} heading phrase)";
    }

    /// <summary>
    /// The Reason for the spec-226 Neutral <c>CorporateAction</c> minted from one or more SEC 8-K item-heading
    /// phrases. The FIRST heading is the rule that fired (first match per type, in table order); every further
    /// heading present in the same searchable text is named too, so an 8-K carrying both Item 1.01 and Item 2.01
    /// headings still says so rather than silently dropping the second:
    /// <c>Matched phrase 'material definitive agreement' (the SEC 8-K Item 1.01 heading phrase); also matched
    /// 'completion of acquisition' (the SEC 8-K Item 2.01 heading phrase) — an item heading is an event type,
    /// not a direction</c>. Advice-free by construction: it states what matched and nothing else.
    /// </summary>
    public static string ItemHeading(IReadOnlyList<SecItemHeadingPhrase> matched)
    {
        ArgumentNullException.ThrowIfNull(matched);
        if (matched.Count == 0)
        {
            throw new ArgumentException("At least one matched item heading is required.", nameof(matched));
        }

        var reason = MatchedPhrase(matched[0].Phrase) + ItemHeadingMarker(matched[0]);
        for (var i = 1; i < matched.Count; i++)
        {
            reason += "; also matched '" + matched[i].Phrase + "'" + ItemHeadingMarker(matched[i]);
        }

        return reason + ItemHeadingIsNotADirection;
    }

    /// <summary>
    /// The SEC items an item-heading Reason NAMES, in the order it names them — the reader-side inverse of
    /// <see cref="ItemHeading"/>, built from the same markers so it cannot drift from the writer. Empty for any
    /// other Reason (including the pre-226 <c>Matched phrase '…'</c> text accrued v8 signals carry).
    /// </summary>
    public static IReadOnlyList<SecItemHeadingPhrase> NamedItemHeadings(string? reason)
    {
        if (string.IsNullOrEmpty(reason) || !reason.EndsWith(ItemHeadingIsNotADirection, StringComparison.Ordinal))
        {
            return [];
        }

        return [.. SecItemHeadingPhrases.All
            .Select(h => (Heading: h, Index: reason.IndexOf(ItemHeadingMarker(h), StringComparison.Ordinal)))
            .Where(x => x.Index >= 0)
            .OrderBy(x => x.Index)
            .Select(x => x.Heading)];
    }
}

/// <summary>One SEC 8-K item heading phrase the keyword extractor matches, and the item it heads.</summary>
/// <param name="Phrase">The lower-case phrase matched case-insensitively on the searchable text.</param>
/// <param name="Item">The SEC 8-K item number the phrase is the official heading of (e.g. <c>1.01</c>).</param>
public sealed record SecItemHeadingPhrase(string Phrase, string Item);

/// <summary>
/// The SEC 8-K item-heading phrases that mint a Neutral <c>CorporateAction</c> since spec 226
/// (<c>radar-keyword-rules-v9</c>). ONE definition shared by the extractor's rule table, its Reason and every
/// reader that splits the signals by item, so the three cannot disagree about which phrase heads which item.
/// </summary>
public static class SecItemHeadingPhrases
{
    /// <summary>Item 1.01 — "Entry into a Material Definitive Agreement": any material contract.</summary>
    public static readonly SecItemHeadingPhrase MaterialDefinitiveAgreement = new("material definitive agreement", "1.01");

    /// <summary>Item 2.01 — "Completion of Acquisition or Disposition of Assets": buying OR selling a business.</summary>
    public static readonly SecItemHeadingPhrase CompletionOfAcquisition = new("completion of acquisition", "2.01");

    /// <summary>Both headings, in rule-table order (1.01 first, so it wins first-match-per-type).</summary>
    public static IReadOnlyList<SecItemHeadingPhrase> All { get; } = [MaterialDefinitiveAgreement, CompletionOfAcquisition];
}
