using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Domain.Scoring;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 147 — a <c>score</c> pass knows the collector VOCABULARY without gaining the ability to collect.
/// Covers the three symptoms spec 144 + 146 composed into: a v9 collector-channel strategy that could not
/// start, snapshots that recorded a collector set they never had, and the inverted ran/did-not-run split.
/// </summary>
public sealed class CollectorVocabularyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"radar-vocab-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>Every file-store root pointed into this test's own temp directory, so nothing writes cruft.</summary>
    private (string Key, string Value)[] TempDirectories() =>
    [
        ("Radar:EvidenceSourceDirectory", Path.Combine(_root, "evidence")),
        ("Radar:EvidenceRawDirectory", Path.Combine(_root, "evidence-raw")),
        ("Radar:SignalsDirectory", Path.Combine(_root, "signals")),
        ("Radar:ScoresDirectory", Path.Combine(_root, "scores")),
        ("Radar:ReportDirectory", Path.Combine(_root, "reports")),
        ("Radar:RunsDirectory", Path.Combine(_root, "runs")),
        ("Radar:ScoringConfigsDirectory", Path.Combine(_root, "scoring-configs")),
    ];

    // ---- §1 the vocabulary is derived from ONE table, which cannot drift from the registrations ---------

    /// <summary>
    /// THE anti-drift test. For every kind in the composition root's table, a provider configured with that
    /// kind must register a collector whose <c>CollectorName</c> is the name the table records — and the
    /// registered set must be EXACTLY what the vocabulary claims. Without this, the vocabulary a score pass
    /// records (and validates v9 channels against) could claim a name no registered collector has, which is
    /// worse than the failure spec 147 replaces: the guard would then pass on a collector that cannot run.
    /// </summary>
    [Fact]
    public void EveryTableEntry_RegistersACollectorWithExactlyTheRecordedName()
    {
        Assert.NotEmpty(RadarWorkerServices.CollectorKindTable);

        foreach (var (kind, expectedName) in RadarWorkerServices.CollectorKindTable)
        {
            // The SEC kinds fail fast without a User-Agent; supplying one costs no request (construction only
            // configures the typed HttpClient — nothing is fetched by resolving the collector).
            using var provider = BuildProvider([
                ("Radar:Collectors:0", kind),
                ("Radar:Sec:UserAgent", "Radar Test test@example.com"),
                ("Radar:SecForm4:UserAgent", "Radar Test test@example.com"),
                ("Radar:Sec13DG:UserAgent", "Radar Test test@example.com"),
                .. TempDirectories(),
            ]);

            // NOTE the configuration binder APPENDS bound entries onto RadarWorkerOptions.Collectors' default
            // (["rss"]) rather than replacing it, so "rss" is always present as well. That is pre-existing
            // behaviour and irrelevant here — what matters is that the kind under test registers ITS collector
            // under the name the table records.
            var registered = provider.GetServices<IEvidenceCollector>().ToList();
            Assert.Single(registered, c => string.Equals(c.CollectorName, expectedName, StringComparison.Ordinal));

            // …and the vocabulary is EXACTLY the registered set, name for name. This is the drift guard: a
            // table entry whose recorded name did not match its collector would fail here.
            Assert.Equal(
                registered.Select(c => c.CollectorName).Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal),
                provider.GetRequiredService<EnabledCollectorVocabulary>().CollectorNames);
        }
    }

    /// <summary>
    /// The valid-kind list quoted in every <c>Radar:Collectors</c> failure message is RENDERED from the same
    /// table, so a new collector cannot be added to one and forgotten in the other.
    /// </summary>
    [Fact]
    public void FailFastMessages_ListExactlyTheTablesKinds()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(("Radar:Collectors:0", "bogus")));

        Assert.Contains(RadarWorkerServices.CollectorKindMessageList, ex.Message, StringComparison.Ordinal);

        foreach (var (kind, _) in RadarWorkerServices.CollectorKindTable)
        {
            Assert.Contains($"\"{kind}\"", ex.Message, StringComparison.Ordinal);
        }

        // The message lists the table and nothing else: one quoted token per table entry.
        Assert.Equal(
            RadarWorkerServices.CollectorKindTable.Count,
            RadarWorkerServices.CollectorKindMessageList.Count(c => c == '"') / 2);
    }

    // ---- §2 provenance: full/collect unchanged, score marked, never empty ------------------------------

    [Fact]
    public void FullMode_RecordsExactlyThePreSpec147ProvenanceString()
    {
        using var provider = BuildProvider([
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "usaspending"),
            .. TempDirectories(),
        ]);

        // Byte-identical to what a full run recorded before spec 147: the bare CSV, no second segment.
        Assert.Equal(
            "collectors=RssPressReleaseCollector,usaspending;",
            provider.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance());
    }

    [Fact]
    public void CollectMode_RecordsExactlyThePreSpec147ProvenanceString()
    {
        using var provider = BuildProvider([
            ("Radar:RunMode", "collect"),
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "usaspending"),
            .. TempDirectories(),
        ]);

        Assert.Equal(
            "collectors=RssPressReleaseCollector,usaspending;",
            provider.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance());
    }

    [Fact]
    public void ReplayMode_IsDeliberatelyNotMarked_SoReplayStaysComparableToForward()
    {
        // Replay registers REAL collectors and spec 139's replay ⊆ forward invariant compares a replay
        // snapshot against a forward one FIELD FOR FIELD. Marking replay would break it, so it is left alone.
        using var provider = BuildProvider([
            ("Radar:RunMode", "replay"),
            ("Radar:Replay:From", "2026-05-01"),
            ("Radar:Replay:To", "2026-05-03"),
            ("Radar:Collectors:0", "rss"),
            ("Radar:ReplayDirectory", Path.Combine(_root, "replays")),
            .. TempDirectories(),
        ]);

        Assert.Equal(
            "collectors=RssPressReleaseCollector;",
            provider.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance());
    }

    [Fact]
    public void ScoreMode_RecordsTheConfiguredVocabulary_AndMarksThatNothingWasCollected()
    {
        // ⛔ The serious symptom, fixed: this used to record "collectors=;" — a claim that no collector
        // existed — over evidence a collect pass had genuinely gathered from these two.
        using var provider = BuildProvider([
            ("Radar:RunMode", "score"),
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "usaspending"),
            .. TempDirectories(),
        ]);

        var descriptor = provider.GetRequiredService<ISignalSourceDescriptor>();

        Assert.Equal(
            "collectors=RssPressReleaseCollector,usaspending;collection=none-this-pass;",
            descriptor.CollectionProvenance());
        Assert.Equal(["RssPressReleaseCollector", "usaspending"], descriptor.EnabledCollectors());

        // …and it is still true that NO collector is constructed. The vocabulary holds strings.
        Assert.Empty(provider.GetServices<IEvidenceCollector>());
    }

    [Fact]
    public void ScoreMode_WithNoCollectorsConfigured_StillRecordsANonEmptyDistinguishableProvenance()
    {
        // The empty-vocabulary edge: "no collector is configured" and "nothing was collected in this pass"
        // are both true here, and the record says so without collapsing to the old ambiguous "collectors=;".
        using var provider = BuildProvider([
            ("Radar:RunMode", "score"),
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "usaspending"),
            .. TempDirectories(),
        ]);

        // (Config binding cannot produce an EMPTY Radar:Collectors — the binder keeps the default when the
        // section has no indexed children — so the empty-vocabulary case is asserted at the descriptor in
        // SignalSourceDescriptorTests. What is asserted here is that a score pass NEVER records an empty
        // provenance string.)
        var provenance = provider.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance();

        Assert.NotEmpty(provenance);
        Assert.NotEqual("collectors=;", provenance);
        Assert.Contains("collection=none-this-pass;", provenance, StringComparison.Ordinal);
    }

    [Fact]
    public void ScoreAndFullModes_StillStampTheSameFingerprint_TheMarkerIsHashedIntoNothing()
    {
        (string, string)[] shared =
        [
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "usaspending"),
        ];

        using var full = BuildProvider([.. shared, .. TempDirectories()]);
        using var score = BuildProvider([("Radar:RunMode", "score"), .. shared, .. TempDirectories()]);

        Assert.Equal(
            full.GetRequiredService<IScoringStrategyFactory>().Primary.Engine.EffectiveConfig.Fingerprint,
            score.GetRequiredService<IScoringStrategyFactory>().Primary.Engine.EffectiveConfig.Fingerprint);

        // The provenance strings differ — that is the whole point — while the identity does not.
        Assert.NotEqual(
            full.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance(),
            score.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance());
    }

    // ---- §3 the typo guard is exactly as strong, in score mode too ------------------------------------

    private (string Key, string Value)[] V9Strategy(string channelCollector) =>
    [
        ("Radar:Strategies:0:Name", "patents-led"),
        ("Radar:Strategies:0:Formula", "radar-formula-v9"),
        ("Radar:Strategies:0:Channels:0:Name", "patents"),
        ("Radar:Strategies:0:Channels:0:Collectors:0", channelCollector),
        ("Radar:Strategies:0:Channels:0:Weight", "1.0"),
        ("Radar:Strategies:0:Channels:0:Saturation", "3"),
        ("Radar:PrimaryStrategy", "patents-led"),
        .. TempDirectories(),
    ];

    [Theory]
    [InlineData("bogus-collector")]
    [InlineData("Patents")] // mis-cased: matched EXACTLY, so a near-miss fails rather than scoring 0 forever
    public void ScoreMode_StillRejectsAChannelNamingAnUnregisteredCollector(string channelCollector)
    {
        using var provider = BuildProvider([
            ("Radar:RunMode", "score"),
            ("Radar:Collectors:0", "patents"),
            .. V9Strategy(channelCollector),
        ]);

        // The guard runs when the runtimes are forced — which StrategyIdentityGuard does as the very first
        // statement of the score pass, so a typo costs nothing.
        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IScoringStrategyFactory>().Runtimes);

        Assert.Contains(channelCollector, ex.Message, StringComparison.Ordinal);
        Assert.Contains("patents-led", ex.Message, StringComparison.Ordinal);
        // It reports the real vocabulary, not "(none)" — the pre-147 message in this mode.
        Assert.DoesNotContain("(none)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FullMode_StillRejectsAChannelNamingAnUnregisteredCollector()
    {
        using var provider = BuildProvider([
            ("Radar:Collectors:0", "patents"),
            .. V9Strategy("bogus-collector"),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IScoringStrategyFactory>().Runtimes);

        Assert.Contains("bogus-collector", ex.Message, StringComparison.Ordinal);
    }

    // ---- the headline acceptance criterion ------------------------------------------------------------

    /// <summary>
    /// A <c>radar-formula-v9</c> collector-channel strategy STARTS AND SCORES under <c>RunMode=score</c> —
    /// which is exactly how spec 140 will want to run strategies. It resolves, it produces a snapshot, and
    /// that snapshot carries the truthful provenance.
    /// </summary>
    [Fact]
    public async Task ScoreMode_V9CollectorChannelStrategy_StartsAndScores()
    {
        using var provider = BuildProvider([
            ("Radar:RunMode", "score"),
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "patents"),
            .. V9Strategy(RadarCollectorNames.Patents),
        ]);

        var primary = provider.GetRequiredService<IScoringStrategyFactory>().Primary;
        Assert.Equal("patents-led", primary.Definition.Name);

        var companyId = Guid.NewGuid();
        var result = await primary.Engine.ScoreCompanyAsync(
            companyId, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        var snapshot = Assert.IsType<CompanyScoreSnapshot>(result.Snapshot);
        Assert.Equal(companyId, snapshot.CompanyId);
        Assert.Equal(
            "collectors=RssPressReleaseCollector,patents;collection=none-this-pass;",
            snapshot.CollectionProvenance);

        // §4, asserted rather than asserted-in-prose: the channel's declared collector reads as RAN, not as
        // "did not run". Before spec 147 the vocabulary was empty in this mode, so this inverted — every
        // declared collector was reported as having not run, for collectors that demonstrably had.
        // CollectorsNotRun is structurally empty in any composed run: the startup guard above validates the
        // channel's collectors against this very vocabulary, so a channel 0 means "no signals from that
        // collector in this window", never an outage.
        Assert.Contains("\"CollectorsRan\":[\"patents\"]", snapshot.ComponentJson, StringComparison.Ordinal);
        Assert.Contains("\"CollectorsNotRun\":[]", snapshot.ComponentJson, StringComparison.Ordinal);

        // No collector was constructed to get here.
        Assert.Empty(provider.GetServices<IEvidenceCollector>());
    }
}
