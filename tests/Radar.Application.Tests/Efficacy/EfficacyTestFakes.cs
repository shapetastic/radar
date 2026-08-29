using Radar.Application.Abstractions.Persistence;
using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Prices;
using Radar.Application.Scoring;
using Radar.Application.Storage;
using Radar.Domain.Companies;
using Radar.Domain.Scoring;

namespace Radar.Application.Tests.Efficacy;

/// <summary>Shared offline fakes for the efficacy Application tests (no disk, no network).</summary>
internal static class EfficacyTestFakes
{
    public static PriceBar Bar(DateOnly date, decimal close) =>
        new(date, Open: close - 1m, High: close + 1m, Low: close - 2m, Close: close, AdjClose: close - 0.5m, Volume: 1000);
}

internal sealed class FakeCompanyRepository(params Company[] companies) : ICompanyRepository
{
    private readonly IReadOnlyList<Company> _companies = companies;

    public Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken ct) => Task.FromResult(_companies);

    public Task AddAsync(Company company, CancellationToken ct) => throw new NotSupportedException();

    public Task<Company?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task AddAliasAsync(CompanyAlias alias, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct) =>
        throw new NotSupportedException();

    public Task AddSourceFeedAsync(CompanySourceFeed feed, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<CompanySourceFeed>> GetSourceFeedsAsync(CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>A read-only score-snapshot store keyed by CompanyId; WriteAsync throws so a write would fail loud.</summary>
internal sealed class FakeScoreSnapshotFileStore : IScoreSnapshotFileStore
{
    private readonly Dictionary<Guid, IReadOnlyList<CompanyScoreSnapshot>> _byCompany = [];

    public int WriteCount { get; private set; }

    public FakeScoreSnapshotFileStore With(Guid companyId, params CompanyScoreSnapshot[] snapshots)
    {
        _byCompany[companyId] = snapshots
            .OrderBy(s => s.CreatedAtUtc)
            .ThenBy(s => s.Id)
            .ToList();
        return this;
    }

    public Task<IReadOnlyList<CompanyScoreSnapshot>> ReadAllForCompanyAsync(
        Guid companyId, CancellationToken ct) =>
        Task.FromResult(_byCompany.TryGetValue(companyId, out var list)
            ? list
            : []);

    public Task<DurableWriteResult> WriteAsync(
        CompanyScoreSnapshot snapshot, IReadOnlyList<ScoreEvidenceLink> links, CancellationToken ct)
    {
        WriteCount++;
        throw new NotSupportedException("The efficacy layer must be read-only over score history.");
    }

    public Task<CompanyScoreSnapshot?> ReadLatestBeforeAsync(
        Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>A read-only price store keyed by (case-insensitive) ticker.</summary>
internal sealed class FakePriceHistoryStore : IPriceHistoryStore
{
    private readonly Dictionary<string, PriceHistory> _byTicker =
        new(StringComparer.OrdinalIgnoreCase);

    public int WriteCount { get; private set; }

    public FakePriceHistoryStore With(string ticker, params PriceBar[] bars)
    {
        _byTicker[ticker] = new PriceHistory(ticker, "test", DateTimeOffset.UnixEpoch, bars);
        return this;
    }

    public Task<PriceHistory?> ReadAsync(string ticker, CancellationToken ct) =>
        Task.FromResult(_byTicker.TryGetValue(ticker, out var h) ? h : null);

    public Task<DurableWriteResult> WriteAsync(PriceHistory history, CancellationToken ct)
    {
        WriteCount++;
        throw new NotSupportedException("The efficacy layer must be read-only over price.");
    }
}

/// <summary>Records the efficacy artifacts it is asked to write.</summary>
internal sealed class RecordingEfficacyArtifactStore : IEfficacyArtifactStore
{
    public List<(string Ticker, string Svg, string Csv)> Written { get; } = [];

    public List<(string Csv, string Markdown)> Leaderboards { get; } = [];

    public List<(string Csv, string Markdown, string BlocksCsv)> PairedComparisons { get; } = [];

    public Task<EfficacyArtifactPaths> WriteAsync(
        string ticker, string svg, string csv, CancellationToken ct)
    {
        Written.Add((ticker, svg, csv));
        return Task.FromResult(new EfficacyArtifactPaths(
            DurableWriteResult.Succeeded($"{ticker}.svg"), DurableWriteResult.Succeeded($"{ticker}.csv")));
    }

    public Task<StrategyLeaderboardPaths> WriteLeaderboardAsync(
        string csv, string markdown, CancellationToken ct)
    {
        Leaderboards.Add((csv, markdown));
        return Task.FromResult(new StrategyLeaderboardPaths(
            DurableWriteResult.Succeeded("strategy-leaderboard.csv"),
            DurableWriteResult.Succeeded("strategy-leaderboard.md")));
    }

    public Task<PairedComparisonPaths> WritePairedComparisonAsync(
        string csv, string markdown, string blocksCsv, CancellationToken ct)
    {
        PairedComparisons.Add((csv, markdown, blocksCsv));
        return Task.FromResult(new PairedComparisonPaths(
            DurableWriteResult.Succeeded("strategy-paired-comparison.csv"),
            DurableWriteResult.Succeeded("strategy-paired-comparison.md"),
            DurableWriteResult.Succeeded("strategy-paired-comparison-blocks.csv")));
    }
}

/// <summary>Hands out a caller-supplied score store per strategy NAME (case-insensitive).</summary>
internal sealed class FakeStrategyScoreSnapshotStoreSelector : IStrategyScoreSnapshotStoreSelector
{
    private readonly Dictionary<string, IScoreSnapshotFileStore> _byStrategy =
        new(StringComparer.OrdinalIgnoreCase);

    public string SeriesDescription => "the test score series";

    public FakeStrategyScoreSnapshotStoreSelector With(string strategyName, IScoreSnapshotFileStore store)
    {
        _byStrategy[strategyName] = store;
        return this;
    }

    public IScoreSnapshotFileStore ForStrategy(ScoringStrategyDefinition strategy) =>
        _byStrategy.TryGetValue(strategy.Name, out var store)
            ? store
            : new FakeScoreSnapshotFileStore();
}
