namespace Radar.Application.Evidence;

public interface IEvidenceNormalizer
{
    NormalizedEvidence Normalize(string? title, string rawText);
}
