using Radar.Application.Collectors;
using Radar.Application.Filings;
using Radar.Application.SignalExtraction;

namespace Radar.Application.Scoring;

/// <summary>
/// Default <see cref="ISignalSourceDescriptor"/>: builds BOTH canonical strings ONCE at construction from the
/// composed signal-source set.
/// <list type="bullet">
/// <item><description>
/// <b>Identity</b> (<see cref="CanonicalDescriptor"/>) —
/// <c>rules=&lt;RuleSetVersion&gt;;[ai=…;]news=…;[newsquery=…;]</c>: the deterministic extractor's rule-set
/// identity (<see cref="KeywordSignalExtractor.RuleSetVersion"/>); then, when the opt-in AI
/// directional-filing path is registered, that source's per-signal magnitudes (its
/// <see cref="IDirectionalFilingSignalSource.ScoringDescriptor"/>, escaped); then, ALWAYS, the spec-194 §2
/// news-read identity (<see cref="NewsJudgmentScoringIdentity"/>); then, when the spec-198 news-feed recency
/// window is enabled, that window (<see cref="NewsQueryScoringIdentity"/>). This is what the
/// <c>ScoringConfigVersion</c> fingerprint hashes.
/// </description></item>
/// <item><description>
/// <b>Provenance</b> (<see cref="CollectionProvenance"/>) — <c>collectors=&lt;csv&gt;;</c>, plus a
/// <c>collection=none-this-pass;</c> marker when this pass ran no collector (spec 147), plus an
/// <c>attribution=inferred-legacy;</c> marker when this pass re-derives collector attribution for
/// pre-spec-146 evidence (spec 151). Recorded on every snapshot, hashed into nothing (spec 141).
/// </description></item>
/// </list>
/// <para>
/// The collector names come from the name-only <see cref="EnabledCollectorVocabulary"/> (spec 147), NOT from
/// the composed <see cref="IEvidenceCollector"/> instances. The descriptor always treated collectors as
/// name-only by contract; making that structural is what lets a spec-144 <c>score</c> pass — which registers
/// no collector at all — still record the truthful collector set instead of an empty one. This type therefore
/// reads only <see cref="IDirectionalFilingSignalSource.ScoringDescriptor"/> and never calls
/// <see cref="IDirectionalFilingSignalSource.ProduceAsync"/>, so it has zero side effects and stays a pure
/// function of the composed signal-source set (AD-3). When the AI source is absent (null — AI off) NOTHING is
/// appended to the identity descriptor, so the AI-off identity is byte-identical to the AI-on-minus-<c>ai=</c>
/// form.
/// </para>
/// </summary>
public sealed class SignalSourceDescriptor : ISignalSourceDescriptor
{
    /// <summary>
    /// The marker segment appended when this pass ran no collector (spec 147). It is a SEGMENT rather than a
    /// different CSV so the collector set stays in exactly one place and a reader that only knows
    /// <c>collectors=</c> keeps working.
    /// </summary>
    internal const string NoCollectionThisPassSegment = "collection=none-this-pass;";

    /// <summary>
    /// The marker segment appended when this pass re-derives collector attribution for evidence that predates
    /// spec 146's recording (spec 151). Provenance, like every other segment here: hashed into nothing, but
    /// stamped on every snapshot the inference could have touched, so a series scored over reconstructed
    /// attribution is never mistaken for one scored over first-hand attribution.
    /// </summary>
    internal const string InferredLegacyAttributionSegment = "attribution=inferred-legacy;";

    private readonly string _identityDescriptor;
    private readonly string _collectionProvenance;
    private readonly IReadOnlyList<string> _enabledCollectors;

