using Microsoft.Extensions.Logging;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.SignalExtraction;

/// <summary>
/// Deterministic, keyword-based <see cref="ISignalExtractor"/> for offline pipeline runs and
/// reproducible tests. It scans the evidence searchable text (<see cref="EvidenceItem.Title"/> joined
/// to <see cref="EvidenceItem.RawText"/>) for a fixed, small table of phrases mapped to MVP
/// <see cref="SignalType"/>s and emits typed <see cref="ExtractedSignal"/>s with a verbatim excerpt
/// taken from the matched title-or-body text (provenance). It performs <b>no</b> entity resolution:
/// <see cref="ExtractedSignal.CompanyMention"/> is the evidence <see cref="EvidenceItem.SourceName"/>
/// placeholder and a company/ticker is never guessed. The placeholder heuristics here are not a tuned
/// scoring model; the real AI extractor is a later, human-owned slice.
/// </summary>
public sealed class KeywordSignalExtractor : ISignalExtractor
{
    // Window of original-cased searchable-text characters captured on either side of a phrase match
    // so the excerpt carries surrounding context while remaining a verbatim slice of the composed
    // searchable text (Title + "\n" + RawText).
    private const int ExcerptWindow = 80;

    // Fixed, ordered, visibly-constant rule table. Phrases are matched case-insensitively as
    // substrings of the evidence searchable text. The first matching rule for a given SignalType wins
    // (deterministic dedupe). All numbers are within domain ranges (Strength/Novelty 1-10,
    // Confidence 0-1) so mapped signals pass SignalValidation. These are placeholder heuristics,
    // not a tuned model.
    private static readonly KeywordSignalRule[] Rules =
    [
        new("multi-year deal", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("selected by", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("deployment", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("contract win", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("wins contract", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("expands agreement", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("renews", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),

        new("partnership", SignalType.StrategicPartnership, SignalDirection.Positive, 5, 5, 0.6m),
        new("partners with", SignalType.StrategicPartnership, SignalDirection.Positive, 5, 5, 0.6m),
        new("teams up", SignalType.StrategicPartnership, SignalDirection.Positive, 5, 5, 0.6m),

        new("appoints", SignalType.ExecutiveHire, SignalDirection.Positive, 4, 5, 0.5m),
        new("names new", SignalType.ExecutiveHire, SignalDirection.Positive, 4, 5, 0.5m),
        new("hires", SignalType.ExecutiveHire, SignalDirection.Positive, 4, 5, 0.5m),

        new("general availability", SignalType.ProductLaunch, SignalDirection.Positive, 5, 6, 0.6m),
        new("new platform", SignalType.ProductLaunch, SignalDirection.Positive, 5, 6, 0.6m),
        new("rolls out", SignalType.ProductLaunch, SignalDirection.Positive, 5, 6, 0.6m),
        new("launches", SignalType.ProductLaunch, SignalDirection.Positive, 5, 6, 0.6m),
        new("unveils", SignalType.ProductLaunch, SignalDirection.Positive, 5, 6, 0.6m),
        new("introduces", SignalType.ProductLaunch, SignalDirection.Positive, 5, 6, 0.6m),

        new("convertible note", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("credit facility", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("debt financing", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("raises $", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("funding round", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("series ", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),

        new("raises guidance", SignalType.GuidanceChange, SignalDirection.Positive, 6, 6, 0.65m),
        new("raises outlook", SignalType.GuidanceChange, SignalDirection.Positive, 6, 6, 0.65m),

        new("cuts guidance", SignalType.GuidanceChange, SignalDirection.Negative, 6, 6, 0.65m),
        new("lowers guidance", SignalType.GuidanceChange, SignalDirection.Negative, 6, 6, 0.65m),
        new("cuts outlook", SignalType.GuidanceChange, SignalDirection.Negative, 6, 6, 0.65m),
        new("lowers outlook", SignalType.GuidanceChange, SignalDirection.Negative, 6, 6, 0.65m),

        new("government contract", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
        new("awarded contract", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
        new("department of defense", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
        new("ministry of defence", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
        new("defence contract", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
        new("defense contract", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
        new("public procurement", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
        new("government grant", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
        new("dod ", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
        new("nasa", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
        new("awarded", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
    ];

    private readonly ILogger<KeywordSignalExtractor> _logger;

    public KeywordSignalExtractor(ILogger<KeywordSignalExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(evidence);

        // Provenance: search and excerpt from the composed searchable text (Title + "\n" + RawText).
        // The mapper's excerpt-in-evidence check validates against the same composition, so a
        // title-drawn excerpt survives the round-trip.
        var searchableText = ComposeSearchableText(evidence.Title, evidence.RawText);

        var emittedTypes = new HashSet<SignalType>();
        var matches = new List<(KeywordSignalRule Rule, int Index)>();

        foreach (var rule in Rules)
        {
            if (emittedTypes.Contains(rule.Type))
                continue;

            // Match case-insensitively directly on the original-cased searchable text so the index
            // stays aligned with it (a lowercased copy can shift indices for Unicode chars whose
            // lowercasing changes length, e.g. dotted I) and to avoid extra allocations.
            var index = searchableText.IndexOf(rule.Phrase, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;

            emittedTypes.Add(rule.Type);
            matches.Add((rule, index));
        }

        // Stable ordering: by SignalType enum order, then by match index.
        matches.Sort(static (a, b) =>
        {
            var byType = ((int)a.Rule.Type).CompareTo((int)b.Rule.Type);
            return byType != 0 ? byType : a.Index.CompareTo(b.Index);
        });

        var signals = new List<ExtractedSignal>(matches.Count);
        foreach (var (rule, index) in matches)
        {
            signals.Add(new ExtractedSignal(
                CompanyMention: evidence.SourceName,
                SignalType: rule.Type.ToString(),
                Direction: rule.Direction.ToString(),
                Strength: rule.Strength,
                Novelty: rule.Novelty,
                Confidence: rule.Confidence,
                SupportingExcerpt: BuildExcerpt(searchableText, index, rule.Phrase.Length),
                Reason: $"Matched phrase '{rule.Phrase}'"));
        }

        _logger.LogDebug(
            "Extracted {SignalCount} signal(s) from evidence {EvidenceId} ({EvidenceTitle}).",
            signals.Count,
            evidence.Id,
            evidence.Title);

        var summary = $"{signals.Count} signal(s) extracted by keyword rules.";
        return Task.FromResult(new ExtractSignalsOutput(signals, summary));
    }

    // Returns a deterministic, verbatim slice of the original-cased searchable text around the match
    // so the excerpt survives the mapper's provenance check.
    private static string BuildExcerpt(string searchableText, int matchIndex, int phraseLength)
    {
        var start = Math.Max(0, matchIndex - ExcerptWindow);
        var end = Math.Min(searchableText.Length, matchIndex + phraseLength + ExcerptWindow);
        return searchableText[start..end];
    }

    // Composed searchable text for an evidence item: Title first (events lead the headline), then a
    // single newline, then the body. Null/empty fields are treated as the empty string. This must
    // agree byte-for-byte with the identical helper in ExtractedSignalMapper.
    private static string ComposeSearchableText(string? title, string? rawText) =>
        (title ?? string.Empty) + "\n" + (rawText ?? string.Empty);
}
