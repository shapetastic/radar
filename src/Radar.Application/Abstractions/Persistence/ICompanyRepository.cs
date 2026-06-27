using Radar.Domain.Companies;

namespace Radar.Application.Abstractions.Persistence;

public interface ICompanyRepository
{
    /// <remarks>
    /// Upsert by Id (last-write-wins). The relational implementation must preserve these
    /// semantics; do not silently switch evidence to upsert or these to insert-only.
    /// </remarks>
    Task AddAsync(Company company, CancellationToken ct);
    Task<Company?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken ct);

    /// <remarks>
    /// Upsert by Id (last-write-wins). The relational implementation must preserve these
    /// semantics; do not silently switch evidence to upsert or these to insert-only.
    /// </remarks>
    Task AddAliasAsync(CompanyAlias alias, CancellationToken ct);
    Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct);
}
