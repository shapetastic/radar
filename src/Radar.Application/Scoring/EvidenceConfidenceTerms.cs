namespace Radar.Application.Scoring;

/// <summary>
/// The three terms <see cref="ScoreSignalMath.EvidenceConfidenceScore"/> multiplies, beside the clamped score
/// they produce (spec 225). <see cref="BestConfidence"/> is the maximum signal confidence in the set,
/// <see cref="BestQualityWeight"/> the maximum configured evidence-quality weight,
/// <see cref="DistinctSourceTypes"/> the count of distinct evidence source types and
/// <see cref="DiversityFactor"/> that count saturated against <see cref="ScoringWeights.DiversityTarget"/>.
/// <para>
/// This record exists so a MEASUREMENT can see which term saturates without re-implementing the formula: it
/// is produced by the very body that scores, through
/// <see cref="ScoreSignalMath.EvidenceConfidenceDecomposition"/>, never by a reporter's own arithmetic. A
/// second copy of the expression would measure itself.
/// </para>
/// </summary>
public sealed record EvidenceConfidenceTerms(
    double BestConfidence,
    double BestQualityWeight,
    int DistinctSourceTypes,
    double DiversityFactor,
    int Score);
