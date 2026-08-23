namespace Radar.Application.Lifecycle;

/// <summary>
/// The parsed content of <c>data/strategy-operating-calls.json</c> (spec 184 §2) — the ONLY runtime input
/// to call resolution (the Markdown journal is audit-only and never parsed). Structural/token validity is
/// the reader's job (unknown tokens fail loudly naming the file); CROSS-strategy validity (unknown
/// strategy, comparator call, exactly-one-Lead-or-StopAll, …) is <see cref="OperatingCallReducer"/>'s.
/// </summary>
/// <param name="Source">
/// Where the calls came from (the file path), carried so every validation failure can name the file.
/// </param>
/// <param name="SchemaVersion">The declared schema version (the reader accepts exactly one).</param>
/// <param name="StopAll">
/// True when <c>globalCall: "StopAll"</c> is present: no Lead exists and the leaders section renders the
/// diagnostic view under an explicit "no lead — StopAll" banner.
/// </param>
/// <param name="Calls">The declared per-strategy calls.</param>
public sealed record StrategyOperatingCallsFile(
    string Source,
    string SchemaVersion,
    bool StopAll,
    IReadOnlyList<StrategyOperatingCall> Calls);
