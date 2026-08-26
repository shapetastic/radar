using Radar.Application.Collectors;

namespace Radar.Application.SignalExtraction;

/// <summary>
/// The ONE definition of the provenance a directional news signal carries (spec 191 §2: "a signal whose
/// provenance cannot be recorded is not emitted directionally"). Keys are declared here and nowhere else,
/// and the envelope is composed through the SHARED <see cref="EvidenceMetadata.Compose"/> / read back
/// through <see cref="EvidenceMetadata.TryRead"/> — the repo's single metadata-envelope definition — rather
/// than a second hand-rolled JSON composer.
/// <para>
/// The envelope's <c>companyHints</c> array is written EMPTY: a signal carries no collector company hints.
/// That one artifact is the price of having exactly one envelope definition instead of two, and it keeps a
/// signal's metadata readable by the same reader every evidence item already uses.
/// </para>
/// </summary>
public static class NewsDirectionalSignalMetadata
{
    /// <summary>The admitted stage-2 judgment record id (spec 185).</summary>
    public const string JudgmentIdKey = "newsJudgmentId";

    /// <summary>The judgment's stage-2 cohort key — the prospectively designated presentation cohort.</summary>
    public const string JudgmentCohortKeyKey = "newsJudgmentCohortKey";

    /// <summary>The matched point-in-time news observation id (spec 177).</summary>
    public const string ObservationIdKey = "newsObservationId";

    /// <summary>The judge's business-trajectory display token, so the direction's REASON is legible on the signal.</summary>
    public const string TrajectoryKey = "newsBusinessTrajectory";

    /// <summary>
    /// Composes the provenance envelope for <paramref name="read"/>. Guids render invariant
    /// (<c>D</c> format) so the value round-trips through <see cref="Guid.Parse(string)"/> exactly.
    /// </summary>
    public static string Compose(NewsDirectionalRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JudgmentIdKey] = read.JudgmentId.ToString("D"),
            [JudgmentCohortKeyKey] = read.JudgmentCohortKey,
            [ObservationIdKey] = read.ObservationId.ToString("D"),
            [TrajectoryKey] = read.TrajectoryToken,
        };

        return EvidenceMetadata.Compose(metadata, []);
    }
}
