using System.Globalization;
using Radar.Domain.Evidence;

namespace Radar.Application.Collectors;

/// <summary>
/// The ONE shared contract for the insider-activity (SEC Form 4) evidence metadata: the metadata keys the
/// collector writes, the closed set of classification tokens the reader picks from, and a defensive
/// <see cref="TryRead"/> that projects a stored <see cref="EvidenceItem"/> back into a typed
/// <see cref="InsiderActivityRead"/>. Infrastructure (the Form 4 reader/collector) WRITES through these
/// consts and the Application report builder READS through them, so neither side re-types a magic string
/// and the dependency direction stays Infrastructure → Application (spec 209; reuse-over-copy).
/// <para>
/// Classification tokens (spec 156, moved here verbatim from Infrastructure's
/// <c>SecForm4ClassificationReasons</c> by spec 209): the stable tokens naming the branch the Form 4 reader
/// took when it classified a filing's transactions into a filing-level direction. Spec 156's audit found
/// the branch was computed live (the 10b5-1 plan flag, the mixed-buy-sell vs no-discretionary distinction)
/// but never persisted — only <c>insiderDirection</c>/<c>insiderNetValue</c> reached disk — so the REASON
/// for every accrued insider classification predating it is permanently Unknown. Going forward the
/// collector writes the token under the <see cref="ClassificationReasonKey"/> metadata key (additive
/// metadata only, never Title/RawText, so evidence identity — the normalized title+body hash alone, spec
/// 145 — is unmoved). The token values are PERSISTED DATA: renaming one would orphan accrued evidence.
/// </para>
/// </summary>
public static class InsiderActivityMetadata
{
    /// <summary>Metadata key carrying the classification token (spec 156).</summary>
    public const string ClassificationReasonKey = "insiderClassificationReason";

    /// <summary>
    /// Metadata key carrying the single captured discretionary value (invariant-culture decimal), written
    /// ONLY when positive. For <see cref="MixedBuySell"/> the persisted figure is
    /// <c>Math.Max(purchaseValue, saleValue)</c> — neither a net nor a total — so it must never be summed.
    /// </summary>
    public const string NetValueKey = "insiderNetValue";

    /// <summary>Metadata key for the multi-insider cluster flag (<c>"true"</c> only when set; spec 93).</summary>
    public const string ClusterKey = "insiderCluster";

    /// <summary>Metadata key carrying the filing-level <c>SignalDirection</c> (debug/traceability marker).</summary>
    public const string DirectionKey = "insiderDirection";

    /// <summary>Metadata key for the SEC form type; a Form 4 carries <see cref="Form4"/>.</summary>
    public const string FormKey = "form";

    /// <summary>Metadata key for the SEC filing date (<c>yyyy-MM-dd</c>).</summary>
    public const string FilingDateKey = "filingDate";

    /// <summary>The <see cref="FormKey"/> value identifying an insider-transaction filing.</summary>
    public const string Form4 = "4";

    /// <summary>Every transaction was skipped because the filing declares a 10b5-1 pre-arranged plan.</summary>
    public const string Plan10b51 = "plan-10b5-1";

    /// <summary>Discretionary open-market purchase value only (the Positive branch).</summary>
    public const string DiscretionaryBuy = "discretionary-buy";

    /// <summary>Discretionary open-market sale value only (the Negative branch).</summary>
    public const string DiscretionarySale = "discretionary-sale";

    /// <summary>Both discretionary purchase and sale value in one filing — genuinely ambiguous, Neutral.</summary>
    public const string MixedBuySell = "mixed-buy-sell";

    /// <summary>
    /// No discretionary transaction value at all: grants/exercises/withholding/gifts (NeutralExcluded
    /// codes), holdings-only, or an empty filing.
    /// </summary>
    public const string NoDiscretionaryTransactions = "no-discretionary-transactions";

    /// <summary>
    /// The closed set of classification tokens, in declaration order. A stored token outside this set is
    /// "unrecognised" (counted, never silently dropped — see the report summary).
    /// </summary>
    public static readonly IReadOnlyList<string> AllClassificationReasons =
    [
        Plan10b51,
        DiscretionaryBuy,
        DiscretionarySale,
        MixedBuySell,
        NoDiscretionaryTransactions,
    ];

    /// <summary>
    /// Projects a stored evidence item into its insider-activity read. Returns <c>null</c> when the item is
    /// not a Form 4 (no readable envelope, or <see cref="FormKey"/> is not <see cref="Form4"/>). Otherwise:
    /// <see cref="InsiderActivityRead.ClassificationReason"/> is <c>null</c> when the token is absent/blank
    /// (legacy pre-156 evidence); <see cref="InsiderActivityRead.NetValue"/> is <c>null</c> when the key is
    /// absent or not an invariant-culture decimal; <see cref="InsiderActivityRead.FilingDate"/> is
    /// <c>null</c> when <see cref="FilingDateKey"/> is absent or not <c>yyyy-MM-dd</c>. Never throws;
    /// <c>null</c> always means "not captured", never a defaulted value.
    /// </summary>
    public static InsiderActivityRead? TryRead(EvidenceItem evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (!EvidenceMetadata.TryRead(evidence.MetadataJson, out var metadata, out _))
        {
            return null;
        }

        if (!metadata.TryGetValue(FormKey, out var form) || !string.Equals(form, Form4, StringComparison.Ordinal))
        {
            return null;
        }

        string? reason = metadata.TryGetValue(ClassificationReasonKey, out var rawReason)
            && !string.IsNullOrWhiteSpace(rawReason)
                ? rawReason.Trim()
                : null;

        decimal? netValue = metadata.TryGetValue(NetValueKey, out var rawValue)
            && decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

        DateOnly? filingDate = metadata.TryGetValue(FilingDateKey, out var rawDate)
            && DateOnly.TryParseExact(
                rawDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;

        return new InsiderActivityRead(reason, netValue, filingDate);
    }
}

/// <summary>
/// The typed projection of one Form 4 evidence item's insider metadata (see
/// <see cref="InsiderActivityMetadata.TryRead"/>). Every member is nullable-meaningful: <c>null</c> is
/// "not captured in the store", never 0 or a default.
/// </summary>
/// <param name="ClassificationReason">One of the <see cref="InsiderActivityMetadata.AllClassificationReasons"/>
/// tokens, an unrecognised stored token, or <c>null</c> for legacy evidence without the key.</param>
/// <param name="NetValue">The captured discretionary value, or <c>null</c> when none was persisted.</param>
/// <param name="FilingDate">The SEC filing date, or <c>null</c> when absent/unparseable.</param>
public sealed record InsiderActivityRead(
    string? ClassificationReason,
    decimal? NetValue,
    DateOnly? FilingDate);
