using System.Globalization;

using Microsoft.Extensions.Logging;
using Radar.Application.Collectors;
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
/// Beyond pure keyword scanning it is <b>source/metadata-aware in exactly three defined ways</b>:
/// (1) <see cref="EvidenceSourceType.NewsArticle"/> evidence emits exactly one <b>Neutral
/// <see cref="SignalType.MediaAttention"/></b> signal — the directional keyword rules are suppressed for
/// news, since third-party coverage is an attention event, not the company's own disclosure (spec 70) —
/// the one and only <see cref="EvidenceItem.SourceType"/>-driven branch; and TWO <b>metadata-driven</b>
/// materiality reads that scale an already-fired signal's <b>Strength</b> through one generic mechanism:
/// (2) a <see cref="SignalType.GovernmentContract"/> Positive signal by the <c>awardAmount</c> key (spec 66),
/// and (3) a <see cref="SignalType.InsiderBuying"/> Positive-or-Negative signal by the <c>insiderNetValue</c>
/// key (the Form 4 collector's discretionary buy/sell $; spec 93), with an additional cluster boost (capped at
/// the domain max 10) when the filing carries an <c>insiderCluster</c> flag (>= 2 insiders same direction). The
/// insider buy/sell materiality tiers and the cluster boost are config-tunable magnitudes injected via
/// <see cref="InsiderMaterialityWeights"/> (spec 96, default == spec 93); the GovernmentContract award tiers
/// remain code constants. Both materiality reads share the generic
/// <c>TryGetDecimalMetadata</c>/<c>StrengthForAmount(amount, tiers)</c> helper — each key is parsed once per
/// evidence. All other signal types stay purely keyword-driven and read neither source type nor metadata.
/// </para>
/// <para>
/// WATCH-ITEM: the InsiderBuying read (3) is <b>metadata-driven, not <see cref="EvidenceSourceType"/>-driven</b>,
/// so it extends the existing generic materiality mechanism rather than adding a third inline source-type
/// branch — there is still exactly ONE <see cref="EvidenceSourceType"/> branch (the NewsArticle one). If a
/// <b>second</b> source-type special-case is ever needed, refactor to an explicit per-<see cref="EvidenceSourceType"/>
/// dispatch/strategy rather than adding a second inline source-type branch here.
/// </para>
/// </summary>
public sealed class KeywordSignalExtractor : ISignalExtractor
{
    // The deterministic extractor's rule-set IDENTITY — the phrase→direction/strength table shape. It is
    // folded into the ScoringConfigVersion content fingerprint (via SignalSourceDescriptor / AD-10) and is
    // human-bumped when the rule table changes in a scoring-affecting STRUCTURAL way, exactly as
    // _formula.Version is bumped for a formula-shape change (AD-6). Spec 96: the insider materiality
    // magnitudes now live in config (InsiderMaterialityWeights) and are hashed by VALUE into the fingerprint,
    // so a tier MAGNITUDE change no longer needs a RuleSetVersion bump — only a rule STRUCTURE change does.
    public const string RuleSetVersion = "radar-keyword-rules-v1";

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

