using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Efficacy.DenominatorAudit;
using Radar.Application.Efficacy.Statistics;
using Radar.Application.Scoring;
using Radar.Domain.Companies;
using Radar.Domain.Scoring;

namespace Radar.Application.Efficacy.EvidenceConfidence;

/// <summary>
/// Builds the spec-225 measurement (<c>evidence-confidence-distribution-v1</c>): does the
/// <c>EvidenceConfidenceScore</c> component discriminate anything across the live universe, or is it a
/// near-constant multiplier removing a fixed share of every score?
/// <para>
/// <b>What it reads.</b> ONE strategy — the one named <see cref="ScoringStrategySet.DefaultStrategyName"/> —
/// through the SAME per-strategy snapshot-store seam the comparison, the screen and the denominator audit
/// read (<see cref="IStrategyScoreSnapshotStoreSelector"/>), taking each seeded company's LATEST persisted
/// snapshot WITH its stored links (<see cref="IScoreSnapshotLinkReader"/>, failing CLOSED exactly as the
/// denominator audit does when the store cannot serve links). The measurement INSTANT is the greatest
/// <c>WindowEndUtc</c> across those latest snapshots; a company whose latest snapshot ends earlier is counted
/// on its own axis and excluded, because it is a different read.
/// </para>
/// <para>
/// <b>What it computes, and through what.</b> Per included company the scored signal set is REBUILT from the
/// stored links (signal by id from one per-company read, evidence by id) and decomposed through
/// <see cref="ScoreSignalMath.EvidenceConfidenceDecomposition"/> — the production body, never a copy — so the
/// three terms reported are the terms the formula multiplied. Opportunity is recomposed through
/// <see cref="ScoreSignalMath.OpportunityComposition"/> + <see cref="ScoreSignalMath.NotednessDiscount"/>
/// from the persisted components, and the counterfactual (EvidenceConfidence held at the universe median,
/// computed but NOT applied) goes through the same two calls. A recomputed value that disagrees with what
/// was persisted is a FINDING, flagged and counted, never silently corrected in either direction: the
/// persisted value stays the one the distribution and correlations use.
/// </para>
/// <para>
/// <b>What it never does.</b> It writes no score, signal, evidence or review, reads no price, touches no
/// weight, formula version or fingerprint input, and changes nothing about how the next run scores. A
/// zero-link snapshot (the formula's all-zero empty-window components) is a DEFAULTED zero and renders as NOT
/// RECORDED everywhere — it is excluded from every distribution, correlation and counterfactual and counted
/// on <c>NoSignalsInWindow</c>.
/// </para>
/// </summary>
public sealed class EvidenceConfidenceDistributionReporter
{
    public const string ArtifactVersion = "evidence-confidence-distribution-v1";

    /// <summary>The counterfactual's top-N membership check; 10 is this rule's choice, stated on the artifact.</summary>
    public const int TopN = 10;

    public const string HeldValueRule =
        "the median persisted EvidenceConfidence over the INCLUDED companies (mean of the two central values "
            + "on an even count), rounded away from zero to an integer";

    public const string RankingRule =
        "descending Opportunity, then descending Trajectory, then ticker (ordinal; a null ticker sorts last), "
            + "then company id";

    /// <summary>The three term names, as they appear on the artifact and in the verdict inputs.</summary>
    public const string TermBestConfidence = "bestConfidence";

    public const string TermBestQualityWeight = "bestQualityWeight";

    public const string TermDiversityFactor = "diversityFactor";

    public const string BestSignalTieBreakRule =
        "among the scored signals whose Confidence equals bestConfidence, the earliest ObservedAtUtc, then the "
            + "lowest signal id (the scoring pass's own order); every tie is counted";

    public const string LogVarianceRule =
        "share_i = Cov(ln f_i, ln P) / Var(ln P) over the term rows, where P = bestConfidence · "
            + "(EcQualityBase + EcQualitySpan · bestQualityWeight) · (EcDiversityBase + EcDiversitySpan · "
            + "diversityFactor) is the UNCLAMPED, UNROUNDED product (factors from ScoreSignalMath's own "
            + "multipliers) and f_i its three factors; the shares sum to 1 exactly";

    public const string HeldTermRule =
        "one term at a time held at its median over the term rows (mean of the two central values on an even "
            + "count; NOT rounded), the other two as measured; EvidenceConfidence recomposed through "
            + "ScoreSignalMath.EvidenceConfidenceComposition, Opportunity through OpportunityComposition with the "
            + "company's own notedness discount; ranked by the same ranking rule over the companies that are both "
            + "term rows and ranked";

    public const string AboveMedianRule =
        "term rows whose PERSISTED EvidenceConfidence is strictly above the median persisted EvidenceConfidence of "
            + "the included companies; the modal category is the largest count, ties broken by category name (ordinal)";

    private readonly ScoringStrategySet _strategies;
    private readonly IStrategyScoreSnapshotStoreSelector _stores;
    private readonly ICompanyRepository _companies;
    private readonly ISignalRepository _signals;
    private readonly IEvidenceRepository _evidence;
    private readonly ILogger<EvidenceConfidenceDistributionReporter> _logger;

    public EvidenceConfidenceDistributionReporter(
        ScoringStrategySet strategies,
        IStrategyScoreSnapshotStoreSelector stores,
        ICompanyRepository companies,
        ISignalRepository signals,
        IEvidenceRepository evidence,
        ILogger<EvidenceConfidenceDistributionReporter> logger)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(companies);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(logger);

