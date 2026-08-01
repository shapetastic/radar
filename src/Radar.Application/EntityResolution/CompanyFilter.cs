namespace Radar.Application.EntityResolution;

/// <summary>
/// The resolved <c>Radar:Companies</c> ticker filter (spec 161): the canonical, de-duped set of tickers a
/// <b>collection</b> pass is restricted to. Holds plain resolved values — no <c>IConfiguration</c> reaches
/// <c>Radar.Application</c> (CLAUDE.md layering) — and it is consumed at exactly one choke point, the
/// <see cref="ICompanySeedSource"/> decorator, because everything downstream (the universe seeder, the
/// collection pass's companies + source feeds, price acquisition, the AI read) flows from the seeded
/// repository.
/// <para>
/// <b>Filtering is collection-only, by design and by guard.</b> A filtered SCORING run would overwrite the
/// date-keyed weekly report with a one-company report and mint sparse as-of dates into the strategy-vs-price
/// efficacy join, so the composition root refuses a non-empty filter outside <c>Radar:RunMode=collect</c>.
/// Nothing here is a scoring or fingerprint input — the company universe is not hashed (AD-10); the filter is
/// recorded as run provenance only.
/// </para>
/// <para>
/// <b>Canonicalisation:</b> each token is trimmed and upper-cased (invariant), then de-duped Ordinal with the
/// configured order preserved (AD-3). Casing only affects the RECORDED/canonical form: matching against the
/// seed is case-INSENSITIVE regardless. Upper-casing is nevertheless the honest canonical form because every
/// ticker in the shipped <c>data/companies.json</c> seed is upper-case, so the recorded provenance reads
/// exactly like the seed it names.
/// </para>
/// <para>
/// <b>Fail fast, never fail open.</b> A null/empty/whitespace token throws (a silently-dropped entry is how a
/// typo becomes a run that "worked" and collected nothing), and so does an empty resulting set. Whether each
/// token actually NAMES a seed company is checked where the seed is known — see the decorator.
/// </para>
/// </summary>
public sealed class CompanyFilter
{
    /// <summary>The configuration key this filter is bound from, quoted verbatim by every failure message.</summary>
    public const string ConfigKey = "Radar:Companies";

    private CompanyFilter(string[] canonicalTickers) => Tickers = Array.AsReadOnly(canonicalTickers);

    /// <summary>
    /// The canonical (trimmed, upper-invariant, Ordinal-distinct) tickers in configured order. Handed out
    /// behind a genuinely read-only wrapper rather than the backing array: this is a process-lifetime
    /// singleton and a bare array can be cast back to <c>string[]</c> and mutated.
    /// </summary>
    public IReadOnlyList<string> Tickers { get; }

    /// <summary>
    /// Canonicalises and validates the configured tickers. Throws
    /// <see cref="InvalidOperationException"/> — naming <c>Radar:Companies</c> in the style of the existing
    /// <c>Radar:Collectors</c> fail-fasts — on a null/empty/whitespace entry, or when the resulting set is
    /// empty (which the caller's "only build a filter when the list is non-empty" rule already prevents; the
    /// guard is asserted anyway rather than assumed).
    /// </summary>
    public static CompanyFilter FromTickers(IEnumerable<string?> tickers)
    {
        ArgumentNullException.ThrowIfNull(tickers);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var canonical = new List<string>();
        foreach (var raw in tickers)
        {
            // Validate BEFORE normalising so a null/empty/whitespace entry fails with its own clear message
            // instead of falling through as an unmatched ticker '' further down the pipeline.
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException(
                    $"{ConfigKey} entries must not be null, empty, or whitespace; each entry is a ticker "
                        + "symbol from the company seed (e.g. \"CASS\"). Omit "
                        + $"{ConfigKey} entirely to collect for the whole watch universe.");
            }

            var ticker = raw.Trim().ToUpperInvariant();
            if (seen.Add(ticker))
            {
                canonical.Add(ticker);
            }
        }

        if (canonical.Count == 0)
        {
            throw new InvalidOperationException(
                $"{ConfigKey} resolved to no tickers; list at least one ticker from the company seed, or omit "
                    + $"{ConfigKey} entirely to collect for the whole watch universe.");
        }

        return new CompanyFilter([.. canonical]);
    }

    /// <summary>The canonical tickers as a comma-separated list — the form logs and the run record quote.</summary>
    public string Describe() => string.Join(",", Tickers);
}