        // CapitalRaise, ordered Negative -> Positive -> Neutral. Dilution/distress capital events are
        // Negative and ordered FIRST so they win first-match-per-type over a co-occurring "raises $" (a
        // registered direct offering that also says "raises $30 million" resolves to Negative). The phrases
        // are deliberately multi-word/specific to avoid false positives — no bare "offering" ("product
        // offering"), no bare "warrant"/"warranty". Going-concern / substantial-doubt is NOT here: it lives
        // in 10-K/10-Q body text Radar does not ingest (SEC evidence carries only 8-K item-title metadata),
        // so a keyword rule would never fire — deferred to a future AI/full-text distress read.
        new("rights offering", SignalType.CapitalRaise, SignalDirection.Negative, 6, 5, 0.6m),
        new("registered direct offering", SignalType.CapitalRaise, SignalDirection.Negative, 6, 5, 0.6m),
        new("at-the-market offering", SignalType.CapitalRaise, SignalDirection.Negative, 6, 5, 0.6m),
        new("atm offering", SignalType.CapitalRaise, SignalDirection.Negative, 6, 5, 0.6m),
        new("shelf registration", SignalType.CapitalRaise, SignalDirection.Negative, 5, 5, 0.55m),
        new("shelf offering", SignalType.CapitalRaise, SignalDirection.Negative, 5, 5, 0.55m),
        new("reverse stock split", SignalType.CapitalRaise, SignalDirection.Negative, 6, 5, 0.6m),
        new("warrants to purchase", SignalType.CapitalRaise, SignalDirection.Negative, 5, 5, 0.55m),
        new("dilution", SignalType.CapitalRaise, SignalDirection.Negative, 5, 4, 0.5m),
        new("dilutive", SignalType.CapitalRaise, SignalDirection.Negative, 5, 4, 0.5m),
        // Positive venture cues: a named funding round / "series" raise / "raises $" is a growth-leaning
        // primary-market capital event.
        new("raises $", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("funding round", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("series a", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("series b", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("series c", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        new("series seed", SignalType.CapitalRaise, SignalDirection.Positive, 5, 5, 0.6m),
        // Neutral cues: debt/hybrid capital events whose valence the code cannot read (a distressed
        // refinancing reads identical to expansion financing at the keyword level; a convertible can be
        // accretive or a death spiral) -> Neutral, contributing 0 to Trajectory.
        new("convertible note", SignalType.CapitalRaise, SignalDirection.Neutral, 4, 5, 0.5m),
        new("credit facility", SignalType.CapitalRaise, SignalDirection.Neutral, 4, 5, 0.5m),
        new("debt financing", SignalType.CapitalRaise, SignalDirection.Neutral, 4, 5, 0.5m),
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

        // InsiderBuying (SEC Form 4; spec 93). Each fixed phrase is chosen by the Form 4 collector after it
        // deterministically classifies the filing's transaction codes (the extractor only maps phrase ->
        // direction, exactly like the GovernmentContract precedent). Ordered Negative -> Positive -> Neutral
        // so a defensive mixed phrase (should never occur — the collector emits one phrase) resolves
        // conservatively. Positive/Negative Strength is scaled by the insiderNetValue metadata materiality
        // tiers below; the Neutral routine phrase keeps its fixed Strength.
        new("insider open-market sale", SignalType.InsiderBuying, SignalDirection.Negative, 6, 5, 0.6m),
        new("insider open-market purchase", SignalType.InsiderBuying, SignalDirection.Positive, 6, 5, 0.6m),
        new("insider stock transaction (routine)", SignalType.InsiderBuying, SignalDirection.Neutral, 3, 4, 0.45m),
    ];

    // Metadata-aware materiality tiers (spec 66 + spec 93): scale an already-fired signal's Strength by a $
    // amount read from a generic, provider-neutral evidence-metadata key (NOT from any source type). These are
    // two of the three defined source/metadata-aware behaviours of the extractor (the third being the spec 70
    // NewsArticle -> Neutral MediaAttention branch in ExtractAsync). Both reads share one generic mechanism
    // (TryGetDecimalMetadata + StrengthForAmount(amount, tiers)) — see the per-match Strength selection below.
    //
    // Each table is ordered DESCENDING by threshold, with inclusive-lower / exclusive-upper boundaries: e.g.
    // exactly $1,000,000 maps to the GovernmentContract Strength 6 and $999,999.99 maps to 4. Monotonic
    // non-decreasing in amount; every Strength is within domain range (1-10) so mapped signals still pass
    // SignalValidation.
    //
    // GovernmentContract (awardAmount key; federal-contract magnitudes) stays a CODE CONSTANT — only the
    // InsiderBuying magnitudes moved to config in spec 96 (a parallel GovernmentContract config move is a
    // possible future slice):
    //   >= $100,000,000  -> 9   very large, thesis-moving award
    //   >= $10,000,000   -> 8   large, clearly material award
    //   >= $1,000,000    -> 6   baseline material award (equals the old fixed Strength => no regression)
    //   >= $100,000      -> 4   small but real, modest thesis contribution
    //   <  $100,000      -> 2   sub-material routine order; deliberately <= 2 so the existing
    //                           DeterministicSignalReviewer (MinMaterialStrength = 3, strict < 3) flags
    //                           it NeedsMoreEvidence — reuse that guardrail, do not add a drop path.
    private static readonly IReadOnlyList<InsiderMaterialityTier> GovernmentContractAmountTiers =
    [
        new(100_000_000m, 9),
        new(10_000_000m, 8),
        new(1_000_000m, 6),
        new(100_000m, 4),
        new(decimal.MinValue, 2),
    ];

    // InsiderBuying (insiderNetValue key; discretionary insider buy/sell $, spec 93) magnitudes now come from
    // the injected InsiderMaterialityWeights (spec 96): a Positive signal scales via BuyTiers, a Negative via
    // SellTiers, and a multi-insider cluster adds ClusterBoost (capped at the domain max). Defaults == spec 93,
    // so a blank/absent config is byte-identical. The buy/sell tables are hashed by value into the
    // ScoringConfigVersion fingerprint, so a magnitude change re-stamps automatically without a RuleSetVersion
    // bump. A Neutral routine phrase or an absent/zero value keeps the fixed rule Strength 3/6.

    private readonly ILogger<KeywordSignalExtractor> _logger;
    private readonly InsiderMaterialityWeights _insiderWeights;

    public KeywordSignalExtractor(ILogger<KeywordSignalExtractor> logger, InsiderMaterialityWeights insiderWeights)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(insiderWeights);
        insiderWeights.Validate();
        _logger = logger;
        _insiderWeights = insiderWeights;
    }

    public Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(evidence);

        // Third-party news coverage is inherently a source-type signal, not a phrase signal: the EXISTENCE of
        // NewsArticle evidence is the attention event (spec 70). Emit exactly one Neutral MediaAttention signal
        // and return, deliberately SUPPRESSING the directional keyword rules for news (news framing != the
        // company's own disclosure; avoids double-counting a press release + its news echo — see spec 70).
        // This is the one and only EvidenceSourceType-driven branch in this deterministic extractor. (The
        // spec 66 GovernmentContract and spec 93 InsiderBuying reads are metadata-driven, not source-type-driven,
        // so they do NOT count as source-type branches — see the class XML doc.) All keyword behaviour for other
        // sources is unchanged below.
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

        // Parse each materiality key ONCE per evidence (not per rule) for determinism and efficiency. Any
        // absent/blank/unparseable/malformed input yields false and never throws.
        var hasAwardAmount = TryGetDecimalMetadata(evidence, "awardAmount", out var awardAmount);
        var hasInsiderNetValue = TryGetDecimalMetadata(evidence, "insiderNetValue", out var insiderNetValue);
        // Multi-insider cluster flag (spec 93): parsed ONCE per evidence, applied only to a directional
        // InsiderBuying signal below (a +1 boost to the materiality tier Strength, capped at the domain max).
        var insiderCluster = TryGetBoolMetadata(evidence, "insiderCluster");

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
            // Two metadata-driven materiality reads through one generic mechanism; every other signal keeps
            // its fixed rule Strength. A GovernmentContract Positive scales by awardAmount (spec 66); an
            // InsiderBuying Positive-OR-Negative scales by insiderNetValue (spec 93 — buy or sell materiality),
            // and additionally gets +1 (capped at the domain max 10) when the filing is a multi-insider cluster
            // (>= 2 owners transacting the same direction — a stronger read than one). A Neutral routine phrase
            // or an absent/zero value keeps the fixed rule Strength; the cluster boost never applies to
            // GovernmentContract or the Neutral routine phrase. Novelty, Confidence, excerpt, CompanyMention,
            // Reason, ordering and dedupe are unchanged — this calibrates Strength alone.
            int strength;
            if (hasAwardAmount
                && rule.Type == SignalType.GovernmentContract
                && rule.Direction == SignalDirection.Positive)
            {
                strength = StrengthForAmount(awardAmount, GovernmentContractAmountTiers);
            }
            else if (hasInsiderNetValue
                && rule.Type == SignalType.InsiderBuying
                && (rule.Direction == SignalDirection.Positive || rule.Direction == SignalDirection.Negative))
            {
                // A Positive (buy) scales via BuyTiers, a Negative (sell) via SellTiers — separate tables so a
                // deliberate buy-vs-sell asymmetry is expressible from config with no code change (spec 96).
                var tiers = rule.Direction == SignalDirection.Positive
                    ? _insiderWeights.BuyTiers
                    : _insiderWeights.SellTiers;
                strength = StrengthForAmount(insiderNetValue, tiers);
                if (insiderCluster)
                {
                    // Multi-insider cluster: several insiders acting together is a stronger read; add the
                    // config ClusterBoost, capped at the domain max so mapped signals still pass SignalValidation.
                    strength = Math.Min(10, strength + _insiderWeights.ClusterBoost);
                }
            }
            else
            {
                strength = rule.Strength;
            }

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

    // Generic materiality read: reads the invariant-culture decimal at root -> "metadata" object -> the given
    // key from the nested evidence-metadata envelope. Defensive at every hop; returns false (and amount = 0)
    // for null/blank MetadataJson, malformed JSON, a missing/mistyped property, a blank/unparseable value, or
    // a non-positive amount. A collector may serialize a "0" sentinel for a missing/non-numeric value, so a
    // "0" (or negative) is treated as an absent amount here and the caller keeps the fixed rule Strength
    // rather than mapping to the floor tier. Never throws. Used for both awardAmount (spec 66) and
    // insiderNetValue (spec 93) — one mechanism, one parse per key per evidence.
    private static bool TryGetDecimalMetadata(EvidenceItem evidence, string key, out decimal amount)
    {
        amount = 0m;

        EvidenceMetadata.TryRead(evidence.MetadataJson, out var metadata, out _);
        if (!metadata.TryGetValue(key, out var value))
            return false;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
            || amount <= 0m)
        {
            amount = 0m;
            return false;
        }

        return true;
    }

    // Generic boolean-flag read from the nested evidence-metadata envelope: root -> "metadata" object -> the
    // given key, treated as true when the value is "true"/"1" (case-insensitive, trimmed). Defensive at every
    // hop; returns false for null/blank/malformed JSON, a missing/mistyped property, or any other value.
    // Never throws. Used for the InsiderBuying cluster flag (spec 93), mirroring the single-parse
    // TryGetDecimalMetadata pattern.
    private static bool TryGetBoolMetadata(EvidenceItem evidence, string key)
    {
        EvidenceMetadata.TryRead(evidence.MetadataJson, out var metadata, out _);
        if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) || trimmed == "1";
    }

    // Walks the given descending tier table and returns the Strength of the first tier whose lower bound is
    // at or below the amount. The floor tier's bound is decimal.MinValue, so any amount maps. One signature
    // serves both the const GovernmentContract tiers and the config-injected InsiderBuying buy/sell tiers.
    private static int StrengthForAmount(decimal amount, IReadOnlyList<InsiderMaterialityTier> tiers)
    {
        foreach (var tier in tiers)
        {
            if (tier.MinInclusive <= amount)
                return tier.Strength;
        }

        // Unreachable: the floor tier (decimal.MinValue) always matches. Kept as a defensive fallback —
        // return the last (floor) tier's Strength rather than a constant, so it stays correct if the tier
        // table evolves (both tables are validated non-empty).
        return tiers[^1].Strength;
    }
}