        _strategies = strategies;
        _stores = stores;
        _companies = companies;
        _signals = signals;
        _evidence = evidence;
        _logger = logger;
    }

    public async Task<EvidenceConfidenceDistributionReport> BuildAsync(CancellationToken ct)
    {
        var strategy = _strategies.Strategies.FirstOrDefault(s =>
            string.Equals(s.Name, ScoringStrategySet.DefaultStrategyName, StringComparison.OrdinalIgnoreCase));
        if (strategy is null)
        {
            // CLAUDE.md: an enabled component that did no work must say so, once, and why.
            var reason =
                $"no configured strategy is named '{ScoringStrategySet.DefaultStrategyName}' (configured: "
                    + string.Join(", ", _strategies.Strategies.Select(s => s.Name)) + ")";
            _logger.LogInformation(
                "EvidenceConfidence distribution ({Artifact}): did NO work — {Reason}. The artifact records "
                    + "the idle state; nothing was measured.",
                ArtifactVersion,
                reason);
            return Idle(reason);
        }

        var store = _stores.ForStrategy(strategy);
        if (store is not IScoreSnapshotLinkReader linkReader)
        {
            throw new InvalidOperationException(
                $"The score-snapshot store for strategy '{strategy.Name}' ({store.GetType().Name}) does not "
                    + "expose the stored evidence links (IScoreSnapshotLinkReader). The EvidenceConfidence "
                    + "measurement fails closed rather than reporting every company as NoSignalsInWindow — a "
                    + "zero-link read would be indistinguishable from a universe that scored nothing.");
        }

        var formulaIsV8 = string.Equals(
            ScoreFormulaVersions.Canonicalize(strategy.Formula), ScoreFormulaVersions.V8, StringComparison.Ordinal);

        var companies = (await _companies.GetAllAsync(ct).ConfigureAwait(false))
            .OrderBy(c => c.Id)
            .ToList();

        // Pass 1: each company's latest snapshot (with links), and the instant they are measured at.
        var latest = new Dictionary<Guid, ScoreSnapshotWithLinks?>(companies.Count);
        DateTimeOffset? instant = null;
        foreach (var company in companies)
        {
            ct.ThrowIfCancellationRequested();
            var series = await linkReader.ReadAllWithLinksForCompanyAsync(company.Id, ct).ConfigureAwait(false);
            var newest = series
                .OrderByDescending(s => s.Snapshot.WindowEndUtc)
                .ThenByDescending(s => s.Snapshot.CreatedAtUtc)
                .ThenByDescending(s => s.Snapshot.Id)
                .FirstOrDefault();
            latest[company.Id] = newest;
            if (newest is not null && (instant is null || newest.Snapshot.WindowEndUtc > instant))
            {
                instant = newest.Snapshot.WindowEndUtc;
            }
        }

        // Pass 2: per-company rows, rebuilt from the stored links and decomposed through the production body.
        var working = new List<WorkingRow>(companies.Count);
        var linkSignalUnresolvable = 0;
        var linkEvidenceUnresolvable = 0;
        DateTimeOffset? windowStart = null;
        var configVersions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var company in companies)
        {
            ct.ThrowIfCancellationRequested();
            var snapshotWithLinks = latest[company.Id];
            if (snapshotWithLinks is null)
            {
                working.Add(WorkingRow.Excluded(company, EvidenceConfidenceCompanyState.NoSnapshot, null, null, 0, 0, 0));
                continue;
            }

            var snapshot = snapshotWithLinks.Snapshot;
            if (snapshot.WindowEndUtc != instant)
            {
                working.Add(WorkingRow.Excluded(
                    company, EvidenceConfidenceCompanyState.SnapshotNotAtInstant, snapshot.WindowEndUtc,
                    snapshot.ScoringConfigVersion, snapshotWithLinks.Links.Count, 0, 0));
                continue;
            }

            windowStart ??= snapshot.WindowStartUtc;
            configVersions.Add(snapshot.ScoringConfigVersion ?? "(not recorded)");

            var links = snapshotWithLinks.Links;
            if (links.Count == 0)
            {
                working.Add(WorkingRow.Excluded(
                    company, EvidenceConfidenceCompanyState.NoSignalsInWindow, snapshot.WindowEndUtc,
                    snapshot.ScoringConfigVersion, 0, 0, 0));
                continue;
            }

            var signalsById = (await _signals.GetByCompanyAsync(company.Id, ct).ConfigureAwait(false))
                .GroupBy(s => s.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var pairs = new List<ScoringSignal>(links.Count);
            var signalMissing = 0;
            var evidenceMissing = 0;
            foreach (var link in links)
            {
                if (!signalsById.TryGetValue(link.SignalId, out var signal))
                {
                    signalMissing++;
                    continue;
                }

                var evidence = await _evidence.GetByIdAsync(link.EvidenceId, ct).ConfigureAwait(false);
                if (evidence is null)
                {
                    evidenceMissing++;
                    continue;
                }

                pairs.Add(new ScoringSignal(signal, evidence));
            }

            linkSignalUnresolvable += signalMissing;
            linkEvidenceUnresolvable += evidenceMissing;
            if (signalMissing > 0 || evidenceMissing > 0)
            {
                working.Add(WorkingRow.Excluded(
                    company, EvidenceConfidenceCompanyState.CompanyWithUnresolvableLink, snapshot.WindowEndUtc,
                    snapshot.ScoringConfigVersion, links.Count, signalMissing, evidenceMissing));
                continue;
            }

            // The scoring pass's own order (ObservedAtUtc, then Id). The decomposition is order-independent;
            // the order is fixed anyway so the rebuilt set is the same list every time.
            pairs.Sort(static (a, b) =>
            {
                var byObserved = a.Signal.ObservedAtUtc.CompareTo(b.Signal.ObservedAtUtc);
                return byObserved != 0 ? byObserved : a.Signal.Id.CompareTo(b.Signal.Id);
            });

            var terms = ScoreSignalMath.EvidenceConfidenceDecomposition(pairs, strategy.Weights);
            working.Add(WorkingRow.Included(company, snapshot, links.Count, terms, BestSignalOf(pairs, terms.BestConfidence)));
        }

        var included = working.Where(w => w.State == EvidenceConfidenceCompanyState.Included).ToList();
        var termRows = included.Where(w => !w.TermsDisagree).ToList();

        var distributions = BuildDistributions(included, termRows);

        var correlations = new List<EvidenceConfidenceCorrelation>(3)
        {
            Correlate(
                "Trajectory",
                included.Select(w => (double)w.Snapshot!.EvidenceConfidenceScore).ToList(),
                included.Select(w => (double)w.Snapshot!.TrajectoryScore).ToList()),
            Correlate(
                "Opportunity",
                included.Select(w => (double)w.Snapshot!.EvidenceConfidenceScore).ToList(),
                included.Select(w => (double)w.Snapshot!.OpportunityScore).ToList()),
            Correlate(
                "DistinctSourceTypes",
                termRows.Select(w => (double)w.Snapshot!.EvidenceConfidenceScore).ToList(),
                termRows.Select(w => (double)w.Terms!.DistinctSourceTypes).ToList()),
        };

        var counterfactual = BuildCounterfactual(strategy, formulaIsV8, included, distributions.MedianEvidenceConfidence);

        var attribution = BuildTermAttribution(strategy, counterfactual, termRows);
        var bestSignal = BuildBestSignalProfile(termRows);
        var aboveMedian = BuildAboveMedianProfile(termRows, distributions.MedianEvidenceConfidence);

        var verdict = EvidenceConfidenceVerdictRule.Evaluate(
            counterfactual.Available,
            counterfactual.NotAvailableReason,
            counterfactual.RankChangedShare,
            distributions.ModalEvidenceConfidenceShare,
            correlations[2].Rho,
            new EvidenceConfidenceEvidenceTypeInputs(
                DominantTerm: attribution.DominantTerm,
                DominantTermLogVarianceShare: attribution.DominantTermLogVarianceShare,
                AboveMedianCompanies: aboveMedian.Companies,
                AboveMedianModalSignalType: aboveMedian.ModalSignalType,
                AboveMedianModalSignalTypeCompanies: aboveMedian.ModalSignalTypeCompanies,
                AboveMedianModalSignalTypeShare: aboveMedian.ModalSignalTypeShare,
                AboveMedianModalProducer: aboveMedian.ModalProducer,
                AboveMedianModalProducerCompanies: aboveMedian.ModalProducerCompanies,
                RhoEvidenceConfidenceVsTrajectory: correlations[0].Rho));

        var counts = new EvidenceConfidenceCounts(
            CompaniesSeeded: companies.Count,
            CompaniesIncluded: included.Count,
            CompaniesRanked: counterfactual.CompaniesRanked,
            NoSnapshot: working.Count(w => w.State == EvidenceConfidenceCompanyState.NoSnapshot),
            SnapshotNotAtInstant: working.Count(w => w.State == EvidenceConfidenceCompanyState.SnapshotNotAtInstant),
            NoSignalsInWindow: working.Count(w => w.State == EvidenceConfidenceCompanyState.NoSignalsInWindow),
            LinkSignalUnresolvable: linkSignalUnresolvable,
            LinkEvidenceUnresolvable: linkEvidenceUnresolvable,
            CompanyWithUnresolvableLink: working.Count(w => w.State == EvidenceConfidenceCompanyState.CompanyWithUnresolvableLink),
            TermsDisagreeWithSnapshot: included.Count(w => w.TermsDisagree),
            OpportunityRecompositionMismatch: included.Count(w => w.RecompositionMismatch),
            FormulaDoesNotComposeOpportunityFromEvidenceConfidence: formulaIsV8 ? 0 : included.Count,
            MixedScoringConfigVersion: configVersions.Count > 1 ? configVersions.Count : 0);

        var rows = working
            .OrderBy(w => w.ActualRank ?? int.MaxValue)
            .ThenBy(w => w.Company.Ticker is null ? 1 : 0)
            .ThenBy(w => w.Company.Ticker, StringComparer.Ordinal)
            .ThenBy(w => w.Company.Id)
            .Select(w => w.ToRow())
            .ToList();

        var report = new EvidenceConfidenceDistributionReport(
            ArtifactVersion: ArtifactVersion,
            VerdictRuleVersion: EvidenceConfidenceVerdictRule.Version,
            StrategyConfigured: true,
            StrategyNotConfiguredReason: null,
            StrategyName: strategy.Name,
            Formula: strategy.Formula,
            FormulaIsV8: formulaIsV8,
            ScoringProfile: strategy.ScoringProfile,
            EvidenceConfidenceWeights: WeightsOf(strategy.Weights),
            SeriesDescription: _stores.SeriesDescription,
            InstantUtc: instant,
            WindowStartUtc: windowStart,
            ScoringConfigVersionsSeen: configVersions.ToList(),
            Counts: counts,
            Distributions: distributions,
            Correlations: correlations,
            Counterfactual: counterfactual,
            TermAttribution: attribution,
            BestSignal: bestSignal,
            AboveMedian: aboveMedian,
            Verdict: verdict,
            Rows: rows);

        _logger.LogInformation(
            "EvidenceConfidence distribution ({Artifact}) over {Series} at {Instant} for strategy {Strategy} "
                + "({Formula}): {Seeded} seeded, {Included} included, {Ranked} ranked; excluded NoSnapshot={NoSnapshot} "
                + "SnapshotNotAtInstant={NotAtInstant} NoSignalsInWindow={NoSignals} "
                + "CompanyWithUnresolvableLink={Unresolvable} (links: signal {LinkSignal}, evidence {LinkEvidence}); "
                + "TermsDisagreeWithSnapshot={TermsDisagree} OpportunityRecompositionMismatch={RecompMismatch} "
                + "MixedScoringConfigVersion={Mixed}; {DistinctValues} distinct EvidenceConfidence value(s), modal "
                + "{Modal} at share {ModalShare}, median {Median}; rank changed for {RankChanged} of {Ranked} under "
                + "the held-median counterfactual; dominant term {DominantTerm} at log-variance share {TermShare}; "
                + "above-median modal best-confidence type {ModalType} ({ModalTypeCompanies} of {AboveMedian}); "
                + "best-confidence ties {Ties} (spanning types {TiesAcrossTypes}), unclassified producer "
                + "{Unclassified}; verdict {Verdict}.",
            ArtifactVersion,
            _stores.SeriesDescription,
            instant?.ToString("O") ?? "(no snapshot)",
            strategy.Name,
            strategy.Formula,
            counts.CompaniesSeeded,
            counts.CompaniesIncluded,
            counts.CompaniesRanked,
            counts.NoSnapshot,
            counts.SnapshotNotAtInstant,
            counts.NoSignalsInWindow,
            counts.CompanyWithUnresolvableLink,
            counts.LinkSignalUnresolvable,
            counts.LinkEvidenceUnresolvable,
            counts.TermsDisagreeWithSnapshot,
            counts.OpportunityRecompositionMismatch,
            counts.MixedScoringConfigVersion,
            distributions.DistinctEvidenceConfidenceValues,
            distributions.ModalEvidenceConfidence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(not recorded)",
            distributions.ModalEvidenceConfidenceShare?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "(not recorded)",
            distributions.MedianEvidenceConfidence?.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) ?? "(not recorded)",
            counterfactual.RankChanged?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(not recorded)",
            counts.CompaniesRanked,
            attribution.DominantTerm ?? "(not recorded)",
            attribution.DominantTermLogVarianceShare?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "(not recorded)",
            aboveMedian.ModalSignalType ?? "(not recorded)",
            aboveMedian.ModalSignalTypeCompanies?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(not recorded)",
            aboveMedian.Companies,
            bestSignal.CompaniesTiedAtBestConfidence,
            bestSignal.TiesSpanningSignalTypes,
            bestSignal.UnclassifiedProducer,
            verdict.Verdict);

        return report;
    }

    private EvidenceConfidenceDistributionReport Idle(string reason) =>
        new(
            ArtifactVersion: ArtifactVersion,
            VerdictRuleVersion: EvidenceConfidenceVerdictRule.Version,
            StrategyConfigured: false,
            StrategyNotConfiguredReason: reason,
            StrategyName: null,
            Formula: null,
            FormulaIsV8: false,
            ScoringProfile: null,
            EvidenceConfidenceWeights: new Dictionary<string, double>(),
            SeriesDescription: _stores.SeriesDescription,
            InstantUtc: null,
            WindowStartUtc: null,
            ScoringConfigVersionsSeen: [],
            Counts: EvidenceConfidenceCounts.Empty,
            Distributions: EvidenceConfidenceDistributions.Empty,
            Correlations: [],
            Counterfactual: EvidenceConfidenceCounterfactual.NotAvailable(reason),
            TermAttribution: EvidenceConfidenceTermAttribution.Empty(reason),
            BestSignal: EvidenceConfidenceBestSignalProfile.Empty,
            AboveMedian: EvidenceConfidenceAboveMedianProfile.Empty(AboveMedianRule),
            Verdict: EvidenceConfidenceVerdictRule.Evaluate(false, reason, null, null, null),
            Rows: []);

    /// <summary>The config values the component multiplies, named so the artifact states what it measured under.</summary>
    private static IReadOnlyDictionary<string, double> WeightsOf(ScoringWeights weights) =>
        new Dictionary<string, double>
        {
            [nameof(ScoringWeights.EcQualityBase)] = weights.EcQualityBase,
            [nameof(ScoringWeights.EcQualitySpan)] = weights.EcQualitySpan,
            [nameof(ScoringWeights.EcDiversityBase)] = weights.EcDiversityBase,
            [nameof(ScoringWeights.EcDiversitySpan)] = weights.EcDiversitySpan,
            [nameof(ScoringWeights.DiversityTarget)] = weights.DiversityTarget,
            [nameof(ScoringWeights.QualityPrimarySource)] = weights.QualityPrimarySource,
            [nameof(ScoringWeights.QualityHigh)] = weights.QualityHigh,
            [nameof(ScoringWeights.QualityMedium)] = weights.QualityMedium,
            [nameof(ScoringWeights.QualityLow)] = weights.QualityLow,
            [nameof(ScoringWeights.QualityUnknown)] = weights.QualityUnknown,
        };

    private static EvidenceConfidenceDistributions BuildDistributions(
        IReadOnlyList<WorkingRow> included, IReadOnlyList<WorkingRow> termRows)
    {
        if (included.Count == 0)
        {
            return EvidenceConfidenceDistributions.Empty;
        }

        var ec = Shares(included, included.Count, w => w.Snapshot!.EvidenceConfidenceScore);
        var modal = ec.OrderByDescending(e => e.Companies).ThenBy(e => e.Value).First();

        return new EvidenceConfidenceDistributions(
            IncludedCount: included.Count,
            TermRowCount: termRows.Count,
            EvidenceConfidence: ec,
            DistinctEvidenceConfidenceValues: ec.Count,
            ModalEvidenceConfidence: modal.Value,
            ModalEvidenceConfidenceShare: modal.Share,
            MedianEvidenceConfidence: Median(included.Select(w => (double)w.Snapshot!.EvidenceConfidenceScore).ToList()),
            BestConfidence: Shares(termRows, termRows.Count, w => w.Terms!.BestConfidence),
            BestQualityWeight: Shares(termRows, termRows.Count, w => w.Terms!.BestQualityWeight),
            DistinctSourceTypes: Shares(termRows, termRows.Count, w => w.Terms!.DistinctSourceTypes),
            DiversityFactor: Shares(termRows, termRows.Count, w => w.Terms!.DiversityFactor));
    }

    private static List<EvidenceConfidenceValueShare<T>> Shares<T>(
        IReadOnlyList<WorkingRow> rows, int denominator, Func<WorkingRow, T> value)
        where T : struct, IComparable<T> =>
        rows.GroupBy(value)
            .OrderBy(g => g.Key)
            .Select(g => new EvidenceConfidenceValueShare<T>(
                g.Key, g.Count(), denominator == 0 ? 0.0 : (double)g.Count() / denominator))
            .ToList();

    private const string FirstSeriesName = "EvidenceConfidence";

    /// <summary>
    /// ρ(EvidenceConfidence, <paramref name="secondSeriesName"/>). The label is BUILT from the two names and the
    /// degeneracy reason names the constant side from the same name — never parsed back out of the label.
    /// </summary>
    private static EvidenceConfidenceCorrelation Correlate(
        string secondSeriesName, IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var result = SpearmanRankCorrelation.Compute(x, y);
        var reason = result.Degeneracy switch
        {
            SpearmanDegeneracy.None => null,
            SpearmanDegeneracy.TooFewObservations => "undefined: fewer than 2 observations",
            SpearmanDegeneracy.ConstantFirst => "undefined: constant series (" + FirstSeriesName + ")",
            SpearmanDegeneracy.ConstantSecond => "undefined: constant series (" + secondSeriesName + ")",
            _ => "undefined",
        };
        return new EvidenceConfidenceCorrelation(
            FirstSeriesName + " vs " + secondSeriesName, result.ObservationCount, result.Rho, reason);
    }

    private static EvidenceConfidenceCounterfactual BuildCounterfactual(
        ScoringStrategyDefinition strategy,
        bool formulaIsV8,
        IReadOnlyList<WorkingRow> included,
        double? medianEvidenceConfidence)
    {
        if (!formulaIsV8)
        {
            return EvidenceConfidenceCounterfactual.NotAvailable(
                $"strategy '{strategy.Name}' runs formula '{strategy.Formula}', which does not compose "
                    + $"Opportunity as Trajectory × EvidenceConfidence × discount ({ScoreFormulaVersions.V8} does); "
                    + "every included company is counted on FormulaDoesNotComposeOpportunityFromEvidenceConfidence");
        }

        if (medianEvidenceConfidence is null)
        {
            return EvidenceConfidenceCounterfactual.NotAvailable("no included company: the median is not recorded");
        }

        var held = (int)Math.Round(medianEvidenceConfidence.Value, MidpointRounding.AwayFromZero);

        var ranked = new List<WorkingRow>(included.Count);
        foreach (var row in included)
        {
            var snapshot = row.Snapshot!;
            var discount = ScoreSignalMath.NotednessDiscount(strategy.Weights, snapshot.AttentionScore, row.Company.FollowingTier);
            var recomposed = ScoreSignalMath.OpportunityComposition(
                snapshot.TrajectoryScore, snapshot.EvidenceConfidenceScore, discount);
            if (recomposed != snapshot.OpportunityScore)
            {
                row.RecompositionMismatch = true;
                continue;
            }

            row.Discount = discount;
            row.CounterfactualOpportunity = ScoreSignalMath.OpportunityComposition(
                snapshot.TrajectoryScore, held, discount);
            ranked.Add(row);
        }

        if (ranked.Count < 2)
        {
            return EvidenceConfidenceCounterfactual.NotAvailable(
                $"fewer than 2 ranked companies ({ranked.Count}); a ranking needs at least two") with
                {
                    HeldEvidenceConfidence = held,
                    CompaniesRanked = ranked.Count,
                };
        }

        foreach (var (row, rank) in RankBy(ranked, w => w.Snapshot!.OpportunityScore))
        {
            row.ActualRank = rank;
        }

        foreach (var (row, rank) in RankBy(ranked, w => w.CounterfactualOpportunity!.Value))
        {
            row.CounterfactualRank = rank;
        }

        var changed = ranked.Where(w => w.ActualRank != w.CounterfactualRank).ToList();
        var maxMover = ranked
            .OrderByDescending(w => Math.Abs(w.CounterfactualRank!.Value - w.ActualRank!.Value))
            .ThenBy(w => w.ActualRank)
            .First();
        var maxAbs = Math.Abs(maxMover.CounterfactualRank!.Value - maxMover.ActualRank!.Value);

        var actualTop = ranked.Where(w => w.ActualRank <= TopN).Select(w => w.Company.Id).ToHashSet();
        var counterfactualTop = ranked.Where(w => w.CounterfactualRank <= TopN).Select(w => w.Company.Id).ToHashSet();

        return new EvidenceConfidenceCounterfactual(
            Available: true,
            NotAvailableReason: null,
            HeldEvidenceConfidence: held,
            HeldValueRule: HeldValueRule,
            RankingRule: RankingRule,
            CompaniesRanked: ranked.Count,
            RankChanged: changed.Count,
            RankChangedShare: (double)changed.Count / ranked.Count,
            MaxAbsRankDelta: maxAbs,
            MaxAbsRankDeltaCompany: maxAbs == 0 ? null : maxMover.Company.Ticker ?? maxMover.Company.Name,
            TopN: TopN,
            EnterTopN: counterfactualTop.Count(id => !actualTop.Contains(id)),
            LeaveTopN: actualTop.Count(id => !counterfactualTop.Contains(id)),
            MedianActualOpportunity: Median(ranked.Select(w => (double)w.Snapshot!.OpportunityScore).ToList()),
            MedianCounterfactualOpportunity: Median(ranked.Select(w => (double)w.CounterfactualOpportunity!.Value).ToList()));
    }

    /// <summary>
    /// 1-based ranks under <see cref="RankingRule"/> with <paramref name="opportunity"/> as the primary key — the
    /// ONE ordering the actual, the held-EC and the held-term rankings all use, so they can only differ by the
    /// Opportunity they rank.
    /// </summary>
    private static IEnumerable<(WorkingRow Row, int Rank)> RankBy(
        IReadOnlyList<WorkingRow> rows, Func<WorkingRow, int> opportunity) =>
        rows
            .OrderByDescending(opportunity)
            .ThenByDescending(w => w.Snapshot!.TrajectoryScore)
            .ThenBy(w => w.Company.Ticker is null ? 1 : 0)
            .ThenBy(w => w.Company.Ticker, StringComparer.Ordinal)
            .ThenBy(w => w.Company.Id)
            .Select((w, i) => (w, i + 1))
            .ToList();

    /// <summary>
    /// The signal that sets <paramref name="bestConfidence"/>, chosen by <see cref="BestSignalTieBreakRule"/> over
    /// <paramref name="orderedPairs"/> (already in the scoring pass's order), with the tie accounting. The equality
    /// is exact by construction: <paramref name="bestConfidence"/> is the production body's
    /// <c>Max((double)Confidence)</c> over this very list, so it IS one member's converted value.
    /// </summary>
    private static BestSignal? BestSignalOf(IReadOnlyList<ScoringSignal> orderedPairs, double bestConfidence)
    {
        var tied = orderedPairs.Where(p => (double)p.Signal.Confidence == bestConfidence).ToList();
        if (tied.Count == 0)
        {
            return null;
        }

        var chosen = tied[0];
        return new BestSignal(
            chosen,
            SignalProducerRule.Classify(chosen.Signal, chosen.Evidence.SourceType),
            SignalProducerRule.ComparabilityCapNoted(chosen.Signal),
            CollectionProvenanceMetadata.Read(chosen.Evidence.MetadataJson),
            tied.Count,
            tied.Select(p => p.Signal.Type.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            tied.Select(p => SignalProducerRule.Classify(p.Signal, p.Evidence.SourceType).ToString())
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }

    private static EvidenceConfidenceTermAttribution BuildTermAttribution(
        ScoringStrategyDefinition strategy,
        EvidenceConfidenceCounterfactual counterfactual,
        IReadOnlyList<WorkingRow> termRows)
    {
        if (termRows.Count == 0)
        {
            return EvidenceConfidenceTermAttribution.Empty("no term rows: nothing to attribute");
        }

        var weights = strategy.Weights;
        string[] names = [TermBestConfidence, TermBestQualityWeight, TermDiversityFactor];
        Func<EvidenceConfidenceTerms, double>[] termOf =
        [
            t => t.BestConfidence,
            t => t.BestQualityWeight,
            t => t.DiversityFactor,
        ];
        Func<EvidenceConfidenceTerms, double>[] factorOf =
        [
            t => t.BestConfidence,
            t => ScoreSignalMath.EvidenceConfidenceQualityMultiplier(t.BestQualityWeight, weights),
            t => ScoreSignalMath.EvidenceConfidenceDiversityMultiplier(t.DiversityFactor, weights),
        ];

        var ec = termRows.Select(w => (double)w.Snapshot!.EvidenceConfidenceScore).ToList();
        var medians = new double[3];
        for (var k = 0; k < 3; k++)
        {
            medians[k] = ExactMedianInterval.MedianOf(termRows.Select(w => termOf[k](w.Terms!)).ToList());
        }

        // ---- log-variance shares (an exact covariance decomposition of ln P = Σ ln f_k) ----
        string? logReason = null;
        double[]? shares = null;
        var nonPositive = termRows.Count(w => factorOf.Any(f => f(w.Terms!) <= 0.0));
        if (termRows.Count < 2)
        {
            logReason = "fewer than 2 term rows";
        }
        else if (nonPositive > 0)
        {
            logReason = $"{nonPositive} term row(s) have a non-positive factor, whose logarithm is undefined";
        }
        else
        {
            var n = termRows.Count;
            var logs = new double[3][];
            for (var k = 0; k < 3; k++)
            {
                logs[k] = termRows.Select(w => Math.Log(factorOf[k](w.Terms!))).ToArray();
            }

            var total = new double[n];
            for (var i = 0; i < n; i++)
            {
                total[i] = logs[0][i] + logs[1][i] + logs[2][i];
            }

            var meanTotal = total.Average();
            var variance = total.Sum(v => (v - meanTotal) * (v - meanTotal)) / n;
            if (variance <= 0.0)
            {
                logReason = "ln(EvidenceConfidence product) is constant across the term rows";
            }
            else
            {
                shares = new double[3];
                for (var k = 0; k < 3; k++)
                {
                    var meanK = logs[k].Average();
                    var cov = 0.0;
                    for (var i = 0; i < n; i++)
                    {
                        cov += (logs[k][i] - meanK) * (total[i] - meanTotal);
                    }

                    shares[k] = cov / n / variance;
                }
            }
        }

        // ---- held-term counterfactuals, through the production composition ----
        var heldRows = termRows.Where(w => w.ActualRank is not null && w.Discount is not null).ToList();
        string? heldReason = !counterfactual.Available
            ? "the held-EvidenceConfidence counterfactual is not available (" + counterfactual.NotAvailableReason + ")"
            : heldRows.Count < 2
                ? $"fewer than 2 companies are both term rows and ranked ({heldRows.Count})"
                : null;

        var actualRanks = heldReason is null
            ? RankBy(heldRows, w => w.Snapshot!.OpportunityScore).ToDictionary(x => x.Row, x => x.Rank)
            : null;

        var rows = new List<EvidenceConfidenceTermAttributionRow>(3);
        for (var k = 0; k < 3; k++)
        {
            var rho = SpearmanRankCorrelation.Compute(ec, termRows.Select(w => termOf[k](w.Terms!)).ToList());
            var rhoReason = rho.Degeneracy switch
            {
                SpearmanDegeneracy.None => null,
                SpearmanDegeneracy.TooFewObservations => "undefined: fewer than 2 observations",
                SpearmanDegeneracy.ConstantFirst => "undefined: constant series (EvidenceConfidence)",
                SpearmanDegeneracy.ConstantSecond => "undefined: constant series (" + names[k] + ")",
                _ => "undefined",
            };

            int? ecChanged = null, rankChanged = null, maxAbs = null;
            double? rankChangedShare = null;
            if (actualRanks is not null)
            {
                var heldEc = new Dictionary<WorkingRow, int>(heldRows.Count);
                foreach (var w in heldRows)
                {
                    var t = w.Terms!;
                    heldEc[w] = ScoreSignalMath.EvidenceConfidenceComposition(
                        k == 0 ? medians[0] : t.BestConfidence,
                        k == 1 ? medians[1] : t.BestQualityWeight,
                        k == 2 ? medians[2] : t.DiversityFactor,
                        weights);
                }

                var heldRanks = RankBy(
                        heldRows,
                        w => ScoreSignalMath.OpportunityComposition(w.Snapshot!.TrajectoryScore, heldEc[w], w.Discount!.Value))
                    .ToDictionary(x => x.Row, x => x.Rank);

                ecChanged = heldRows.Count(w => heldEc[w] != w.Snapshot!.EvidenceConfidenceScore);
                rankChanged = heldRows.Count(w => heldRanks[w] != actualRanks[w]);
                rankChangedShare = (double)rankChanged.Value / heldRows.Count;
                maxAbs = heldRows.Max(w => Math.Abs(heldRanks[w] - actualRanks[w]));
            }

            rows.Add(new EvidenceConfidenceTermAttributionRow(
                Term: names[k],
                Median: medians[k],
                RhoWithEvidenceConfidence: rho.Rho,
                RhoUndefinedReason: rhoReason,
                LogVarianceShare: shares?[k],
                EvidenceConfidenceChangedWhenHeld: ecChanged,
                RankChangedWhenHeld: rankChanged,
                RankChangedShareWhenHeld: rankChangedShare,
                MaxAbsRankDeltaWhenHeld: maxAbs));
        }

        var dominant = shares is null
            ? null
            : rows.OrderByDescending(r => r.LogVarianceShare!.Value).First();

        return new EvidenceConfidenceTermAttribution(
            TermRowCount: termRows.Count,
            HeldTermRankedCount: heldReason is null ? heldRows.Count : 0,
            LogVarianceRule: LogVarianceRule,
            LogVarianceUndefinedReason: logReason,
            HeldTermRule: HeldTermRule,
            HeldTermNotAvailableReason: heldReason,
            Terms: rows,
            DominantTerm: dominant?.Term,
            DominantTermLogVarianceShare: dominant?.LogVarianceShare);
    }

    private static EvidenceConfidenceBestSignalProfile BuildBestSignalProfile(IReadOnlyList<WorkingRow> termRows)
    {
        var withBest = termRows.Where(w => w.Best is not null).ToList();
        var denominator = termRows.Count;

        var byValue = withBest
            .GroupBy(w => (w.Terms!.BestConfidence, w.Best!.Pair.Signal.Type, w.Best.Producer))
            .Select(g => new EvidenceConfidenceBestSignalShare(
                g.Key.BestConfidence,
                g.Key.Type,
                g.Key.Producer,
                g.Count(),
                denominator == 0 ? 0.0 : (double)g.Count() / denominator,
                g.Count(w => w.Best!.ComparabilityCapNoted)))
            .OrderByDescending(s => s.BestConfidence)
            .ThenByDescending(s => s.Companies)
            .ThenBy(s => s.SignalType.ToString(), StringComparer.Ordinal)
            .ThenBy(s => s.Producer.ToString(), StringComparer.Ordinal)
            .ToList();

        return new EvidenceConfidenceBestSignalProfile(
            ProducerRuleVersion: SignalProducerRule.Version,
            TieBreakRule: BestSignalTieBreakRule,
            CompaniesWithBestSignal: withBest.Count,
            ByValueTypeProducer: byValue,
            ByProducerAndDirection: CategoryCounts(
                withBest, denominator, w => w.Best!.Producer + " / " + w.Best.Pair.Signal.Direction),
            CompaniesTiedAtBestConfidence: withBest.Count(w => w.Best!.TieCount > 1),
            TiesSpanningSignalTypes: withBest.Count(w => w.Best!.TiedSignalTypes.Count > 1),
            TiesSpanningProducers: withBest.Count(w => w.Best!.TiedProducers.Count > 1),
            UnclassifiedProducer: withBest.Count(w => w.Best!.Producer == SignalProducer.Unclassified));
    }

    private static EvidenceConfidenceAboveMedianProfile BuildAboveMedianProfile(
        IReadOnlyList<WorkingRow> termRows, double? medianEvidenceConfidence)
    {
        if (medianEvidenceConfidence is null)
        {
            return EvidenceConfidenceAboveMedianProfile.Empty(AboveMedianRule);
        }

        var above = termRows
            .Where(w => w.Best is not null && w.Snapshot!.EvidenceConfidenceScore > medianEvidenceConfidence.Value)
            .ToList();
        if (above.Count == 0)
        {
            return EvidenceConfidenceAboveMedianProfile.Empty(AboveMedianRule) with
            {
                MedianEvidenceConfidence = medianEvidenceConfidence,
            };
        }

        var byType = CategoryCounts(above, above.Count, w => w.Best!.Pair.Signal.Type.ToString());
        var byProducer = CategoryCounts(above, above.Count, w => w.Best!.Producer.ToString());
        return new EvidenceConfidenceAboveMedianProfile(
            Rule: AboveMedianRule,
            MedianEvidenceConfidence: medianEvidenceConfidence,
            Companies: above.Count,
            BySignalType: byType,
            ByProducer: byProducer,
            ByDirection: CategoryCounts(above, above.Count, w => w.Best!.Pair.Signal.Direction.ToString()),
            ModalSignalType: byType[0].Category,
            ModalSignalTypeCompanies: byType[0].Companies,
            ModalSignalTypeShare: byType[0].Share,
            ModalProducer: byProducer[0].Category,
            ModalProducerCompanies: byProducer[0].Companies,
            ModalProducerShare: byProducer[0].Share);
    }

    /// <summary>Counts per category, largest first, ties by category name (ordinal) — so "modal" is reproducible.</summary>
    private static List<EvidenceConfidenceCategoryCount> CategoryCounts(
        IReadOnlyList<WorkingRow> rows, int denominator, Func<WorkingRow, string> category) =>
        rows.GroupBy(category, StringComparer.Ordinal)
            .Select(g => new EvidenceConfidenceCategoryCount(
                g.Key, g.Count(), denominator == 0 ? 0.0 : (double)g.Count() / denominator))
            .OrderByDescending(c => c.Companies)
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .ToList();

    /// <summary>The best-confidence signal of one company and its tie accounting.</summary>
    private sealed record BestSignal(
        ScoringSignal Pair,
        SignalProducer Producer,
        bool ComparabilityCapNoted,
        string? Collector,
        int TieCount,
        IReadOnlyList<string> TiedSignalTypes,
        IReadOnlyList<string> TiedProducers);

    /// <summary>
    /// The repo's ONE median (<see cref="ExactMedianInterval.MedianOf"/>: mean of the two central values on an
    /// even count — stated on the artifact because the convention is ambiguous), or NOT RECORDED for an empty set.
    /// </summary>
    internal static double? Median(IReadOnlyList<double> values) =>
        values.Count == 0 ? null : ExactMedianInterval.MedianOf(values);

    /// <summary>The mutable per-company accumulator; projected onto the immutable row at the end.</summary>
    private sealed class WorkingRow
    {
        public required Company Company { get; init; }

        public required EvidenceConfidenceCompanyState State { get; init; }

        public CompanyScoreSnapshot? Snapshot { get; init; }

        public DateTimeOffset? WindowEndUtc { get; init; }

        public string? ScoringConfigVersion { get; init; }

        public int LinkCount { get; init; }

        public int LinkSignalUnresolvable { get; init; }

        public int LinkEvidenceUnresolvable { get; init; }

        public EvidenceConfidenceTerms? Terms { get; init; }

        public bool TermsDisagree { get; init; }

        public bool RecompositionMismatch { get; set; }

        public BestSignal? Best { get; init; }

        /// <summary>The company's notedness discount, recorded when its Opportunity recomposed (ranked rows only).</summary>
        public double? Discount { get; set; }

        public int? CounterfactualOpportunity { get; set; }

        public int? ActualRank { get; set; }

        public int? CounterfactualRank { get; set; }

        public static WorkingRow Excluded(
            Company company,
            EvidenceConfidenceCompanyState state,
            DateTimeOffset? windowEnd,
            string? configVersion,
            int linkCount,
            int signalMissing,
            int evidenceMissing) =>
            new()
            {
                Company = company,
                State = state,
                WindowEndUtc = windowEnd,
                ScoringConfigVersion = configVersion,
                LinkCount = linkCount,
                LinkSignalUnresolvable = signalMissing,
                LinkEvidenceUnresolvable = evidenceMissing,
            };

        public static WorkingRow Included(
            Company company,
            CompanyScoreSnapshot snapshot,
            int linkCount,
            EvidenceConfidenceTerms terms,
            BestSignal? best) =>
            new()
            {
                Company = company,
                State = EvidenceConfidenceCompanyState.Included,
                Snapshot = snapshot,
                WindowEndUtc = snapshot.WindowEndUtc,
                ScoringConfigVersion = snapshot.ScoringConfigVersion,
                LinkCount = linkCount,
                Terms = terms,
                TermsDisagree = terms.Score != snapshot.EvidenceConfidenceScore,
                Best = best,
            };

        public EvidenceConfidenceCompanyRow ToRow()
        {
            var flags = new List<string>();
            if (State != EvidenceConfidenceCompanyState.Included)
            {
                flags.Add(State.ToString());
            }

            if (TermsDisagree)
            {
                flags.Add("TermsDisagreeWithSnapshot");
            }

            if (RecompositionMismatch)
            {
                flags.Add("OpportunityRecompositionMismatch");
            }

            return new EvidenceConfidenceCompanyRow(
                CompanyId: Company.Id,
                Ticker: Company.Ticker,
                Name: Company.Name,
                FollowingTier: Company.FollowingTier,
                State: State,
                SnapshotWindowEndUtc: WindowEndUtc,
                ScoringConfigVersion: ScoringConfigVersion,
                LinkCount: LinkCount,
                LinkSignalUnresolvable: LinkSignalUnresolvable,
                LinkEvidenceUnresolvable: LinkEvidenceUnresolvable,
                EvidenceConfidencePersisted: Snapshot?.EvidenceConfidenceScore,
                EvidenceConfidenceRecomputed: Terms?.Score,
                TermsDisagreeWithSnapshot: TermsDisagree,
                BestConfidence: Terms?.BestConfidence,
                BestQualityWeight: Terms?.BestQualityWeight,
                DistinctSourceTypes: Terms?.DistinctSourceTypes,
                DiversityFactor: Terms?.DiversityFactor,
                Trajectory: Snapshot?.TrajectoryScore,
                Attention: Snapshot?.AttentionScore,
                Opportunity: Snapshot?.OpportunityScore,
                OpportunityRecompositionMismatch: RecompositionMismatch,
                CounterfactualOpportunity: CounterfactualOpportunity,
                ActualRank: ActualRank,
                CounterfactualRank: CounterfactualRank,
                RankDelta: ActualRank is { } a && CounterfactualRank is { } c ? c - a : null,
                Flags: flags,
                BestConfidenceSignalId: Best?.Pair.Signal.Id,
                BestConfidenceSignalType: Best?.Pair.Signal.Type,
                BestConfidenceSignalDirection: Best?.Pair.Signal.Direction,
                BestConfidenceSignalStrength: Best?.Pair.Signal.Strength,
                BestConfidenceProducer: Best?.Producer,
                BestConfidenceEvidenceSourceType: Best?.Pair.Evidence.SourceType,
                BestConfidenceCollector: Best?.Collector,
                BestConfidenceEvidenceTitle: Best?.Pair.Evidence.Title,
                BestConfidenceObservedAtUtc: Best?.Pair.Signal.ObservedAtUtc,
                BestConfidenceComparabilityCapNoted: Best?.ComparabilityCapNoted,
                BestConfidenceTieCount: Best?.TieCount,
                BestConfidenceTiedSignalTypes: Best?.TiedSignalTypes,
                BestConfidenceTiedProducers: Best?.TiedProducers);
        }
    }
}
