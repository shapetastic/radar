using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Signals;

namespace Radar.IntegrationTests;

/// <summary>
/// A read-through memoization of <see cref="ISignalFileStore.ReadApprovedInWindowAsync"/>, keyed by its
/// EXACT arguments, for the read-only paired counterfactual harnesses (spec 196 §7, spec 198 §4).
/// <para>
/// <b>Why it exists, quantified.</b> <c>FileSignalStore.ReadApprovedInWindowAsync</c> deliberately keeps its
/// own month-scoped DISK SCAN rather than serving from the spec-142 hydration index — it answers a different
/// question (the activity-only previous window, AD-6) under semantics pinned by its own tests. That is
/// correct for the pipeline, which asks once per company per run, and ruinous for a PAIRED harness, which
/// asks the identical question once per company per ARM: <c>ScoreCompanyAsync</c> calls it for the current
/// AND the previous/velocity window, so two arms over the 94-company universe (spec 199 took it 74 -> 94)
/// issue ~188 scans over signal partitions holding tens of thousands of JSON files each. The spec-198
/// measurement below was taken at 74 companies (~148 scans). Measured on the live store (2026-08-29): the
/// spec-198 counterfactual ran 14 minutes on ~2 seconds of CPU — pure disk thrash — before this was shared.
/// </para>
/// <para>
/// <b>It is answer-preserving.</b> It changes no result, only how many times the same files are opened; the
/// key is the full argument tuple, so two genuinely different questions never collide.
/// <see cref="WriteAsync"/> THROWS — these harnesses are read-only and must fail loudly if that ever stops
/// being true.
/// </para>
/// <para>
/// ONE definition, two consumers (CLAUDE.md reuse-over-copy, which applies to harnesses too): extracted from
/// <see cref="AttentionPolicyCounterfactualTests"/>'s private copy when spec 198 added the second harness,
/// alongside <see cref="ReadOnlyHarnessSourceDescriptor"/>. A second copy would let the two harnesses drift
/// into caching different things while both claiming to hold the read constant.
/// </para>
/// </summary>
internal sealed class MemoizingSignalWindowReads(ISignalFileStore inner) : ISignalFileStore
{
    private readonly Dictionary<(Guid, DateTimeOffset, DateTimeOffset, DateTimeOffset),
        IReadOnlyList<Signal>> _cache = [];

    public Task<DurableWriteResult> WriteAsync(Signal signal, SignalReview review, CancellationToken ct) =>
        throw new InvalidOperationException(
            "A read-only counterfactual harness must never write a signal.");

    public async Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
        Guid companyId,
        DateTimeOffset startExclusiveUtc,
        DateTimeOffset endInclusiveUtc,
        DateTimeOffset knownAsOfUtc,
        CancellationToken ct)
    {
        var key = (companyId, startExclusiveUtc, endInclusiveUtc, knownAsOfUtc);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var read = await inner.ReadApprovedInWindowAsync(
            companyId, startExclusiveUtc, endInclusiveUtc, knownAsOfUtc, ct);
        _cache[key] = read;
        return read;
    }
}
