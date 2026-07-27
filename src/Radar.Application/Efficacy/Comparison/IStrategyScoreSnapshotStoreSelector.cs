using Radar.Application.Scoring;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// Hands the comparison the READ-ONLY score-snapshot store holding one strategy's series (spec 140).
/// <para>
/// It exists so the comparison can read either the LIVE forward series — where the primary strategy resolves,
/// by construction, to the very store the spec-101/108 single-series read already uses, and every non-primary
/// strategy to its <c>strategies/{name}/</c> scope — or a spec-139 REPLAY series under one run label, without
/// the comparison knowing which. The two selectors live in Infrastructure because the choice is a
/// configuration concern.
/// </para>
/// <para>
/// The comparison only ever calls <c>ReadAllForCompanyAsync</c> on what it is handed; nothing here writes.
/// </para>
/// </summary>
public interface IStrategyScoreSnapshotStoreSelector
{
    /// <summary>The store holding <paramref name="strategy"/>'s persisted snapshot series.</summary>
    IScoreSnapshotFileStore ForStrategy(ScoringStrategyDefinition strategy);

    /// <summary>
    /// A short, human-readable description of WHICH series this selector reads (e.g. the live forward series,
    /// or a named replay run). Logged once per comparison so an artifact is never ambiguous about its input.
    /// </summary>
    string SeriesDescription { get; }
}
