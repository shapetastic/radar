using System.Globalization;

namespace Radar.Application.Efficacy.EvidenceConfidence;

/// <summary>
/// The measured inputs of the verdict's EVIDENCE-TYPE axis (added by <c>evidence-confidence-verdict-v2</c>): which
/// term carries the spread of EvidenceConfidence, which signal type / producer carries that term among the
/// above-median companies, and ρ(EvidenceConfidence, Trajectory). All measured; none is a threshold.
/// </summary>
public sealed record EvidenceConfidenceEvidenceTypeInputs(
    string? DominantTerm,
    double? DominantTermLogVarianceShare,
    int AboveMedianCompanies,
    string? AboveMedianModalSignalType,
    int? AboveMedianModalSignalTypeCompanies,
    double? AboveMedianModalSignalTypeShare,
    string? AboveMedianModalProducer,
    int? AboveMedianModalProducerCompanies,
    double? RhoEvidenceConfidenceVsTrajectory)
{
    public static EvidenceConfidenceEvidenceTypeInputs NotRecorded { get; } =
        new(null, null, 0, null, null, null, null, null, null);
}

/// <summary>
/// The deterministic spec-225 §3 rule, <c>evidence-confidence-verdict-v2</c>. Its thresholds are the RULE's
/// choices, printed beside the verdict — a reader who disagrees with them can re-read the same inputs under
/// another rule. Pure (AD-3).
/// <list type="number">
/// <item>No counterfactual (non-v8 formula, or fewer than two ranked companies) ⇒ <c>NotDetermined</c>.</item>
/// <item>(b) FLAT TAX when the share of companies whose rank changed under the held-constant counterfactual
/// is ≤ <see cref="RankChangedShareAtOrBelowIsFlatTax"/>, OR the modal value's share of included companies is
/// ≥ <see cref="ModalShareAtOrAboveIsFlatTax"/>.</item>
/// <item>(c) DISCRIMINATES THE WRONG THING otherwise when EITHER axis fires:
/// <b>CollectorCount</b> — |ρ(EvidenceConfidence, DistinctSourceTypes)| ≥
/// <see cref="AbsRhoDistinctSourceTypesAtOrAboveIsWrongThing"/>; <b>EvidenceType</b> — the dominant term is
/// <c>bestConfidence</c> with a log-variance share ≥ <see cref="DominantTermLogVarianceShareAtOrAboveIsSingleTerm"/>
/// AND the modal signal type's share of the above-median companies' best-confidence signals is ≥
/// <see cref="AboveMedianModalSignalTypeShareAtOrAboveIsSingleType"/>. An undefined input cannot satisfy an axis.</item>
/// <item>(a) DISCRIMINATES otherwise.</item>
/// </list>
/// <para>
/// <b>Why v2 exists.</b> v1 tested (c) ONLY on the collector-count axis — the spec's own example taken literally —
/// and read the live store as (a). The term decomposition then showed <c>bestQualityWeight</c> and
/// <c>distinctSourceTypes</c> nearly constant, so the spread had to be coming from <c>bestConfidence</c>, an axis v1
/// never examined. v2 keeps v1's branches unchanged and adds that axis.
/// </para>
/// </summary>
public static class EvidenceConfidenceVerdictRule
{
    public const string Version = "evidence-confidence-verdict-v2";

    public const double RankChangedShareAtOrBelowIsFlatTax = 0.25;

    public const double ModalShareAtOrAboveIsFlatTax = 0.50;

    public const double AbsRhoDistinctSourceTypesAtOrAboveIsWrongThing = 0.5;

    public const double DominantTermLogVarianceShareAtOrAboveIsSingleTerm = 0.75;

    public const double AboveMedianModalSignalTypeShareAtOrAboveIsSingleType = 0.75;

    public const string AxisCollectorCount = "CollectorCount";

    public const string AxisEvidenceType = "EvidenceType";

