using System.Text;
using Radar.Application.Abstractions.Persistence;

namespace Radar.Application.EntityResolution;

/// <summary>
/// Deterministic, conservative company resolver. Loads the seed universe (companies and
/// aliases) through <see cref="ICompanyRepository"/> and maps a mention string to a single
/// company via exact, normalized name/alias matching, or an exact case-insensitive ticker
/// match. Anything ambiguous, empty, or unmatched resolves to an unresolved result rather
/// than guessing. No fuzzy, substring, or AI matching is performed.
/// </summary>
public sealed class CompanyResolver : ICompanyResolver
{
    // Small, documented set of trailing company suffixes stripped during normalization.
    // Stripped only as whole trailing tokens, never as substrings inside a word.
    private static readonly string[] TrailingSuffixes =
    {
        "incorporated",
        "corporation",
        "inc",
        "corp",
        "ltd",
        "plc",
        "llc",
        "co",
    };

    private readonly ICompanyRepository _companyRepository;

    public CompanyResolver(ICompanyRepository companyRepository)
    {
        ArgumentNullException.ThrowIfNull(companyRepository);
        _companyRepository = companyRepository;
    }

    public async Task<CompanyResolutionResult> ResolveAsync(string mentionText, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mentionText);

        if (string.IsNullOrWhiteSpace(mentionText))
        {
            return new CompanyResolutionResult(null, 0m, "Empty mention", null);
        }

        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);
        var aliases = await _companyRepository.GetAliasesAsync(ct).ConfigureAwait(false);

        var normalizedMention = Normalize(mentionText);

        // Collect every company reachable from the normalized name/alias lookup. Using a
        // set of distinct company ids means a key shared by two companies is ambiguous.
        var matchedCompanyIds = new HashSet<Guid>();
        string? matchedKey = null;

        foreach (var company in companies)
        {
            if (Normalize(company.Name) == normalizedMention)
            {
                matchedCompanyIds.Add(company.Id);
                matchedKey ??= normalizedMention;
            }
        }

        foreach (var alias in aliases)
        {
            if (Normalize(alias.Alias) == normalizedMention)
            {
                matchedCompanyIds.Add(alias.CompanyId);
                matchedKey ??= normalizedMention;
            }
        }

        if (matchedCompanyIds.Count > 1)
        {
            return new CompanyResolutionResult(null, 0m, "Ambiguous mention", null);
        }

        if (matchedCompanyIds.Count == 1)
        {
            var companyId = matchedCompanyIds.Single();

            // Prefer a name-match reason when the mention normalizes to a company name;
            // otherwise it resolved through an alias.
            var matchedByName = companies.Any(c =>
                c.Id == companyId && Normalize(c.Name) == normalizedMention);

            var reason = matchedByName ? "Exact name match" : "Exact alias match";
            return new CompanyResolutionResult(companyId, 1.0m, reason, matchedKey);
        }

        // Ticker is matched only as an exact, case-insensitive whole-string match of the
        // raw mention (no suffix stripping or normalization): tickers are short and
        // ambiguous, so we never infer one. Ambiguous tickers across companies are also
        // treated as unresolved.
        var trimmedRaw = mentionText.Trim();
        var tickerCompanyIds = new HashSet<Guid>();
        foreach (var company in companies)
        {
            if (!string.IsNullOrWhiteSpace(company.Ticker) &&
                string.Equals(company.Ticker.Trim(), trimmedRaw, StringComparison.OrdinalIgnoreCase))
            {
                tickerCompanyIds.Add(company.Id);
            }
        }

        if (tickerCompanyIds.Count > 1)
        {
            return new CompanyResolutionResult(null, 0m, "Ambiguous mention", null);
        }

        if (tickerCompanyIds.Count == 1)
        {
            return new CompanyResolutionResult(tickerCompanyIds.Single(), 0.9m, "Exact ticker match", null);
        }

        return new CompanyResolutionResult(null, 0m, "No match", null);
    }

    /// <summary>
    /// Normalizes a candidate string: trim, lower-case (invariant), collapse internal
    /// whitespace to single spaces, strip surrounding/embedded <c>,</c> and <c>.</c>, and
    /// remove a small set of trailing company suffixes (see <see cref="TrailingSuffixes"/>).
    /// Suffixes are removed only as whole trailing tokens.
    /// </summary>
    private static string Normalize(string value)
    {
        // Lower-case and remove the punctuation we treat as insignificant ('.' and ',').
        var lowered = value.ToLowerInvariant();
        var withoutPunctuation = new StringBuilder(lowered.Length);
        foreach (var c in lowered)
        {
            if (c is '.' or ',')
            {
                continue;
            }

            withoutPunctuation.Append(c);
        }

        // Split on whitespace, dropping empties, which both trims and collapses runs.
        var tokens = withoutPunctuation
            .ToString()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // Strip a single trailing company-suffix token if present (whole token only).
        if (tokens.Length > 1 && Array.IndexOf(TrailingSuffixes, tokens[^1]) >= 0)
        {
            tokens = tokens[..^1];
        }

        return string.Join(' ', tokens);
    }
}
