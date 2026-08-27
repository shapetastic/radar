namespace Radar.Application.SignalExtraction;

/// <summary>
/// The ONE definition of the provenance keys a judgment-derived news signal carries. Keys are declared here
/// and nowhere else, and the envelope is composed through the SHARED <c>EvidenceMetadata.Compose</c> / read
/// back through <c>EvidenceMetadata.TryRead</c> — the repo's single metadata-envelope definition — rather
/// than a second hand-rolled JSON composer.
/// <para>
/// <b>SPEC 194 — these keys are now READ before they are written.</b> Spec 191 wrote them onto directional
/// news signals minted during extraction, from a company judgment that had never read the matched article.
/// That producer is retired and its <c>Compose</c> overload with it, but the keys survive deliberately: the
/// signals it wrote are on disk, they are append-only, and they must not be deleted or rewritten. They are
/// the shape the spec-194 §1.4 legacy-inheritance admission transform matches on in order to fail those
/// accrued directions CLOSED to Neutral, and the shape the §1.2 materializer's versioned envelope extends
/// with <c>newsJudgmentSignalVersion</c>. Do not add a second metadata parser beside this one.
/// </para>
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
}
