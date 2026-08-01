using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.EntityResolution;

namespace Radar.Infrastructure.Sources;

/// <summary>
/// The <c>Radar:Companies</c> filter (spec 161), applied as a decorator over the real
/// <see cref="ICompanySeedSource"/>. This is the ONE choke point: <c>CompanyUniverseSeeder</c>, the
/// collection pass (companies + source feeds via the company repository), price acquisition and the AI read
/// all flow from the seeded repository, so filtering the seed filters the whole pass — collectors never see an
/// excluded company's feeds and no per-collector code changes.
/// <para>
/// <b>The seed document is filtered CONSISTENTLY:</b> the named companies, and only the aliases and source
/// feeds whose <c>CompanyId</c> is in the retained set. A feed surviving its excluded company would collect
/// evidence that resolves to a company the repository does not hold.
/// </para>
/// <para>
/// <b>Fail fast, never fail open.</b> A configured ticker that matches NO seed company throws, naming the
/// token and stating how many tickers the seed actually holds (plus any near-misses). A typo that silently
/// filtered to nothing would be the fail-open shape: a run that "worked" and collected nothing. Matching is
/// case-insensitive and whitespace-trimmed on both sides. The inner seed is never mutated and inner order is
/// preserved throughout (AD-3).
/// </para>
/// <para>
/// This runs at seeding time — the first thing the worker does — so a bad ticker fails the run before any
/// collector issues a request.
/// </para>
/// <para>
/// <b>Deliberate deviation from the <see cref="ICompanySeedSource"/> contract</b> ("returns an empty payload
/// rather than throwing"), which exists so a missing/unreadable seed FILE degrades gracefully. That is a DATA
/// condition; an unmatched ticker is a MISCONFIGURATION, and degrading it to an empty seed is precisely the
/// fail-open shape this decorator exists to prevent. The inner source keeps its graceful degradation — this
/// only refuses to filter an inventory it was told to filter and could not.
/// </para>
/// </summary>
public sealed class FilteredCompanySeedSource : ICompanySeedSource
{
    /// <summary>How many near-miss seed tickers a failure message names before it stops listing them.</summary>
    private const int MaxNearMisses = 5;

    private readonly ICompanySeedSource _inner;
    private readonly CompanyFilter _filter;
    private readonly ILogger<FilteredCompanySeedSource> _logger;

    public FilteredCompanySeedSource(
        ICompanySeedSource inner,
        CompanyFilter filter,
        ILogger<FilteredCompanySeedSource> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _filter = filter;
        _logger = logger;
    }

    public async Task<CompanySeedData> GetSeedAsync(CancellationToken ct)
    {
        var seed = await _inner.GetSeedAsync(ct).ConfigureAwait(false);

        // Company ids of every seed entry whose ticker one of the configured tokens names. A HashSet (not a
        // per-company scan) so the alias/feed retention below is a membership test on the SAME set — a feed
        // can never survive a company that did not.
        var retainedIds = new HashSet<Guid>();
        List<string>? unmatched = null;
        foreach (var ticker in _filter.Tickers)
        {
            var matched = false;
            foreach (var company in seed.Companies)
            {
                if (!Matches(company.Ticker, ticker))
                {
                    continue;
                }

                retainedIds.Add(company.Id);
                matched = true;
            }

            if (!matched)
            {
                (unmatched ??= []).Add(ticker);
            }
        }

        if (unmatched is not null)
        {
            throw new InvalidOperationException(BuildUnmatchedMessage(unmatched, seed));
        }

        // Inner order preserved (AD-3); the inner lists are never mutated.
        var companies = seed.Companies.Where(c => retainedIds.Contains(c.Id)).ToList();

        // Unreachable given every token matched at least one company — asserted rather than assumed, because
        // an empty universe would silently turn a filtered pass into a run that collects nothing.
        if (companies.Count == 0)
        {
            throw new InvalidOperationException(
                $"{CompanyFilter.ConfigKey} ({_filter.Describe()}) retained no companies from the "
                    + $"{seed.Companies.Count}-company seed, so this run would collect nothing. Omit "
                    + $"{CompanyFilter.ConfigKey} to collect for the whole watch universe.");
        }

        var aliases = seed.Aliases.Where(a => retainedIds.Contains(a.CompanyId)).ToList();
        var feeds = seed.SourceFeeds.Where(f => retainedIds.Contains(f.CompanyId)).ToList();

        _logger.LogInformation(
            "Company filter {ConfigKey} is active: {Tickers} — {RetainedCompanies} of {SeedCompanies} seed "
                + "companies retained ({Aliases} alias(es), {Feeds} source feed(s)). This is a PARTIAL "
                + "collection pass: scoring stays whole-universe on the next full/score run.",
            CompanyFilter.ConfigKey,
            _filter.Describe(),
            companies.Count,
            seed.Companies.Count,
            aliases.Count,
            feeds.Count);

        return new CompanySeedData(companies, aliases, feeds);
    }

    /// <summary>
    /// Case-insensitive, whitespace-trimmed ticker match. The configured token is already canonical
    /// (<see cref="CompanyFilter"/>); the seed side is trimmed here because seed data is curated by hand.
    /// </summary>
    private static bool Matches(string? seedTicker, string canonicalToken) =>
        !string.IsNullOrWhiteSpace(seedTicker)
            && seedTicker.Trim().Equals(canonicalToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The fail-fast message for tokens that name no seed company: the token(s), the seed's ticker COUNT (not
    /// the whole 70+ list), and any cheap, deterministic near-misses. Mirrors the existing
    /// <c>Radar:Collectors</c> fail-fast style — name the offending value, then how to fix it.
    /// </summary>
    private static string BuildUnmatchedMessage(IReadOnlyList<string> unmatched, CompanySeedData seed)
    {
        var seedTickers = seed.Companies
            .Select(c => c.Ticker?.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nearMisses = unmatched
            .SelectMany(token => seedTickers.Where(seedTicker => IsNearMiss(seedTicker, token)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.Ordinal)
            .Take(MaxNearMisses)
            .ToList();

        var suggestion = nearMisses.Count == 0
            ? string.Empty
            : $" Did you mean: {string.Join(", ", nearMisses)}?";

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} ticker(s) {1} match no company in the seed, which holds {2} ticker(s).{3} A filter that "
                + "matched nothing would silently collect nothing, so this fails rather than running. Check "
                + "the ticker against the company seed, or omit {0} to collect for the whole watch universe.",
            CompanyFilter.ConfigKey,
            string.Join(", ", unmatched.Select(t => $"'{t}'")),
            seedTickers.Count,
            suggestion);
    }

    /// <summary>
    /// A cheap, deterministic near-miss rule: either string is a prefix of the other (case-insensitively), so
    /// "CAS"/"CASSS" both suggest "CASS". Deliberately narrow — a fuzzy rule would list half the universe and
    /// the COUNT, not the suggestion, is the part of the message that always holds.
    /// </summary>
    private static bool IsNearMiss(string seedTicker, string token) =>
        seedTicker.StartsWith(token, StringComparison.OrdinalIgnoreCase)
            || token.StartsWith(seedTicker, StringComparison.OrdinalIgnoreCase);
}
