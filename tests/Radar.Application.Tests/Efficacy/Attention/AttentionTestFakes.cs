using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Efficacy.Attention;
using Radar.Application.Pipeline;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;
using Radar.Application.Storage;

namespace Radar.Application.Tests.Efficacy.Attention;

/// <summary>Shared offline fakes + builders for the AD-16 attention-arrival tests (no disk, no network).</summary>
internal static class AttentionTestFakes
{
    public const string NewsSearch = "newssearch";
    public const string Gdelt = "news";

    public static AttentionArrivalOptions Options(string attentionCollector = NewsSearch) =>
        new(attentionCollector, [NewsSearch, Gdelt]);

    public static Company Company(Guid id, string ticker) =>
        new(
            Id: id,
            Name: ticker + " Inc.",
            LegalName: null,
            Ticker: ticker,
            Exchange: null,
            CountryCode: null,
            Sector: null,
            Industry: null,
            Status: CompanyStatus.Active,
            CreatedAtUtc: DateTimeOffset.UnixEpoch,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch,
            Themes: []);

    /// <summary>A news-article evidence item carrying the collector stamp and the real publisher metadata.</summary>
    public static EvidenceItem NewsEvidence(
        Guid id,
        string? publisher,
        string? collector = NewsSearch,
        EvidenceSourceType sourceType = EvidenceSourceType.NewsArticle)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["quality"] = "Medium" };
        if (publisher is not null)
        {
            metadata["publisher"] = publisher;
        }

        if (collector is not null)
        {
            metadata[CollectionProvenanceMetadata.MetadataKey] = collector;
        }

        return new EvidenceBuilder()
            .WithId(id)
            .WithSourceType(sourceType)
            // The collector's own blank-publisher fallback: SourceName carries the per-company FEED name,
            // which is Radar's label and NOT a third-party publisher.
            .WithSourceName(publisher ?? "Acme — News attention (Google News)")
            .WithMetadataJson(EvidenceMetadata.Compose(metadata, []))
            .Build();
    }

    /// <summary>
    /// A stand-in for spec 151's opt-in <c>InferringCollectorAttributionResolver</c> (which is Infrastructure
    /// -internal): a RECORDED stamp always wins, and evidence carrying none is INFERRED onto
    /// <paramref name="inferredCollector"/> rather than left unattributed.
    /// </summary>
    public sealed class InferringResolver(string inferredCollector = NewsSearch) : ICollectorAttributionResolver
    {
        public CollectorAttribution Resolve(EvidenceItem? evidence)
        {
            var recorded = CollectionProvenanceMetadata.Read(evidence);
            return recorded is not null
                ? CollectorAttribution.Recorded(recorded)
                : CollectorAttribution.Inferred(inferredCollector);
        }
    }

    public static Signal MediaAttentionSignal(
        Guid companyId,
        Guid evidenceId,
        DateTimeOffset observedAtUtc,
        SignalReviewStatus status = SignalReviewStatus.Approved,
        SignalType type = SignalType.MediaAttention) =>
        new SignalBuilder()
            .WithCompanyId(companyId)
            .WithEvidenceId(evidenceId)
            .WithType(type)
            .WithReviewStatus(status)
            .WithObservedAtUtc(observedAtUtc)
            .Build();

    /// <summary>A run record that IS a complete newssearch checkpoint for every listed company.</summary>
    public static PipelineRunRecord CompleteCheckpoint(
        DateTimeOffset createdAtUtc, params Guid[] companyIds) =>
        Checkpoint(
            createdAtUtc,
            [.. companyIds.Select(id => new CollectorCompanyCoverage(id, 1, 1, false, []))]);

    /// <summary>A run record carrying an explicit newssearch coverage set.</summary>
    public static PipelineRunRecord Checkpoint(
        DateTimeOffset createdAtUtc,
        IReadOnlyList<CollectorCompanyCoverage>? coverage,
        IReadOnlyList<string>? collectors = null,
        IReadOnlyList<string>? strategies = null,
        IReadOnlyList<string>? companyFilter = null,
        IReadOnlyList<CollectorRunRecord>? collectorRuns = null) =>
        new(
            Id: DeterministicRunId(createdAtUtc),
            CreatedAtUtc: createdAtUtc,
            Collectors: collectors ?? [NewsSearch],
            EvidenceCollected: 1,
            EvidenceNew: 1,
            SignalsExtracted: 1,
            SignalsValid: 1,
            SignalsApproved: 1,
            SignalsNeedingReview: 0,
            CompaniesScored: 1,
            SourcesChecked: 1,
            SourcesFailed: 0,
            ReportId: null,
            CollectionWarnings: null,
            Strategies: strategies ?? [AttentionArrivalScreen.PrimaryStrategyName],
            PrimaryStrategy: "default",
            CompanyFilter: companyFilter,
            CollectorRuns: collectorRuns
                ?? [new CollectorRunRecord(NewsSearch, 1, 1, 0, 1, [], coverage)]);

    /// <summary>Run ids derived from the instant so a fixture's ordering is stable across runs (AD-3).</summary>
    public static Guid DeterministicRunId(DateTimeOffset instant)
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes, instant.UtcTicks);
        return new Guid(bytes);
    }
}

