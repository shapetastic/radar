using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Fcc;

/// <summary>
/// The single per-company input an <c>fccauth</c> feed carries in its <c>Url</c> field, parsed from the
/// documented token <c>grantee=&lt;grantee organization name&gt;</c>
/// (e.g. <c>grantee=Mercury Systems, Inc.</c>). This keeps the shared
/// <see cref="Radar.Domain.Companies.CompanySourceFeed"/> record unchanged. <see cref="GranteeName"/> is the
/// exact grantee/applicant organization name sent to the FCC OET EAS GenericSearch applicant-name filter (the
/// value may contain spaces and commas; the token is NOT URL-decoded). An unparsable/blank token — or one
/// missing the <c>grantee=</c> key — yields <see langword="null"/> so the collector can degrade it to a source
/// failure rather than throwing.
/// <para>
/// A SINGLE-key token, so it routes through the shared single-key splitter
/// (<see cref="Radar.Infrastructure.Sources.SingleKeyFeedToken"/>) exactly like the sibling
/// <c>PatentFeedTarget</c> (reuse-over-copy). The reject-blank-value policy is an explicit per-caller hook.
/// </para>
/// </summary>
internal sealed record FccFeedTarget(string GranteeName)
{
    private const string GranteeKey = "grantee=";

    /// <summary>
    /// Parses a <c>grantee=&lt;name&gt;</c> token. Robust to surrounding whitespace (the token is NOT
    /// URL-decoded, so the name's own spaces/commas are preserved). Returns <see langword="null"/> when the
    /// token is blank, the <c>grantee=</c> key is missing, or the name is empty after trimming.
    /// </summary>
    public static FccFeedTarget? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (!SingleKeyFeedToken.TrySplit(token.Trim(), GranteeKey, out var name)
            || string.IsNullOrEmpty(name))
        {
            return null;
        }

        return new FccFeedTarget(name);
    }
}
