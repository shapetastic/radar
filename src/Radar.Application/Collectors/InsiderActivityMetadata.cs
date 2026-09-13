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

    /// <summary>
    /// Metadata key carrying the filing's PRIMARY reporting owner's name (spec 224) — the first
    /// <c>reportingOwner/reportingOwnerId/rptOwnerName</c> in the ownership XML, trimmed, written only when
    /// non-blank. The insider IDENTITY the scoring-time <c>InsiderActivityCollapse</c> buckets on when no
    /// CIK was captured. Before spec 224 the name reached the store ONLY inside the evidence title, so accrued
    /// evidence lacks this key; for that evidence <see cref="TryRead"/> recovers the name from the title
    /// (<see cref="InsiderActivityTitle.TryParseOwner"/>) at READ time and records that it did
    /// (<see cref="InsiderOwnerSource.Title"/>) — nothing on disk is rewritten (AD-8). When this key is present
    /// it always wins. ADDITIVE metadata only, never Title/RawText — evidence identity (the normalized
    /// title+body hash, spec 145) is unmoved.
    /// </summary>
    public const string OwnerNameKey = "insiderOwnerName";

    /// <summary>
    /// Metadata key carrying the same primary reporting owner's SEC CIK (spec 224) —
    /// <c>reportingOwner/reportingOwnerId/rptOwnerCik</c>, trimmed, written only when non-blank. The
    /// PREFERRED insider identity (a person's CIK is stable across filings that spell the name differently);
    /// <see cref="OwnerNameKey"/> is the fallback. Same additive-only rule as the name. A title carries no CIK,
    /// so accrued pre-224 evidence has none and no read-time fallback can supply one.
    /// </summary>
    public const string OwnerCikKey = "insiderOwnerCik";

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
    /// <c>null</c> when <see cref="FilingDateKey"/> is absent or not <c>yyyy-MM-dd</c>;
    /// <see cref="InsiderActivityRead.OwnerCik"/> is <c>null</c> when the key is absent or blank (legacy pre-224
    /// evidence carries none); <see cref="InsiderActivityRead.OwnerName"/> is the trimmed
    /// <see cref="OwnerNameKey"/> value when present and non-blank (<see cref="InsiderOwnerSource.Metadata"/> —
    /// structured metadata always wins), else the owner recovered from <see cref="EvidenceItem.Title"/> in one of
    /// the collector's fixed shapes (<see cref="InsiderOwnerSource.Title"/> — legacy pre-224 evidence), else
    /// <c>null</c> (<see cref="InsiderOwnerSource.NotRecorded"/>: an unparseable title, or the collector's
    /// anonymous-owner placeholder, which is never an identity);
    /// <see cref="InsiderActivityRead.HasCluster"/> is <c>true</c> only when <see cref="ClusterKey"/> holds
    /// <c>"true"</c>/<c>"1"</c> (trimmed, case-insensitive — the same rule the extractor's generic flag read
    /// applies; the collector writes the key only when the flag is set, so absent == not a cluster). Never
    /// throws; <c>null</c> always means "not captured", never a defaulted value.
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

        // Structured metadata always wins; the title fallback (the ONE title parse, shared with the collector's
        // writer) serves accrued pre-224 evidence and is recorded as such, never silently.
        string? ownerName;
        InsiderOwnerSource ownerSource;
        if (metadata.TryGetValue(OwnerNameKey, out var rawOwnerName) && !string.IsNullOrWhiteSpace(rawOwnerName))
        {
            ownerName = rawOwnerName.Trim();
            ownerSource = InsiderOwnerSource.Metadata;
        }
        else if (InsiderActivityTitle.TryParseOwner(evidence.Title) is { } titleOwner)
        {
            ownerName = titleOwner;
            ownerSource = InsiderOwnerSource.Title;
        }
        else
        {
            ownerName = null;
            ownerSource = InsiderOwnerSource.NotRecorded;
        }

        var ownerCik = metadata.TryGetValue(OwnerCikKey, out var rawOwnerCik)
            && !string.IsNullOrWhiteSpace(rawOwnerCik)
                ? rawOwnerCik.Trim()
                : null;

        var cluster = metadata.TryGetValue(ClusterKey, out var rawCluster) ? rawCluster?.Trim() : null;
        var hasCluster = !string.IsNullOrEmpty(cluster)
            && (string.Equals(cluster, "true", StringComparison.OrdinalIgnoreCase) || cluster == "1");

        return new InsiderActivityRead(reason, netValue, filingDate, ownerName, ownerCik, hasCluster, ownerSource);
    }
}

/// <summary>
/// Where <see cref="InsiderActivityRead.OwnerName"/> came from (spec 224 amendment). The CIK has no source axis:
/// it is only ever read from <see cref="InsiderActivityMetadata.OwnerCikKey"/>, because a title carries none.
/// </summary>
public enum InsiderOwnerSource
{
    /// <summary>No owner name could be recovered: no structured key, and a title in no known shape or naming only the anonymous placeholder.</summary>
    NotRecorded = 0,

    /// <summary>The structured <see cref="InsiderActivityMetadata.OwnerNameKey"/> the collector writes since spec 224.</summary>
    Metadata,

    /// <summary>Recovered at read time from the evidence title by <see cref="InsiderActivityTitle.TryParseOwner"/> (accrued pre-224 evidence).</summary>
    Title,
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
/// <param name="OwnerName">The primary reporting owner's name (spec 224): from the structured metadata key
/// when present, else recovered from the evidence title (accrued pre-224 evidence), else <c>null</c> — see
/// <paramref name="OwnerSource"/>.</param>
/// <param name="OwnerCik">The primary reporting owner's SEC CIK (spec 224), or <c>null</c> when not captured —
/// every pre-224 evidence item, since a title carries no CIK. Preferred over <paramref name="OwnerName"/> as the
/// insider identity.</param>
/// <param name="HasCluster">True when the filing carried the spec-93 multi-insider cluster flag. Defaults to
/// <c>false</c> so the two pre-224 construction sites stay source-compatible; the collector writes the key
/// only when set, so <c>false</c> is a measured absence for its evidence, not a defaulted one.</param>
/// <param name="OwnerSource">Where <paramref name="OwnerName"/> came from. Defaults to
/// <see cref="InsiderOwnerSource.NotRecorded"/>, which is consistent with the defaulted <c>null</c> name;
/// <see cref="InsiderActivityMetadata.TryRead"/> always sets it explicitly.</param>
public sealed record InsiderActivityRead(
    string? ClassificationReason,
    decimal? NetValue,
    DateOnly? FilingDate,
    string? OwnerName = null,
    string? OwnerCik = null,
    bool HasCluster = false,
    InsiderOwnerSource OwnerSource = InsiderOwnerSource.NotRecorded);
