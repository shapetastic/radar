namespace Radar.Application.News;

/// <summary>
/// The ONE definition of the Google News <c>" - Publisher"</c> headline suffix rule (reuse over copy —
/// extracted by spec 179 from <c>NewsAttentionCollector</c>, which now routes through it). Google News
/// appends the outlet name to every RSS item title; comparisons that should run against the REAL headline
/// (the collector's relevance check, and the spec-179 duplicate-headline collapse) strip it first, while the
/// stored headline always keeps it for provenance.
/// </summary>
public static class GoogleNewsHeadline
{
    /// <summary>The exact separator Google News uses between the headline and the outlet name.</summary>
    public const string PublisherSuffixSeparator = " - ";

    /// <summary>
    /// Removes a trailing <c>" - Publisher"</c> suffix (splitting on the LAST separator occurrence, so a
    /// headline that itself contains <c>" - "</c> loses only the outlet). Returns the input unchanged when
    /// no suffix is present.
    /// </summary>
    public static string? StripPublisherSuffix(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return title;
        }

        var separatorIndex = title.LastIndexOf(PublisherSuffixSeparator, StringComparison.Ordinal);
        return separatorIndex >= 0 ? title[..separatorIndex] : title;
    }
}
