using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.Collectors;
using Radar.Application.Scoring;

namespace Radar.Worker.Tests;

/// <summary>
/// SPEC 198 §3, asserted through the REAL composed Worker graphs (the spec-147/194 pattern): the news-feed
/// QUERY identity must be constructible in every run mode from configuration alone, so <c>full</c>,
/// <c>collect</c>, <c>score</c> and <c>replay</c> over the same <c>Radar:News:RecencyWindowDays</c> stamp
/// the SAME <c>ScoringConfigVersion</c> — while changing the window moves it, on both the AI-off and the
/// AI-on side, and the other <c>Radar:News</c> knobs (which are operational posture, not identity) do not.
/// </summary>
public sealed class NewsQueryScoringIdentityModeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"radar-newsquery-identity-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            merged[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(merged).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        return services.BuildServiceProvider();
    }

    private (string Key, string Value)[] TempDirectories() =>
    [
        ("Radar:EvidenceSourceDirectory", Path.Combine(_root, "evidence")),
        ("Radar:EvidenceRawDirectory", Path.Combine(_root, "evidence-raw")),
        ("Radar:SignalsDirectory", Path.Combine(_root, "signals")),
        ("Radar:ScoresDirectory", Path.Combine(_root, "scores")),
        ("Radar:ReportDirectory", Path.Combine(_root, "reports")),
        ("Radar:RunsDirectory", Path.Combine(_root, "runs")),
        ("Radar:ScoringConfigsDirectory", Path.Combine(_root, "scoring-configs")),
        ("Radar:NewsResearch:ObservationDirectory", Path.Combine(_root, "news-observations")),
    ];

    /// <summary>The newssearch collector enabled at an explicit recency window.</summary>
    private (string Key, string Value)[] WithWindow(
        string window, params (string Key, string Value)[] extra) =>
    [
        ("Radar:Collectors:0", "newssearch"),
        ("Radar:News:RecencyWindowDays", window),
        .. TempDirectories(),
        .. extra,
    ];

    private static string FingerprintOf(ServiceProvider provider) =>
        provider.GetRequiredService<IScoringStrategyFactory>().Primary.Engine.EffectiveConfig.Fingerprint;

    [Theory]
    [InlineData("score")]
    [InlineData("collect")]
    public void FullAndNonFullModes_StampTheSameFingerprint(string mode)
    {
        // THE constructibility criterion, and the reason the identity is registered by the Worker rather
        // than by AddNewsAttentionCollector: a score pass registers NO collector at all (spec 144), yet it
        // must stamp exactly what a full run over the same configuration stamps, or collect-then-score
        // stops being equivalent to the combined run.
        using var full = BuildProvider(WithWindow("7"));
        using var other = BuildProvider(WithWindow("7", ("Radar:RunMode", mode)));

        Assert.Equal(FingerprintOf(full), FingerprintOf(other));
    }

    [Fact]
    public void ReplayMode_StampsTheSameFingerprint()
    {
        using var full = BuildProvider(WithWindow("7"));
        using var replay = BuildProvider(WithWindow("7",
            ("Radar:Replay:Enabled", "true"),
            ("Radar:Replay:From", "2026-05-01"),
            ("Radar:Replay:To", "2026-05-03"),
            ("Radar:Replay:Step", "1d")));

        Assert.Equal(FingerprintOf(full), FingerprintOf(replay));
    }

    [Fact]
    public void AScorePass_ResolvesTheIdentity_WithoutRegisteringTheNewsCollector()
    {
        // It holds an int, so knowing the window costs no HttpClient and no request (the spec-147
        // EnabledCollectorVocabulary posture).
        using var score = BuildProvider(WithWindow("7", ("Radar:RunMode", "score")));

        Assert.Empty(score.GetServices<IEvidenceCollector>());
        Assert.Equal(7, score.GetRequiredService<NewsQueryScoringIdentity>().WindowDays);
        Assert.Equal("newsquery=7d;", score.GetRequiredService<NewsQueryScoringIdentity>().Segment);
    }

    [Fact]
    public void ChangingTheRecencyWindow_MovesTheFingerprint()
    {
        // THE spec-198 §3 acceptance criterion at the composed graph: the query decides which evidence
        // exists, so two runs at different windows must never share a ScoringConfigVersion.
        using var seven = BuildProvider(WithWindow("7"));
        using var fourteen = BuildProvider(WithWindow("14"));
        using var disabled = BuildProvider(WithWindow("0"));

        Assert.NotEqual(FingerprintOf(seven), FingerprintOf(fourteen));
        Assert.NotEqual(FingerprintOf(seven), FingerprintOf(disabled));
        Assert.NotEqual(FingerprintOf(fourteen), FingerprintOf(disabled));

        Assert.Equal(string.Empty, disabled.GetRequiredService<NewsQueryScoringIdentity>().Segment);
    }

    [Fact]
    public void TheWindowMovesTheFingerprint_EvenWithNoNewsCollectorConfigured()
    {
        // Deliberate, and the honest consequence of registering the identity from CONFIGURATION rather than
        // from the collector set: the window is recorded whether or not `newssearch` is in Radar:Collectors.
        // The alternative — gating it on the collector — is exactly the spec-147 failure, where a score pass
        // recorded provenance its configuration did not support.
        using var seven = BuildProvider([
            ("Radar:Collectors:0", "rss"),
            ("Radar:News:RecencyWindowDays", "7"),
            .. TempDirectories(),
        ]);
        using var fourteen = BuildProvider([
            ("Radar:Collectors:0", "rss"),
            ("Radar:News:RecencyWindowDays", "14"),
            .. TempDirectories(),
        ]);

        Assert.NotEqual(FingerprintOf(seven), FingerprintOf(fourteen));
    }

    [Fact]
    public void AnAbsentWindowKey_ResolvesTheShippedDefault()
    {
        // The default is defined ONCE (NewsQueryScoringIdentity.DefaultRecencyWindowDays) and both
        // NewsWorkerOptions and NewsCollectorOptions read it, so an unconfigured deployment stamps the same
        // identity as one that declares the default explicitly.
        using var implicitDefault = BuildProvider([
            ("Radar:Collectors:0", "newssearch"),
            .. TempDirectories(),
        ]);
        using var explicitDefault = BuildProvider(
            WithWindow(NewsQueryScoringIdentity.DefaultRecencyWindowDays.ToString()));

        Assert.Equal(FingerprintOf(implicitDefault), FingerprintOf(explicitDefault));
        Assert.Equal(
            NewsQueryScoringIdentity.DefaultRecencyWindowDays,
            implicitDefault.GetRequiredService<NewsQueryScoringIdentity>().WindowDays);
    }

    [Theory]
    [InlineData("Radar:News:MaxRecordsPerCompany", "10")]
    [InlineData("Radar:News:InterRequestDelaySeconds", "5")]
    public void OtherNewsKnobs_DoNotMoveTheFingerprint(string key, string value)
    {
        // The spec-141 rule: a fingerprint records identity, not operational posture. The retention limit
        // and the pacing delay change how much Radar retains and how politely it asks, never which time
        // range it asks about. (Spec 198 §5 holds both fixed anyway; this pins that they are not folded.)
        using var baseline = BuildProvider(WithWindow("7"));
        using var altered = BuildProvider(WithWindow("7", (key, value)));

        Assert.Equal(FingerprintOf(baseline), FingerprintOf(altered));
    }

    [Theory]
    [InlineData("full")]
    [InlineData("collect")]
    [InlineData("score")]
    public void ANegativeWindow_FailsStartupInEveryMode_NamingTheKey(string mode)
    {
        // A negative window is configuration nonsense, not a disabled filter, and it must fail identically
        // wherever it is detected — including in score mode, which never runs the collector registration
        // that carries the other guard.
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(WithWindow("-1", ("Radar:RunMode", mode))));

        Assert.Contains("Radar:News:RecencyWindowDays", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroWindow_StartsCleanly_AndReproducesThePre198Descriptor()
    {
        // Zero is the supported opt-out: the segment is empty, so the composed identity descriptor is
        // byte-identical to a pre-198 one.
        using var provider = BuildProvider(WithWindow("0"));

        var descriptor = provider.GetRequiredService<ISignalSourceDescriptor>().CanonicalDescriptor();
        Assert.DoesNotContain("newsquery=", descriptor, StringComparison.Ordinal);
        Assert.EndsWith(";", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkerOptionDefault_AgreesWithTheOneSharedConstant()
    {
        // Anti-drift: the shipped default lives in exactly one place, and both the Worker option and the
        // Infrastructure collector option read it. If they ever disagree, a live run would SEND one window
        // and HASH another.
        Assert.Equal(
            NewsQueryScoringIdentity.DefaultRecencyWindowDays,
            new NewsWorkerOptions().RecencyWindowDays);
    }
}
