namespace Radar.Application.Lifecycle;

/// <summary>
/// Reads the committed operating-calls file (spec 184 §2). Returns <c>null</c> when NO file exists — with
/// multiple strategies the report then states plainly that no call is declared (and prominence stays with
/// the storage primary by default); with a single strategy the source is never consulted at all. A file
/// that EXISTS but is malformed (unparseable JSON, unknown token, unknown property, wrong schema version)
/// must throw an <see cref="InvalidOperationException"/> naming the file and the violated rule — a typo'd
/// call silently read as "no calls" would hand prominence out by accident.
/// </summary>
public interface IOperatingCallSource
{
    Task<StrategyOperatingCallsFile?> ReadAsync(CancellationToken ct);
}

/// <summary>
/// The inert default: no calls file. Library compositions that never wire the file-backed source keep
/// today's behaviour (storage-primary prominence, an explicit "no operating call is declared" line in a
/// multi-strategy report, nothing at all in a single-strategy one).
/// </summary>
public sealed class NullOperatingCallSource : IOperatingCallSource
{
    public static NullOperatingCallSource Instance { get; } = new();

    private NullOperatingCallSource()
    {
    }

    public Task<StrategyOperatingCallsFile?> ReadAsync(CancellationToken ct) =>
        Task.FromResult<StrategyOperatingCallsFile?>(null);
}
