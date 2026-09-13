using Radar.Application.Abstractions.Persistence;
using Radar.Domain.Evidence;

namespace Radar.IntegrationTests;

/// <summary>
/// A pass-through over the raw evidence store whose ONLY job is to make a write impossible from a read-only
/// harness: <see cref="AddIfNewAsync"/> throws. Every read is the production read path, unmodified.
/// <para>
/// ONE definition, two consumers (CLAUDE.md reuse-over-copy, which applies to harnesses too): extracted by spec
/// 225 from <see cref="InsiderCollapseCounterfactualTests"/>' private copy when the spec-225 read-only harness
/// needed the same guard, alongside <see cref="MemoizingSignalWindowReads"/> and
/// <see cref="ReadOnlyHarnessSourceDescriptor"/>.
/// </para>
/// </summary>
internal sealed class ReadOnlyEvidenceRepository(IEvidenceRepository inner) : IEvidenceRepository
{
    public Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct) =>
        throw new InvalidOperationException("A read-only harness must never write evidence.");

    public Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct) => inner.GetByIdAsync(id, ct);

    public Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct) =>
        inner.GetByContentHashAsync(contentHash, ct);

    public Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct) => inner.GetAllAsync(ct);
}
