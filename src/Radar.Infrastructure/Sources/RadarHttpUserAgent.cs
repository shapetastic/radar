namespace Radar.Infrastructure.Sources;

/// <summary>
/// The ONE definition of Radar's generic, polite outbound <c>User-Agent</c>, shared by every typed
/// <c>HttpClient</c> whose upstream needs no bespoke identification (RSS press releases, USPTO patents,
/// openFDA, USPTO trademarks). It lives in the shared <c>Radar.Infrastructure.Sources</c> home rather than
/// being pasted at each registration site: a duplicated literal silently drifts (only one copy gets the next
/// edit), which is exactly the reuse-over-copy rule the architecture reviewer keeps flagging.
/// <para>
/// This is deliberately NOT the SEC User-Agent. SEC fair-access requires a caller-declared contact string
/// (name + email) supplied per deployment via <c>Radar:Sec:UserAgent</c>, so those clients keep their own
/// configured value and must not be routed through this constant.
/// </para>
/// </summary>
internal static class RadarHttpUserAgent
{
    /// <summary>
    /// The polite identifier Radar sends: a product token plus a comment pointing at the project, so an
    /// operator seeing it in their logs can find out who is calling. Some IR hosts (e.g. Energy Recovery's
    /// press-release feed) return HTTP 403 to any request with NO <c>User-Agent</c> at all, so sending one is
    /// a correctness requirement and not merely good manners.
    /// </summary>
    public const string Default = "Radar/1.0 (+https://github.com/shapetastic/radar)";
}
