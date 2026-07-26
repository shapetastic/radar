using System.Security.Cryptography;
using System.Text;

namespace Radar.Application.Identity;

/// <summary>
/// The single definition of Radar's "canonical string → stable <see cref="Guid"/>" step.
///
/// <para>
/// Several places need an id that is a pure function of the thing it identifies rather than of the run
/// that minted it — a seed alias/source-feed row that must upsert onto the same row on every re-seed
/// (<c>LocalFileCompanySeedSource</c>), and an evidence item that must resolve to the same record across
/// runs and across collectors (<see cref="Radar.Application.Evidence.EvidenceIdentity"/>, spec 145).
/// They differ in what their canonical string IS; they must not differ in how that string becomes a
/// <see cref="Guid"/>. Two copies of this step would silently drift (only one copy gets the next fix), so
/// the CANONICALISATION stays with each caller (it is domain-specific) and the HASH lives here (it is not).
/// </para>
/// <para>
/// Pure and culture-invariant: no I/O, no clock, no randomness, no culture-sensitive formatting. MD5 is
/// used purely as a fast non-cryptographic way to obtain a deterministic 128-bit value from a string —
/// <b>never</b> for security, and never as a content-integrity claim (evidence content integrity is the
/// SHA-256 <c>ContentHash</c> the <c>EvidenceNormalizer</c> computes).
/// </para>
/// <para>
/// <b>Byte-stability is a compatibility contract.</b> Existing persisted ids were produced by exactly this
/// algorithm — UTF-8 bytes of the canonical string, MD5, reinterpreted through <c>new Guid(byte[])</c>.
/// Changing the hash, the encoding, or the byte-to-Guid reinterpretation would re-mint every derived id and
/// orphan the rows/files that reference them. Do not "improve" it.
/// </para>
/// </summary>
public static class DeterministicGuid
{
    /// <summary>
    /// Derives the stable <see cref="Guid"/> for an already-canonicalised identity string. Callers own
    /// their own canonical form (namespacing, separators, normalisation) — this only hashes it.
    /// </summary>
    public static Guid FromCanonicalString(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);

        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(bytes);
    }
}
