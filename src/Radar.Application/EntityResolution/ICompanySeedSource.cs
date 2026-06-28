namespace Radar.Application.EntityResolution;

/// <summary>
/// Provider-independent source of the seed company watch-universe. Implementations read from a file,
/// embedded resource, or (later) a database. Returns an empty payload rather than throwing when the
/// source is missing or unreadable.
/// </summary>
public interface ICompanySeedSource
{
    Task<CompanySeedData> GetSeedAsync(CancellationToken ct);
}
