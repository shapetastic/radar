using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// An insert-only store faithful to <c>FileNewsJudgmentStore</c>: write-once per deterministic id, a
/// duplicate id is a dedupe that returns <c>true</c>, and enumeration is deterministic.
/// <para>
/// Shared by the spec-187 §1 attempt-bound tests and the spec-187 §7 provider-timing tests rather than
/// copied into each — a second copy would drift, and both suites depend on the DEDUPE behaviour being
/// exactly the production one (it is what let a real hosted call vanish before spec 186 §2).
/// </para>
/// </summary>
internal sealed class InsertOnlyStore : INewsJudgmentStore
{
    private readonly List<NewsJudgmentRecord> _records = [];

    public IReadOnlyList<NewsJudgmentRecord> Records => _records;

    public bool FailWrites { get; set; }

    public Task<bool> WriteAsync(NewsJudgmentRecord record, CancellationToken ct)
    {
        if (FailWrites)
        {
            return Task.FromResult(false);
        }

        if (_records.All(r => r.JudgmentId != record.JudgmentId))
        {
            _records.Add(record);
        }

        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<NewsJudgmentRecord>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NewsJudgmentRecord>>(
            [.. _records.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.JudgmentId)]);

    public Task<NewsJudgmentRecord?> FindCompletedAsync(
        string cohortKey, Guid companyId, string familySetHash, CancellationToken ct) =>
        Task.FromResult(_records.LastOrDefault(r =>
            r.CohortKey == cohortKey
            && r.CompanyId == companyId
            && r.FamilySetHash == familySetHash
            && r.IsCompletedJudgment));
}

/// <summary>A batch reader that resolves nothing — the coverage dimensions are not what these suites test.</summary>
internal sealed class NullBatchReader : INewsObservationBatchReader
{
    public Task<NewsObservationBatch?> GetBatchAsync(Guid batchId, CancellationToken ct) =>
        Task.FromResult<NewsObservationBatch?>(null);
}

/// <summary>
/// A judge analyzer that COUNTS its invocations — the thing spec 187 §1's bound actually protects and the
/// thing spec 187 §7's progress counters must agree with (records on disk can be deduplicated by identity,
/// calls cannot be taken back).
/// <para>
/// <see cref="OnCall"/> receives the 1-based call ordinal and exists so a timing test can advance a fake
/// monotonic clock from INSIDE the call — simulating latency with no wall-clock sleep.
/// </para>
/// </summary>
internal sealed class CountingAnalyzer(
    Func<NewsJudgmentAnalysisRequest, NewsJudgmentAnalysisOutcome> respond) : INewsJudgmentAnalyzer
{
    public int Calls { get; private set; }

    public Action<int>? OnCall { get; set; }

    public Task<NewsJudgmentAnalysisOutcome> AnalyzeAsync(
        NewsJudgmentAnalysisRequest request, CancellationToken ct)
    {
        Calls++;
        OnCall?.Invoke(Calls);
        return Task.FromResult(respond(request));
    }
}