/// <summary>An in-memory read-only signal repository. Every mutation throws so a write would fail loud.</summary>
internal sealed class FakeSignalRepository : ISignalRepository
{
    private readonly List<Signal> _signals = [];

    public FakeSignalRepository With(params Signal[] signals)
    {
        _signals.AddRange(signals);
        return this;
    }

    public Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Signal>>([.. _signals.Where(s => s.CompanyId == companyId)]);

    public Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_signals.FirstOrDefault(s => s.Id == id));

    public Task AddAsync(Signal signal, CancellationToken ct) =>
        throw new NotSupportedException("The attention screen must be read-only over signals.");
}

/// <summary>An in-memory read-only evidence repository. Every mutation throws so a write would fail loud.</summary>
internal sealed class FakeEvidenceRepository : IEvidenceRepository
{
    private readonly Dictionary<Guid, EvidenceItem> _byId = [];

    public FakeEvidenceRepository With(params EvidenceItem[] items)
    {
        foreach (var item in items)
        {
            _byId[item.Id] = item;
        }

        return this;
    }

    public Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EvidenceItem>>([.. _byId.Values]);

    public Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct) =>
        throw new NotSupportedException("The attention screen must be read-only over evidence.");
}

/// <summary>A run store serving a fixed history; writes throw.</summary>
internal sealed class FakePipelineRunStore(params PipelineRunRecord[] records) : IPipelineRunStore
{
    public Task<DurableWriteResult> WriteAsync(PipelineRunRecord record, CancellationToken ct) =>
        throw new NotSupportedException("The attention screen must be read-only over the run log.");

    public Task<IReadOnlyList<PipelineRunRecord>> ReadRecentAsync(int count, CancellationToken ct) =>
        throw new NotSupportedException(
            "The screen must use the TIME-bounded read: a newest-N truncation would read as absence.");

    public Task<IReadOnlyList<PipelineRunRecord>> ReadBetweenAsync(
        DateTimeOffset startInclusiveUtc, DateTimeOffset endInclusiveUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PipelineRunRecord>>([
            .. records
                .Where(r => r.CreatedAtUtc >= startInclusiveUtc && r.CreatedAtUtc <= endInclusiveUtc)
                .OrderBy(r => r.CreatedAtUtc)
                .ThenBy(r => r.Id),
        ]);
}

/// <summary>A cohort store returning a fixed, caller-supplied result.</summary>
internal sealed class FakeExcludedCohortStore(ExcludedCohortSet set) : IExcludedCohortStore
{
    public static FakeExcludedCohortStore Empty { get; } = new(ExcludedCohortSet.Available([]));

    public Task<ExcludedCohortSet> LoadAsync(CancellationToken ct) => Task.FromResult(set);
}

/// <summary>Records the artifacts it is asked to write.</summary>
internal sealed class RecordingAttentionArrivalArtifactStore : IAttentionArrivalArtifactStore
{
    public List<(string Json, string Csv, string Markdown)> Written { get; } = [];

    public Task<AttentionArrivalArtifactPaths> WriteAsync(
        string json, string csv, string markdown, CancellationToken ct)
    {
        Written.Add((json, csv, markdown));
        return Task.FromResult(new AttentionArrivalArtifactPaths(
            DurableWriteResult.Succeeded("attention-arrival-screen.json"),
            DurableWriteResult.Succeeded("attention-arrival-screen.csv"),
            DurableWriteResult.Succeeded("attention-arrival-screen.md")));
    }
}
