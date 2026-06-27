using Radar.Domain.Companies;

namespace Radar.Application.Abstractions.Persistence;

public interface ICompanyRepository
{
    Task AddAsync(Company company, CancellationToken ct);
    Task<Company?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken ct);
    Task AddAliasAsync(CompanyAlias alias, CancellationToken ct);
    Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct);
}
