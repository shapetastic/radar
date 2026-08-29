using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Scoring;

namespace Radar.Application.Efficacy.DenominatorAudit;

/// <summary>
/// The spec-172 audit orchestration. For each configured strategy it resolves the SAME per-strategy snapshot
/// store the spec-140 comparison and the spec-169 screen read (the shared
/// <see cref="IStrategyScoreSnapshotStoreSelector"/> seam — no second path resolution), reads each company's
/// persisted snapshots WITH their stored evidence links, and hands the series to the pure
/// <see cref="ScoreMoveDenominatorAudit"/> computation. Read-only over score history; writes only the audit
/// artifact pair.
/// <para>
/// <b>The link read fails CLOSED.</b> The selected store must expose the link-bearing read
/// (<see cref="IScoreSnapshotLinkReader"/> — the file-backed store implements it alongside the scalar seam,
/// spec 142's one-format pattern). A store that cannot serve links throws, naming the type, rather than
/// silently reporting zero links for every snapshot — a zero-link series would look exactly like the finding
/// under audit.
/// </para>
/// <para>
/// Companies are walked in ascending-Id order (AD-3) so the artifact is byte-identical regardless of the
/// repository's enumeration order; per-company observations are already in as-of order.
/// </para>
/// </summary>
public sealed class ScoreMoveDenominatorAuditGenerator : IScoreMoveDenominatorAuditGenerator
{
    private readonly ScoringStrategySet _strategies;
    private readonly IStrategyScoreSnapshotStoreSelector _stores;
    private readonly ICompanyRepository _companyRepository;
    private readonly ScoreMoveDenominatorAuditRenderer _renderer;
    private readonly IDenominatorAuditArtifactStore _artifactStore;
    private readonly ILogger<ScoreMoveDenominatorAuditGenerator> _logger;

    public ScoreMoveDenominatorAuditGenerator(
        ScoringStrategySet strategies,
        IStrategyScoreSnapshotStoreSelector stores,
        ICompanyRepository companyRepository,
        ScoreMoveDenominatorAuditRenderer renderer,
        IDenominatorAuditArtifactStore artifactStore,
        ILogger<ScoreMoveDenominatorAuditGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(logger);

        _strategies = strategies;
        _stores = stores;
        _companyRepository = companyRepository;
        _renderer = renderer;
        _artifactStore = artifactStore;
        _logger = logger;
    }

    public async Task<DenominatorAuditReport> GenerateAsync(CancellationToken ct)
    {
        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);
        var orderedCompanyIds = companies
            .Select(c => c.Id)
            .OrderBy(id => id)
            .ToList();

        var results = new List<DenominatorAuditStrategyResult>(_strategies.Strategies.Count);
        foreach (var strategy in _strategies.Strategies)
        {
            ct.ThrowIfCancellationRequested();

            var store = _stores.ForStrategy(strategy);
            if (store is not IScoreSnapshotLinkReader linkReader)
            {
                throw new InvalidOperationException(
                    $"The score-snapshot store for strategy '{strategy.Name}' ({store.GetType().Name}) does "
                        + "not expose the stored evidence links (IScoreSnapshotLinkReader). The denominator "
                        + "audit fails closed rather than reporting zero links for every snapshot — a "
                        + "zero-link series would be indistinguishable from the finding under audit.");
            }

            var observations = new List<DenominatorObservation>();
            var companiesWithPairs = 0;
            foreach (var companyId in orderedCompanyIds)
            {
                ct.ThrowIfCancellationRequested();

                var series = await linkReader
                    .ReadAllWithLinksForCompanyAsync(companyId, ct)
                    .ConfigureAwait(false);
                var companyObservations = ScoreMoveDenominatorAudit.BuildObservations(strategy.Name, series);
                if (companyObservations.Count > 0)
                {
                    companiesWithPairs++;
                }

                observations.AddRange(companyObservations);
            }

            results.Add(ScoreMoveDenominatorAudit.Compute(
                strategy.Name, orderedCompanyIds.Count, companiesWithPairs, observations));
        }

        var report = new DenominatorAuditReport(results);
        var csv = _renderer.RenderCsv(report);
        var markdown = _renderer.RenderMarkdown(report);
        var paths = await _artifactStore.WriteAsync(csv, markdown, ct).ConfigureAwait(false);
        if (paths.NotPersistedCount > 0)
        {
            // Spec 201 §1: the summary line below names the paths; this says which of them is not there.
            _logger.LogWarning(
                "Score-move denominator audit: {FilesNotPersisted} of 2 artifact file(s) could NOT be "
                    + "durably persisted; the on-disk audit is missing or STALE.",
                paths.NotPersistedCount);
        }

        _logger.LogInformation(
            "Score-move denominator audit over {Series}: {Strategies} strateg(ies), "
                + "{Observations} consecutive-pair observation(s) across {Companies} compan(ies); "
                + "artifacts at {CsvPath} and {MarkdownPath}.",
            _stores.SeriesDescription,
            results.Count,
            results.Sum(r => r.Observations.Count),
            orderedCompanyIds.Count,
            paths.CsvPath,
            paths.MarkdownPath);

        return report;
    }
}
