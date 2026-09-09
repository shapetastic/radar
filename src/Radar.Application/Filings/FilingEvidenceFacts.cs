using System.Text.RegularExpressions;

using Radar.Application.Collectors;
using Radar.Domain.Evidence;

namespace Radar.Application.Filings;

/// <summary>Why a filing evidence item could not be resolved to (CIK, accession, form, items).</summary>
public enum FilingEvidenceRejection
{
    /// <summary>Resolved — not a rejection.</summary>
    None = 0,

    /// <summary>The evidence is not a <see cref="EvidenceSourceType.Filing"/> item.</summary>
    NotAFiling,

    /// <summary>The metadata declares no form, or a form other than the one asked for.</summary>
    FormMismatch,

    /// <summary>The filing carries no item codes, or not the item code asked for.</summary>
    ItemMismatch,

    /// <summary>The CIK/accession could not be parsed from the index <c>SourceUrl</c> — never guessed.</summary>
    UnparseableSourceUrl,

    /// <summary>
    /// The accession parsed from the URL disagrees with <c>metadata.accessionNumber</c>. The identifiers
    /// are not trustworthy, so the item is skipped rather than fetched under a guessed accession.
    /// </summary>
    AccessionMismatch,
}

/// <summary>
/// The identifiers an SEC filing evidence item carries: the CIK and DASHED accession parsed from its index
/// <c>SourceUrl</c>, the form token and the raw item-code list its metadata declares, and the primary
/// document file name when the collector recorded one.
/// </summary>
public sealed record FilingEvidenceIdentifiers(
    string Cik,
    string Accession,
    string Form,
    string Items,
    string? PrimaryDocument);

/// <summary>
/// THE shared reader of SEC filing evidence identifiers (form / item codes / CIK / dashed accession /
/// primary document), extracted from <c>DirectionalFilingSignalSource.TryResolveFiling</c> so the spec-217
/// item-1.01 acquisition recognition and the spec-119 item-2.02 earnings read consume ONE definition
/// (reuse over copy — CLAUDE.md). Two copies would drift: only one would get the next fix to the
/// items-from-title fallback or the accession cross-check, and a drifted copy here would make the two
/// readers disagree about which filings exist.
/// <para>
/// Pure and defensive at every hop (AD-3): null/blank/malformed metadata, a missing form, an unparseable
/// URL and a disagreeing accession each return a NAMED <see cref="FilingEvidenceRejection"/> so the caller
/// can COUNT the skip rather than swallowing it. Logging stays with the caller, so each reader keeps its
/// own wording.
/// </para>
/// </summary>
public static partial class FilingEvidenceFacts
{
    /// <summary>
    /// Resolves <paramref name="evidence"/> when it is an SEC filing of <paramref name="requiredForm"/>
    /// carrying <paramref name="requiredItemCode"/>.
    /// </summary>
    /// <param name="requiredForm">The form token to require (e.g. <c>8-K</c>), compared case-insensitively.</param>
    /// <param name="requiredItemCode">
    /// The exact item code to require (e.g. <c>2.02</c> for earnings, <c>1.01</c> for a material definitive
    /// agreement). Compared ordinally against the comma-separated <c>metadata.items</c> list, falling back
    /// to the <c>[items: …]</c> segment of the title for evidence collected before that key existed.
    /// </param>
    public static bool TryResolve(
        EvidenceItem evidence,
        string requiredForm,
        string requiredItemCode,
        out FilingEvidenceIdentifiers? identifiers,
        out FilingEvidenceRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredForm);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredItemCode);

        identifiers = null;

        if (evidence.SourceType != EvidenceSourceType.Filing)
        {
            rejection = FilingEvidenceRejection.NotAFiling;
            return false;
        }

        EvidenceMetadata.TryRead(evidence.MetadataJson, out var metadata, out _);

        var form = metadata.TryGetValue("form", out var f) ? f : null;
        if (form is null || !string.Equals(form, requiredForm, StringComparison.OrdinalIgnoreCase))
        {
            rejection = FilingEvidenceRejection.FormMismatch;
            return false;
        }

        // Prefer the discrete items metadata key (written by the collector); fall back to parsing the
        // "[items: ...]" segment from the Title so older evidence without the key still gates correctly.
        var items = metadata.TryGetValue("items", out var i) && !string.IsNullOrWhiteSpace(i)
            ? i
            : ParseItemsFromTitle(evidence.Title);
        if (!ContainsItem(items, requiredItemCode))
        {
            rejection = FilingEvidenceRejection.ItemMismatch;
            return false;
        }

        var parsed = ParseCikAndAccession(evidence.SourceUrl);
        if (parsed is null)
        {
            rejection = FilingEvidenceRejection.UnparseableSourceUrl;
            return false;
        }

        // Cross-check the parsed accession against the metadata accessionNumber when present; a mismatch
        // means the identifiers are not trustworthy, so skip rather than guess.
        if (metadata.TryGetValue("accessionNumber", out var metaAccession)
            && !string.IsNullOrWhiteSpace(metaAccession)
            && !string.Equals(metaAccession, parsed.Value.Accession, StringComparison.Ordinal))
        {
            rejection = FilingEvidenceRejection.AccessionMismatch;
            return false;
        }

        var primaryDocument = metadata.TryGetValue("primaryDocument", out var pd)
            && !string.IsNullOrWhiteSpace(pd)
                ? pd
                : null;

        identifiers = new FilingEvidenceIdentifiers(
            parsed.Value.Cik, parsed.Value.Accession, form, items ?? string.Empty, primaryDocument);
        rejection = FilingEvidenceRejection.None;
        return true;
    }

    /// <summary>True when the comma-separated item list contains <paramref name="code"/> exactly.</summary>
    public static bool ContainsItem(string? items, string code)
    {
        if (string.IsNullOrWhiteSpace(items))
        {
            return false;
        }

        foreach (var token in items.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(token, code, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ParseItemsFromTitle(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var match = ItemsInTitleRegex().Match(title);
        return match.Success ? match.Groups["items"].Value : null;
    }

    private static (string Cik, string Accession)? ParseCikAndAccession(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var match = IndexUrlRegex().Match(sourceUrl);
        if (!match.Success)
        {
            return null;
        }

        var cik = match.Groups["cik"].Value.TrimStart('0');
        if (cik.Length == 0)
        {
            cik = "0";
        }

        var accession = match.Groups["accession"].Value;
        return string.IsNullOrWhiteSpace(accession) ? null : (cik, accession);
    }

    [GeneratedRegex(@"\[items:\s*(?<items>[^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ItemsInTitleRegex();

    [GeneratedRegex(
        @"/edgar/data/(?<cik>\d+)/[^/]+/(?<accession>[^/]+?)-index\.html?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex IndexUrlRegex();
}
