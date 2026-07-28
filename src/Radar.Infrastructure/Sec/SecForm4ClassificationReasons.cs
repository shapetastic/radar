namespace Radar.Infrastructure.Sec;

/// <summary>
/// The closed set of stable tokens naming the branch <see cref="HttpSecForm4Reader"/> took when it
/// classified a Form 4 filing's transactions into a filing-level direction (spec 156). Spec 156's audit
/// found the branch was computed live (the 10b5-1 plan flag, the mixed-buy-sell vs no-discretionary
/// distinction) but never persisted — only <c>insiderDirection</c>/<c>insiderNetValue</c> reached disk —
/// so the REASON for every accrued insider classification is permanently Unknown. Going forward the
/// collector writes the token under the <c>insiderClassificationReason</c> metadata key (additive metadata
/// only, never Title/RawText, so evidence identity — the normalized title+body hash alone, spec 145 — is
/// unmoved). Consts so the reader, collector and tests share one definition instead of scattered literals.
/// </summary>
internal static class SecForm4ClassificationReasons
{
    /// <summary>Every transaction was skipped because the filing declares a 10b5-1 pre-arranged plan.</summary>
    public const string Plan10b51 = "plan-10b5-1";

    /// <summary>Discretionary open-market buy value only (the Positive branch).</summary>
    public const string DiscretionaryBuy = "discretionary-buy";

    /// <summary>Discretionary open-market sell value only (the Negative branch).</summary>
    public const string DiscretionarySale = "discretionary-sale";

    /// <summary>Both discretionary buy and sell value in one filing — genuinely ambiguous, Neutral.</summary>
    public const string MixedBuySell = "mixed-buy-sell";

    /// <summary>
    /// No discretionary transaction value at all: grants/exercises/withholding/gifts (NeutralExcluded
    /// codes), holdings-only, or an empty filing.
    /// </summary>
    public const string NoDiscretionaryTransactions = "no-discretionary-transactions";
}
