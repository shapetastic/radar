using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    private readonly bool _ownsRoot;

    private ReplayTestHarness(
        string root, ServiceProvider provider, CapturingLoggerProvider logs, bool ownsRoot)
    {
        Root = root;
        Provider = provider;
        Logs = logs;
        _ownsRoot = ownsRoot;
    }

    public string Root { get; }

    public ServiceProvider Provider { get; }

    /// <summary>Every log entry the composed graph emitted, so aggregated operator warnings are assertable.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>The on-disk signal store root — the cross-run previous/velocity window's source.</summary>
    public string SignalsDirectory => Path.Combine(Root, "signals");

    /// <summary>The LIVE scores root. A replay must never write a single byte under here.</summary>
    public string ScoresDirectory => Path.Combine(Root, "scores");

    /// <summary>The replay output root — its own directory, not under <see cref="ScoresDirectory"/>.</summary>
    public string ReplaysDirectory => Path.Combine(Root, "replays");

    /// <summary>
    /// The content-addressed effective-scoring-config store root, plus its per-strategy-name identity records
    /// under <c>strategies/</c>. Spec 148: a replay writes here — provenance, not a scoring mutation.
    /// </summary>
    public string ScoringConfigsDirectory => Path.Combine(Root, "scoring-configs");

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

    /// <summary>
    /// A SECOND, independently-composed graph over an existing harness's data root — a stand-in for "the same
    /// operator, a later process, a different replay label". It never deletes the root; the harness that
    /// created it still owns cleanup.
    /// </summary>
    public static ReplayTestHarness CreateSharingRootOf(ReplayTestHarness other, ReplayPlan plan) =>
        Create(plan, root: other.Root, ownsRoot: false);

    public static ReplayTestHarness Create(
        ReplayPlan plan,
        ScoringStrategySet? strategies = null,
        IScoreFormulaFactory? formulaFactory = null,
        TimeSpan? scoringWindow = null,
        string? root = null,
        bool ownsRoot = true)
    {
        root ??= Path.Combine(Path.GetTempPath(), $"radar-replay-{Guid.NewGuid():N}");

        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));

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
        // Spec 148: replay records its provenance (the effective config) and runs the identity tripwire, so
        // the graph needs the same scoring-config store the Worker registers in every run mode.
        services.AddFileScoringConfigStore(Path.Combine(root, "scoring-configs"));

        // The composition root owns the plan (config parsing never reaches Radar.Application).
        services.AddSingleton(plan);
        services.AddRadarReplay(Path.Combine(root, "replays"));

        return new ReplayTestHarness(root, services.BuildServiceProvider(), logs, ownsRoot);
    }

    /// <summary>
    /// Seeds one Approved signal + its evidence into the in-memory repositories AND the on-disk signal store,
    /// mirroring exactly what the pipeline persists for a signal. <paramref name="createdAtUtc"/> is the
    /// spec-136 knowledge date — when Radar learned this, as opposed to when it happened.
    /// <para>
    /// Set <paramref name="persistEvidence"/> to false to reproduce the accrued shape spec 145 heals only
    /// FORWARD: a signal whose <c>EvidenceId</c> resolves to nothing, which the engine must drop and count
    /// rather than score.
    /// </para>
    /// </summary>
    public async Task<(Signal Signal, EvidenceItem Evidence)> SeedSignalAsync(
        Guid companyId,
        SignalType type,
        DateTimeOffset observedAtUtc,
        DateTimeOffset createdAtUtc,
        EvidenceQuality quality = EvidenceQuality.High,
        SignalDirection direction = SignalDirection.Positive,
        int strength = 6,
        string? metadataJson = null,
        bool persistEvidence = true)
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
            // Spec 191: the optional signal provenance envelope (null == not recorded, the default).
            .WithMetadataJson(metadataJson)
            .Build();

        if (persistEvidence)
        {
            await Evidence.AddIfNewAsync(evidence, CancellationToken.None);
        }

        await Signals.AddAsync(signal, CancellationToken.None);
        await SignalFileStore.WriteAsync(signal, ReviewFor(signal), CancellationToken.None);

        return (signal, evidence);
    }

    /// <summary>
    /// Seeds a company into this graph's company repository. <paramref name="id"/> lets a second harness over
    /// a SHARED data root enrol the SAME subject company: the repositories are per-container, so a replay
    /// there would otherwise enumerate a brand-new, signal-less company instead of replaying the same series.
    /// </summary>
    public async Task<Guid> SeedCompanyAsync(string ticker = "ACME", Guid? id = null)
    {
        var company = new CompanyBuilder().WithId(id ?? Guid.NewGuid()).WithTicker(ticker).Build();
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

    /// <summary>
    /// Captures every log entry the composed graph emits, keyed by category, so a test can assert on an
    /// aggregated operator warning (spec 148's same-label overwrite) rather than on a side effect of it.
    /// </summary>
    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
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

        if (!_ownsRoot)
        {
            // A shared-root harness never deletes the directory out from under its owner.
            return;
        }

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
