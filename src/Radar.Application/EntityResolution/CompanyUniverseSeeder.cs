using Microsoft.Extensions.Logging;
using Radar.Application.Abstractions.Persistence;

namespace Radar.Application.EntityResolution;

/// <summary>
/// Idempotent seeder that loads the watch-universe from an <see cref="ICompanySeedSource"/> into the
/// <see cref="ICompanyRepository"/>. Relies on the repository's documented upsert-by-Id semantics for
/// idempotency: re-running with the same stable Ids overwrites the same rows rather than duplicating
/// them, so the seeder adds no dedupe logic of its own.
/// </summary>
public sealed class CompanyUniverseSeeder : ICompanyUniverseSeeder
{
    private readonly ICompanySeedSource _source;
    private readonly ICompanyRepository _companyRepository;
    private readonly ILogger<CompanyUniverseSeeder> _logger;

    public CompanyUniverseSeeder(
        ICompanySeedSource source,
        ICompanyRepository companyRepository,
        ILogger<CompanyUniverseSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _companyRepository = companyRepository;
        _logger = logger;
    }

    public async Task<int> SeedAsync(CancellationToken ct)
    {
        var seed = await _source.GetSeedAsync(ct).ConfigureAwait(false);

        // Source order is preserved (deterministic). Upsert-by-Id makes repeated runs idempotent.
        foreach (var company in seed.Companies)
        {
            await _companyRepository.AddAsync(company, ct).ConfigureAwait(false);
        }

        foreach (var alias in seed.Aliases)
        {
            await _companyRepository.AddAliasAsync(alias, ct).ConfigureAwait(false);
        }

        foreach (var feed in seed.SourceFeeds)
        {
            await _companyRepository.AddSourceFeedAsync(feed, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Seeded company watch-universe: {CompanyCount} companies, {AliasCount} aliases, {FeedCount} source feeds.",
            seed.Companies.Count,
            seed.Aliases.Count,
            seed.SourceFeeds.Count);

        return seed.Companies.Count;
    }
}
