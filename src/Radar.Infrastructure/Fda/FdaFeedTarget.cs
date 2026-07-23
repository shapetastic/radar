using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Fda;

/// <summary>
/// The single per-company input an <c>fda</c> feed carries in its <c>Url</c> field, parsed from the
/// documented token <c>applicant=&lt;applicant organization name&gt;</c> (e.g. <c>applicant=TransMedics</c>).
/// This keeps the shared <see cref="Radar.Domain.Companies.CompanySourceFeed"/> record unchanged.
/// <see cref="ApplicantName"/> is the exact applicant/sponsor name sent to the openFDA <c>applicant</c>
/// filter (the value may contain spaces and commas; the token is NOT URL-decoded). An unparsable/blank token
/// — or one missing the <c>applicant=</c> key — yields <see langword="null"/> so the collector can degrade it
/// to a source failure rather than throwing.
/// <para>
/// The single-key split is routed through the shared
/// <see cref="Radar.Infrastructure.Sources.SingleKeyFeedToken"/> (the same splitter the patents parser uses);
/// the trim + empty-name blank-null discipline stays this parser's own explicit per-caller hook.
/// </para>
/// </summary>
internal sealed record FdaFeedTarget(string ApplicantName)
{
    private const string ApplicantKey = "applicant=";

    /// <summary>
    /// Parses an <c>applicant=&lt;name&gt;</c> token. Robust to surrounding whitespace (the token is NOT
    /// URL-decoded, so the name's own spaces/commas are preserved). Returns <see langword="null"/> when the
    /// token is blank, the <c>applicant=</c> key is missing, or the name is empty after trimming.
    /// </summary>
    public static FdaFeedTarget? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();

        if (!SingleKeyFeedToken.TrySplit(trimmed, ApplicantKey, out var name) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        return new FdaFeedTarget(name);
    }
}
