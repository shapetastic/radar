namespace Radar.Application.Scoring;

/// <summary>
/// The pure output of an <see cref="IScoreFormula"/>: the component scores, a human-readable
/// explanation, a machine-readable component breakdown (JSON), and the per-signal contributions used
/// to build <c>ScoreEvidenceLink</c> rows. Contains no Ids/timestamps — the engine assigns those.
/// </summary>
public sealed record ScoreComputation(
    ScoreComponents Components,
    string Explanation,
    string ComponentJson,
    IReadOnlyList<ScoreContribution> Contributions);
