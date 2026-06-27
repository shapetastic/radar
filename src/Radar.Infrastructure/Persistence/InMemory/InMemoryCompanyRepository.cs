using System.Collections.Concurrent;
using Radar.Application.Abstractions.Persistence;
using Radar.Domain.Companies;

namespace Radar.Infrastructure.Persistence.InMemory;

public sealed class InMemoryCompanyRepository : ICompanyRepository
{
    private readonly ConcurrentDictionary<Guid, Company> _companies = new();
    private readonly ConcurrentDictionary<Guid, CompanyAlias> _aliases = new();

    public Task AddAsync(Company company, CancellationToken ct)
    {
        _companies[company.Id] = company;
        return Task.CompletedTask;
    }

    public Task<Company?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        _companies.TryGetValue(id, out var company);
        return Task.FromResult(company);
    }

    public Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken ct)
    {
        IReadOnlyList<Company> result = _companies.Values
            .OrderBy(c => c.CreatedAtUtc)
            .ThenBy(c => c.Id)
            .ToList();
        return Task.FromResult(result);
    }

    public Task AddAliasAsync(CompanyAlias alias, CancellationToken ct)
    {
        _aliases[alias.Id] = alias;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct)
    {
        IReadOnlyList<CompanyAlias> result = _aliases.Values
            .OrderBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .ToList();
        return Task.FromResult(result);
    }
}
