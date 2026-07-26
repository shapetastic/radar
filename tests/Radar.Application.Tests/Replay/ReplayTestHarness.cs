using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Replay;
using Radar.Application.Scoring;
using Radar.Application.Signals;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.TestSupport;

namespace Radar.Application.Tests.Replay;

/// <summary>
/// A whole Radar scoring graph over a throwaway temp directory: the LIVE scoring path (the pipeline's
/// strategy factory + live score stores) and the REPLAY path (<see cref="IReplayRunner"/>) resolved from the
/// SAME container, so both see identical weights, fingerprints and signal-type filters.
/// <para>
/// That shared container is the point: the replay⊆forward assertion is only meaningful if the two engines
/// were configured identically by the real composition, not by the test lining them up by hand.
/// </para>
/// </summary>
internal sealed class ReplayTestHarness : IDisposable
{
    private ReplayTestHarness(string root, ServiceProvider provider)
    {
        Root = root;
        Provider = provider;
    }

    public string Root { get; }

    public ServiceProvider Provider { get; }

    /// <summary>The on-disk signal store root — the cross-run previous/velocity window's source.</summary>
    public string SignalsDirectory => Path.Combine(Root, "signals");

    /// <summary>The LIVE scores root. A replay must never write a single byte under here.</summary>
    public string ScoresDirectory => Path.Combine(Root, "scores");

    /// <summary>The replay output root — its own directory, not under <see cref="ScoresDirectory"/>.</summary>
    public string ReplaysDirectory => Path.Combine(Root, "replays");

    public ISignalRepository Signals => Provider.GetRequiredService<ISignalRepository>();

    public IEvidenceRepository Evidence => Provider.GetRequiredService<IEvidenceRepository>();

    public ICompanyRepository Companies => Provider.GetRequiredService<ICompanyRepository>();

    public ISignalFileStore SignalFileStore => Provider.GetRequiredService<ISignalFileStore>();

    /// <summary>The SHARED score repository the weekly report renders — replay must not add to it.</summary>
    public IScoreRepository LiveScoreRepository => Provider.GetRequiredService<IScoreRepository>();

    public IScoringStrategyFactory LiveStrategies => Provider.GetRequiredService<IScoringStrategyFactory>();

    public IScoreSnapshotFileStoreFactory LiveScoreFileStores =>
        Provider.GetRequiredService<IScoreSnapshotFileStoreFactory>();

    public IReplayRunner ReplayRunner => Provider.GetRequiredService<IReplayRunner>();

    public static ReplayTestHarness Create(
        ReplayPlan plan,
        ScoringStrategySet? strategies = null,
        IScoreFormulaFactory? formulaFactory = null,
        TimeSpan? scoringWindow = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"radar-replay-{Guid.NewGuid():N}");

        var services = new ServiceCollection();
        services.AddLogging();

        // Registered BEFORE AddRadarApplicationServices so these concrete instances win over its TryAdd
        // defaults — exactly the precedence the Worker composition relies on.
        if (strategies is not null)
        {
            services.AddSingleton(strategies);
        }

        if (formulaFactory is not null)
        {
            services.AddSingleton(formulaFactory);
        }

        if (scoringWindow is { } window)
        {
            services.AddSingleton(new ScoringOptions { Window = window });
        }

        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        services.AddFileSignalStore(Path.Combine(root, "signals"));
        services.AddFileScoreStore(Path.Combine(root, "scores"));

        // The composition root owns the plan (config parsing never reaches Radar.Application).
        services.AddSingleton(plan);
        services.AddRadarReplay(Path.Combine(root, "replays"));

        return new ReplayTestHarness(root, services.BuildServiceProvider());
    }

    /// <summary>
    /// Seeds one Approved signal + its evidence into the in-memory repositories AND the on-disk signal store,
    /// mirroring exactly what the pipeline persists for a signal. <paramref name="createdAtUtc"/> is the
    /// spec-136 knowledge date — when Radar learned this, as opposed to when it happened.
    /// </summary>
    public async Task<(Signal Signal, EvidenceItem Evidence)> SeedSignalAsync(
        Guid companyId,
        SignalType type,
        DateTimeOffset observedAtUtc,
        DateTimeOffset createdAtUtc,
        EvidenceQuality quality = EvidenceQuality.High,
        SignalDirection direction = SignalDirection.Positive,
        int strength = 6)
    {
        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithContentHash(Guid.NewGuid().ToString("N"))
            .WithQuality(quality)
            .WithPublishedAtUtc(observedAtUtc)
            .Build();

        var signal = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithCompanyId(companyId)
            .WithType(type)
            .WithDirection(direction)
            .WithStrength(strength)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(observedAtUtc)
            .WithCreatedAtUtc(createdAtUtc)
            .Build();

        await Evidence.AddIfNewAsync(evidence, CancellationToken.None);
        await Signals.AddAsync(signal, CancellationToken.None);
        await SignalFileStore.WriteAsync(signal, ReviewFor(signal), CancellationToken.None);

        return (signal, evidence);
    }

    public async Task<Guid> SeedCompanyAsync(string ticker = "ACME")
    {
        var company = new CompanyBuilder().WithId(Guid.NewGuid()).WithTicker(ticker).Build();
        await Companies.AddAsync(company, CancellationToken.None);
        return company.Id;
    }

    /// <summary>Every file under <paramref name="directory"/>, relative + sorted, or empty when absent.</summary>
    public static IReadOnlyList<string> FilesUnder(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)];
    }

    /// <summary>Every file under <paramref name="directory"/> mapped to its exact content.</summary>
    public static IReadOnlyDictionary<string, string> SnapshotOf(string directory)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in FilesUnder(directory))
        {
            map[relative] = File.ReadAllText(Path.Combine(directory, relative));
        }

        return map;
    }

    private static Radar.Domain.Signals.SignalReview ReviewFor(Signal signal) => new(
        Id: Guid.NewGuid(),
        SignalId: signal.Id,
        ReviewerName: "test-reviewer",
        Decision: SignalReviewDecision.Approve,
        Summary: "Seeded by the replay harness.",
        IssuesJson: null,
        ReviewedAtUtc: signal.CreatedAtUtc);

    public void Dispose()
    {
        Provider.Dispose();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
