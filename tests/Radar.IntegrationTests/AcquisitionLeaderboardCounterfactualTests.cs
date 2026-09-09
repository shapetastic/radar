using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Acquisitions;
using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Scoring;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.Infrastructure.Sources;

using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.EntityResolution;
using Radar.Application.Prices;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// SPEC 217 §3 — the READ-ONLY, OFFLINE, PAIRED RECOMPUTATION of the strategy leaderboard with and without
/// the recognised MarineMax acquisition, so the PR body can state what the new exclusion actually costs
/// rather than asserting that it is small.
/// <para>
/// <b>Why paired and not "before the merge / after the merge".</b> Comparing two nightly artifacts is
/// confounded — the store grows between them. Here ONE store, ONE set of score series, ONE price side, ONE
/// frozen benchmark and ONE options object are held fixed, and the ONLY thing that differs between the two
/// arms is the <see cref="PendingAcquisitions"/> projection handed to the harness. It is the spec-196 §7
/// counterfactual pattern, reused rather than reinvented.
/// </para>
/// <para>
/// <b>Nothing is written and nothing is seeded into the accrued store.</b> The acquisition is constructed
/// IN MEMORY from the publicly filed facts (HZO, announced 2026-08-10, acquirer Safe Harbor Marinas,
/// $53.00 per share in cash, accession 0001193125-26-341302); the data root is opened read-only, and no
/// score, snapshot, artifact or acquisitions file is produced. It needs NO network.
/// </para>
/// <para>
/// <b>ENV-GATED so CI stays green and fast</b> (the <c>AttentionPolicyCounterfactualTests</c> precedent —
/// skipped with a NAMED reason, never silently). Set <c>RADAR_ACQ_LEADERBOARD_DATA_ROOT</c> to a Radar data
/// root (the one holding <c>companies.json</c>, <c>scores/</c>, <c>prices/</c> and
/// <c>efficacy/benchmark-universe-v1.json</c>). The output is markdown on the test log, ready to paste into
/// a PR body. Deterministic (AD-3) in everything but the store it is pointed at.
/// </para>
/// </summary>
public sealed class AcquisitionLeaderboardCounterfactualTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_ACQ_LEADERBOARD_DATA_ROOT";

    internal const string SkipReason =
        "Spec 217 §3 paired leaderboard recomputation: set " + DataRootVariable
            + " to a Radar data root (companies.json, scores/, prices/, efficacy/) to run it. It is "
            + "read-only and needs no network.";

    /// <summary>The data root under measurement, or null when the variable is unset/not a directory.</summary>
    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    /// <summary>
    /// The ONE recognised acquisition in the live universe today, from the public filing: MarineMax (HZO),
    /// 8-K accession 0001193125-26-341302 filed 2026-08-10, Safe Harbor Marinas at $53.00 per share in cash.
    /// The company id is resolved from the seed at run time — never hard-coded — so a re-seeded universe
    /// cannot silently point this at the wrong company.
    /// </summary>
    private const string TargetTicker = "HZO";

    private static readonly DateTimeOffset AnnouncedUtc = new(2026, 8, 10, 8, 0, 12, TimeSpan.Zero);

    private const string Accession = "0001193125-26-341302";

    [AcquisitionLeaderboardFact]
    public async Task PairedRecomputation_BeforeAndAfterTheCorporateActionExclusion()
    {
        var root = DataRoot()!;
        var ct = CancellationToken.None;

        await using var provider = BuildReadOnlyComposition(root);
        // The seed is hydrated into the in-memory repository exactly as a live run hydrates it — no second
        // seed reader, and nothing is written back to disk.
        await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);
        var companies = provider.GetRequiredService<ICompanyRepository>();
        var prices = provider.GetRequiredService<IPriceHistoryStore>();
        var builder = provider.GetRequiredService<EfficacyDatasetBuilder>();
        var universeSource = provider.GetRequiredService<IBenchmarkUniverseSource>();

        var all = await companies.GetAllAsync(ct);
        var target = all.FirstOrDefault(c =>
            string.Equals(c.Ticker, TargetTicker, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(target);

        var acquisitions = new PendingAcquisitions(new AcquisitionStoreReadResult(
            [
                new PendingAcquisitionRecord(
                    Id: PendingAcquisitionRecord.IdentityFor(
                        AcquisitionAgreementScan.Version, target!.Id, Accession),
                    CompanyId: target.Id,
                    Accession: Accession,
                    EvidenceId: Guid.Empty,
                    AnnouncedOnUtc: AnnouncedUtc,
                    AcquirerName: "Safe Harbor Marinas, LLC",
                    ConsiderationPerShare: "53.00",
                    ConsiderationCurrency: "$",
                    ConsiderationKind: AcquisitionConsiderationKind.Cash,
                    ConsiderationQuote: "(supplied by the harness from the public filing)",
                    TargetQuote: "(supplied by the harness from the public filing)",
                    ScanVersion: AcquisitionAgreementScan.Version,
                    Verification: AcquisitionVerification.Verbatim),
            ],
            Unreadable: 0));

        // Every strategy series on disk: the primary at scores/, each other at scores/strategies/{name}/.
        var series = new List<StrategyScoreSeries>();
        var scoresRoot = Path.Combine(root, "scores");
        series.Add(new StrategyScoreSeries(
            "default", await builder.BuildAsync(StoreAt(scoresRoot), ct)));

        var strategiesRoot = Path.Combine(scoresRoot, "strategies");
        if (Directory.Exists(strategiesRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(strategiesRoot)
                .OrderBy(d => d, StringComparer.Ordinal))
            {
                series.Add(new StrategyScoreSeries(
                    Path.GetFileName(directory), await builder.BuildAsync(StoreAt(directory), ct)));
            }
        }

        var universe = await universeSource.ReadAsync(ct);
        Assert.NotNull(universe);

        var bars = new Dictionary<string, IReadOnlyList<PriceBar>>(StringComparer.Ordinal);
        foreach (var member in universe!.Members)
        {
            var history = await prices.ReadAsync(member.PriceSeriesKey, ct);
            if (history is { Bars.Count: > 0 })
            {
                bars[member.PriceSeriesKey] = history.Bars;
            }
        }

        var options = StrategyComparisonOptions.Default;
        var harness = new StrategyComparisonHarness();

        // ARM 1 — excess-vs-universe-v1 + observation-eligibility-v1 (the pre-217 rules).
        var before = harness.Compare(
            series, options, new UniverseBenchmark(universe, bars), PendingAcquisitions.None);

        // ARM 2 — excess-vs-universe-v2 + observation-eligibility-v2, differing ONLY in the projection.
        var after = harness.Compare(
            series, options, new UniverseBenchmark(universe, bars, acquisitions), acquisitions);

        output.WriteLine(Render(before, after, target.Name, options));

        // The measurement is the deliverable; these are the invariants it must not violate.
        Assert.Equal(before.StrategiesConsidered, after.StrategiesConsidered);
        Assert.All(after.Rows, r => Assert.True(r.ObservationsCorporateActionInWindow >= 0));
        Assert.All(before.Rows, r => Assert.Equal(0, r.ObservationsCorporateActionInWindow));
    }

    private static IScoreSnapshotFileStore StoreAt(string directory) =>
        new FileScoreSnapshotStore(
            new FileScoreSnapshotStoreOptions { RootDirectory = directory },
            NullLogger<FileScoreSnapshotStore>.Instance);

    /// <summary>
    /// A composition that can only READ: the seed, the durable score snapshots, the price store and the
    /// frozen benchmark artifact. No score repository, no run store, no artifact store, no collector — so
    /// there is nothing this harness could write even by accident.
    /// </summary>
    private static ServiceProvider BuildReadOnlyComposition(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        services.AddLocalFileCompanySeed(Path.Combine(root, "companies.json"));
        services.AddFilePriceHistoryStore(Path.Combine(root, "prices"));
        services.AddFileBenchmarkUniverseSource(Path.Combine(root, "efficacy"));
        services.AddSingleton(new FileScoreSnapshotStoreOptions
        {
            RootDirectory = Path.Combine(root, "scores"),
        });
        services.AddSingleton<IScoreSnapshotFileStore, FileScoreSnapshotStore>();
        services.AddSingleton<EfficacyDatasetBuilder>();
        return services.BuildServiceProvider();
    }

    private static string Render(
        StrategyLeaderboard before,
        StrategyLeaderboard after,
        string targetName,
        StrategyComparisonOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("### Spec 217 §3 — leaderboard BEFORE and AFTER, same store, one changed input\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Horizon {options.ForwardHorizonDays}d · exit tolerance {options.ExitToleranceDays}d · hold-out {options.HoldOutFraction:P0} · minimum observations {options.MinimumObservations}.\n");
        sb.Append(CultureInfo.InvariantCulture, $"BEFORE = observation-eligibility-v1 + excess-vs-universe-v1. AFTER = {ObservationEligibility.Version} + {UniverseBenchmark.ExcessRuleVersion}, with the ONE recognised acquisition ({targetName}, announced 2026-08-10) projected.\n\n");

        sb.Append("| strategy | rank before → after | in-sample rho before → after | in-sample 95% CI after | oos rho before → after | oos 95% CI after | oos obs before → after | CorporateActionInWindow |\n");
        sb.Append("| --- | --- | --- | --- | --- | --- | --- | ---: |\n");

        var names = before.Rows.Select(r => r.StrategyName)
            .Concat(after.Rows.Select(r => r.StrategyName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var name in names)
        {
            var b = before.Rows.FirstOrDefault(r => r.StrategyName == name);
            var a = after.Rows.FirstOrDefault(r => r.StrategyName == name);
            sb.Append(CultureInfo.InvariantCulture,
                $"| {name} | {Rank(b)} → {Rank(a)} | {Rho(b?.InSample)} → {Rho(a?.InSample)} | {Ci(a?.InSample)} | {Rho(b?.OutOfSample)} → {Rho(a?.OutOfSample)} | {Ci(a?.OutOfSample)} | {Obs(b?.OutOfSample)} → {Obs(a?.OutOfSample)} | {a?.ObservationsCorporateActionInWindow.ToString(CultureInfo.InvariantCulture) ?? "—"} |\n");
        }

        sb.Append('\n');
        sb.Append(CultureInfo.InvariantCulture, $"Lead-vs-comparators ordering BEFORE: {Order(before)}\n");
        sb.Append(CultureInfo.InvariantCulture, $"Lead-vs-comparators ordering AFTER:  {Order(after)}\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Dropped from ranking — before {before.DroppedStrategies.Count}, after {after.DroppedStrategies.Count}.\n");
        foreach (var drop in after.DroppedStrategies)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- AFTER dropped: {drop.StrategyName} ({drop.Reason}; in {drop.InSampleObservations}, oos {drop.OutOfSampleObservations})\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"\nAs-of dates before {before.Windows.TotalAsOfDates} (in {before.Windows.InSampleAsOfDates} / oos {before.Windows.OutOfSampleAsOfDates}); after {after.Windows.TotalAsOfDates} (in {after.Windows.InSampleAsOfDates} / oos {after.Windows.OutOfSampleAsOfDates}).\n");

        if (after.Benchmark is { } benchmark)
        {
            var daysWithExclusions = benchmark.Days.Count(d => d.PendingAcquisitionExcludedMembers > 0);
            sb.Append(CultureInfo.InvariantCulture, $"Benchmark ({UniverseBenchmark.ExcessRuleVersion}): {benchmark.MemberCount} frozen members; {daysWithExclusions} of {benchmark.Days.Count} as-of dates had a pending-acquisition member removed from the peer mean.\n");
        }

        return sb.ToString();
    }

    private static string Rank(StrategyLeaderboardRow? row) =>
        row is null ? "dropped" : row.Rank.ToString(CultureInfo.InvariantCulture);

    private static string Rho(StrategyWindowMetric? metric) =>
        metric is null ? "—" : metric.Correlation.Rho.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string Ci(StrategyWindowMetric? metric) =>
        metric is null
            ? "—"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{metric.Correlation.LowerBound:0.0000} to {metric.Correlation.UpperBound:0.0000}");

    private static string Obs(StrategyWindowMetric? metric) =>
        metric is null ? "—" : metric.Coverage.Observations.ToString(CultureInfo.InvariantCulture);

    private static string Order(StrategyLeaderboard leaderboard) =>
        leaderboard.Rows.Count == 0
            ? "(nothing ranked)"
            : string.Join(" > ", leaderboard.Rows.Select(r => r.StrategyName));
}

/// <summary>Runs the spec-217 §3 recomputation only when a data root is configured; otherwise skips with a named reason.</summary>
public sealed class AcquisitionLeaderboardFactAttribute : FactAttribute
{
    public AcquisitionLeaderboardFactAttribute()
    {
        if (AcquisitionLeaderboardCounterfactualTests.DataRoot() is null)
        {
            Skip = AcquisitionLeaderboardCounterfactualTests.SkipReason;
        }
    }
}
