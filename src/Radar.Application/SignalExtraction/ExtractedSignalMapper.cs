using System.Text.RegularExpressions;

using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Domain.Validation;

namespace Radar.Application.SignalExtraction;

public static partial class ExtractedSignalMapper
{
    public static SignalMappingResult ToSignal(
        ExtractedSignal extracted,
        EvidenceItem evidence,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(extracted);
        ArgumentNullException.ThrowIfNull(evidence);

        var errors = new List<string>();

        if (!TryParseEnum<SignalType>(extracted.SignalType, out var type))
            errors.Add($"Unknown signal type '{extracted.SignalType}'.");

        if (!TryParseEnum<SignalDirection>(extracted.Direction, out var direction))
            errors.Add($"Unknown direction '{extracted.Direction}'.");

        var companyMention = (extracted.CompanyMention ?? string.Empty).Trim();
        if (companyMention.Length == 0)
            errors.Add("Company mention must not be empty.");

        // Emptiness is validated by SignalValidation below; the mapper only owns the
        // provenance check (excerpt must be traceable to the evidence title or body).
        var supportingExcerpt = (extracted.SupportingExcerpt ?? string.Empty).Trim();
        var searchableText = ComposeSearchableText(evidence.Title, evidence.RawText);
        if (supportingExcerpt.Length > 0 && !ExcerptIsInEvidence(supportingExcerpt, searchableText))
        {
            errors.Add("Supporting excerpt not found in evidence.");
        }

        var observedAtUtc = evidence.PublishedAtUtc ?? evidence.CollectedAtUtc;

        var candidate = new Signal(
            Id: Guid.NewGuid(),
            EvidenceId: evidence.Id,
            CompanyId: null,
            CompanyMention: companyMention,
            Type: type,
            Direction: direction,
            Strength: extracted.Strength,
            Novelty: extracted.Novelty,
            Confidence: extracted.Confidence,
            SupportingExcerpt: supportingExcerpt,
            Reason: (extracted.Reason ?? string.Empty).Trim(),
            ReviewStatus: SignalReviewStatus.Pending,
            ObservedAtUtc: observedAtUtc,
            CreatedAtUtc: createdAtUtc);

        errors.AddRange(SignalValidation.Validate(candidate));

        return errors.Count > 0
            ? new SignalMappingResult(null, errors)
            : new SignalMappingResult(candidate, []);
    }

    private static bool TryParseEnum<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // Reject numeric input: Enum.TryParse accepts digit-only strings (e.g. "999")
        // which must not be treated as a known enum name.
        if (IsDigitsOnly(value))
            return false;

        if (!Enum.TryParse(value, ignoreCase: true, out result))
            return false;

        // Ensure the parsed value corresponds to a defined enum member.
        return Enum.IsDefined(result);
    }

    private static bool IsDigitsOnly(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;

        foreach (var c in trimmed)
        {
            if (!char.IsDigit(c) && c != '+' && c != '-')
                return false;
        }

        return true;
    }

    private static bool ExcerptIsInEvidence(string excerpt, string? searchableText) =>
        Normalize(searchableText).Contains(Normalize(excerpt), StringComparison.Ordinal);

    // Composed searchable text for an evidence item: Title first (events lead the headline), then a
    // single newline, then the body. Null/empty fields are treated as the empty string. This must
    // agree byte-for-byte with the identical helper in KeywordSignalExtractor.
    private static string ComposeSearchableText(string? title, string? rawText) =>
        (title ?? string.Empty) + "\n" + (rawText ?? string.Empty);

    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var collapsed = WhitespaceRegex().Replace(text, " ").Trim();
        return collapsed.ToLowerInvariant();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
