using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Trademarks;

/// <summary>
/// The single per-company input a <c>trademarks</c> feed carries in its <c>Url</c> field, parsed from the
/// documented token <c>owner=&lt;owner organization name&gt;</c> (e.g. <c>owner=WD-40 Company</c>). This keeps
/// the shared <see cref="Radar.Domain.Companies.CompanySourceFeed"/> record unchanged. <see cref="OwnerName"/>
/// is the exact owner/applicant organization name sent to the USPTO trademark <c>owner</c> filter (the value
/// may contain spaces, hyphens, and commas; the token is NOT URL-decoded). An unparsable/blank token — or one
/// missing the <c>owner=</c> key — yields <see langword="null"/> so the collector can degrade it to a source
/// failure rather than throwing.
/// <para>
/// The single-key split is routed through the shared
/// <see cref="Radar.Infrastructure.Sources.SingleKeyFeedToken"/> (the same splitter the patents and FDA
/// parsers use); the trim + empty-name blank-null discipline stays this parser's own explicit per-caller hook.
/// </para>
/// </summary>
internal sealed record TrademarkFeedTarget(string OwnerName)
{
    private const string OwnerKey = "owner=";

    /// <summary>
    /// Parses an <c>owner=&lt;name&gt;</c> token. Robust to surrounding whitespace (the token is NOT
    /// URL-decoded, so the name's own spaces/hyphens/commas are preserved). Returns <see langword="null"/> when
    /// the token is blank, the <c>owner=</c> key is missing, or the name is empty after trimming.
    /// </summary>
    public static TrademarkFeedTarget? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();

        if (!SingleKeyFeedToken.TrySplit(trimmed, OwnerKey, out var name) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        return new TrademarkFeedTarget(name);
    }
}
