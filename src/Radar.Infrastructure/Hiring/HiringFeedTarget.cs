using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Hiring;

/// <summary>
/// The two per-company inputs a <c>hiringats</c> feed carries in its single <c>Url</c> field, parsed from
/// the documented token <c>platform=&lt;greenhouse|lever&gt;&amp;board=&lt;board token&gt;</c>
/// (e.g. <c>platform=greenhouse&amp;board=mercury</c>). This keeps the shared
/// <see cref="Radar.Domain.Companies.CompanySourceFeed"/> record unchanged. <see cref="Platform"/> selects
/// the <see cref="IJobBoardReader"/> in the collector's platform→reader map; <see cref="BoardToken"/> is
/// the hand-verified ATS board slug the reader fetches. An unparsable token yields <see langword="null"/>
/// so the collector can degrade it to a source failure rather than throwing.
/// </summary>
internal sealed record HiringFeedTarget(string Platform, string BoardToken)
{
    private const string PlatformKey = "platform=";
    private const string BoardKey = "board=";

    /// <summary>
    /// Parses a <c>platform=...&amp;board=...</c> token. Robust to key ordering and surrounding whitespace
    /// (the token is NOT URL-decoded). Returns <see langword="null"/> when either key is missing/empty or
    /// the token is blank/malformed. The two-key split routes through the shared
    /// <see cref="TwoKeyFeedToken"/>; the both-keys-required and no-blank-value semantics stay this
    /// parser's own hooks.
    /// </summary>
    public static HiringFeedTarget? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();

        // The shared order-robust split (first '&' between the two keys).
        if (!TwoKeyFeedToken.TrySplit(trimmed, PlatformKey, BoardKey, out var platform, out var board))
        {
            return null;
        }

        if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(board))
        {
            return null;
        }

        return new HiringFeedTarget(platform, board);
    }
}
