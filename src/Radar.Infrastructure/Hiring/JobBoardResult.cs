namespace Radar.Infrastructure.Hiring;

/// <summary>
/// The normalized shape both ATS platforms (Greenhouse's <c>{"jobs":[…]}</c> object and Lever's top-level
/// posting array) map to. <see cref="TotalRoles"/> is the count of parsed job entries — authoritative and
/// deterministic from the returned payload itself; Greenhouse's <c>meta.total</c> is deliberately NOT
/// trusted (it may be a server-side/paginated figure). <see cref="Titles"/> carries the non-blank job
/// titles (an entry with a blank/absent title still counts as a role but contributes no title), used only
/// for the deterministic senior/engineering keyword tallies and the bounded provenance sample — raw titles
/// NEVER enter evidence Title/RawText (keyword contamination; see <see cref="HiringBoardCollector"/>).
/// </summary>
internal sealed record JobBoardResult(int TotalRoles, IReadOnlyList<string> Titles);