    public static EvidenceConfidenceVerdictSection Evaluate(
        bool counterfactualAvailable,
        string? counterfactualNotAvailableReason,
        double? rankChangedShare,
        double? modalShare,
        double? rhoEvidenceConfidenceVsDistinctSourceTypes,
        EvidenceConfidenceEvidenceTypeInputs? evidenceType = null)
    {
        var et = evidenceType ?? EvidenceConfidenceEvidenceTypeInputs.NotRecorded;

        if (!counterfactualAvailable || rankChangedShare is null || modalShare is null)
        {
            var reason = counterfactualNotAvailableReason
                ?? "the counterfactual inputs were not recorded";
            return Section(
                EvidenceConfidenceVerdict.NotDetermined,
                "Verdict: NOT DETERMINED — " + reason + "; none of (a)/(b)/(c) can be read from this artifact.",
                reason,
                rankChangedShare,
                modalShare,
                rhoEvidenceConfidenceVsDistinctSourceTypes,
                [],
                et);
        }

        var inputs =
            $"rank-change share {Fmt(rankChangedShare.Value)} (rule: ≤ {Fmt(RankChangedShareAtOrBelowIsFlatTax)} is flat), "
                + $"modal share {Fmt(modalShare.Value)} (rule: ≥ {Fmt(ModalShareAtOrAboveIsFlatTax)} is flat), "
                + "|ρ(EvidenceConfidence, DistinctSourceTypes)| "
                + (rhoEvidenceConfidenceVsDistinctSourceTypes is { } r
                    ? Fmt(Math.Abs(r))
                    : "undefined")
                + $" (rule: ≥ {Fmt(AbsRhoDistinctSourceTypesAtOrAboveIsWrongThing)} is the collector-count axis), "
                + "dominant term " + (et.DominantTerm ?? "undefined") + " at log-variance share "
                + FmtOr(et.DominantTermLogVarianceShare)
                + $" (rule: bestConfidence at ≥ {Fmt(DominantTermLogVarianceShareAtOrAboveIsSingleTerm)} is single-term), "
                + "above-median modal best-confidence signal type " + (et.AboveMedianModalSignalType ?? "undefined")
                + " " + (et.AboveMedianModalSignalTypeCompanies?.ToString(CultureInfo.InvariantCulture) ?? "undefined")
                + "/" + et.AboveMedianCompanies.ToString(CultureInfo.InvariantCulture)
                + " = " + FmtOr(et.AboveMedianModalSignalTypeShare)
                + $" (rule: ≥ {Fmt(AboveMedianModalSignalTypeShareAtOrAboveIsSingleType)} is single-type)";

        if (rankChangedShare.Value <= RankChangedShareAtOrBelowIsFlatTax
            || modalShare.Value >= ModalShareAtOrAboveIsFlatTax)
        {
            return Section(
                EvidenceConfidenceVerdict.FlatTax,
                "Verdict: (b) it is a FLAT TAX — the data supports (b), not (a) or (c): " + inputs + ".",
                null,
                rankChangedShare,
                modalShare,
                rhoEvidenceConfidenceVsDistinctSourceTypes,
                [],
                et);
        }

        var collectorAxis = rhoEvidenceConfidenceVsDistinctSourceTypes is { } rho
            && Math.Abs(rho) >= AbsRhoDistinctSourceTypesAtOrAboveIsWrongThing;
        var evidenceTypeAxis =
            string.Equals(et.DominantTerm, EvidenceConfidenceDistributionReporter.TermBestConfidence, StringComparison.Ordinal)
            && et.DominantTermLogVarianceShare is { } termShare
            && termShare >= DominantTermLogVarianceShareAtOrAboveIsSingleTerm
            && et.AboveMedianModalSignalTypeShare is { } typeShare
            && typeShare >= AboveMedianModalSignalTypeShareAtOrAboveIsSingleType;

        if (evidenceTypeAxis)
        {
            List<string> axes = collectorAxis ? [AxisCollectorCount, AxisEvidenceType] : [AxisEvidenceType];
            return Section(
                EvidenceConfidenceVerdict.DiscriminatesTheWrongThing,
                EvidenceTypeSentence(et, collectorAxis, rankChangedShare.Value, modalShare.Value) + " — " + inputs + ".",
                null,
                rankChangedShare,
                modalShare,
                rhoEvidenceConfidenceVsDistinctSourceTypes,
                axes,
                et);
        }

        if (collectorAxis)
        {
            return Section(
                EvidenceConfidenceVerdict.DiscriminatesTheWrongThing,
                "Verdict: (c) it DISCRIMINATES THE WRONG THING, on the collector-count axis — the data supports (c), "
                    + "not (a) or (b): " + inputs + ".",
                null,
                rankChangedShare,
                modalShare,
                rhoEvidenceConfidenceVsDistinctSourceTypes,
                [AxisCollectorCount],
                et);
        }

        return Section(
            EvidenceConfidenceVerdict.Discriminates,
            "Verdict: (a) it DISCRIMINATES — the data supports (a), not (b) or (c): " + inputs + ".",
            null,
            rankChangedShare,
            modalShare,
            rhoEvidenceConfidenceVsDistinctSourceTypes,
            [],
            et);
    }

