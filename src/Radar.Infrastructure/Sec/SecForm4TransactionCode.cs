namespace Radar.Infrastructure.Sec;

/// <summary>
/// Deterministic classification of a single SEC Form 4 transaction, keyed on the one-letter
/// <c>transactionCode</c>. Only two codes are directional; everything else (and any unknown/blank code)
/// is <see cref="InsiderTxnClassification.NeutralExcluded"/> — a conservative default so a code Radar has
/// not modelled never misfires as bullish/bearish.
/// </summary>
internal enum InsiderTxnClassification
{
    /// <summary>Open-market/private purchase (code <c>P</c>) — insider buying with own money.</summary>
    Buy,

    /// <summary>Open-market/private sale (code <c>S</c>) — discretionary insider selling.</summary>
    Sell,

    /// <summary>Grant/exercise/withholding/gift/conversion/other/unknown — not a discretionary market signal.</summary>
    NeutralExcluded,
}

/// <summary>
/// The SEC Form 4 transaction-code → classification table. Source: SEC Form 4 general instructions and the
/// EDGAR ownership XSL legend. Only <c>P</c> (purchase) and <c>S</c> (sale) are directional; every other
/// modelled code is compensation/mechanical/tax/gift/ambiguous and classifies as
/// <see cref="InsiderTxnClassification.NeutralExcluded"/>, as does any unknown/blank code (conservative
/// default). The 10b5-1 pre-arranged-plan override (a planned sale is not discretionary) lives in the
/// reader, not here — this table classifies the raw code only.
/// <para>
/// Code legend: <c>P</c> open-market/private purchase; <c>S</c> open-market/private sale; <c>A</c>
/// grant/award/other acquisition from the issuer; <c>M</c> exercise/conversion of a derivative security;
/// <c>F</c> payment of exercise price or tax by withholding securities; <c>G</c> bona-fide gift; <c>D</c>
/// disposition to the issuer (e.g. forfeiture); <c>X</c> exercise of an in/at-the-money derivative; <c>C</c>
/// conversion of a derivative; <c>J</c> other (footnote-described).
/// </para>
/// </summary>
internal static class SecForm4TransactionCode
{
    /// <summary>
    /// Classifies a raw <c>transactionCode</c> (case-insensitive, trimmed). <c>P</c> → Buy, <c>S</c> → Sell;
    /// <c>A</c>/<c>M</c>/<c>F</c>/<c>G</c>/<c>D</c>/<c>X</c>/<c>C</c>/<c>J</c> and any unknown/blank code →
    /// <see cref="InsiderTxnClassification.NeutralExcluded"/> (conservative default).
    /// </summary>
    public static InsiderTxnClassification Classify(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return InsiderTxnClassification.NeutralExcluded;
        }

        return code.Trim().ToUpperInvariant() switch
        {
            "P" => InsiderTxnClassification.Buy,
            "S" => InsiderTxnClassification.Sell,
            // A grant, M exercise/conversion, F tax-withholding, G gift, D disposition to issuer,
            // X exercise, C conversion, J other — none are a discretionary open-market market signal.
            "A" or "M" or "F" or "G" or "D" or "X" or "C" or "J" => InsiderTxnClassification.NeutralExcluded,
            // Any unknown/unmodelled code is Neutral by the conservative default.
            _ => InsiderTxnClassification.NeutralExcluded,
        };
    }
}
