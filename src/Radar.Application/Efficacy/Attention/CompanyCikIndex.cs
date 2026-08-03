using System.Text.RegularExpressions;

using Radar.Domain.Companies;

namespace Radar.Application.Efficacy.Attention;

/// <summary>
/// Recovers each watched company's SEC CIK from its configured <c>sec</c> source feed, so a cohort
/// declaration's <c>cik</c> can be checked against the company its <c>ticker</c> actually resolves to
/// (spec 169).
/// <para>
/// <b>Why a derivation rather than a field.</b> <see cref="Company"/> carries no CIK — the identifier lives in
/// the seeded EDGAR submissions URL (<c>https://data.sec.gov/submissions/CIK##########.json</c>), which is
/// where every SEC collector reads it from. Deriving it here reuses that one recorded fact instead of adding a
/// second, separately-maintainable copy that could disagree with the URL the collectors actually fetch.
/// </para>
/// <para>
/// The check it feeds is deliberately ASYMMETRIC: a company with no derivable CIK is "cannot verify", never a
/// contradiction, because failing an evaluation over a feed-shape Radar merely does not recognise would be a
/// false alarm. A CIK that IS derivable and DISAGREES is a real contradiction and suppresses the primary
/// status.
/// </para>
/// </summary>
internal static partial class CompanyCikIndex
{
    private const string SecFeedType = "sec";

    /// <summary>Builds companyId → 10-digit zero-padded CIK for every company whose <c>sec</c> feed carries one.</summary>
    public static IReadOnlyDictionary<Guid, string> Build(IReadOnlyList<CompanySourceFeed> feeds)
    {
        ArgumentNullException.ThrowIfNull(feeds);

        var byCompany = new Dictionary<Guid, string>();
        foreach (var feed in feeds.OrderBy(f => f.Id))
        {
            if (!string.Equals(feed.FeedType, SecFeedType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = SubmissionsCikRegex().Match(feed.Url ?? string.Empty);
            if (!match.Success)
            {
                continue;
            }

            // Ordered by feed Id and first-wins, so a company with two sec feeds resolves the same way on
            // every run (AD-3).
            byCompany.TryAdd(feed.CompanyId, Normalize(match.Groups[1].Value)!);
        }

        return byCompany;
    }

    /// <summary>
    /// Normalizes a declared CIK to the 10-digit zero-padded form EDGAR uses, so <c>1759774</c>,
    /// <c>0001759774</c> and <c>CIK0001759774</c> all compare equal. Returns null when no digits are present.
    /// </summary>
    public static string? Normalize(string? cik)
    {
        if (string.IsNullOrWhiteSpace(cik))
        {
            return null;
        }

        var digits = new string([.. cik.Where(char.IsAsciiDigit)]);
        return digits.Length == 0 ? null : digits.TrimStart('0').PadLeft(10, '0');
    }

    [GeneratedRegex(@"CIK(\d{10})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubmissionsCikRegex();
}
