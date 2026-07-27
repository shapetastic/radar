using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.Collectors;
using Radar.Application.Filings;
using Radar.Application.Pipeline;
using Radar.Application.Replay;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 144 — the <c>Radar:RunMode</c> config surface: the two new verbs (<c>collect</c> / <c>score</c>),
/// their reconciliation with the spec-139 <c>Radar:Replay:Enabled</c> switch, and the two properties the
/// whole slice rests on — a <c>score</c> pass registers NO collector, and it DOES still register the AI seam
/// (which is a <c>ScoringConfigVersion</c> input, so dropping it would move the fingerprint).
/// </summary>
public sealed class RunModeWorkerOptionsTests
{
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

    // ---- mode selection -----------------------------------------------------------------------------

    [Fact]
    public void NoRunMode_IsTheCombinedPass_Unchanged()
    {
        using var provider = BuildProvider();

        Assert.IsType<RadarPipelineRunner>(provider.GetRequiredService<IRadarPipeline>());
        Assert.Equal(RadarRunMode.Full, provider.GetRequiredService<WorkerRunOptions>().Mode);
        Assert.NotNull(provider.GetService<ICollectionPass>());
        Assert.NotNull(provider.GetService<IScoringPass>());
    }

    [Theory]
    [InlineData("full")]
    [InlineData("FULL")]
    [InlineData("  full  ")]
    public void FullMode_IsCaseAndWhitespaceInsensitive(string value)
    {
        using var provider = BuildProvider(("Radar:RunMode", value));

        Assert.IsType<RadarPipelineRunner>(provider.GetRequiredService<IRadarPipeline>());
    }

    [Theory]
    [InlineData("collect")]
    [InlineData("Collect")]
    public void CollectMode_RegistersTheCollectOnlyPipeline_AndTheCollectors(string value)
    {
        using var provider = BuildProvider(
            ("Radar:RunMode", value),
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "localfile"));

        Assert.IsType<CollectOnlyPipelineRunner>(provider.GetRequiredService<IRadarPipeline>());
        Assert.Equal(RadarRunMode.Collect, provider.GetRequiredService<WorkerRunOptions>().Mode);

        // Stages 1–5 are present; stage 6 has no place in a collect pass.
        Assert.NotNull(provider.GetService<ICollectionPass>());
        Assert.Null(provider.GetService<IScoringPass>());

