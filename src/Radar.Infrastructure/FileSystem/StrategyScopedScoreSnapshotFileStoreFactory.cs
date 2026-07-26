using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Radar.Application.Scoring;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// The default <see cref="IScoreSnapshotFileStoreFactory"/> (spec 137). The <b>primary</b> strategy is handed
/// the registered <see cref="IScoreSnapshotFileStore"/> verbatim, so its snapshots keep landing at
/// <c>{scoresRoot}/{companyId}/{snapshotId}.json</c> — the existing location the spec-101/108 efficacy read,
/// the report's "vs previous run" read and all accrued history already use, with no migration and no
/// path-fallback logic. Every <b>non-primary</b> strategy gets a <see cref="FileScoreSnapshotStore"/> rooted
/// at <c>{scoresRoot}/strategies/{strategyName}/</c>, so the series can never collide.
/// <para>
/// The strategy-scoped stores are cached per strategy name (case-insensitive, matching
/// <see cref="ScoringStrategySet"/>'s uniqueness rule). Strategy names are validated at composition time to
/// be usable as a single directory segment, so the join can never escape the scores root.
/// </para>
/// </summary>
public sealed class StrategyScopedScoreSnapshotFileStoreFactory : IScoreSnapshotFileStoreFactory
{
    /// <summary>The directory segment that groups every non-primary strategy's snapshots.</summary>
    public const string StrategiesSegment = "strategies";

    private readonly IScoreSnapshotFileStore _primary;
    private readonly FileScoreSnapshotStoreOptions _options;
    private readonly ILogger<FileScoreSnapshotStore> _storeLogger;

    private readonly ConcurrentDictionary<string, IScoreSnapshotFileStore> _byStrategy =
        new(StringComparer.OrdinalIgnoreCase);

    public StrategyScopedScoreSnapshotFileStoreFactory(
        IScoreSnapshotFileStore primary,
        FileScoreSnapshotStoreOptions options,
        ILogger<FileScoreSnapshotStore> storeLogger)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storeLogger);
        _primary = primary;
        _options = options;
        _storeLogger = storeLogger;
    }

    public IScoreSnapshotFileStore ForStrategy(ScoringStrategyDefinition strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        if (strategy.IsPrimary)
        {
            return _primary;
        }

        return _byStrategy.GetOrAdd(
            strategy.Name,
            name => new FileScoreSnapshotStore(
                new FileScoreSnapshotStoreOptions
                {
                    RootDirectory = Path.Combine(_options.RootDirectory, StrategiesSegment, name),
                },
                _storeLogger));
    }
}
