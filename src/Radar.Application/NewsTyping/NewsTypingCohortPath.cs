using System.Text;

namespace Radar.Application.NewsTyping;

/// <summary>
/// The ONE filesystem-safe encoding of a cohort's (provider, model) pair, used as the on-disk layout segment
/// under <c>{root}/typings/…</c> and <c>{root}/families/…</c>. LAYOUT ONLY: the <c>CohortKey</c> FIELD on
/// each record stays the authoritative identity — two cohorts that happened to collapse onto one segment
/// would still be distinguishable (and cached separately) by their keys.
/// </summary>
public static class NewsTypingCohortPath
{
    /// <summary>Lowercases <c>{provider}-{modelId}</c> and replaces every character outside <c>[a-z0-9.-]</c> with <c>-</c>.</summary>
    public static string PolicySegment(string provider, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var raw = (provider + "-" + modelId).ToLowerInvariant();
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            sb.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' ? c : '-');
        }

        return sb.ToString();
    }
}
