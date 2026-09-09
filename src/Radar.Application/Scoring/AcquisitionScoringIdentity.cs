using Radar.Application.Acquisitions;

namespace Radar.Application.Scoring;

/// <summary>
/// SPEC 217 §2 — the ONE declaration of the acquisition-recognition segment the
/// <c>ScoringConfigVersion</c> fingerprint hashes, composed from the two versions it names so a bump to
/// either moves the pins on its own instead of leaving a stale literal behind (the spec-216 <c>rm=</c>
/// precedent).
/// <para>
/// The segment is <c>acq={scan};supersede={rule};</c> and is rendered UNCONDITIONALLY: the supersede is
/// pure assembly code that runs in every composition, so there is no "disabled" state to distinguish — only
/// whether a company happens to have a recognised acquisition, which is DATA, not identity. It is
/// deliberately not AI-gated, so both the AI-ON and the AI-OFF pin families move with it.
/// </para>
/// <para>
/// It carries no acquisition CONTENT — no company, accession, acquirer or consideration. Hashing an outcome
/// would re-stamp every strategy the moment one company was taken over, which is a fact about the world and
/// not about the scoring hypothesis.
/// </para>
/// </summary>
public static class AcquisitionScoringIdentity
{
    /// <summary>The rendered segment, computed once from the two shipped version tokens.</summary>
    public static string Segment { get; } =
        $"acq={DescriptorEscaping.Escape(AcquisitionAgreementScan.Version)};"
            + $"supersede={DescriptorEscaping.Escape(CorporateActionSupersede.Version)};";
}