    /// <summary>
    /// The (c)-on-evidence-type sentence. It states what is MEASURED (the term share, the type share, ρ) and names
    /// the producer as the classification rule reports it; it claims nothing about whether the confidence is right.
    /// </summary>
    private static string EvidenceTypeSentence(
        EvidenceConfidenceEvidenceTypeInputs et, bool alsoCollectorAxis, double rankChangedShare, double modalShare)
    {
        var producerNamed = Enum.TryParse<SignalProducer>(et.AboveMedianModalProducer, out var producer);
        var narrative = producerNamed && producer == SignalProducer.AiEarningsReadDirectional
            ? "whether a company's strongest in-window signal is the AI earnings read and how confident that read "
                + "reported itself to be (or the comparability cap, where the release declared a break)"
            : producerNamed && producer == SignalProducer.KeywordPhraseRule
                ? "which keyword rule fired, whose confidence is the rule's fixed constant"
                : "which kind of signal is a company's strongest and the confidence its producer assigned it";

        return "Verdict: (c) it DISCRIMINATES THE WRONG THING, on the evidence-TYPE axis"
            + (alsoCollectorAxis ? " (and the collector-count axis)" : string.Empty)
            + " — the data supports (c), not (a) or (b): it is not a flat tax (rank-change share "
            + Fmt(rankChangedShare) + ", modal share " + Fmt(modalShare) + "), but "
            + et.DominantTerm + " carries " + FmtOr(et.DominantTermLogVarianceShare)
            + " of the variance of ln EvidenceConfidence and "
            + (et.AboveMedianModalSignalTypeCompanies?.ToString(CultureInfo.InvariantCulture) ?? "undefined")
            + " of " + et.AboveMedianCompanies.ToString(CultureInfo.InvariantCulture)
            + " above-median companies take it from a " + et.AboveMedianModalSignalType + " signal ("
            + (et.AboveMedianModalProducerCompanies?.ToString(CultureInfo.InvariantCulture) ?? "undefined")
            + " of the " + et.AboveMedianCompanies.ToString(CultureInfo.InvariantCulture) + " from "
            + (producerNamed ? SignalProducerRule.Describe(producer) : "an unrecorded producer")
            + "), so it ranks companies chiefly on " + narrative
            + "; a directional signal's Confidence also weights its own direction mass in Trajectory, so the two "
            + "multiplied terms share an input and ρ(EvidenceConfidence, Trajectory) = "
            + FmtOr(et.RhoEvidenceConfidenceVsTrajectory) + " cannot be read as independent corroboration";
    }

    private static EvidenceConfidenceVerdictSection Section(
        EvidenceConfidenceVerdict verdict,
        string sentence,
        string? notDeterminedReason,
        double? rankChangedShare,
        double? modalShare,
        double? rho,
        IReadOnlyList<string> axes,
        EvidenceConfidenceEvidenceTypeInputs et) =>
        new(
            RuleVersion: Version,
            Verdict: verdict,
            Sentence: sentence,
            NotDeterminedReason: notDeterminedReason,
            RankChangedShare: rankChangedShare,
            ModalShare: modalShare,
            RhoEvidenceConfidenceVsDistinctSourceTypes: rho,
            RankChangedShareAtOrBelowIsFlatTax: RankChangedShareAtOrBelowIsFlatTax,
            ModalShareAtOrAboveIsFlatTax: ModalShareAtOrAboveIsFlatTax,
            AbsRhoDistinctSourceTypesAtOrAboveIsWrongThing: AbsRhoDistinctSourceTypesAtOrAboveIsWrongThing,
            WrongThingAxes: axes,
            DominantTerm: et.DominantTerm,
            DominantTermLogVarianceShare: et.DominantTermLogVarianceShare,
            AboveMedianCompanies: et.AboveMedianCompanies,
            AboveMedianModalSignalType: et.AboveMedianModalSignalType,
            AboveMedianModalSignalTypeCompanies: et.AboveMedianModalSignalTypeCompanies,
            AboveMedianModalSignalTypeShare: et.AboveMedianModalSignalTypeShare,
            AboveMedianModalProducer: et.AboveMedianModalProducer,
            AboveMedianModalProducerCompanies: et.AboveMedianModalProducerCompanies,
            RhoEvidenceConfidenceVsTrajectory: et.RhoEvidenceConfidenceVsTrajectory,
            DominantTermLogVarianceShareAtOrAboveIsSingleTerm: DominantTermLogVarianceShareAtOrAboveIsSingleTerm,
            AboveMedianModalSignalTypeShareAtOrAboveIsSingleType: AboveMedianModalSignalTypeShareAtOrAboveIsSingleType);

    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FmtOr(double? value) => value is { } v ? Fmt(v) : "undefined";
}
