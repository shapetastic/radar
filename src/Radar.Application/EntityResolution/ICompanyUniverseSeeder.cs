namespace Radar.Application.EntityResolution;

/// <summary>Loads the seed universe into the company repository. Safe to run on every startup.</summary>
public interface ICompanyUniverseSeeder
{
    /// <returns>The number of companies seeded.</returns>
    Task<int> SeedAsync(CancellationToken ct);
}
