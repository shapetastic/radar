using System.Text;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<CompanyResolver> _logger;

    public CompanyResolver(ICompanyRepository companyRepository, ILogger<CompanyResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(logger);
        _companyRepository = companyRepository;
        _logger = logger;
    }

    public Task<CompanyResolutionResult> ResolveAsync(string mentionText, CancellationToken ct) =>
        ResolveAsync(mentionText, Array.Empty<string>(), ct);

    public async Task<CompanyResolutionResult> ResolveAsync(
        string mentionText, IReadOnlyList<string> companyHints, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mentionText);
        ArgumentNullException.ThrowIfNull(companyHints);

        // Fast-path the common invalid-input case: a blank mention with no usable hints can
        // never resolve, so return before touching the repository to avoid a needless read
        // (and potential DB roundtrip). A blank mention WITH a usable hint must still fall
        // through, since the hint path below can resolve it.
        if (string.IsNullOrWhiteSpace(mentionText) && !companyHints.Any(h => !string.IsNullOrWhiteSpace(h)))
        {
            return LogAndReturn(mentionText, new CompanyResolutionResult(null, 0m, "Empty mention", null));
        }

        // GetAllAsync and GetAliasesAsync are independent; start both before awaiting so
        // they run concurrently (matters more once the repository is not in-memory). Loaded once
        // here and shared by both the hint path and the mention-based logic below.
        var companiesTask = _companyRepository.GetAllAsync(ct);
        var aliasesTask = _companyRepository.GetAliasesAsync(ct);
        var companies = (await companiesTask.ConfigureAwait(false)).ToList();
        var aliases = await aliasesTask.ConfigureAwait(false);

        // Highest-precedence path: a collector hint (e.g. the ticker of a company-specific feed).
        // Evaluated BEFORE the empty-mention early return so a hint can resolve even when the
        // mention text is blank. Conservative: exact ticker / normalized name+alias only, and a
        // hint naming an unknown company is ignored — never fabricated into a company.
        var hintMatchedCompanyIds = new HashSet<Guid>();
        string? matchedHint = null;
        foreach (var hint in companyHints)
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                continue;
            }

            var trimmedHint = hint.Trim();
            var normalizedHint = Normalize(hint);

            foreach (var company in companies)
            {
                var tickerMatch = !string.IsNullOrWhiteSpace(company.Ticker) &&
                    string.Equals(company.Ticker.Trim(), trimmedHint, StringComparison.OrdinalIgnoreCase);
                var nameMatch = Normalize(company.Name) == normalizedHint;
                if (tickerMatch || nameMatch)
                {
                    if (hintMatchedCompanyIds.Add(company.Id))
                    {
                        matchedHint ??= hint;
                    }
                }
            }

            foreach (var alias in aliases)
            {
                if (Normalize(alias.Alias) == normalizedHint)
                {
                    if (hintMatchedCompanyIds.Add(alias.CompanyId))
                    {
                        matchedHint ??= hint;
                    }
                }
            }
        }

        if (hintMatchedCompanyIds.Count == 1)
        {
            return LogAndReturn(
                mentionText,
                new CompanyResolutionResult(hintMatchedCompanyIds.Single(), 0.95m, "Company hint match", matchedHint));
        }

        if (hintMatchedCompanyIds.Count > 1)
        {
            _logger.LogDebug(
                "Ignoring ambiguous company hints for mention {Mention}: matched {Count} companies.",
                mentionText,
                hintMatchedCompanyIds.Count);
        }

        if (string.IsNullOrWhiteSpace(mentionText))
        {
            return LogAndReturn(mentionText, new CompanyResolutionResult(null, 0m, "Empty mention", null));
        }

        if (companies.Count == 0)
        {
            _logger.LogDebug("Company seed universe is empty while resolving mention {Mention}.", mentionText);
        }

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
            return LogAndReturn(mentionText, new CompanyResolutionResult(null, 0m, "Ambiguous mention", null));
        }

        if (matchedCompanyIds.Count == 1)
        {
            var companyId = matchedCompanyIds.Single();

            // Prefer a name-match reason when the mention normalizes to a company name;
            // otherwise it resolved through an alias.
            var matchedByName = companies.Any(c =>
                c.Id == companyId && Normalize(c.Name) == normalizedMention);

            var reason = matchedByName ? "Exact name match" : "Exact alias match";
            return LogAndReturn(mentionText, new CompanyResolutionResult(companyId, 1.0m, reason, matchedKey));
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
            return LogAndReturn(mentionText, new CompanyResolutionResult(null, 0m, "Ambiguous mention", null));
        }

        if (tickerCompanyIds.Count == 1)
        {
            return LogAndReturn(mentionText, new CompanyResolutionResult(tickerCompanyIds.Single(), 0.9m, "Exact ticker match", null));
        }

        return LogAndReturn(mentionText, new CompanyResolutionResult(null, 0m, "No match", null));
    }

    /// <summary>
    /// Emits a structured <c>Debug</c> log describing the resolution outcome and returns the result
    /// unchanged. Logging is side-effect only and never alters the returned value.
    /// </summary>
    private CompanyResolutionResult LogAndReturn(string mentionText, CompanyResolutionResult result)
    {
        if (result.CompanyId is { } companyId)
        {
            _logger.LogDebug(
                "Resolved mention {Mention} to company {CompanyId} at confidence {Confidence}.",
                mentionText,
                companyId,
                result.Confidence);
        }
        else
        {
            _logger.LogDebug(
                "Unresolved mention {Mention}: {Reason}.",
                mentionText,
                result.Reason);
        }

        return result;
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
