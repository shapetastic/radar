using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Replay;
using Radar.Application.Scoring;
using Radar.Application.Storage;
using Radar.Domain.Scoring;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Routes a replay's snapshots to <c>{replayRoot}/{runLabel}/strategies/{strategyName}/{companyId}/…</c>
/// (spec 139), reusing <see cref="FileScoreSnapshotStore"/> verbatim — the on-disk snapshot format is
/// identical to a forward snapshot's, so the same readers work over a replay series without a second format.
/// <para>
/// <b>The replay root is its own configured directory (<c>Radar:ReplayDirectory</c>,
/// default <c>data/replays</c>) and is deliberately NOT under the scores root.</b> "Replay never writes into
/// the live scores directory" then holds structurally rather than incidentally: there is no path arithmetic,
/// and no future change to the strategy-scoped layout, that could put a replay inside the sacred forward
/// efficacy series (spec 101/108).
/// </para>
/// <para>
/// The <c>strategies/</c> grouping segment is the SHARED constant from
/// <see cref="StrategyScopedScoreSnapshotFileStoreFactory.StrategiesSegment"/>, so a replay's layout and the
/// live non-primary layout cannot drift apart. Unlike the live factory, EVERY strategy is scoped here —
/// including the primary: replay has no legacy location to preserve, and a uniform layout means a consumer
/// never has to know which strategy happened to be primary.
/// </para>
/// <para>
/// Stores are cached per (label, strategy) pair, case-insensitively — matching the uniqueness rule
/// <see cref="ScoringStrategySet"/> applies to strategy names — so a strategy's whole series lands in one
/// store. Both segments are validated against the shared <see cref="StorageSegmentName"/> rule before any
/// join, so a hand-constructed label can never escape the replay root.
/// </para>
/// </summary>
public sealed class ReplayScopedScoreSnapshotFileStoreFactory : IReplayScoreSnapshotFileStoreFactory
{
    private readonly string _replayRootDirectory;
    private readonly ILogger<FileScoreSnapshotStore> _storeLogger;

    private readonly ConcurrentDictionary<string, IScoreSnapshotFileStore> _byLabelAndStrategy =
        new(StringComparer.OrdinalIgnoreCase);

    public ReplayScopedScoreSnapshotFileStoreFactory(
        string replayRootDirectory,
        ILogger<FileScoreSnapshotStore> storeLogger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayRootDirectory);
        ArgumentNullException.ThrowIfNull(storeLogger);

        _replayRootDirectory = replayRootDirectory;
        _storeLogger = storeLogger;
    }

    public IScoreSnapshotFileStore ForStrategy(string runLabel, ScoringStrategyDefinition strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        if (!StorageSegmentName.IsUsable(runLabel))
        {
            throw new ArgumentException(
                $"'{runLabel}' is not a usable replay run label; it is used verbatim as a storage directory "
                    + $"segment, so {StorageSegmentName.Rule}.",
                nameof(runLabel));
        }

        if (!StorageSegmentName.IsUsable(strategy.Name))
        {
            throw new ArgumentException(
                $"'{strategy.Name}' is not a usable strategy name; it is used verbatim as a storage directory "
                    + $"segment, so {StorageSegmentName.Rule}.",
                nameof(strategy));
        }

        // A NUL-joined composite key: neither segment can contain it (both passed the segment rule), so two
        // different pairs can never collide onto one cache entry.
        var cacheKey = $"{runLabel}\0{strategy.Name}";

        return _byLabelAndStrategy.GetOrAdd(
            cacheKey,
            _ => new FileScoreSnapshotStore(
                new FileScoreSnapshotStoreOptions
                {
                    RootDirectory = Path.Combine(
                        _replayRootDirectory,
                        runLabel,
                        StrategyScopedScoreSnapshotFileStoreFactory.StrategiesSegment,
                        strategy.Name),
                    SnapshotFileName = AsOfSnapshotFileName,
                },
                _storeLogger));
    }

    /// <summary>
    /// Names a replayed snapshot by its AS-OF instant rather than by its (freshly minted, per-call) id, so
    /// re-running the same replay over an unchanged signal store OVERWRITES its previous output instead of
    /// accumulating a second copy of it. That is what makes replay idempotent on disk: the spec requires two
    /// identical replays to be diffable to zero, and id-named files would differ on every run even when every
    /// score was byte-identical.
    /// <para>
    /// The rendering is <b>LOSSLESS</b>: whole-second instants (every realistic step) use the readable
    /// second-resolution form, and anything with a sub-second component appends full tick precision. That
    /// distinction is load-bearing, not cosmetic — <see cref="ReplaySeries"/> points are strictly ascending,
    /// but a sub-second step is reachable through config (<c>Radar:Replay:Step</c> accepts a plain TimeSpan
    /// string, so <c>"00:00:00.5"</c> yields two as-of points inside one second). At second resolution those
    /// two DISTINCT scorings would render to the SAME path and the later would silently overwrite the
    /// earlier, while the run's reported snapshot count still claimed both — exactly the silent truncation
    /// the spec forbids. Keying on the full instant makes distinct as-of points distinct files by
    /// construction, mirroring the same sub-second reasoning the default run label already applies.
    /// </para>
    /// </summary>
    private static string AsOfSnapshotFileName(CompanyScoreSnapshot snapshot)
    {
        var asOfUtc = snapshot.WindowEndUtc.UtcDateTime;

        // Whole seconds keep the established readable name (and are what every {n}d/h/m/s step produces);
        // only a genuinely sub-second instant pays for the extra precision.
        var format = asOfUtc.Ticks % TimeSpan.TicksPerSecond == 0
            ? "yyyyMMdd'T'HHmmss'Z'"
            : "yyyyMMdd'T'HHmmss'.'fffffff'Z'";

        return asOfUtc.ToString(format, CultureInfo.InvariantCulture) + ".json";
    }
}
