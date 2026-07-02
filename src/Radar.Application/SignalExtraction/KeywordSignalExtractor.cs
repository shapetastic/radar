using System.Globalization;
using System.Text.Json;

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
/// <para>
/// Beyond pure keyword scanning it is <b>source-type-aware in exactly two defined ways</b>:
/// (1) <see cref="EvidenceSourceType.NewsArticle"/> evidence emits exactly one <b>Neutral
/// <see cref="SignalType.MediaAttention"/></b> signal — the directional keyword rules are suppressed for
/// news, since third-party coverage is an attention event, not the company's own disclosure (spec 70);
/// (2) a <see cref="SignalType.GovernmentContract"/> signal's <b>Strength</b> is scaled by the award
/// amount read from <c>evidence</c> metadata (the <c>awardAmount</c> key; spec 66). All other signal
/// types stay purely keyword-driven and read neither <see cref="EvidenceItem.SourceType"/> nor metadata.
/// </para>
/// <para>
/// WATCH-ITEM: these are two justified branches. If a <b>third</b> source-type special-case is ever
/// needed, refactor to an explicit per-<see cref="EvidenceSourceType"/> dispatch/strategy rather than
/// adding a third inline branch here — do not keep growing inline special-cases.
/// </para>
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
        new("selected to", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("customer deployment", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("production deployment", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("contract win", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("wins contract", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("project win", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("production order", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("largest order", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("receives order", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("expands agreement", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("renews contract", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),
        new("renews agreement", SignalType.CustomerWin, SignalDirection.Positive, 6, 5, 0.6m),

        new("partnership", SignalType.StrategicPartnership, SignalDirection.Positive, 5, 5, 0.6m),
        new("partners with", SignalType.StrategicPartnership, SignalDirection.Positive, 5, 5, 0.6m),
        new("teams up", SignalType.StrategicPartnership, SignalDirection.Positive, 5, 5, 0.6m),
        // SEC 8-K item titles (1.01 / 2.01): inherently growth-leaning corporate events. Also legitimate
        // press-release phrases, so no source-coupling is introduced.
        new("material definitive agreement", SignalType.StrategicPartnership, SignalDirection.Positive, 4, 5, 0.5m),
        new("completion of acquisition", SignalType.StrategicPartnership, SignalDirection.Positive, 4, 5, 0.5m),

        new("appoints", SignalType.ExecutiveHire, SignalDirection.Positive, 4, 5, 0.5m),
        new("names new", SignalType.ExecutiveHire, SignalDirection.Positive, 4, 5, 0.5m),
        new("hires", SignalType.ExecutiveHire, SignalDirection.Positive, 4, 5, 0.5m),
        // SEC 8-K item 5.02 covers both departures and appointments; the code alone cannot tell which, so
        // these officer/director-change phrases are Neutral (event type without valence).
        new("appointment of certain officers", SignalType.ExecutiveHire, SignalDirection.Neutral, 4, 5, 0.5m),
        new("election of directors", SignalType.ExecutiveHire, SignalDirection.Neutral, 4, 5, 0.5m),

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
        new("series a", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("series b", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("series c", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("series seed", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        // SEC 8-K item titles (2.03 / 3.02): a debt facility or an equity issuance. Both are capital
        // events but the code reveals no directional read, so Neutral.
        new("direct financial obligation", SignalType.CapitalRaise, SignalDirection.Neutral, 4, 5, 0.5m),
        new("unregistered sales of equity", SignalType.CapitalRaise, SignalDirection.Neutral, 4, 5, 0.5m),

        new("raises guidance", SignalType.GuidanceChange, SignalDirection.Positive, 6, 6, 0.65m),
        new("raises outlook", SignalType.GuidanceChange, SignalDirection.Positive, 6, 6, 0.65m),
        new("raises full-year", SignalType.GuidanceChange, SignalDirection.Positive, 6, 6, 0.65m),
        new("exceeded outlook", SignalType.GuidanceChange, SignalDirection.Positive, 6, 6, 0.65m),
        new("above the high end", SignalType.GuidanceChange, SignalDirection.Positive, 6, 6, 0.65m),
        new("record revenue", SignalType.GuidanceChange, SignalDirection.Positive, 6, 6, 0.65m),
        new("beats expectations", SignalType.GuidanceChange, SignalDirection.Positive, 6, 6, 0.65m),

        new("cuts guidance", SignalType.GuidanceChange, SignalDirection.Negative, 6, 6, 0.65m),
        new("lowers guidance", SignalType.GuidanceChange, SignalDirection.Negative, 6, 6, 0.65m),
        new("cuts outlook", SignalType.GuidanceChange, SignalDirection.Negative, 6, 6, 0.65m),
        new("lowers outlook", SignalType.GuidanceChange, SignalDirection.Negative, 6, 6, 0.65m),
        // SEC 8-K item 2.02 title. The item code encodes "earnings released" but not beat/miss, so this is
        // Neutral — a filing whose text carries only this phrase must NOT surface a Positive trajectory
        // signal. Placed last in the GuidanceChange group so directional press-release phrases (raises /
        // cuts guidance) still win first-match-per-type when they co-occur.
        new("results of operations", SignalType.GuidanceChange, SignalDirection.Neutral, 3, 4, 0.4m),

        // Canonical federal-award cue emitted by the USASpending collector on every GovernmentContract
        // evidence item ("Federal contract award {AwardId} — {Agency} → {Recipient} …"). Placed first in
        // the GovernmentContract group so first-match-per-type claims the type uniformly for every award
        // regardless of awarding agency (DoD, HHS, GSA, VA, DOE, …). Ordinary business phrases, not a
        // source-type coupling: the phrases are matched on searchable text like every other rule.
        new("federal contract award", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
        new("contract award", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m),
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
    ];

    // First metadata-aware rule (spec 66): scales the Strength of an already-fired GovernmentContract
    // Positive signal by the award dollar amount (materiality). This is the ONLY signal type whose
    // magnitude is refined by evidence metadata; every other SignalType stays source/metadata-agnostic.
    // This metadata read is one of the two defined source/metadata-aware behaviours of the extractor
    // (the other being the spec 70 NewsArticle -> Neutral MediaAttention branch in ExtractAsync). The
    // amount is read from a generic, provider-neutral metadata key (awardAmount), not from any source type.
    //
    // Visibly-constant, ordered tier table sorted DESCENDING by threshold. Boundaries are
    // inclusive-lower / exclusive-upper: e.g. exactly $1,000,000 maps to Strength 6, and $999,999.99
    // maps to Strength 4. Monotonic non-decreasing in amount; every Strength is within domain range
    // (1-10) so mapped signals still pass SignalValidation.
    //
    //   >= $100,000,000  -> 9   very large, thesis-moving award
    //   >= $10,000,000   -> 8   large, clearly material award
    //   >= $1,000,000    -> 6   baseline material award (equals the old fixed Strength => no regression)
    //   >= $100,000      -> 4   small but real, modest thesis contribution
    //   <  $100,000      -> 2   sub-material routine order; deliberately <= 2 so the existing
    //                           DeterministicSignalReviewer (MinMaterialStrength = 3, strict < 3) flags
    //                           it NeedsMoreEvidence — reuse that guardrail, do not add a drop path.
    private static readonly (decimal MinInclusive, int Strength)[] GovernmentContractAmountTiers =
    [
        (100_000_000m, 9),
        (10_000_000m, 8),
        (1_000_000m, 6),
        (100_000m, 4),
        (decimal.MinValue, 2),
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

        // Third-party news coverage is inherently a source-type signal, not a phrase signal: the EXISTENCE of
        // NewsArticle evidence is the attention event (spec 70). Emit exactly one Neutral MediaAttention signal
        // and return, deliberately SUPPRESSING the directional keyword rules for news (news framing != the
        // company's own disclosure; avoids double-counting a press release + its news echo — see spec 70).
        // This is the second source-type-aware branch in this deterministic extractor (spec 66 was the first,
        // metadata-aware for GovernmentContract materiality); all keyword behaviour for other sources is
        // unchanged below.
        if (evidence.SourceType == EvidenceSourceType.NewsArticle)
        {
            var searchable = EvidenceSearchableText.Compose(evidence.Title, evidence.RawText);
            var excerpt = BuildExcerpt(searchable, matchIndex: 0, phraseLength: 0);   // verbatim provenance slice
            var signal = new ExtractedSignal(
                CompanyMention: evidence.SourceName,
                SignalType: SignalType.MediaAttention.ToString(),
                Direction: SignalDirection.Neutral.ToString(),
                Strength: 4,
                Novelty: 4,
                Confidence: 0.5m,
                SupportingExcerpt: excerpt,
                Reason: "Third-party news coverage (media attention)");
            return Task.FromResult(new ExtractSignalsOutput(
                new List<ExtractedSignal> { signal },
                "1 media-attention signal extracted from news coverage."));
        }

        // Provenance: search and excerpt from the composed searchable text (Title + "\n" + RawText).
        // The composition lives in the shared EvidenceSearchableText helper that the mapper also
        // uses, so a title-drawn excerpt survives the mapper's excerpt-in-evidence round-trip.
        var searchableText = EvidenceSearchableText.Compose(evidence.Title, evidence.RawText);

        // Parse the award amount ONCE per evidence (not per rule) for determinism and efficiency. Any
        // absent/blank/unparseable/malformed input yields hasAmount == false and never throws.
        var hasAmount = TryGetAwardAmount(evidence, out var awardAmount);

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
            // Only a GovernmentContract Positive signal with a parseable award amount has its Strength
            // scaled by materiality; every other signal keeps its fixed rule Strength. Novelty,
            // Confidence, excerpt, CompanyMention, Reason, ordering and dedupe are unchanged — this
            // slice calibrates Strength alone.
            var strength = hasAmount
                    && rule.Type == SignalType.GovernmentContract
                    && rule.Direction == SignalDirection.Positive
                ? StrengthForAmount(awardAmount)
                : rule.Strength;

            signals.Add(new ExtractedSignal(
                CompanyMention: evidence.SourceName,
                SignalType: rule.Type.ToString(),
                Direction: rule.Direction.ToString(),
                Strength: strength,
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

    // Reads the invariant-culture decimal award amount from the nested evidence metadata written by the
    // USASpending collector: root -> "metadata" object -> "awardAmount" string. Defensive at every hop;
    // returns false (and amount = 0) for null/blank MetadataJson, malformed JSON, a missing/mistyped
    // property, a blank/unparseable value, or a non-positive amount. The USASpending reader normalizes a
    // missing/non-numeric "Award Amount" to 0m and still serializes it, so a "0" (or negative) is treated
    // as an absent amount here and falls back to the fixed rule Strength 6 rather than the floor tier.
    // Never throws.
    private static bool TryGetAwardAmount(EvidenceItem evidence, out decimal amount)
    {
        amount = 0m;

        if (string.IsNullOrWhiteSpace(evidence.MetadataJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(evidence.MetadataJson);

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (!root.TryGetProperty("metadata", out var metadata)
                || metadata.ValueKind != JsonValueKind.Object)
                return false;

            if (!metadata.TryGetProperty("awardAmount", out var awardAmount)
                || awardAmount.ValueKind != JsonValueKind.String)
                return false;

            var value = awardAmount.GetString();
            if (string.IsNullOrWhiteSpace(value))
                return false;

            // A non-positive amount (the collector's 0m sentinel for a missing/non-numeric "Award Amount",
            // or any negative) is not a usable award magnitude: treat it as absent so the caller keeps the
            // fixed rule Strength instead of mapping to the floor tier.
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
                || amount <= 0m)
            {
                amount = 0m;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            amount = 0m;
            return false;
        }
    }

    // Walks the descending tier table and returns the Strength of the first tier whose lower bound is
    // at or below the amount. The floor tier's bound is decimal.MinValue, so any amount maps.
    private static int StrengthForAmount(decimal amount)
    {
        foreach (var (minInclusive, strength) in GovernmentContractAmountTiers)
        {
            if (minInclusive <= amount)
                return strength;
        }

        // Unreachable: the floor tier (decimal.MinValue) always matches. Kept as a defensive fallback.
        return 2;
    }
}
