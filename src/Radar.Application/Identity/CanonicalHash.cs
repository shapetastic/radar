using System.Security.Cryptography;
using System.Text;

namespace Radar.Application.Identity;

/// <summary>
/// The single definition of Radar's "canonical string → stable SHA-256 hex digest" step — the sibling of
/// <see cref="DeterministicGuid"/> for the places that want a printable content hash rather than a
/// <see cref="Guid"/>.
///
/// <para>
/// The idiom <c>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))</c> had been
/// hand-copied at every site that needed one (the judgment family-set hash, the benchmark-universe content
/// hash, the news-observation identity, the scoring-config fingerprint…). Copies of a hashing step drift
/// silently — only one copy would get the next fix, and a drifted copy re-mints every id derived from it —
/// so the CANONICALISATION stays with each caller (it is domain-specific) and the HASH lives here (it is
/// not).
/// </para>
/// <para>
/// Pure, culture-invariant and machine-independent: no I/O, no clock, no randomness, no filesystem
/// metadata, no culture-sensitive formatting (AD-3). SHA-256 is used as a deterministic content digest, not
/// as a security primitive.
/// </para>
/// <para>
/// <b>Byte-stability is a compatibility contract.</b> Persisted hashes were produced by exactly this
/// algorithm — UTF-8 bytes of the canonical string, SHA-256, lower-case hex. Changing the hash, the
/// encoding or the hex casing would re-mint every derived identity. Do not "improve" it.
/// </para>
/// </summary>
public static class CanonicalHash
{
    /// <summary>
    /// Hashes an already-canonicalised identity string. Callers own their own canonical form (namespacing,
    /// separators, escaping, number formatting) — this only hashes it.
    /// </summary>
    public static string Sha256Hex(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Convenience overload for the common builder-accumulated canonical string. Identical to
    /// <see cref="Sha256Hex(string)"/> over <c>canonical.ToString()</c>.
    /// </summary>
    public static string Sha256Hex(StringBuilder canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return Sha256Hex(canonical.ToString());
    }
}
