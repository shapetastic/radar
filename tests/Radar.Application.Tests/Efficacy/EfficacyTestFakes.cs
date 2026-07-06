using Radar.Application.Abstractions.Persistence;
using Radar.Application.Efficacy;
using Radar.Application.Prices;
using Radar.Application.Scoring;
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

    public Task<string> WriteAsync(
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

    public Task<string> WriteAsync(PriceHistory history, CancellationToken ct)
    {
        WriteCount++;
        throw new NotSupportedException("The efficacy layer must be read-only over price.");
    }
}

/// <summary>Records the efficacy artifacts it is asked to write.</summary>
internal sealed class RecordingEfficacyArtifactStore : IEfficacyArtifactStore
{
    public List<(string Ticker, string Svg, string Csv)> Written { get; } = [];

    public Task<EfficacyArtifactPaths> WriteAsync(
        string ticker, string svg, string csv, CancellationToken ct)
    {
        Written.Add((ticker, svg, csv));
        return Task.FromResult(new EfficacyArtifactPaths($"{ticker}.svg", $"{ticker}.csv"));
    }
}
