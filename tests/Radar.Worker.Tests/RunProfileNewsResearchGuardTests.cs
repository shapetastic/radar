using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.TestSupport;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 187 §6: the shipped run profiles must survive the COMPLETE <c>Radar:NewsResearch</c> strict-key
/// guard set — the spec-177 section guard, the spec-179 shadow readers, the spec-181 typing block and the
/// spec-185 judgment block with its prospectively designated presentation cohort — not only the older
/// spec-174 scoring/insider/media/attention binders the Infrastructure-side compatibility test covers.
/// <para>
/// WHY THIS EXISTS. On 2026-08-23 the scheduled baseline crashed at startup with an unknown-key failure
/// because <c>run-radar.ps1</c> skipped only the exact <c>_comment</c> annotation while
/// <c>Radar:NewsResearch</c> had grown a <c>_comment2</c>, which therefore reached the strict allowlist as
/// configuration. The suite was green: no test bound the NewsResearch guards through the script's own
/// flattening. So this test composes the committed profiles through the SHARED
/// <see cref="RunProfileMirror"/> — the one C# mirror of the script's flatten-and-merge — and drives them
/// through the REAL composition root (<c>AddRadarWorker</c>), which is where every one of those guards
/// actually runs.
/// </para>
/// <para>
/// Hermetic: <c>AddRadarWorker</c> only COMPOSES the graph — no collector fetch, no AI call, and nothing
/// written anywhere (the only files READ are the committed profiles and the Worker's own
/// <c>appsettings.json</c>, and every output directory is pointed at a throwaway temp root). The two
/// machine-supplied values the script adds at runtime — the SEC User-Agent and the hosted API key, neither
/// of which is ever committed — are supplied here as obvious test placeholders; only the env-var NAME comes
/// from the profile, and the value never leaves this process.
/// </para>
/// </summary>
public sealed class RunProfileNewsResearchGuardTests
{
    /// <summary>The env var the committed profile NAMES for its hosted OpenAI-compatible readers.</summary>
    private const string HostedKeyEnvVar = "DEEPINFRA_API_KEY";

    /// <summary>
    /// Composes configuration the way a live run composes it: the Worker's own
    /// <c>src/Radar.Worker/appsettings.json</c> FIRST (the process's base configuration — it supplies the
    /// per-collector SEC settings the profiles deliberately omit), then the flattened profile on top, which
    /// is exactly what <c>dotnet run … -- --Radar:…=value</c> does. Plus the values
    /// <c>run-radar.ps1</c> injects at runtime rather than committing: the SEC User-Agent and the output
    /// directories — here rooted in a throwaway temp folder, so no test can ever point a composed graph at
    /// the repo's real <c>data/</c>.
    /// </summary>
    private static IConfiguration Compose(string? overlayProfileName, string outputRoot)
    {
        var merged = RunProfileMirror.Compose(overlayProfileName);
        foreach (var (key, value) in RuntimeScriptArguments(outputRoot))
        {
            merged[key] = value;
        }

        return new ConfigurationBuilder()
            .AddJsonFile(WorkerAppSettingsPath(), optional: false)
            .AddInMemoryCollection(merged)
            .Build();
    }

    /// <summary>
    /// The subset of <c>run-radar.ps1</c>'s runtime-supplied arguments this test needs: the SEC
    /// User-Agent (never committed — a real contact email) and the news-research output roots. The
    /// script's remaining directory overrides touch none of the guards under test.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string?>> RuntimeScriptArguments(string outputRoot) =>
    [
        new("Radar:Sec:UserAgent", "Radar Research test@example.com"),
        new("Radar:NewsResearch:ObservationDirectory", Path.Combine(outputRoot, "news-observations")),
        new("Radar:NewsResearch:Shadow:OutputDirectory", Path.Combine(outputRoot, "news-risk")),
        new("Radar:NewsResearch:Typing:OutputDirectory", Path.Combine(outputRoot, "news-typing")),
    ];

