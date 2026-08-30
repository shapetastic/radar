using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Signals;

namespace Radar.IntegrationTests;

/// <summary>
/// A read-through memoization of <see cref="ISignalFileStore.ReadApprovedInWindowAsync"/>, keyed by its
/// EXACT arguments, for the read-only paired counterfactual harnesses (spec 196 §7, spec 198 §4).
/// <para>
/// <b>Why it exists.</b> HISTORY (dated): before spec 203 §2, <c>FileSignalStore.ReadApprovedInWindowAsync</c>
/// was a per-call month-scoped DISK SCAN, and a PAIRED harness asks the identical question once per company
/// per ARM (<c>ScoreCompanyAsync</c> reads the previous/velocity window through it), so on 2026-08-29 the
/// spec-198 counterfactual ran 14 minutes on ~2 seconds of CPU — pure disk thrash — until this memoizer was
/// shared between the arms. Spec 203 §2 then moved that read onto the spec-142 hydration index: it opens NO
/// file after hydration, so the disk cost is gone for the pipeline and the harnesses alike. What the memoizer
/// buys TODAY is smaller and still worth having: it avoids repeating the in-memory filter + cross-run
/// collapse + sort per arm, and — the property the paired design actually rests on — it GUARANTEES both arms
/// see the identical list for the identical argument tuple.
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
