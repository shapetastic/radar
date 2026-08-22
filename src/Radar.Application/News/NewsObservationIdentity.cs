using System.Security.Cryptography;
using System.Text;

using Radar.Application.Identity;

namespace Radar.Application.News;

/// <summary>
/// The single definition of a news observation's IDENTITY (spec 177 §4): the payload hash over a VERSIONED
/// canonical encoding of the exact bounded provider fields, and the deterministic observation id derived
/// from the normalized landing URL plus that hash.
/// <para>
/// The same URL with changed provider content therefore hashes differently and becomes a LATER observation
/// — content drift is recorded, never overwritten — while re-observing byte-identical content in any later
/// run (or any later year/month partition) resolves to the SAME id and dedupes. The
/// <see cref="NewsObservationCaptureMode"/> is folded into the encoding so a legacy headline-only record, a
/// prospective RSS record and a retrospective fetch of the same URL are three distinguishable observations
/// rather than one colliding id; for a retrospective fetch the fetched content hash is folded in too, so
/// re-fetching an unchanged page is idempotent and a changed page is a new observation.
/// </para>
/// <para>
/// The canonical encoding is length-prefixed (<c>{utf8ByteCount}:{bytes}</c>, <c>-1:</c> for null) rather
/// than delimiter-joined, so a headline containing a delimiter can never collide with a different field
/// split — and <c>null</c> is distinguishable from the empty string (an absent description is a different
/// observation than an empty one). The Guid step reuses the shared
/// <see cref="DeterministicGuid.FromCanonicalString"/> (spec 145's extraction) — never a second copy.
/// </para>
/// </summary>
public static class NewsObservationIdentity
{
    /// <summary>
    /// The canonical-encoding version. Changing the encoding (field set, order, prefix scheme) re-mints
    /// every observation id and payload hash — treat it as a persisted format constant and bump it only
    /// with a deliberate migration story.
    /// </summary>
    public const string PayloadEncodingVersion = "news-payload-v1";

    /// <summary>
    /// The id namespace, folded into the canonical string so observation ids can never collide with any
    /// other deterministic-Guid family Radar derives (seed rows, evidence ids).
    /// </summary>
    private const string IdNamespace = "radar:news-observation:";

    /// <summary>
    /// SHA-256 (lower-case hex) over the versioned canonical encoding of the exact bounded provider fields:
    /// capture mode, landing URL, headline, publisher, raw description, and — for a retrospective fetch —
    /// the fetched content hash. Explicitly EXCLUDED (each varies between two retrievals of the same
    /// payload): retrieval/publication timestamps, run/batch ids, feed binding, company attribution and the
    /// plain-text description rendering (derived, so hashing it would double-count the raw field).
    /// </summary>
    public static string ComputePayloadHash(
        NewsObservationCaptureMode captureMode,
        string landingUrl,
        string headline,
        string publisher,
        string? descriptionRaw,
        string? fetchedContentHash = null)
    {
        ArgumentNullException.ThrowIfNull(landingUrl);
        ArgumentNullException.ThrowIfNull(headline);
        ArgumentNullException.ThrowIfNull(publisher);

        var canonical = new StringBuilder(PayloadEncodingVersion)
            .Append('|');
        AppendLengthPrefixed(canonical, captureMode.ToString());
        AppendLengthPrefixed(canonical, NormalizeLandingUrl(landingUrl));
        AppendLengthPrefixed(canonical, headline);
        AppendLengthPrefixed(canonical, publisher);
        AppendLengthPrefixed(canonical, descriptionRaw);
        AppendLengthPrefixed(canonical, fetchedContentHash);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// The stable observation id for a (normalized landing URL, payload hash) pair. Pure and
    /// culture-invariant: same inputs, same <see cref="Guid"/>, in every process, forever.
    /// </summary>
    public static Guid ObservationIdFor(string landingUrl, string payloadHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(landingUrl);
        ArgumentException.ThrowIfNullOrEmpty(payloadHash);

        return DeterministicGuid.FromCanonicalString(
            IdNamespace + NormalizeLandingUrl(landingUrl) + "|" + payloadHash);
    }

    /// <summary>
    /// Landing-URL normalization for identity purposes: trim only. Deliberately conservative — Google News
    /// landing URLs are opaque stable tokens, and any cleverer canonicalisation (case-folding, query-param
    /// stripping) risks merging genuinely distinct articles. The payload hash already guards content.
    /// </summary>
    private static string NormalizeLandingUrl(string landingUrl) => landingUrl.Trim();

    private static void AppendLengthPrefixed(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }

        canonical
            .Append(Encoding.UTF8.GetByteCount(value))
            .Append(':')
            .Append(value);
    }
}