        Assert.Equal(2, provider.GetServices<IEvidenceCollector>().Count());
    }

    [Theory]
    [InlineData("score")]
    [InlineData("Score")]
    public void ScoreMode_RegistersTheScoreOnlyPipeline_AndNoCollectionPass(string value)
    {
        using var provider = BuildProvider(("Radar:RunMode", value));

        Assert.IsType<ScoreOnlyPipelineRunner>(provider.GetRequiredService<IRadarPipeline>());
        Assert.Equal(RadarRunMode.Score, provider.GetRequiredService<WorkerRunOptions>().Mode);

        Assert.NotNull(provider.GetService<IScoringPass>());
        Assert.Null(provider.GetService<ICollectionPass>());
    }

    [Fact]
    public void UnknownRunMode_FailsFast_ListingTheValidValues()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(("Radar:RunMode", "scor")));

        Assert.Contains("Radar:RunMode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("scor", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"collect\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"score\"", ex.Message, StringComparison.Ordinal);
    }

    // ---- reconciliation with spec 139's replay switch ------------------------------------------------

    [Fact]
    public void ReplayEnabledAlone_StillSelectsReplay_Unchanged()
    {
        // The spec-139 behaviour run-radar.ps1 -Replay depends on: Radar:Replay:Enabled with no RunMode is
        // a replay, and the pipeline graph is exactly what it was (the replay runner replaces the run).
        using var provider = BuildProvider(
            ("Radar:Replay:Enabled", "true"),
            ("Radar:Replay:From", "2026-05-01"),
            ("Radar:Replay:To", "2026-05-03"));

        Assert.NotNull(provider.GetService<IReplayRunner>());
        Assert.IsType<RadarPipelineRunner>(provider.GetRequiredService<IRadarPipeline>());
        Assert.Equal(RadarRunMode.Replay, provider.GetRequiredService<WorkerRunOptions>().Mode);
    }

    [Fact]
    public void RunModeReplay_SelectsReplay_WithoutTheBooleanSwitch()
    {
        using var provider = BuildProvider(
            ("Radar:RunMode", "replay"),
            ("Radar:Replay:From", "2026-05-01"),
            ("Radar:Replay:To", "2026-05-03"));

        Assert.NotNull(provider.GetService<IReplayRunner>());
        Assert.Equal(RadarRunMode.Replay, provider.GetRequiredService<WorkerRunOptions>().Mode);
    }

    [Fact]
    public void RunModeReplay_WithoutARange_StillFailsFastInTheReplayPlanBuilder()
    {
        // One message for "no replay range", wherever the replay was selected from.
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(("Radar:RunMode", "replay")));

        Assert.Contains("Radar:Replay:From", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("collect")]
    [InlineData("score")]
    public void LiveModeCombinedWithReplayEnabled_FailsFast_NamingBothKeys(string mode)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(
            ("Radar:RunMode", mode),
            ("Radar:Replay:Enabled", "true"),
            ("Radar:Replay:From", "2026-05-01"),
            ("Radar:Replay:To", "2026-05-03")));

        Assert.Contains("Radar:RunMode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Radar:Replay:Enabled", ex.Message, StringComparison.Ordinal);
        Assert.Contains(mode, ex.Message, StringComparison.Ordinal);
    }

    // ---- a score pass constructs no collector --------------------------------------------------------

    [Fact]
    public void ScoreMode_RegistersZeroCollectors_EvenWhenCollectorsAreConfigured()
    {
        // THE acceptance criterion. Registration is the guarantee: constructing a collector is what opens
        // its typed HttpClient, so "constructs and invokes no collector" has to mean "is never registered".
        using var provider = BuildProvider(
            ("Radar:RunMode", "score"),
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "localfile"));

        Assert.Empty(provider.GetServices<IEvidenceCollector>());
    }

    [Fact]
    public void ScoreMode_DoesNotRequireCollectorsToBeConfigured()
    {
        // A blank Radar:Collectors is a hard error in full/collect mode; in score mode there is nothing to
        // collect with, so the list is not read at all.
        using var provider = BuildProvider(("Radar:RunMode", "score"), ("Radar:Collectors:0", ""));

        Assert.NotNull(provider.GetService<IRadarPipeline>());
        Assert.Empty(provider.GetServices<IEvidenceCollector>());
    }

    [Fact]
    public void FullMode_StillRejectsABlankCollectorEntry()
    {
        // The regression guard for the mode gate above: the existing validation is untouched everywhere else.
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(("Radar:Collectors:0", "")));

        Assert.Contains("Radar:Collectors", ex.Message, StringComparison.Ordinal);
    }

    // ---- …but the AI SEAM is still registered, because it is a fingerprint input ---------------------

    [Fact]
    public void ScoreMode_StillRegistersTheAiSeam_BecauseItIsAScoringFingerprintInput()
    {
        // IDirectionalFilingSignalSource.ScoringDescriptor() is folded into ScoringConfigVersion via
        // SignalSourceDescriptor's ai= segment (spec 106/119). Omitting it from a score pass would move the
        // fingerprint and break "collect-then-score is byte-identical to the combined run". The source is
        // only ever INVOKED by the collection pass, which a score pass does not have — so "no AI read" still
        // holds. Practical consequence: a score pass needs the same Radar:Ai config (and API key) as collect.
        // The SEC User-Agent is required because the AI seam also registers the EX-99.1 earnings reader —
        // an operational consequence worth stating: a score pass with AI enabled needs the same Radar:Sec
        // and Radar:Ai configuration a collect pass does, even though it never issues a request.
        using var provider = BuildProvider(
            ("Radar:RunMode", "score"),
            ("Radar:Sec:UserAgent", "Radar Test test@example.com"),
            ("Radar:Ai:Provider", "ollama"),
            ("Radar:Ai:Model", "llama3.1"));

        Assert.NotNull(provider.GetService<IDirectionalFilingSignalSource>());
        Assert.Empty(provider.GetServices<IEvidenceCollector>());
    }

    [Fact]
    public void ScoreAndFullModes_StampTheSameScoringConfigFingerprint()
    {
        // The consequence of the two tests above, asserted directly: dropping the collectors but keeping the
        // AI seam leaves every strategy's identity byte-identical. Only CollectionProvenance differs — it is
        // recorded, never hashed (spec 141).
        (string, string)[] ai =
        [
            ("Radar:Sec:UserAgent", "Radar Test test@example.com"),
            ("Radar:Ai:Provider", "ollama"),
            ("Radar:Ai:Model", "llama3.1"),
        ];

        using var full = BuildProvider([("Radar:Collectors:0", "rss"), .. ai]);
        using var score = BuildProvider(
            [("Radar:RunMode", "score"), ("Radar:Collectors:0", "rss"), .. ai]);

        var fullPrimary = full.GetRequiredService<Radar.Application.Scoring.IScoringStrategyFactory>().Primary;
        var scorePrimary = score.GetRequiredService<Radar.Application.Scoring.IScoringStrategyFactory>().Primary;

        Assert.Equal(
            fullPrimary.Engine.EffectiveConfig.Fingerprint,
            scorePrimary.Engine.EffectiveConfig.Fingerprint);
    }

    // ---- Radar:Score:AsOfUtc ------------------------------------------------------------------------

    [Fact]
    public void ScoreAsOfUtc_BlankByDefault_MeansNow()
    {
        using var provider = BuildProvider(("Radar:RunMode", "score"));

        Assert.Null(provider.GetRequiredService<ScoringPassOptions>().AsOfUtc);
    }

    [Fact]
    public void ScoreAsOfUtc_IsParsedAsUtc_LikeTheReplayBounds()
    {
        using var provider = BuildProvider(
            ("Radar:RunMode", "score"),
            ("Radar:Score:AsOfUtc", "2026-05-01"));

        // No explicit offset ⇒ read as UTC (AssumeUniversal), NOT as machine-local time.
        Assert.Equal(
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            provider.GetRequiredService<ScoringPassOptions>().AsOfUtc);
    }

    [Fact]
    public void ScoreAsOfUtc_Unparseable_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(
            ("Radar:RunMode", "score"),
            ("Radar:Score:AsOfUtc", "yesterday")));

        Assert.Contains("Radar:Score:AsOfUtc", ex.Message, StringComparison.Ordinal);
        Assert.Contains("yesterday", ex.Message, StringComparison.Ordinal);
    }
}
