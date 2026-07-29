using System.Security.Cryptography;
using System.Text;

namespace Radar.CalibrationAudit;

/// <summary>
/// The ONE definition of the study's deterministic accession ordering key (spec 162): the lowercase hex of
/// SHA-256(UTF-8(accession)). Every hash-ordered artifact in the audit — the sealed worksheet, the exhibit
/// manifest, the fetch order, and (in <c>analyze-labels.ps1</c>, which implements the same function in
/// PowerShell) the calibration probability sample and the Phase-B batching — sorts by this key ASCENDING
/// with ordinal string comparison, so the ordering is deterministic without tracking CIK-prefix order
/// (which a plain accession sort would).
/// </summary>
public static class AccessionHash
{
    public static string HexOf(string accession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accession);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(accession)));
    }
}