    /// <summary>
    /// Walks up from the test binary to the repo root (the first ancestor carrying the Worker's
    /// <c>appsettings.json</c>) so the test does not depend on its working directory.
    /// </summary>
    private static string WorkerAppSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Radar.Worker", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate src/Radar.Worker/appsettings.json from " + AppContext.BaseDirectory);
    }

    private static ServiceProvider BuildWorkerGraph(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Sets the hosted API-key env var the committed profile NAMES, restoring whatever was there before.
    /// The VALUE is a placeholder that never leaves the process: nothing in graph composition contacts a
    /// provider, and a real key is deliberately not required to prove a config file binds.
    /// </summary>
    private sealed class HostedKeyScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(HostedKeyEnvVar);

        public HostedKeyScope() =>
            Environment.SetEnvironmentVariable(HostedKeyEnvVar, "not-a-real-key");

        public void Dispose() => Environment.SetEnvironmentVariable(HostedKeyEnvVar, _previous);
    }

    /// <summary>A throwaway output root, so a composed graph can never point at the repo's real data.</summary>
    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "radar-profile-guard-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }
    }

    public static TheoryData<string?> ComposableProfiles()
    {
        var data = new TheoryData<string?> { null };
        foreach (var profileName in RunProfileMirror.OverlayProfileNames())
        {
            data.Add(profileName);
        }

        return data;
    }

    [Fact]
    public void DefaultProfile_BindsTheCompleteNewsResearchBlock_ThroughTheRealStrictKeyGuards()
    {
        using var key = new HostedKeyScope();
        using var root = new TempRoot();
        using var provider = BuildWorkerGraph(Compose(overlayProfileName: null, root.Path));

        // The spec-177 archive, the spec-179 shadow, the spec-181 typing pass and the spec-185 judge are
        // all COMPOSED — i.e. every strict-key guard on the path accepted the committed profile.
        Assert.NotNull(provider.GetService<INewsObservationArchive>());
        Assert.NotNull(provider.GetService<INewsRiskShadowGenerator>());
        Assert.NotNull(provider.GetService<INewsTypingGenerator>());
        Assert.NotNull(provider.GetService<INewsJudgmentGenerator>());

        // Spec 187 §2: the ONE candidate-selection seam is registered with judgment in the SHIPPED
        // profile, so the live baseline run genuinely types the companies it is about to judge — and the
        // Worker resolves it through its optional ctor parameter rather than silently running without it.
        Assert.NotNull(provider.GetService<INewsJudgmentCandidatePlanner>());
        Assert.NotNull(provider.GetServices<IHostedService>().OfType<Worker>().SingleOrDefault());
    }

    [Fact]
    public void DefaultProfile_ResolvesTypingAndJudgmentEnabled_WithTheConfiguredHostedReaders()
    {
        using var key = new HostedKeyScope();
        using var root = new TempRoot();
        var configuration = Compose(overlayProfileName: null, root.Path);

        // The flattened values the Worker actually binds (spec 187 §8's shipped posture).
        Assert.Equal("true", configuration["Radar:NewsResearch:Typing:Enabled"]);
        Assert.Equal("true", configuration["Radar:NewsResearch:Judgment:Enabled"]);
        Assert.Equal(
            "deepseek-ai/DeepSeek-V4-Flash",
            configuration["Radar:NewsResearch:Typing:Readers:0:OpenAi:Model"]);
        Assert.Equal(
            "deepseek-ai/DeepSeek-V4-Flash",
            configuration["Radar:NewsResearch:Judgment:Judges:0:OpenAi:Model"]);
        Assert.Equal(
            HostedKeyEnvVar, configuration["Radar:NewsResearch:Typing:Readers:0:OpenAi:ApiKeyEnvVar"]);

        var options = configuration.GetSection("Radar").Get<RadarWorkerOptions>()!.NewsResearch;

        Assert.True(options.Typing.Enabled);
        Assert.True(options.Judgment.Enabled);

        var typingReader = Assert.Single(options.Typing.Readers);
        Assert.Equal("deepinfra-deepseek", typingReader.Name);
        Assert.Equal("openai", typingReader.Provider);
        Assert.Equal("deepseek-ai/DeepSeek-V4-Flash", typingReader.OpenAi.Model);

        var judge = Assert.Single(options.Judgment.Judges);
        Assert.Equal("deepinfra-deepseek", judge.Name);
        Assert.Equal("openai", judge.Provider);

        // Spec 187 §8: exactly ONE hosted shadow reader is scheduled — no local reader beside it.
        var shadowReader = Assert.Single(options.Shadow.Readers);
        Assert.Equal("deepinfra-deepseek", shadowReader.Name);

        // The presentation cohort (spec 185 §4) must NAME the configured judge and extractor; the Worker's
        // referential validation ran as part of composing the graph above.
        Assert.Equal(judge.Name, options.Judgment.PresentationCohort.Judge);
        Assert.Equal(typingReader.Name, options.Judgment.PresentationCohort.Extractor);
    }

    [Theory]
    [MemberData(nameof(ComposableProfiles))]
    public void EveryShippedProfile_ComposedAsRunRadarDoes_PassesEveryNewsResearchGuard(
        string? overlayProfileName)
    {
        // The guards must reject typos, not the profiles we actually run — every committed overlay,
        // composed the way the script composes it, builds the whole Worker graph without a throw.
        using var key = new HostedKeyScope();
        using var root = new TempRoot();
        using var provider = BuildWorkerGraph(Compose(overlayProfileName, root.Path));

        Assert.NotNull(provider.GetService<INewsTypingGenerator>());
    }

    [Fact]
    public void ACommentStarKeyThatReachesTheWorker_FailsStartup_WhichIsWhatTheFlattenerMustPrevent()
    {
        // The 2026-08-23 crash, reproduced deliberately: the strict allowlist is doing its job — the defect
        // was the flattener letting an annotation through. Pinning the failure here is what makes the skip
        // rule load-bearing rather than cosmetic.
        using var hostedKey = new HostedKeyScope();
        using var root = new TempRoot();
        var merged = RunProfileMirror.Compose(overlayProfileName: null);
        foreach (var (key, value) in RuntimeScriptArguments(root.Path))
        {
            merged[key] = value;
        }

        merged["Radar:NewsResearch:_comment2"] = "an annotation that must never become configuration";
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(WorkerAppSettingsPath(), optional: false)
            .AddInMemoryCollection(merged)
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => BuildWorkerGraph(configuration).Dispose());

        Assert.Contains("_comment2", ex.Message, StringComparison.Ordinal);
    }
}