    public SignalSourceDescriptor(
        EnabledCollectorVocabulary collectors,
        IDirectionalFilingSignalSource? aiFilingSource = null,
        CollectionPassOptions? collectionPass = null,
        CollectorAttributionOptions? attribution = null,
        NewsJudgmentScoringIdentity? newsJudgment = null,
        NewsQueryScoringIdentity? newsQuery = null)
    {
        ArgumentNullException.ThrowIfNull(collectors);

        // ONE ordered-distinct projection (owned by EnabledCollectorVocabulary since spec 147) feeds BOTH the
        // provenance CSV and EnabledCollectors() — so the snapshot's recorded collector set and a v9 channel's
        // ran/did-not-run provenance can never disagree, and neither can the spec-146 startup guard that
        // validates channel collectors against the very same list.
        var names = collectors.CollectorNames;
        _enabledCollectors = names;

        var csv = string.Join(',', names.Select(DescriptorEscaping.Escape));

        // COLLECTION PROVENANCE (spec 141), hashed into nothing. CollectorName is each collector's stable
        // provenance identifier (e.g. "RssPressReleaseCollector", "sec-edgar", "sec-form4", "usaspending",
        // "newssearch") — NOT the Radar:Collectors config "kind" token. Treat it as opaque: it is
        // delimiter-free today, but escaping keeps the serialization injective (AD-3) so a name that ever
        // contained a reserved delimiter cannot collide with a different collector set.
        //
        // SPEC 147: "no collector is CONFIGURED" and "no collection happened in THIS pass" are different
        // facts, and only the second is ever true of a standalone score pass. A bare `collectors=;` claimed
        // the first when the second was true — a lie about provenance, which is sacred. The marker segment
        // makes a score pass's record unmistakable while keeping the configured vocabulary in the CSV. Note
        // precisely what that CSV is: the collector set the SCORING process is configured with, which is not
        // necessarily the set that collected the evidence — if config changed between the collect and score
        // passes the two differ. Answering "what produced this data" per-signal is the spec's option (C),
        // deliberately deferred; the marker is what keeps this record honest in the meantime, because it says
        // "configured vocabulary, nothing collected here" rather than claiming a collection.
        // A Collected pass renders exactly what it always did, byte for byte, and never carries a second segment.
        var provenance = (collectionPass?.Kind ?? CollectionPassKind.Collected) switch
        {
            CollectionPassKind.NoCollectionThisPass => $"collectors={csv};{NoCollectionThisPassSegment}",
            _ => $"collectors={csv};",
        };

        // SPEC 151: a THIRD orthogonal fact — whether this pass re-derived collector attribution for evidence
        // that predates spec 146's recording. Appended as its own trailing segment for the same reason the
        // spec-147 marker is a segment rather than a different CSV: a reader that only knows `collectors=`
        // (and `collection=`) keeps working, and the collector set stays in exactly one place. It is appended
        // ONLY when the inference is enabled, so every pre-151 composition renders a byte-identical string.
        // Recording it is not decoration: an inference is not a recorded fact, and a snapshot that cannot say
        // which it rests on invites a backtest to treat a reconstruction as first-hand provenance.
        if (attribution?.InferLegacyAttribution == true)
        {
            provenance += InferredLegacyAttributionSegment;
        }

        _collectionProvenance = provenance;

        // STRATEGY IDENTITY (spec 141): the extractor rule-set identity, plus the AI directional-filing
        // magnitudes when that path is registered (fixed field ordering, AD-3, reusing the shared
        // DescriptorEscaping so the whole descriptor stays injective). The enabled-collector set is
        // deliberately ABSENT: a collector toggle must not move a strategy's identity, because it does not
        // change what hypothesis the strategy scores. The ai= segment stays here because it carries per-signal
        // magnitudes and the reading model, which change signal DIRECTION (spec 119) — genuinely different
        // scorings that must never share a fingerprint. The spec-147 pass kind is likewise absent: it is
        // provenance, not identity. So is the spec-151 attribution mode — it changes WHICH DATA a v9 channel
        // can see, not what hypothesis the strategy scores, and folding it in would re-stamp every v8
        // strategy in the process for a setting that cannot touch them.
        var descriptor = $"rules={KeywordSignalExtractor.RuleSetVersion};";
        if (aiFilingSource is not null)
        {
            descriptor += $"ai={DescriptorEscaping.Escape(aiFilingSource.ScoringDescriptor())};";
        }

        // SPEC 194 §2: the NEWS read's identity, appended LAST so the existing rules=/ai= prefix stays
        // byte-stable and a pin move is unambiguously attributable. Unlike the ai= segment it is
        // UNCONDITIONAL — a disabled judgment renders `news=disabled:…;` rather than nothing, because a
        // silent absence would be byte-identical to a pre-194 composition and the two are different facts
        // (spec 147's `collectors=;` reasoning). A composition that never registered the identity at all is
        // treated as disabled: it scores exactly as a disabled one does.
        //
        // Why the news read belongs on the IDENTITY side and not beside CollectionProvenance: judgment
        // enablement, the judge MODEL and the designated presentation cohort change signal DIRECTION — the
        // same argument that put the AI filing read's model in the ai= segment (spec 119). What is
        // deliberately NOT in it: API keys, call budgets and retry caps, which change only how much Radar
        // spends looking.
        descriptor += (newsJudgment ?? NewsJudgmentScoringIdentity.Disabled).Segment;

        // SPEC 198 §3: the NEWS FEED QUERY's recency window, appended LAST — after the spec-194 news=
        // segment — for exactly the reason that segment is appended after ai=: the whole post-197 prefix
        // stays byte-stable, so a pin move is unambiguously attributable to this one input.
        //
        // Why the news query belongs on the IDENTITY side: the query decides WHICH evidence exists at all.
        // A `when:{n}d` term bounds the Google News RSS response to the last n days, so it changes the
        // NewsArticle evidence Radar admits and therefore AttentionReach, OpportunityScore and every rank —
        // the same argument that put the AI reading model in ai= (spec 119) and the judge cohort in news=
        // (spec 194 §2). What is deliberately NOT in it: the retention limit, the parse ceiling, the pacing
        // delay and the locale flag — operational posture, not identity (spec 141).
        //
        // The NULL FALLBACK IS THE DEFAULT, NOT `None`, and the asymmetry with newsJudgment above is
        // deliberate. The stage-2 judgment is an opt-in step a composition must switch on, so an unaware
        // composition genuinely scores as Disabled. The recency window is a SHIPPED CODE DEFAULT that
        // applies whenever the newssearch collector runs, so an unaware composition collects the filtered
        // feed; falling back to None would stamp "no filter" over a run that filtered. A composition that
        // genuinely wants no filter configures the window to 0, which renders an EMPTY segment and
        // reproduces the pre-198 descriptor byte-for-byte.
        descriptor += (newsQuery ?? NewsQueryScoringIdentity.Default).Segment;

        // SPEC 217 §2: the ACQUISITION-RECOGNITION identity — the deterministic item-1.01 scan version and
        // the scoring-assembly supersede rule it drives.
        //
        // WHY IT IS HASHED, and hashed on the IDENTITY side. `acq-supersede-v1` REWRITES a signal the
        // formula scores: the keyword extractor's Positive StrategicPartnership read (strength 4) of a
        // recognised item-1.01 8-K becomes a Neutral CorporateAction at strength 0. That changes
        // TrajectoryScore, OpportunityScore and rank for the affected company — measured: MarineMax's
        // trajectory rose 56 → 62 on 2026-08-10 on exactly that signal. A scoring-assembly rule that can
        // move a score and is NOT hashed is the comparability hole spec 194 §2 closed for the judgment read
        // and spec 198 §3 closed for the feed query. `acqscan-v1` is folded in beside it because the
        // supersede's rule is meaningless without the recognition rule that decides which evidence it
        // applies to.
        //
        // IT IS UNCONDITIONAL, AND NOT AI-GATED — which is the whole point (spec 217 §2 says so explicitly).
        // The supersede runs in EVERY composition: it is pure assembly code, not an opt-in seam, and only
        // the DATA (whether this company has a recognised acquisition) varies. So BOTH the AI-ON and the
        // AI-OFF pin families move once, exactly as they did for spec 198's newsquery= segment and unlike
        // spec 197's / 214–216's judgment- and filing-read moves. An unchanged AI-OFF pin here would mean
        // the rule is not actually hashed.
        //
        // PLACEMENT: appended LAST, after the spec-198 newsquery= segment, for the reason every segment
        // since spec 194 has been appended last — the whole preceding prefix stays byte-stable, so this pin
        // move is unambiguously attributable to this one input and to nothing else. "Beside the extractor
        // rule-set identity" (spec 217 §2) is a statement about which SIDE it belongs to — the
        // deterministic, always-present, non-AI-gated side that `rules=` heads — not about byte position;
        // inserting it after `rules=` would have re-ordered every existing segment for no gain and made the
        // move indistinguishable from a re-ordering.
        //
        // What is deliberately NOT in it: AcquisitionRecognitionOptions.MaxFetchesPerRun, which bounds how
        // many filings are READ per run and never whether a filing that IS read is recognised — the same
        // operational-cap exclusion spec 105 applied to the directional read's caps.
        descriptor += AcquisitionScoringIdentity.Segment;

        _identityDescriptor = descriptor;
    }

    /// <inheritdoc />
    public string CanonicalDescriptor() => _identityDescriptor;

    /// <inheritdoc />
    public string CollectionProvenance() => _collectionProvenance;

    /// <inheritdoc />
    public IReadOnlyList<string> EnabledCollectors() => _enabledCollectors;
}
