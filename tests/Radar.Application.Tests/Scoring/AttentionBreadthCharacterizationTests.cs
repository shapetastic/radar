using Microsoft.Extensions.DependencyInjection;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.DependencyInjection;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// <b>CHARACTERIZATION test (spec 124 — the Option-A diagnosis of spec 122). It documents CURRENT
/// behaviour; it is deliberately NOT a red/failing test and nothing here is asserted as "correct".</b>
///
/// <para>
/// Spec 122's open decision (b) is that Radar's <c>AttentionScore</c> appears to UNDERSTATE how noticed a
/// company is: a name covered by many outlets around ONE event scores a low Attention, indistinguishable
/// from a genuinely under-followed name. <c>AttentionScore</c> is
/// <c>100·reach/(reach + AttentionHalfSaturation)</c> where
/// <c>reach = Σ tierWeight(distinct third-party SourceName) + MediaReachWeight·mediaSignalCount</c>
/// (<see cref="RadarScoreFormulaV7"/>) — and crucially the formula is handed the signal set AFTER
/// <see cref="ScoringEngine"/> applies the spec-109 <see cref="MediaAttentionCollapse"/>, which keeps ONE
/// representative per same-event bucket and discards the distinct-publisher identity of the rest.
/// </para>
///
/// <para>
/// The same 15 distinct third-party publishers (7 outlets in the curated
/// <see cref="AttentionSourceTierOptions.Default"/> "Genuine" tier at weight 1.0, and 8 realistic outlets
/// absent from the tier map that therefore resolve to <c>UnknownWeight</c> 0.25) are run three ways so the
/// collapse effect and the tiering effect are separately visible:
/// </para>
/// <list type="number">
/// <item><b>Single-event burst</b> — all 15 observed inside one <c>MediaCollapseOptions.EventWindow</c>,
/// through the REAL engine + collapse + formula + configured tier map. Isolates the collapse: 15 publishers
/// reduce to the single earliest representative, so <c>weightedBreadth</c> sees ONE <c>SourceName</c> and
/// <c>mediaSignalCount</c> is 1.</item>
/// <item><b>Spread coverage (control)</b> — the SAME 15 publishers, each on its own event more than an
/// <c>EventWindow</c> apart, through the same real path. Nothing collapses, so this is the breadth the
/// pipeline can express for 15 publishers under the current tier map.</item>
/// <item><b>Pre-collapse isolation</b> — the single-event set from (1) handed DIRECTLY to
/// <see cref="RadarScoreFormulaV7"/>, bypassing ONLY the collapse (same weights, same tier map). Separates
/// "the collapse removed the breadth" from "the tier map priced the breadth low".</item>
/// </list>
///
/// <para>
/// The pinned numbers are the diagnosis evidence. <b>Changing them is the spec-122 decision</b> (an AD-6-gated
/// formula-structure question — counting pre-collapse distinct publishers, or a recency-gated/velocity term),
/// <b>not</b> a bug fix: if a future slice moves these values it must do so deliberately, under that decision.
/// Generic by construction — no ticker or company special-casing; AEHR is only the motivating example.
/// Deterministic (AD-3): no network, no clock, no randomness.
/// </para>
/// </summary>
public sealed class AttentionBreadthCharacterizationTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A window long enough to hold the spread-coverage scenario's 15 events spaced beyond the collapse
    /// window. Test configuration only (<see cref="ScoringOptions.Window"/> is tunable pipeline scaffolding,
    /// not a formula weight) — both engine scenarios use it so the two numbers are directly comparable.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromDays(90);

    /// <summary>Days between consecutive events in the spread scenario (&gt; the 3-day default EventWindow).</summary>
    private const int SpreadEventGapDays = 4;

    /// <summary>Hours between consecutive publishers in the single-event burst (15 × 2h = 28h &lt; 3 days).</summary>
    private const int BurstPublisherGapHours = 2;

    /// <summary>
    /// 15 distinct third-party publishers covering the same company. The first entry is the EARLIEST in the
    /// burst scenario and is therefore the surviving collapse representative — realistically an outlet that
    /// is not on the curated genuine list, so it resolves to <c>UnknownWeight</c>. Seven entries are on
    /// <see cref="AttentionSourceTierOptions.Default"/>'s "Genuine" tier (weight 1.0); the remaining eight are
    /// absent from every tier (weight 0.25). None are on the "Mill" denylist.
    /// </summary>
    private static readonly string[] Publishers =
    {
        "Regional Business Journal",   // unknown → 0.25 (earliest ⇒ the collapse representative)
        "Reuters",                     // genuine → 1.0
        "Bloomberg",                   // genuine → 1.0
        "The Wall Street Journal",     // genuine → 1.0
        "CNBC",                        // genuine → 1.0
        "Associated Press",            // genuine → 1.0
        "Financial Times",             // genuine → 1.0
        "SpaceNews",                   // genuine → 1.0
        "Industry Wire Daily",         // unknown → 0.25
        "Semiconductor Report",        // unknown → 0.25
        "The Morning Ledger",          // unknown → 0.25
        "Capital Markets Today",       // unknown → 0.25
        "TechSector Weekly",           // unknown → 0.25
        "The Evening Dispatch",        // unknown → 0.25
        "Global Trade Review",         // unknown → 0.25
    };

    // ---- The pinned characterization numbers (spec 124) ----

    /// <summary>
    /// 15 publishers, ONE event: the collapse keeps one representative (an unknown-tier outlet, 0.25), so
    /// reach = 0.25 + 0.10·1 = 0.35 and Attention = 100·0.35/(0.35+3.0) = 10.4 → <b>10</b>.
    /// </summary>
    private const int SingleEventBurstAttention = 10;

    /// <summary>
    /// The SAME 15 publishers across 15 separate events: nothing collapses, so
    /// reach = (7·1.0 + 8·0.25) + 0.10·15 = 10.5 and Attention = 100·10.5/(10.5+3.0) = 77.8 → <b>78</b>.
    /// </summary>
    private const int SpreadCoverageAttention = 78;

    [Fact]
    public async Task SingleEventBurst_FifteenDistinctPublishers_CollapseToOne_YieldsLowAttention()
    {
        var observations = Enumerable.Range(0, Publishers.Length)
            .Select(i => WindowEnd.AddDays(-30).AddHours(i * BurstPublisherGapHours))
            .ToArray();

        var attention = await ScoreAttentionThroughRealEngineAsync(observations);

        // CHARACTERIZATION: 15 distinct genuine-and-unknown outlets covering ONE event express the breadth of
        // exactly ONE publisher, because the spec-109 collapse hands the formula a single representative.
        Assert.Equal(SingleEventBurstAttention, attention);
    }

    [Fact]
    public async Task SpreadCoverage_SameFifteenPublishersAcrossDistinctEvents_YieldsMuchHigherAttention()
    {
        // Control: identical publisher set, but each on its own event more than EventWindow apart, so the
        // collapse is a no-op and every distinct SourceName reaches the breadth sum.
        var observations = Enumerable.Range(0, Publishers.Length)
            .Select(i => WindowEnd.AddDays(-(Publishers.Length - i) * SpreadEventGapDays))
            .ToArray();

        var attention = await ScoreAttentionThroughRealEngineAsync(observations);

        Assert.Equal(SpreadCoverageAttention, attention);
    }

    [Fact]
    public void PreCollapse_SingleEventBurstStraightToFormula_MatchesSpreadCoverage()
    {
        // Isolation: the SAME single-event burst as scenario 1, handed directly to the formula so ONLY the
        // spec-109 collapse is bypassed (identical weights and identical configured tier map). It reproduces
        // the spread-coverage number exactly, which attributes the entire 10 → 78 gap to the collapse: the
        // tier map is not what suppresses the burst, it only sets the ceiling (78 rather than the 85 that 15
        // fully-genuine outlets would reach).
        var sourceWeights = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);
        var formula = new RadarScoreFormulaV7(new ScoringWeights(), sourceWeights);

        var companyId = Guid.NewGuid();
        var signals = Enumerable.Range(0, Publishers.Length)
            .Select(i => BuildMediaAttentionPair(
                companyId,
                Publishers[i],
                WindowEnd.AddDays(-30).AddHours(i * BurstPublisherGapHours)))
            .Select(pair => new ScoringSignal(pair.Signal, pair.Evidence))
            .ToArray();

        var result = formula.Compute(new ScoringInput(
            CompanyId: companyId,
            WindowStartUtc: WindowEnd - Window,
            WindowEndUtc: WindowEnd,
            Signals: signals,
            PreviousSignals: Array.Empty<Signal>()));

        Assert.Equal(SpreadCoverageAttention, result.Components.AttentionScore);
    }

    /// <summary>
    /// Seeds one Approved <see cref="SignalType.MediaAttention"/> signal (over third-party
    /// <see cref="EvidenceSourceType.NewsArticle"/> evidence) per publisher at the supplied observation time,
    /// then scores the company through the REAL wired graph — <see cref="ScoringEngine"/>, the real
    /// <see cref="MediaAttentionCollapse"/> on its default options, <see cref="RadarScoreFormulaV7"/> on
    /// default <see cref="ScoringWeights"/>, and <see cref="ConfiguredAttentionSourceWeights"/> over
    /// <see cref="AttentionSourceTierOptions.Default"/> — and returns the resulting AttentionScore. Reuses the
    /// DI composition (rather than a hand-built harness) so the characterization pins the production path.
    /// </summary>
    private static async Task<int> ScoreAttentionThroughRealEngineAsync(
        IReadOnlyList<DateTimeOffset> observedAtUtc)
    {
        var signalStoreDir = Path.Combine(Path.GetTempPath(), $"radar-signals-{Guid.NewGuid():N}");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemoryRadarPersistence();
        // Registered BEFORE the application services so it wins over their TryAddSingleton default (30 days).
        services.AddSingleton(new ScoringOptions { Window = Window });
        services.AddRadarApplicationServices();
        // The engine reads the previous window from the on-disk signal store; it is empty here and only feeds
        // SignalVelocity, never Attention.
        services.AddFileSignalStore(signalStoreDir);

        await using var provider = services.BuildServiceProvider();
        try
        {
            var evidenceRepository = provider.GetRequiredService<IEvidenceRepository>();
            var signalRepository = provider.GetRequiredService<ISignalRepository>();
            var engine = provider.GetRequiredService<IScoringEngine>();

            var companyId = Guid.NewGuid();
            for (var i = 0; i < Publishers.Length; i++)
            {
                var (signal, evidence) = BuildMediaAttentionPair(companyId, Publishers[i], observedAtUtc[i]);
                await evidenceRepository.AddIfNewAsync(evidence, CancellationToken.None);
                await signalRepository.AddAsync(signal, CancellationToken.None);
            }

            var result = await engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
            return result.Snapshot.AttentionScore;
        }
        finally
        {
            if (Directory.Exists(signalStoreDir))
            {
                Directory.Delete(signalStoreDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// One publisher's coverage: third-party <see cref="EvidenceSourceType.NewsArticle"/> evidence carrying the
    /// real outlet as its <c>SourceName</c> (the shape <c>NewsAttentionCollector</c> produces to preserve
    /// distinct-outlet breadth) plus its Approved, direction-Neutral <see cref="SignalType.MediaAttention"/>
    /// signal.
    /// </summary>
    private static (Signal Signal, EvidenceItem Evidence) BuildMediaAttentionPair(
        Guid companyId, string publisher, DateTimeOffset observedAtUtc)
    {
        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithSourceName(publisher)
            .WithQuality(EvidenceQuality.Medium)
            .WithContentHash(Guid.NewGuid().ToString("N"))
            .Build();

        var signal = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithCompanyId(companyId)
            .WithType(SignalType.MediaAttention)
            .WithDirection(SignalDirection.Neutral)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(observedAtUtc)
            .Build();

        return (signal, evidence);
    }
}
