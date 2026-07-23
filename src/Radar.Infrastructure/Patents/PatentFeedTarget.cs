namespace Radar.Infrastructure.Patents;

/// <summary>
/// The single per-company input a <c>patents</c> feed carries in its <c>Url</c> field, parsed from the
/// documented token <c>assignee=&lt;assignee organization name&gt;</c>
/// (e.g. <c>assignee=Mercury Systems, Inc.</c>). This keeps the shared
/// <see cref="Radar.Domain.Companies.CompanySourceFeed"/> record unchanged. <see cref="AssigneeName"/> is
/// the exact legal-entity name sent to the PatentsView assignee-organization filter (the value may contain
/// spaces and commas; the token is NOT URL-decoded). An unparsable/blank token — or one missing the
/// <c>assignee=</c> key — yields <see langword="null"/> so the collector can degrade it to a source failure
/// rather than throwing.
/// <para>
/// This is a SINGLE-key token, so it does not use the shared two-key splitter
/// (<see cref="Radar.Infrastructure.Sources.TwoKeyFeedToken"/>, which handles two keys). It mirrors the
/// established single-key trimming/blank-null discipline of
/// <see cref="Radar.Infrastructure.Sources.QueryFeedTarget"/>. FOLLOW-UP: extract a shared single-key token
/// splitter once a second single-key parser exists (there is none yet).
/// </para>
/// </summary>
internal sealed record PatentFeedTarget(string AssigneeName)
{
    private const string AssigneeKey = "assignee=";

    /// <summary>
    /// Parses an <c>assignee=&lt;name&gt;</c> token. Robust to surrounding whitespace (the token is NOT
    /// URL-decoded, so the name's own spaces/commas are preserved). Returns <see langword="null"/> when the
    /// token is blank, the <c>assignee=</c> key is missing, or the name is empty after trimming.
    /// </summary>
    public static PatentFeedTarget? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();

        var keyIndex = trimmed.IndexOf(AssigneeKey, StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return null;
        }

        var name = trimmed[(keyIndex + AssigneeKey.Length)..].Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return new PatentFeedTarget(name);
    }
}
