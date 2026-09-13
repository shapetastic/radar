using System.Globalization;
using System.Text.RegularExpressions;

using Radar.Domain.Signals;

namespace Radar.Application.Collectors;

/// <summary>
/// THE one definition of the Form 4 evidence TEXT shapes — the <c>Title</c>/<c>RawText</c> that
/// <c>SecForm4Collector.MapToEvidence</c> synthesizes from real filing metadata, the anonymous-owner
/// placeholder it substitutes for a blank owner, and the inverse parse that recovers the owner name from a
/// stored title. Both directions route through the SAME phrase pieces below, so the writer and the reader of
/// the shape can never drift (CLAUDE.md reuse-over-copy; spec 224 amendment, 2026-09-13).
/// <para>
/// <b>Why the parse exists.</b> Spec 224 added the structured <see cref="InsiderActivityMetadata.OwnerNameKey"/>
/// / <see cref="InsiderActivityMetadata.OwnerCikKey"/> at collection time, but every Form 4 evidence item
/// accrued before it carries the reporting owner ONLY inside this title — which Radar's own collector wrote in
/// one of three fixed shapes. Measured over the live store on 2026-09-13, a parse over those shapes resolves
/// the owner for 1,253 of 1,253 accrued <c>sec-form4</c> records. <see cref="InsiderActivityMetadata.TryRead"/>
/// therefore falls back to <see cref="TryParseOwner"/> when the structured name is absent, at READ time: no
/// evidence file is written, modified or backfilled (AD-8). It is the ONLY place a title is parsed — the
/// collapse and the scorer consume <see cref="InsiderActivityRead"/> only.
/// </para>
/// <para>
/// <b>The composed text is evidence IDENTITY.</b> Evidence identity is the normalized title+body hash
/// (spec 145), so any byte change to <see cref="Compose"/>'s output would re-hash every newly collected filing
/// and break cross-run dedupe. The output is pinned byte-exact for all three shapes by
/// <c>InsiderActivityTitleTests</c> and <c>SecForm4CollectorTests</c>. The phrases also carry the direction
/// the <c>KeywordSignalExtractor</c> rule table matches (<c>insider open-market purchase</c> / <c>sale</c> /
/// <c>insider stock transaction (routine)</c>); that table is a separate, versioned rule set and is not
/// routed through here.
/// </para>
/// </summary>
public static class InsiderActivityTitle
{
    /// <summary>
    /// The placeholder the collector writes in place of a blank primary owner name. It is NEVER an identity:
    /// a title-derived owner equal to it resolves to "not recorded", so anonymous filings for one company can
    /// never collapse into one "person".
    /// </summary>
    public const string AnonymousOwner = "An insider";

    // --- The phrase pieces: the ONE definition both Compose and the parse are built from. ---
    private const string TitleLead = "Form 4 — ";
    private const string PurchasePhrase = "insider open-market purchase";
    private const string SalePhrase = "insider open-market sale";
    private const string RoutinePhrase = "insider stock transaction (routine)";
    private const string BoughtVerb = "bought";
    private const string SoldVerb = "sold";
    private const string PhraseOwnerSeparator = ": ";
    private const string SharesLead = " shares (~$";
    private const string ValueClose = ")";
    private const string DateOpen = " (";
    private const string DateClose = ")";
    private const string BodyLead = "Form 4 accession ";
    private const string BodyFiled = " filed ";
    private const string BodyPhraseOwnerSeparator = " — ";
    private const string BodyClose = ".";

    private static readonly Regex[] OwnerShapes =
    [
        DirectionalShape(PurchasePhrase, BoughtVerb),
        DirectionalShape(SalePhrase, SoldVerb),
        new(
            "^" + Regex.Escape(TitleLead + RoutinePhrase + PhraseOwnerSeparator) + "(?<owner>.+)"
                + Regex.Escape(DateOpen) + "[^()]*" + Regex.Escape(DateClose) + @"\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline),
    ];

    /// <summary>
    /// Composes the evidence <c>Title</c> and <c>RawText</c> for one classified Form 4 filing — byte-identical
    /// to the text the collector has always written. Positive ⇒ the open-market purchase shape, Negative ⇒ the
    /// open-market sale shape, anything else ⇒ the routine shape (no shares/value). A blank
    /// <paramref name="primaryOwnerName"/> is written as <see cref="AnonymousOwner"/>; a non-blank one is
    /// written VERBATIM (untrimmed), exactly as before. Shares and value use invariant-culture <c>N0</c>.
    /// </summary>
    public static (string Title, string RawText) Compose(
        SignalDirection direction,
        string? primaryOwnerName,
        decimal shares,
        decimal netValue,
        string filingDate,
        string accession)
    {
        var owner = string.IsNullOrWhiteSpace(primaryOwnerName) ? AnonymousOwner : primaryOwnerName;
        var netValueText = netValue.ToString("N0", CultureInfo.InvariantCulture);
        var sharesText = shares.ToString("N0", CultureInfo.InvariantCulture);

        switch (direction)
        {
            case SignalDirection.Positive:
            case SignalDirection.Negative:
                var (phrase, verb) = direction == SignalDirection.Positive
                    ? (PurchasePhrase, BoughtVerb)
                    : (SalePhrase, SoldVerb);
                var transaction = owner + " " + verb + " " + sharesText + SharesLead + netValueText + ValueClose;
                return (
                    TitleLead + phrase + PhraseOwnerSeparator + transaction + DateOpen + filingDate + DateClose,
                    BodyLead + accession + BodyFiled + filingDate + PhraseOwnerSeparator + phrase
                        + BodyPhraseOwnerSeparator + transaction + BodyClose);
            default:
                return (
                    TitleLead + RoutinePhrase + PhraseOwnerSeparator + owner + DateOpen + filingDate + DateClose,
                    BodyLead + accession + BodyFiled + filingDate + PhraseOwnerSeparator + RoutinePhrase
                        + BodyPhraseOwnerSeparator + owner + BodyClose);
        }
    }

    /// <summary>
    /// Recovers the reporting owner's name from a stored Form 4 title in one of the three shapes
    /// <see cref="Compose"/> writes. Returns <c>null</c> — "not recorded", never a guess — when the title
    /// matches no shape, when the owner segment is blank, or when it is the <see cref="AnonymousOwner"/>
    /// placeholder. The returned name is trimmed. Never throws on any string.
    /// </summary>
    public static string? TryParseOwner(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        foreach (var shape in OwnerShapes)
        {
            var match = shape.Match(title);
            if (!match.Success)
            {
                continue;
            }

            var owner = match.Groups["owner"].Value.Trim();
            return owner.Length == 0 || string.Equals(owner, AnonymousOwner, StringComparison.Ordinal)
                ? null
                : owner;
        }

        return null;
    }

    private static Regex DirectionalShape(string phrase, string verb) =>
        new(
            "^" + Regex.Escape(TitleLead + phrase + PhraseOwnerSeparator) + "(?<owner>.+)"
                + Regex.Escape(" " + verb + " ") + "-?[0-9,]+" + Regex.Escape(SharesLead) + "-?[0-9,]+"
                + Regex.Escape(ValueClose + DateOpen) + "[^()]*" + Regex.Escape(DateClose) + @"\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
}
