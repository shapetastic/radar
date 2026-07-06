namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Shared on-disk ticker-key sanitizer for the file stores keyed by ticker (the price reference store and the
/// efficacy artifact store). Lowercases the ticker (invariant) and validates it carries no
/// <see cref="Path.GetInvalidFileNameChars"/>; a blank or invalid ticker returns <c>null</c> so a caller never
/// writes outside its root. Extracted so the price file (<c>{ticker}.json</c>) and the efficacy artifacts
/// (<c>{ticker}.svg</c>/<c>.csv</c>) share ONE ticker key and cannot drift (reuse over copy).
/// </summary>
internal static class FileTickerKey
{
    public static string? Sanitize(string? ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return null;
        }

        var trimmed = ticker.Trim().ToLowerInvariant();
        if (trimmed.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return trimmed;
    }
}
