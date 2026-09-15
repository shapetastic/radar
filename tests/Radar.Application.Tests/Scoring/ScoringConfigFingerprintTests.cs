using System.Globalization;
using System.Reflection;
using Radar.Application.Filings;
using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Infrastructure.Attention;

namespace Radar.Application.Tests.Scoring;

public sealed class ScoringConfigFingerprintTests
{
    // The canonical descriptor of the default attention tier map (spec 88 seed lists, re-based by spec 196
    // onto the four-tier Wire/Mill/Platform/Genuine policy with the inverted 0.1 unknown default).
    // Application.Tests already references Infrastructure (AD-4), so the real
    // ConfiguredAttentionSourceWeights can produce it — which is why a tier-map edit re-stamps all six pins
    // below automatically rather than needing anything here to be updated by hand.
    private static string DefaultTierDescriptor() =>
        new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default).CanonicalDescriptor();

    // The signal-source IDENTITY descriptor of the default run profile (spec 95, narrowed by spec 141): the
    // extractor rule-set identity, canonicalized. It is folded into the fingerprint after the attention
    // descriptor, so the default fingerprint value depends on it.
    //
    // SPEC 141 REMOVED THE COLLECTOR CSV FROM THIS STRING. Until this slice it also carried
    // `collectors=RssPressReleaseCollector,fda,newssearch,sec-13dg,sec-edgar,sec-form4,usaspending;` — the
    // 7-collector default — which meant enabling an eighth collector re-stamped every strategy's identity
    // even when its scores were bit-for-bit identical. The enabled-collector set is now recorded per-snapshot
    // as `CollectionProvenance` (see SignalSourceDescriptorTests) and hashed into NOTHING, so it is correctly
    // absent here. SPEC 191 moved the rule-set identity to radar-keyword-rules-v7 (the NewsArticle branch
    // took its DIRECTION from the admitted stage-2 news judgment) and SPEC 194 §1.1 moves it again, to
    // radar-keyword-rules-v8: that read consulted a company judgment WHILE extracting an article the
    // judgment had never seen, so the seam is retired and ordinary news extraction is the pre-191 Neutral
    // media-attention event once more. v8 is a CORRECTION, not a rollback to v6 — the emitted signal matches
    // v6's but the regime does not, because direction now rides a separate judgment-derived signal — and it
    // is a rule-STRUCTURE change, so it re-stamps every pin below.
    // (v6 was spec 130's TrademarkActivity group; spec 129 added RegulatoryApproval; spec 127 added
    // PatentActivity; spec 103 added HiringActivity.) SPEC 226 moves it once more, to radar-keyword-rules-v9:
    // the SEC 8-K Item 1.01 / 2.01 heading phrases mint a Neutral CorporateAction instead of a Positive
    // StrategicPartnership — a heading is an event type, not a direction. The literal below is kept equal to
    // the shipped KeywordSignalExtractor.RuleSetVersion (Compute_ChangedExtractorRuleSet_ChangesFingerprint
    // keeps its perturbation one version ahead of it).
    //
    // SPEC 194 §2 APPENDS A SECOND SEGMENT: the news-read identity, ALWAYS present, rendered here in its
    // DISABLED form because this constant is the code-default composition — nothing optional registered, the
    // same reason it carries no ai= segment. Built through the real NewsJudgmentScoringIdentity rather than
    // written as a literal, so it cannot drift from what SignalSourceDescriptor actually emits.
    //
    // SPEC 198 §3 APPENDS A THIRD SEGMENT, LAST: the news-feed QUERY identity (the recency window). Built
    // through the real NewsQueryScoringIdentity.Default so it cannot drift from the shipped
    // NewsQueryScoringIdentity.DefaultRecencyWindowDays — which is also what NewsCollectorOptions and
    // NewsWorkerOptions default off, so this constant describes what a live run stamps. A window of 0
    // renders an EMPTY segment, which is why Compute_NewsQueryWindowDisabled_ReproducesNoNewsQueryPins below can
    // reproduce every pre-198 value exactly.
    //
    // SPEC 217 §2 APPENDS A FOURTH SEGMENT, LAST: the ACQUISITION-RECOGNITION identity
    // (as shipped: acq=acqscan-v1;supersede=acq-supersede-v1; — spec 226 moved the supersede half to v2 and spec
    // 227 the scan half to acqscan-v2; the real segment is composed below). It is UNCONDITIONAL and NOT AI-gated — the
    // corporate-action supersede is pure assembly code that runs in every composition, so there is no
    // "disabled" form of it — which is precisely why BOTH the AI-OFF and the AI-ON pin families move once
    // for it (the spec-198 newsquery pattern, not the spec-197/214–216 AI-ON-only one). Built through the
    // real AcquisitionScoringIdentity.Segment rather than written as a literal, so a bump to
    // AcquisitionAgreementScan.Version or CorporateActionSupersede.Version re-stamps every pin on its own
    // instead of leaving a stale copy here.
    private static readonly string SourceDescriptor =
        "rules=radar-keyword-rules-v9;"
            + NewsJudgmentScoringIdentity.Disabled.Segment
            + NewsQueryScoringIdentity.Default.Segment
            + AcquisitionScoringIdentity.Segment;

    /// <summary>
    /// The pre-spec-198 AI-OFF descriptor: identical but for the news-query segment, which
    /// <see cref="NewsQueryScoringIdentity.None"/> renders as the empty string. It is the additivity CONTROL
    /// — a descriptor built this way must reproduce the post-197 pins byte-for-byte.
    /// </summary>
    private static readonly string SourceDescriptorWithoutNewsQuery =
        "rules=radar-keyword-rules-v9;"
            + NewsJudgmentScoringIdentity.Disabled.Segment
            + NewsQueryScoringIdentity.None.Segment
            + AcquisitionScoringIdentity.Segment;

    // The live baseline's news-read identity: scripts/run-profiles/default.json enables the stage-2 judgment
    // and designates the DeepInfra DeepSeek reader as BOTH the presentation judge and the presentation
    // stage-1 extractor. Composed through the real NewsJudgmentPresentationCohort.ComposeCohortKey — the
    // very method the run-time resolution and the config-time resolution both call — so this is byte-
    // identical to what a live run stamps, in the same way AiOnSourceDescriptor is built through the real
    // escaping rather than hand-written. The provider/model literals mirror default.json, exactly as
    // AiDirectionalDescriptor's model= field does.
    private static readonly string LiveNewsJudgmentSegment = NewsJudgmentScoringIdentityFactory
        .ForPresentationCohort(NewsJudgmentPresentationCohort.ComposeCohortKey(
            new NewsJudgmentReaderIdentity("deepinfra-deepseek", "openai", "deepseek-ai/DeepSeek-V4-Flash"),
            new NewsTypingReaderIdentity("deepinfra-deepseek", "openai", "deepseek-ai/DeepSeek-V4-Flash")))
        .Segment;

    // The insider-materiality descriptor of the default config (spec 96): the config-tunable buy/sell tiers +
    // cluster boost, folded into the fingerprint after the signal-source descriptor. Computed from the record
    // so it can't drift from the code default (== spec 93).
    private static readonly string InsiderDescriptor = new InsiderMaterialityWeights().CanonicalDescriptor();

    // The media-collapse descriptor of the default config (spec 109): the same-event media-attention collapse
    // structure (media-collapse-v2 since spec 194 §1.5) + the tunable window (default 3 days), folded in after
    // the insider-materiality descriptor. Computed from the default so it can't drift from the code default.
    private static readonly string MediaCollapseDescriptor =
        new MediaAttentionCollapse(new MediaCollapseOptions()).CanonicalDescriptor();

    // The insider-collapse descriptor of the default config (spec 224): the same-insider Form 4 collapse
    // structure (insider-collapse-v1) + the tunable window (default 30 days), folded in immediately after the
    // media-collapse descriptor and before the window. Computed from the defaults so it can't drift from the
    // code default. UNCONDITIONAL (pure assembly code, no AI gate), which is why introducing it moved BOTH
    // pin families — see Compute_DefaultConfig_MatchesPinnedFingerprint.
    private static readonly string InsiderCollapseDescriptor =
        new InsiderActivityCollapse(new InsiderCollapseOptions(), new InsiderMaterialityWeights())
            .CanonicalDescriptor();

    // The recent-signal window of the default config (spec 148), the LAST hashed field. Taken from
    // ScoringOptions rather than written as a literal 30 days so the pins below cannot silently disagree with
    // the code default: if someone changes the default window, the pinned values fail here rather than in a
    // live run six weeks later.
    private static readonly TimeSpan DefaultWindow = new ScoringOptions().Window;

    /// <summary>
    /// The default-config fingerprint, with every hashed field supplied from the code defaults. One helper so
    /// the ~20 call sites below cannot drift from each other.
    /// </summary>
    private static string DefaultFingerprint(
        string? formulaVersion = null,
        ScoringWeights? weights = null,
        string? attentionDescriptor = null,
        string? sourceDescriptor = null,
        string? insiderDescriptor = null,
        string? mediaCollapseDescriptor = null,
        string? insiderCollapseDescriptor = null,
        TimeSpan? window = null) =>
        ScoringConfigFingerprint.Compute(
            "mvp-engine-v1",
            formulaVersion ?? "radar-formula-v8",
            weights ?? new ScoringWeights(),
            attentionDescriptor ?? DefaultTierDescriptor(),
            sourceDescriptor ?? SourceDescriptor,
            insiderDescriptor ?? InsiderDescriptor,
            mediaCollapseDescriptor ?? MediaCollapseDescriptor,
            insiderCollapseDescriptor ?? InsiderCollapseDescriptor,
            window ?? DefaultWindow);

    [Fact]
    public void Compute_SameInputs_ProduceSameFingerprint()
    {
        Assert.Equal(DefaultFingerprint(), DefaultFingerprint());
    }

    [Fact]
    public void Compute_ReturnsLowercaseHexToken_OfStableLength()
    {
        var fp = DefaultFingerprint();

        const string prefix = "radar-scoring-fp-";
        Assert.StartsWith(prefix, fp, StringComparison.Ordinal);

        var hex = fp[prefix.Length..];
        Assert.Equal(12, hex.Length);
        Assert.All(hex, ch => Assert.True(Uri.IsHexDigit(ch) && !char.IsUpper(ch), $"'{ch}' must be lowercase hex"));
    }

    [Fact]
    public void Compute_IsCultureInvariant()
    {
        var invariant = DefaultFingerprint();

        var original = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal locale would corrupt any non-invariant number formatting — including the
            // spec-148 window field, whose tick count is large enough for a locale group separator to matter.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(invariant, DefaultFingerprint());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Compute_DefaultConfig_MatchesPinnedFingerprint()
    {
        // THIS PIN IS A CHANGE-DETECTOR, NOT AN INVARIANT (spec 141).
        //
        // Moving it is a NORMAL, INTENDED act that requires a conscious update in the same slice — it is not
        // "scope leakage". Its job is to make a fingerprint move IMPOSSIBLE TO MAKE ACCIDENTALLY: an
        // unnoticed default-weight, default-tier, rule-set, insider-materiality, media-collapse or scoring-
        // window drift fails here, and the author must then decide whether the move was intended and record
        // its lineage.
        //
        // What the pin no longer does is pretend the fingerprint never changes. It had already changed 17
        // times over 851 live snapshots (11 radar-scoring-fp-* + 6 legacy radar-scoring-config-vN); the
        // largest cohort is 133 snapshots ≈ 3 runs, and the spec-141 AI-ON value below had exactly 43 — one
        // single run. The score series is keyed by StrategyName now (ScoreSeriesKey), so a pin move no longer
        // fragments anything; it re-stamps recorded provenance and trips StrategyIdentityGuard, which is
        // exactly what it should do.
        //
        // Lineage: spec 133 (radar-scoring-fp-6b2f468041b9 — the 7-collector default) → spec 141, which
        // removed the enabled-collector CSV from the hashed identity altogether
        // (radar-scoring-fp-2ce20f8fc497) → SPEC 148, which folds in the two remaining output-affecting
        // inputs that had been hashed into NOTHING: the recent-signal WINDOW (a 14-day and a 30-day run
        // produce materially different Trajectory/SignalVelocity/Attention yet stamped the same value) and
        // ScoringWeights.TrajectoryCorroborationK (the v8 Trajectory denominator, and since spec 146 the v9
        // channel direction factor's denominator too). THE MOVE IS THE DELIVERABLE, per AD-10 as amended by
        // spec 141. Scoring math is byte-identical — no _formula.Version bump, no RuleSetVersion bump, not a
        // single weight edited (asserted separately by ScoringEngineTests); only the stamp differs.
        //
        // SPEC 146 DELIBERATELY DID NOT MOVE THIS PIN. It added a per-strategy Formula and a channel budget,
        // and it extracted v8's per-signal primitives into the shared ScoreSignalMath — but the default
        // strategy still names radar-formula-v8, still declares no channels (ScoringChannelSet.Empty folds in
        // as a verbatim passthrough), and the extraction preserved v8's expression shapes and accumulation
        // order, so both the hashed inputs and the scores are unchanged.
        //
        // ⚠ SPEC 148 BROKE THE "PIN == LIVE STAMP" EQUIVALENCE, and that is worth stating where the pin is.
        // Every earlier slice's pin doubled as the value a live baseline run stamps, because every hashed
        // input was a code default. The window is not: this pin is computed at the ScoringOptions CODE
        // DEFAULT of 30 days (DefaultWindow, above), while the live baseline runs at
        // Radar:ScoringWindowDays = 60 (RadarWorkerOptions/appsettings.json; scripts/run-profiles/default.json
        // does not override it) and therefore stamps radar-scoring-fp-2cbbd056ffe5 (spec 194 §2; it was
        // radar-scoring-fp-61891b37e429 after spec 194 §1.5, radar-scoring-fp-06e4781f86bb after spec 194
        // §1.1, radar-scoring-fp-58c289cd0113 under spec 191, and radar-scoring-fp-4eb2fe5d3cdf from spec
        // 148 before that; the 120-day -Profile long-window AI-OFF value moved with it at every step,
        // radar-scoring-fp-5cb9dc71f309 → f160ee8faaa6 → radar-scoring-fp-f68e6481b136). The
        // live pair is recorded in default.json's own comment, which is the operator-facing
        // record; this pin is the unit-level change-detector. Both are correct at their own window — do not
        // "reconcile" them.
        //
        // SPEC 191 MOVED THIS PIN (radar-scoring-fp-0c46e07b94db → radar-scoring-fp-be417df3b731) for the
        // RuleSetVersion bump v6 → v7, which made the NewsArticle branch take its DIRECTION from the admitted
        // stage-2 news judgment.
        //
        // SPEC 194 §1.1 MOVED IT AGAIN, DELIBERATELY: radar-scoring-fp-be417df3b731 →
        // radar-scoring-fp-023b1af1e3d4, for the RuleSetVersion bump v7 → v8. The v7 read ran DURING
        // collection while the stage-2 judge runs AFTER it, so a newly collected article could only ever
        // inherit a judgment produced from EARLIER articles it had never read — one verdict multiplied by
        // however many later headlines arrived, which is the volume/size proxy spec 191 set out to remove,
        // reintroduced through stale direction. The seam is retired and ordinary news extraction is the
        // pre-191 Neutral media-attention event again; direction now rides its own judgment-derived signal
        // (spec 194 §1.2), materialized after the judgment exists. v8 is a CORRECTION, not a rollback: the
        // emitted signal matches v6's but the regime does not.
        //
        // SPEC 194 §1.5 MOVES IT ONCE MORE, TO THE VALUE BELOW: radar-scoring-fp-023b1af1e3d4 →
        // radar-scoring-fp-a47076995bf5, for the MediaAttentionCollapse.Version bump media-collapse-v1 →
        // media-collapse-v2. That descriptor is a hashed field in its own right (mediaCollapseDescriptor),
        // so the bump re-stamps with no RuleSetVersion or _formula.Version change. v2 keeps v1's greedy
        // event-window BOUNDARIES byte-for-byte and changes only which real member of a completed bucket
        // represents it: a grounded news-judgment-signal-v1 direction now outranks an earlier ordinary
        // Neutral member, so a validated read can no longer be de-noised away by an unread duplicate. An
        // all-ordinary bucket still produces v1's exact result — the structure version moves because the
        // RULE changed, not because every outcome did.
        //
        // SPEC 194 §2 MOVES IT ONE FINAL TIME, TO THE VALUE BELOW: radar-scoring-fp-a47076995bf5 →
        // radar-scoring-fp-5036d7f73af3, for the news-read scoring identity (NewsJudgmentScoringIdentity)
        // now appended to SignalSourceDescriptor.CanonicalDescriptor() as a `news=…;` segment AFTER the
        // existing rules= and optional ai= segments. This closes the recorded AD-10 hole: judgment off/on,
        // the judge MODEL, the prospectively designated presentation cohort, the news-judgment materializer
        // identity, the trajectory→direction mapping with every strength constant, the legacy-inheritance
        // neutralization rule version and the judgment-signal supersede rule version were ALL hashed into
        // nothing, so two materially different scorings shared one stamp and ScoreSeriesKey pooled them into
        // one series. The segment is UNCONDITIONAL — a disabled judgment renders `news=disabled:…;` rather
        // than nothing — which is why this AI-OFF pin moves too; a silent absence would be byte-identical to
        // a pre-194 composition, and "judgment off" and "a Radar that predates the judgment read" are
        // different facts (spec 147's `collectors=;` reasoning). Cost controls (API keys, call budgets,
        // retry caps) are deliberately NOT folded in: they change what Radar spends, never what a judgment
        // means.
        //
        // SPEC 194 MOVED THE PINS TWICE ON ITS OWN BRANCH — once for media-collapse-v2 (§1.5) and once for
        // this segment (§2). The values in this file are the FINAL post-194 values; every earlier value
        // named above is historical lineage, kept for reconciling accrued snapshots and nothing else.
        //
        // TWO INTENTIONAL SCORING-IDENTITY MOVES IN THE SAME WEEK (spec 191's v6 → v7, then spec 194's
        // v7 → v8 plus media-collapse-v2 plus this segment), and therefore THREE semantic regimes with two
        // close discontinuities: pre-191 Neutral news, spec-191 inherited direction (known DEFECTIVE and NOT
        // a valid control cohort — do not pool it across the boundary or use it as a control), and post-194
        // grounded judgment signals. History is deliberately NOT regenerated, rewritten or backfilled
        // (AD-8/AD-1). No _formula.Version bump, no RuleSetVersion bump (it stays radar-keyword-rules-v8),
        // no weight edit.
        //
        // SPEC 196 MOVES IT AGAIN — THE THIRD SCORING-IDENTITY MOVE IN AS MANY WEEKS:
        // radar-scoring-fp-5036d7f73af3 → radar-scoring-fp-54e845330f96, for the attention publisher TIER
        // MAP, which is the `attnDesc` hashed field. Two changes, both in that one field: the unknown
        // default was INVERTED from 0.25 to 0.1 (the Mill weight — an explicit entry is now required to
        // count as NOTICE rather than to be DISCOUNTED), and the map gained the four-tier policy
        // (Wire 0.05 / Mill 0.1 / Platform 0.3 / Genuine 1.0) with ~50 publishers classified by the sampled
        // audit committed at docs/cohorts/attention-publisher-audit-v1.md. Measured cause: over the live
        // 60-day corpus at the pinned instant 2026-08-27T21:42:45.4943606Z, 50.1 % of 2,865 observations
        // were unclassified and therefore weighted 0.25 — two and a half times a Mill publisher — while
        // GENUINE notice was 0.5 %, so Attention was measuring aggregator database coverage rather than
        // market notice (mean 73.4 with 53 of 75 companies between 70 and 89: a near-uniform tax, not a
        // discriminator). No _formula.Version bump, no RuleSetVersion bump (still radar-keyword-rules-v8),
        // no MediaAttentionCollapse.Version bump (still media-collapse-v2), no weight edit, and the
        // DESCRIPTOR'S SHAPE is unchanged — tier NAMES are deliberately not hashed, so a rename with
        // identical weights and membership re-stamps nothing. The map moved; that is the only reason
        // these six values moved.
        //
        // ⚠ THE ATTENTION REGIME BEFORE THIS PIN IS NOT COMPARABLE WITH THE ONE AFTER IT. Accrued snapshots
        // keep their old attention values (history is not regenerated — AD-8/AD-1, the spec-148 precedent);
        // they were computed against a map under which half the observed volume outranked a content mill.
        // The live 60-day AI-OFF/AI-ON values are now radar-scoring-fp-8daa662a57a6 /
        // radar-scoring-fp-65eb592d0354, and the 120-day -Profile long-window values
        // radar-scoring-fp-f610244e23c6 / radar-scoring-fp-a89b6d9ad0a5.
        //
        // OPERATOR ACTION, and the ORDER is load-bearing: (1) do not touch the ignored identity records
        // while a pre-196 baseline is running; (2) after merge and BEFORE the first post-196 baseline,
        // consciously delete or re-record every configured data/scoring-configs/strategies/{name}.json;
        // (3) verify the first run reports the expected new fingerprint before treating subsequent snapshots
        // as the corrected series. That path is git-ignored, so those records cannot be committed from a
        // worktree and must not be fabricated. If step 2 is missed, StrategyIdentityGuard halts the run
        // before collection — that halt is CORRECT and must not be bypassed.
        //
        // ⚠ SPEC 197 DELIBERATELY DID NOT MOVE THIS PIN, NOR EITHER OF THE TWO AI-OFF LIVE-WINDOW PINS
        // (Compute_LiveWindowAiOffStamps_ArePinned) — AND THAT NON-MOVE IS AN ASSERTED DELIVERABLE, NOT AN
        // OMISSION. Spec 197 moved the three AI-ON pins for two reasons folded into ONE recomputation:
        // news-judgment-signal-v2 (§1.3 — the materializer/metadata identity fork, because the
        // observation→evidence match ladder changes WHICH judgments can produce a scoring input) and
        // news-judgment-prompt-v3 / news-judgment-schema-v3 (§2.2 — the forked citation grammar, which
        // enters the resolved PRESENTATION COHORT KEY). Both reach the hash through the ALREADY-HASHED
        // spec-194 §2 `news=` segment, and neither is reachable from this side: the AI-OFF descriptor
        // renders NewsJudgmentScoringIdentity.Disabled, i.e.
        // `news=disabled:legacy-news-inheritance-v1:news-judgment-supersede-v1;`, which carries NEITHER the
        // presentation cohort NOR the materializer version. A disabled pin moving here would therefore
        // indicate SCOPE LEAKAGE — some spec-197 change escaping into an input it has no business touching —
        // rather than a deliverable, which is exactly why §4 states the expected split in advance and why
        // this test is the check for it.
        //
        // Nothing else moved either: no _formula.Version bump, no KeywordSignalExtractor.RuleSetVersion bump
        // (still radar-keyword-rules-v8), no MediaAttentionCollapse.Version bump (still media-collapse-v2),
        // no NewsJudgmentSignalSupersede/LegacyNewsInheritanceNeutralization version bump, no attention tier
        // edit and no weight edit. Spec 197 §3 (moving the two repeated engine Warnings to an aggregated
        // pass-level pair) is transient diagnostic state hashed into nothing and would move NOTHING on its
        // own. See Compute_AiOnDefault_MatchesPinnedFingerprint for the AI-ON lineage and the ordered
        // post-197 operator action.
        // ⚠ SPEC 198 MOVES THIS PIN — radar-scoring-fp-54e845330f96 → radar-scoring-fp-56c8e882beed —
        // AND ALL FIVE OTHERS WITH IT. THE SIXTH SCORING-IDENTITY BOUNDARY IN THREE WEEKS (191, 194 §1.5,
        // 194 §2, 196, 197, 198). The cause is the news-feed QUERY identity: Radar:News:RecencyWindowDays
        // (default 7) is now appended to SignalSourceDescriptor.CanonicalDescriptor() as a trailing
        // `newsquery=7d;` segment, AFTER the spec-194 §2 `news=` segment.
        //
        // WHY IT IS HASHED. The feed query decides WHICH evidence exists at all: a `when:{n}d` term bounds
        // the Google News RSS response to the last n days, so it changes the NewsArticle evidence Radar
        // admits and therefore AttentionReach, OpportunityScore and every rank. It was hashed into NOTHING,
        // so narrowing the query would have moved every score while StrategyIdentityGuard stayed silent and
        // ScoreSeriesKey drew both cohorts as one continuous series - the same comparability hole spec 194
        // §2 closed for the judgment read. Measured basis for the operator itself, verified against the
        // LIVE endpoint on 2026-08-29 for the phrase "Caterpillar Inc": the unfiltered query returned 100
        // items with the oldest dated 24 Jun 2026, while `when:7d` returned 66 with the oldest dated
        // 23 Aug 2026 - so it demonstrably BOUNDS the response and does not degrade to unfiltered.
        //
        // UNLIKE SPEC 194's SEGMENT, THIS ONE IS CONDITIONAL, AND THAT IS THE ADDITIVITY PROOF. A window of
        // 0 renders the EMPTY string, so a disabled configuration reproduces the post-197 descriptor
        // byte-for-byte - asserted directly by Compute_NewsQueryWindowDisabled_ReproducesNoNewsQueryPins below,
        // for BOTH AI-off and AI-on at all three windows. Spec 194 made the opposite choice because
        // "judgment off" and "a Radar that predates the judgment read" are different facts; here the input
        // is a plain magnitude with a code default, and rendering `newsquery=0d;` would have re-stamped
        // every composition for a filter that does nothing. Because the segment is NOT judgment-gated, BOTH
        // the AI-off and the AI-on pins move - an unchanged AI-off pin would mean the window is not actually
        // hashed (spec 198 §3 says so explicitly), which is the exact opposite of spec 197's split.
        //
        // Nothing else moved: no _formula.Version bump, no KeywordSignalExtractor.RuleSetVersion bump (still
        // radar-keyword-rules-v8), no MediaAttentionCollapse.Version bump (still media-collapse-v2), no
        // supersede/neutralization rule bump, no attention tier edit, no weight edit, and
        // Radar:News:MaxRecordsPerCompany / the 100-item parse ceiling / request count / pacing are all
        // untouched (spec 198 §5).
        //
        // BOTH DERIVATIONS AGREE (the spec-194/196/197 practice): each of the six values was computed
        // through ScoringConfigFingerprint.Compute over the real descriptors AND re-derived outside .NET by
        // rebuilding the canonical string, writing it with no trailing newline and hashing it with
        // sha256sum.
        //
        // ⚠ THE COLLECTION REGIME BEFORE THIS BOUNDARY IS NOT COMPARABLE WITH THE ONE AFTER IT.
        // Pre-198 runs read an unfiltered feed whose median item was weeks old and spent most of the 25-slot
        // budget re-reading known articles; post-198 runs read a 7-day window (a company's FIRST collection
        // stays unfiltered, spec 198 §2, so seeding still acquires back history). History is deliberately
        // NOT regenerated, rewritten or backfilled (AD-8/AD-1, the spec-148 precedent).
        //
        // OPERATOR ACTION AFTER SPEC 198 - and the ORDER is load-bearing: (1) do not touch the ignored
        // identity records while a pre-198 baseline is running; (2) after merge and BEFORE the first
        // post-198 baseline, consciously delete or re-record every configured
        // data/scoring-configs/strategies/{name}.json; (3) verify the first run reports
        // radar-scoring-fp-11240da5aeb0 (the shipped profile is AI-ON at 60 days) before treating later
        // snapshots as the corrected series. That path is git-ignored, so those records cannot ride in a PR
        // and MUST NEVER be fabricated. If step 2 is missed, StrategyIdentityGuard halts the run before
        // collection - that halt is CORRECT and must not be bypassed.
        //
        // ⚠ SPEC 217 §2 MOVES THIS PIN — radar-scoring-fp-56c8e882beed → radar-scoring-fp-66fca8c5f1fc —
        // AND ALL FIVE OTHERS WITH IT, INCLUDING THE THREE AI-OFF ONES. That BOTH sides move is the
        // deliverable, and it is the spec-198 pattern rather than the spec-197/214-216 one: the cause is a
        // trailing `acq=acqscan-v1;supersede=acq-supersede-v1;` segment appended to
        // SignalSourceDescriptor.CanonicalDescriptor() AFTER the spec-198 `newsquery=` segment, and it is
        // UNCONDITIONAL — not AI-gated, not judgment-gated.
        //
        // WHY IT IS HASHED. `acq-supersede-v1` REWRITES a signal the formula scores: the keyword extractor's
        // POSITIVE StrategicPartnership read (strength 4) of an item-1.01 8-K that `acqscan-v1` recognised as
        // an agreement to acquire the company becomes a NEUTRAL CorporateAction at strength 0. That changes
        // TrajectoryScore, OpportunityScore and rank. Measured, on the live store: MarineMax's 2026-08-10
        // merger 8-K minted exactly that signal, trajectory rose 56 → 62, and the report labelled the
        // company "Thesis improving" — a $1.5B all-cash sale of the whole company read as a partnership. A
        // scoring-assembly rule that can move a score and is hashed into NOTHING is the same comparability
        // hole spec 194 §2 closed for the judgment read and spec 198 §3 for the feed query.
        //
        // WHY IT IS UNCONDITIONAL, and why an unchanged AI-OFF pin would be the DEFECT. The supersede is
        // pure assembly code inside ScoringEngine — it runs in every composition, and only the DATA (whether
        // a company has a recognised acquisition) varies. There is therefore no "disabled" form to render,
        // and if the AI-OFF halves had not moved, the rule would not actually be hashed. Contrast specs
        // 197 / 214 / 215 / 216, whose inputs ride `news=enabled:…` or the `ai=` descriptor and therefore
        // CANNOT reach the disabled composition.
        //
        // Nothing else moved: no _formula.Version bump, no KeywordSignalExtractor.RuleSetVersion bump (still
        // radar-keyword-rules-v8 — the "material definitive agreement" rule is deliberately UNCHANGED, spec
        // 217 §2; ⚠ spec 226 later DID change it, to a Neutral CorporateAction under v9 — see below), no
        // MediaAttentionCollapse.Version bump (still media-collapse-v2), no
        // supersede/neutralization version bump for the pre-217 transforms, no attention tier edit, no
        // weight edit, and AcquisitionRecognitionOptions.MaxFetchesPerRun is deliberately EXCLUDED (it
        // bounds how many filings are read, never whether a read filing is recognised — the spec-105 rule).
        //
        // ⚠ OPERATOR ACTION WAS OWED — a THIRD, SEPARATE step in this arc, not one shared with 214/215 (whose
        // step was performed 2026-09-08) or with 216 (which required its own). SINCE SATISFIED:
        // run-20260909T234658242Z-5c6644f6 stamped the spec-219 value, so no 216/217/219 step remains
        // outstanding. The step was: delete or re-record every
        // configured data/scoring-configs/strategies/{name}.json BEFORE the first post-217 baseline; that
        // path is git-ignored, so those records cannot ride in a PR and MUST NEVER be fabricated. If the
        // step is missed, StrategyIdentityGuard halts the run before collection — that halt is CORRECT.
        //
        // ⚠ THE COHORT BEFORE THIS BOUNDARY IS NOT COMPARABLE WITH THE ONE AFTER IT for any company whose
        // item-1.01 filing is recognised, and the benchmark-adjusted efficacy series is not comparable at
        // all (excess-vs-universe-v1 → v2 removes a pinned member from the peer mean, which moves EVERY
        // company's excess). History is deliberately NOT regenerated, rewritten or backfilled (AD-8/AD-1).
        // The precommitted 2026-09-29 AD-15 claim date is UNCHANGED — spec 217 declares its benchmark and
        // eligibility rules PROSPECTIVELY, before any eligible claim date exists.
        //
        // ⚠ SPEC 224 MOVES THIS PIN — radar-scoring-fp-66fca8c5f1fc → radar-scoring-fp-4244521af873 — for
        // the new `insiderCollapse=` fingerprint field (insider-collapse-v1;window=30;), appended immediately
        // after `mediaCollapse` and before `window`. It is UNCONDITIONAL and NOT AI-gated — the same-insider
        // Form 4 collapse is pure assembly code that runs in every composition — so BOTH the AI-OFF and the
        // AI-ON families move, on all three windows (the spec-198/217 shape): twelve asserted values in this
        // file, seven of them in this method and Compute_NewsQueryWindowDisabled_ReproducesNoNewsQueryPins.
        // The MEASURED basis (2026-09-12, live store): 244 discretionary-sale filings concentrated by REPEAT
        // filer, not headcount — ATNI's 13 negative insider signals from THREE people, OOMA's five sale
        // signals from three decisions across five weeks — each filing adding its full weight to Mneg
        // independently. Scoring math is byte-identical: no _formula.Version bump, no RuleSetVersion bump,
        // no weight, tier or window edited; only the insider INPUT SET changes (the media-collapse precedent).
        // ONE operator step is owed after merge (delete/re-record every configured
        // data/scoring-configs/strategies/{name}.json BEFORE the first post-merge run — git-ignored, never
        // fabricated; a StrategyIdentityGuard halt before that is CORRECT). If no baseline runs between the
        // spec-220/221 merges and this one, it collapses with their still-outstanding step into ONE.
        //
        // ⚠ SPEC 226 MOVES THIS PIN — radar-scoring-fp-4244521af873 → radar-scoring-fp-fa64e525e7b0 — AND ALL
        // ELEVEN OTHERS IN THIS FILE WITH IT, BOTH FAMILIES, for TWO causes folded into ONE recomputation, both
        // inside SignalSourceDescriptor.CanonicalDescriptor() and both UNCONDITIONAL (not AI-gated):
        //   (a) the `rules=` token: KeywordSignalExtractor.RuleSetVersion radar-keyword-rules-v8 → v9. The SEC
        //       8-K Item 1.01 ("material definitive agreement") and Item 2.01 ("completion of acquisition")
        //       heading phrases stop minting a POSITIVE StrategicPartnership and mint a NEUTRAL CorporateAction
        //       at the same strength/novelty/confidence. MEASURED basis (2026-09-14, live store): in the ~60-day
        //       window those headings covered credit agreements with a new direct financial obligation (item
        //       2.03 beside 1.01 — ASIX, DGII, CAT, CALM, MYRG), an equity issuance (3.02 — EOSE), a completed
        //       acquisition (ESQ) and acquisitions-or-disposals (THRM, NOVT), every one scored as a positive
        //       partnership. A heading is an event type, not a direction.
        //   (b) the `acq=` segment: CorporateActionSupersede.Version acq-supersede-v1 → v2. The supersede's MATCH
        //       widened to StrategicPartnership OR CorporateAction (accrued v8 reads stay partnerships on disk,
        //       new v9 reads are corporate actions) with a one-CorporateAction-per-recognised-filing guard.
        // Scoring math is otherwise byte-identical: no _formula.Version bump, no weight, tier, window,
        // media-collapse or insider-collapse change. ONE operator step was owed after merge (TAKEN 2026-09-14): delete or re-record
        // every configured data/scoring-configs/strategies/{name}.json BEFORE the first post-226 run (git-ignored,
        // never fabricated; a StrategyIdentityGuard halt before that step is CORRECT), then verify the first
        // run's stamp against Compute_LiveWindowAiOnStamps_ArePinned. Accrued v8 signals are never backfilled
        // (AD-8), so the boundary is NOT a step: it heals forward as accrued v8 reads age out of the window.
        //
        // ⚠ SPEC 227 MOVES THIS PIN — radar-scoring-fp-fa64e525e7b0 → radar-scoring-fp-e060ada97074 — AND ALL
        // ELEVEN OTHERS IN THIS FILE WITH IT, BOTH FAMILIES, for ONE cause: the `acq=` segment's scan half,
        // AcquisitionAgreementScan.Version acqscan-v1 → acqscan-v2 (unconditional, not AI-gated — the spec-217
        // shape). The recognition RULE changed: acqscan-v1 closed Steven Madden's (SHOO) thesis on a Q1-results
        // 8-K (a heading bound "Steven Madden" to "acquisition of Kurt Geiger", a quarterly dividend was read as
        // the deal price, "Lead Borrower" as the acquirer) and recorded MarineMax (HZO) at the $0.001 par value
        // with acquirer "Parent". MEASURED basis (2026-09-14, live store, spec 227 §3 harness over 159 scanned
        // item-1.01 filings): v1 recognised 2, v2 recognises 1 (HZO at $53.00, acquirer SHM Holdco, LLC). Which
        // records GOVERN a consumer now also depends on the version (PendingAcquisitions admits only the current
        // one), so an unchanged pin would let a v1-governed snapshot and a v2-governed one read as comparable.
        // acq-supersede-v2 does NOT move (its match and collapse are unchanged; it acts on whatever the projection
        // admits). No _formula.Version, RuleSetVersion, weight, tier, window, collapse, news= or ai= change.
        // ONE OPERATOR STEP WAS OWED after merge (TAKEN 2026-09-15): delete or re-record every configured
        // data/scoring-configs/strategies/{name}.json BEFORE the first post-227 run (git-ignored, never
        // fabricated; a StrategyIdentityGuard halt before that step is CORRECT), then verify that run's stamp
        // against Compute_LiveWindowAiOnStamps_ArePinned.
        //
        // ⚠ SPEC 228 MOVES THIS PIN — radar-scoring-fp-e060ada97074 → radar-scoring-fp-7544950c14d4 — AND ALL
        // ELEVEN OTHERS IN THIS FILE WITH IT, BOTH FAMILIES, for ONE cause: the `acq=` segment's scan half,
        // AcquisitionAgreementScan.Version acqscan-v2 → acqscan-v3 (unconditional, not AI-gated). The RULE did
        // not change; the READ did, and the version now covers the read: the item-1.01 body reader never read
        // the 8-K — EDGAR links the iXBRL primary through /ix?doc=…, the shared index parser dropped that row,
        // and the first-untyped-row fallback took an EX-10.1 / EX-2.1 / EX-1.1 / EX-4.1 exhibit as "the
        // primary" (153 of 154 live reads; 31 more failed outright). Since spec 228 the body is the declared or
        // form-typed 8-K plus EX-99.1, so the same accession can answer differently, and every v2 answer and
        // record is retired through spec 227's machinery. acq-supersede-v2 does NOT move. No _formula.Version,
        // RuleSetVersion, weight, tier, window, collapse, news= or ai= change. The MEASURED basis is spec 228
        // §3 (docs/architecture-history.md, spec-228 bullet). ⚠ ONE OPERATOR STEP IS OWED after merge: delete
        // or re-record every configured data/scoring-configs/strategies/{name}.json BEFORE the first post-228
        // run (git-ignored, never fabricated; a StrategyIdentityGuard halt before that step is CORRECT), then
        // verify that run's stamp against Compute_LiveWindowAiOnStamps_ArePinned.
        Assert.Equal("radar-scoring-fp-7544950c14d4", DefaultFingerprint());
    }

    [Fact]
    public void Compute_NewsQueryWindowDisabled_ReproducesNoNewsQueryPins()
    {
        // THE SPEC 198 §3 ADDITIVITY PROOF, and the reason the segment is conditional rather than
        // unconditional. With the recency window set to 0 the news-query segment is EMPTY, so the composed
        // descriptor is byte-identical to the CURRENT descriptor minus the newsquery segment, and the six
        // values below are the no-newsquery halves of the current pins (AI-OFF unchanged since 198, AI-ON
        // moved by 214) - which is what makes the spec-198 move attributable to the window and to nothing
        // else. If an AI-OFF assertion here fails, something in the current slice leaked into an input it
        // has no business touching. (Renamed by spec 214 from …ReproducesPost197Pins: the AI-ON halves are
        // no longer the post-197 values.)
        //
        // SPEC 214 MOVED THE THREE AI-ON HALVES (30d radar-scoring-fp-e7317fd038ac →
        // radar-scoring-fp-568a6612d541; 60d radar-scoring-fp-81a397434756 → radar-scoring-fp-c11b49eafb2e;
        // 120d radar-scoring-fp-e9d9819a2b41 → radar-scoring-fp-333786375292) and NOT the three AI-OFF
        // halves: the `news=enabled:…` segment carries the presentation cohort key and the materializer
        // identity, while the disabled segment carries neither. SPEC 215 MOVED THE SAME THREE AI-ON HALVES
        // AGAIN TO THE VALUES BELOW (30d radar-scoring-fp-568a6612d541 → radar-scoring-fp-6dce61d377d6; the
        // 60d and 120d halves likewise — see the assertions) for news-judgment-prompt-v5, schema-v4 and
        // reference-projection-v1 in the cohort key, and again NOT the AI-OFF halves. SPEC 216 MOVED THE
        // SAME THREE AI-ON HALVES AGAIN TO THE VALUES BELOW (30d radar-scoring-fp-6dce61d377d6 →
        // radar-scoring-fp-0dfa4463ddd3; 60d radar-scoring-fp-771ac5fb8a83 → radar-scoring-fp-0dc1d67de19d;
        // 120d radar-scoring-fp-7bfef3b8873b → radar-scoring-fp-66434c0af13f) for the ai= descriptor's new
        // rm= field, reported-metrics-v2 and reference-projection-v2 + news-judgment-prompt-v6, and AGAIN
        // NOT the AI-OFF halves. The proof this test makes is unchanged: with the news-query segment empty,
        // the values are the post-216 no-newsquery values, so the spec-198 segment is still exactly
        // additive on top of them.
        //
        // SPEC 217 §2 MOVED ALL SIX HALVES — both the AI-ON and, for the first time in this arc, the AI-OFF
        // ones (30d radar-scoring-fp-54e845330f96 → radar-scoring-fp-db96e3862fae; 60d
        // radar-scoring-fp-8daa662a57a6 → radar-scoring-fp-2237fb804628; 120d
        // radar-scoring-fp-f610244e23c6 → radar-scoring-fp-28fcca88a36a; AI-ON 30d
        // radar-scoring-fp-0dfa4463ddd3 → radar-scoring-fp-cba1bb64447f; 60d radar-scoring-fp-0dc1d67de19d
        // → radar-scoring-fp-588094848be3; 120d radar-scoring-fp-66434c0af13f →
        // radar-scoring-fp-15a8e0c1520f) — because the acq= segment is UNCONDITIONAL and sits OUTSIDE the
        // news-query segment. The proof this test makes is unchanged and is in fact sharpened: with the
        // news-query segment empty, the composed descriptor is still byte-identical to the current one minus
        // `newsquery=`, acq= and all, so spec 198's segment remains exactly additive.
        //
        // SPEC 219 §6 MOVED THE THREE AI-ON HALVES ONLY, to the values below (30d
        // radar-scoring-fp-cba1bb64447f → radar-scoring-fp-284ba31ce3d2; 60d radar-scoring-fp-588094848be3
        // → radar-scoring-fp-0beffdd089fd; 120d radar-scoring-fp-15a8e0c1520f →
        // radar-scoring-fp-f0cadce0add6), for the trailing enabled-only coverage-policy field on the
        // `news=` segment. The three AI-OFF halves did NOT move — the `news=disabled:…` segment carries no
        // such field — and that asymmetry is exactly what this test's AI-OFF assertions are for.
        //
        // SPEC 220 §1 MOVED THE THREE AI-ON HALVES ONLY, to the values below (30d
        // radar-scoring-fp-284ba31ce3d2 → the 30-day AI-ON value below; the 60d and 120d halves likewise —
        // see the assertions), for the trailing `ordering=family-ordering-v2` segment on the judgment cohort
        // key, which rides the enabled `news=` segment. The three AI-OFF halves did NOT move — the disabled
        // segment carries no cohort key.
        //
        // SPEC 221 MOVED THE THREE AI-ON HALVES ONLY, to the values below (30d radar-scoring-fp-e1d1ea16679a →
        // radar-scoring-fp-2f70cbbc010b; 60d radar-scoring-fp-4f1ceee0c60c and 120d
        // radar-scoring-fp-1b516442dff5 likewise — see the assertions), through the enabled `news=` segment
        // only: `ordering=family-ordering-v3`, news-judgment-prompt-v7 and news-judgment-schema-v5 in the
        // cohort key, and a fifth trajectory→direction mapping token (`NoBusinessSignal>none`). The three
        // AI-OFF halves did NOT move — the disabled segment carries neither a cohort key nor a mapping.
        //
        // ⚠ SPEC 224 MOVES ALL SIX HALVES — old → new — for the new `insiderCollapse=` fingerprint field
        // (insider-collapse-v1;window=30;), unconditionally, so both the AI-OFF and the AI-ON families move
        // (the spec-217 shape): AI-OFF 30d radar-scoring-fp-db96e3862fae → radar-scoring-fp-c658889c7b2f;
        // 60d radar-scoring-fp-2237fb804628 → radar-scoring-fp-c769b5237ca3; 120d
        // radar-scoring-fp-28fcca88a36a → radar-scoring-fp-075046305fbc; AI-ON 30d
        // radar-scoring-fp-2f70cbbc010b → radar-scoring-fp-e7473d744633; 60d radar-scoring-fp-9500928bf9d6
        // → radar-scoring-fp-553b5d5cc2d9; 120d radar-scoring-fp-a678b6c789b3 →
        // radar-scoring-fp-b4cebdfe9560. The field sits OUTSIDE the source descriptor altogether (it is its
        // own fixed-position fingerprint field, like mediaCollapse), so the proof this test makes is
        // unchanged: with the news-query segment empty the composed descriptor is still byte-identical to
        // the current one minus `newsquery=`, and spec 198's segment remains exactly additive. ONE operator
        // step is owed after merge (see Compute_DefaultConfig_MatchesPinnedFingerprint).
        //
        // ⚠ SPEC 226 MOVES ALL SIX HALVES AGAIN, both families (the rules= token radar-keyword-rules-v8 → v9
        // and acq-supersede-v1 → v2, both unconditional and both OUTSIDE the news-query segment): AI-OFF 30d
        // radar-scoring-fp-c658889c7b2f → radar-scoring-fp-691701d72fbe; 60d radar-scoring-fp-c769b5237ca3 →
        // radar-scoring-fp-1add9ab3b327; 120d radar-scoring-fp-075046305fbc → radar-scoring-fp-b9c73c39d0b8;
        // AI-ON 30d radar-scoring-fp-e7473d744633 → radar-scoring-fp-6ced12c5b920; 60d
        // radar-scoring-fp-553b5d5cc2d9 → radar-scoring-fp-e2927f5508e2; 120d radar-scoring-fp-b4cebdfe9560 →
        // radar-scoring-fp-f64eb428a991. The additivity proof is unchanged: with the news-query segment empty
        // the composed descriptor is still the current one minus `newsquery=`.
        //
        // ⚠ SPEC 227 MOVES ALL SIX HALVES AGAIN, both families (the acq= scan half acqscan-v1 → acqscan-v2,
        // unconditional and OUTSIDE the news-query segment): AI-OFF 30d radar-scoring-fp-691701d72fbe →
        // radar-scoring-fp-694724ee4c5e; 60d radar-scoring-fp-1add9ab3b327 → radar-scoring-fp-2b13c2bafbb1; 120d
        // radar-scoring-fp-b9c73c39d0b8 → radar-scoring-fp-6e7b65972129; AI-ON 30d radar-scoring-fp-6ced12c5b920
        // → radar-scoring-fp-068937f7ec96; 60d radar-scoring-fp-e2927f5508e2 → radar-scoring-fp-6262cedb8d00;
        // 120d radar-scoring-fp-f64eb428a991 → radar-scoring-fp-eeacbc8972d5. The additivity proof is unchanged.
        //
        // ⚠ SPEC 228 MOVES ALL SIX HALVES AGAIN, both families (the acq= scan half acqscan-v2 → acqscan-v3 — the
        // read changed, not the rule — unconditional and OUTSIDE the news-query segment): AI-OFF 30d
        // radar-scoring-fp-694724ee4c5e → radar-scoring-fp-dc81936adff5; 60d radar-scoring-fp-2b13c2bafbb1 →
        // radar-scoring-fp-4bda731619b7; 120d radar-scoring-fp-6e7b65972129 → radar-scoring-fp-afe9c0be1d54; AI-ON
        // 30d radar-scoring-fp-068937f7ec96 → radar-scoring-fp-88345c34e6f8; 60d radar-scoring-fp-6262cedb8d00 →
        // radar-scoring-fp-84ce04b1980e; 120d radar-scoring-fp-eeacbc8972d5 → radar-scoring-fp-2c1715c48512. The
        // additivity proof is unchanged.
        Assert.Equal(string.Empty, NewsQueryScoringIdentity.None.Segment);

        // 30-day ScoringOptions code default (the unit pins).
        Assert.Equal(
            "radar-scoring-fp-dc81936adff5",
            DefaultFingerprint(sourceDescriptor: SourceDescriptorWithoutNewsQuery));
        Assert.Equal(
            "radar-scoring-fp-88345c34e6f8",
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptorWithoutNewsQuery));

        // 60-day live baseline (Radar:ScoringWindowDays = 60).
        Assert.Equal(
            "radar-scoring-fp-4bda731619b7",
            DefaultFingerprint(
                sourceDescriptor: SourceDescriptorWithoutNewsQuery, window: TimeSpan.FromDays(60)));
        Assert.Equal(
            "radar-scoring-fp-84ce04b1980e",
            DefaultFingerprint(
                sourceDescriptor: AiOnSourceDescriptorWithoutNewsQuery, window: TimeSpan.FromDays(60)));

        // 120-day -Profile long-window.
        Assert.Equal(
            "radar-scoring-fp-afe9c0be1d54",
            DefaultFingerprint(
                sourceDescriptor: SourceDescriptorWithoutNewsQuery, window: TimeSpan.FromDays(120)));
        Assert.Equal(
            "radar-scoring-fp-2c1715c48512",
            DefaultFingerprint(
                sourceDescriptor: AiOnSourceDescriptorWithoutNewsQuery, window: TimeSpan.FromDays(120)));
    }

    [Fact]
    public void Compute_ChangedNewsQueryWindow_ChangesFingerprint_OnBothAiOffAndAiOn()
    {
        // The other half of the spec-198 §3 criterion: a CHANGED window re-stamps. Asserted on BOTH sides,
        // because the segment is not judgment-gated - an AI-off pin that did not move would mean the window
        // is not actually hashed, which is precisely the silent-move failure this slice exists to prevent.
        var fourteen = NewsQueryScoringIdentity.ForWindowDays(14);

        var offSeven = DefaultFingerprint(sourceDescriptor: SourceDescriptor);
        var offFourteen = DefaultFingerprint(
            sourceDescriptor: "rules=radar-keyword-rules-v9;"
                + NewsJudgmentScoringIdentity.Disabled.Segment
                + fourteen.Segment
                + AcquisitionScoringIdentity.Segment);
        Assert.NotEqual(offSeven, offFourteen);

        var onSeven = DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor);
        var onFourteen = DefaultFingerprint(
            sourceDescriptor: AiOnSourceDescriptorWith(AiDirectionalDescriptor, fourteen));
        Assert.NotEqual(onSeven, onFourteen);

        // ...and DISABLING it is a third distinct identity, never a collision with either configured window.
        Assert.NotEqual(offSeven, DefaultFingerprint(sourceDescriptor: SourceDescriptorWithoutNewsQuery));
        Assert.NotEqual(
            onSeven, DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptorWithoutNewsQuery));
    }

    [Fact]
    public void Compute_ChangedWeight_ChangesFingerprint()
    {
        Assert.NotEqual(
            DefaultFingerprint(),
            DefaultFingerprint(weights: new ScoringWeights { AttentionHalfSaturation = 12.0 }));
    }

    [Fact]
    public void Compute_ChangedTierDescriptor_ChangesFingerprint()
    {
        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(attentionDescriptor: "unknown=0.9;"));
    }

    [Fact]
    public void Compute_ChangedExtractorRuleSet_ChangesFingerprint()
    {
        // RETARGETED BY SPEC 141. This test used to assert that DROPPING A COLLECTOR re-stamps the
        // fingerprint (spec 95). That is now the OPPOSITE of the intended behaviour — a collector toggle must
        // leave a strategy's identity untouched, which SignalSourceDescriptorTests asserts directly at the
        // source. What remains true, and is what this test now guards, is that a change to the signal-source
        // IDENTITY descriptor — the extractor rule STRUCTURE identity, which does change what is scored —
        // still re-stamps. The perturbation target is deliberately kept one version AHEAD of the shipped
        // RuleSetVersion (spec 194 §1.1 moved the default to v8 and this perturbed to v9; spec 226 moved the
        // default to v9, so this now perturbs to v10) — a perturbation
        // equal to the default would make this test VACUOUS, which is exactly what happened when the shipped
        // version caught up with a perturbation literal that was not moved with it. Whoever bumps
        // KeywordSignalExtractor.RuleSetVersion next must move this literal in the same slice.
        // SPEC 194 §2: the perturbation carries the SAME news segment as the default, so the only thing that
        // differs is the rules= token — otherwise this would prove that two descriptors differing in two
        // places hash differently, which is a weaker claim.
        var perturbed = "rules=radar-keyword-rules-v10;"
            + NewsJudgmentScoringIdentity.Disabled.Segment
            + NewsQueryScoringIdentity.Default.Segment
            + AcquisitionScoringIdentity.Segment;

        // Non-vacuity, guarded against the SHIPPED const rather than against this file's own literal: the day
        // production bumps to v9 this fails here, naming the reason, instead of silently asserting that a
        // fingerprint differs from itself.
        Assert.DoesNotContain(KeywordSignalExtractor.RuleSetVersion, perturbed, StringComparison.Ordinal);
        Assert.NotEqual(perturbed, SourceDescriptor);

        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(sourceDescriptor: perturbed));
    }

    [Fact]
    public void Compute_ChangedInsiderTiers_ChangesFingerprint()
    {
        // Changing an insider tier (or the cluster boost) changes the effective scoring config, so the
        // fingerprint must re-stamp automatically (spec 96 — magnitudes hashed by value, no RuleSetVersion bump).
        var changedInsider = new InsiderMaterialityWeights { ClusterBoost = 2 }.CanonicalDescriptor();

        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(insiderDescriptor: changedInsider));
    }

    // The directional-filing source's own descriptor for the default live run (pinned field-for-field by
    // DirectionalFilingSignalSourceTests.ScoringDescriptor_EncodesPerSignalMagnitudes_InCanonicalForm): the default
    // Strength/Novelty/MinConfidence == 8/6/0.6 (spec-112 Strength 6→8 recalibration) plus the spec-119
    // earnings-read model identity — scripts/run-profiles/default.json now configures the DeepInfra
    // OpenAI-compatible provider with deepseek-ai/DeepSeek-V4-Flash, and the Worker composes the identity as
    // "{provider}:{effective model}" — plus (spec 160, appended LAST so the existing prefix stays byte-stable)
    // the comparability-scan structure identity (cmpscan=cmpscan-v1) and the comparability confidence cap by
    // value (cmpcap, default 0.65, G29 like minconf): the cap bounds the confidence of emitted signals, a
    // comparability input exactly like MinConfidence and the reading model.
    //
    // SPEC 216 §5 APPENDS `rm=` LAST — the reported-metrics policy token, `disabled` or the current
    // ReportedMetricsPolicy.Version. It is spliced from the CONST rather than written as a literal so a
    // policy bump moves these pins on its own instead of leaving a stale copy here. It carries the ENABLED
    // token because scripts/run-profiles/default.json re-enables the ledger in this same slice.
    private static readonly string AiDirectionalDescriptor =
        "directional-filing:str=8;nov=6;minconf=0.6;model=openai:deepseek-ai/DeepSeek-V4-Flash;cmpscan=cmpscan-v1;cmpcap=0.65"
            + $";rm={ReportedMetricsPolicy.Version}";

    /// <summary>
    /// The same descriptor with metric extraction OFF (spec 216 §5). It is an INTERMEDIATE state that
    /// exists only in the mutation tests below and in a profile that switches the ledger off — never a
    /// separate shipped regime, because §6 re-enables the ledger in the same PR that adds the field.
    /// </summary>
    private static readonly string AiDirectionalDescriptorWithoutReportedMetrics =
        "directional-filing:str=8;nov=6;minconf=0.6;model=openai:deepseek-ai/DeepSeek-V4-Flash;cmpscan=cmpscan-v1;cmpcap=0.65"
            + $";rm={ReportedMetricsPolicy.DisabledToken}";

    // The AI-ON signal-source descriptor (spec 106): the rules= identity with the directional-filing
    // descriptor appended as an ESCAPED ai=… segment. Built through the real DescriptorEscaping (not a hand-written
    // literal) so this is byte-identical to what SignalSourceDescriptor actually produces when the opt-in AI path is
    // registered — the pre-spec-119 literal omitted that escaping, so the old AI-ON pin was not the value a live
    // AI-ON run stamped; spec 119 corrects that at the same time as folding the model in.
    //
    // SPEC 194 §2: this side carries the LIVE news-read identity, and the split from the AI-OFF descriptor's
    // DISABLED one is deliberate. "AI-ON" here has always meant "the live baseline's optional reads are
    // registered" — scripts/run-profiles/default.json enables the AI filing read AND the stage-2 judgment
    // together — and the 60-day AI-ON pin's job is to be the operator-facing live stamp. Splitting the two
    // optional reads into four pins per window would quadruple the pin family without adding a
    // change-detector the dedicated off/on, model/cohort and strength-constant tests below do not already
    // provide directly.
    //
    // NOTE the segment ORDER: rules= then ai= then news=. The news segment is appended LAST by
    // SignalSourceDescriptor precisely so the pre-194 prefix stays byte-stable, and it must be composed in
    // that order here or this literal would stop describing production.
    // SPEC 198 §3: the news-QUERY segment is appended after the news-READ segment, LAST, mirroring
    // SignalSourceDescriptor's own composition order. Same reasoning as the news segment's placement: the
    // whole post-197 prefix stays byte-stable, so a pin move is attributable to exactly one input.
    // SPEC 217 §2: the acq= segment is appended LAST here too, mirroring SignalSourceDescriptor's own
    // composition order — same reasoning as every segment since spec 194.
    private static string AiOnSourceDescriptorWith(
        string aiDirectionalDescriptor, NewsQueryScoringIdentity? newsQuery = null) =>
        "rules=radar-keyword-rules-v9;"
            + $"ai={DescriptorEscaping.Escape(aiDirectionalDescriptor)};"
            + LiveNewsJudgmentSegment
            + (newsQuery ?? NewsQueryScoringIdentity.Default).Segment
            + AcquisitionScoringIdentity.Segment;

    private static readonly string AiOnSourceDescriptor =
        AiOnSourceDescriptorWith(AiDirectionalDescriptor);

    /// <summary>The AI-ON additivity control: identical but for an EMPTY news-query segment.</summary>
    private static readonly string AiOnSourceDescriptorWithoutNewsQuery =
        AiOnSourceDescriptorWith(AiDirectionalDescriptor, NewsQueryScoringIdentity.None);

    [Fact]
    public void Compute_AiOnSourceDescriptor_DiffersFromAiOff()
    {
        // Enabling the AI directional-filing path widens the signal-production surface (it emits directional
        // GuidanceChange signals), so the fingerprint MUST re-stamp — closing the AD-10 comparability gap between
        // an AI-on and an AI-off run (the AI analogue of spec 95's secform4 fix).
        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor));
    }

    [Fact]
    public void Compute_AiOnDefault_MatchesPinnedFingerprint()
    {
        // The AI-ON default fingerprint AT THE ScoringOptions CODE-DEFAULT 30-DAY WINDOW: with an AI provider
        // registered (as scripts/run-profiles/default.json configures), the AI directional-filing descriptor is
        // folded in (AiOnSourceDescriptor above), so the effective config differs from the AI-OFF pin. Pinned so
        // an accidental drift in the AI directional magnitudes, the earnings-read model, or any other folded
        // input is caught for the AI-ON run too.
        //
        // ⚠ NOT the live stamp any more — see the AI-OFF pin above. Since spec 148 the window is hashed, and
        // the live baseline runs at Radar:ScoringWindowDays = 60, where the AI-ON value is
        // radar-scoring-fp-81a397434756 since spec 197 (recorded in default.json's comment and asserted by
        // Compute_LiveWindowAiOnStamps_ArePinned below). This pin is the unit-level change-detector at the
        // code default; that one is the operator-facing live record.
        //
        // A CHANGE-DETECTOR, NOT AN INVARIANT — see the AI-OFF pin above for why a deliberate move is normal.
        // This particular value is the sharpest evidence for that position: the spec-141 value it replaces had
        // exactly 43 snapshots on the live store, i.e. ONE RUN of history.
        //
        // Lineage: spec 112 (radar-scoring-fp-454984785732) → spec 117 radar-formula-v7 structure bump +
        // following-discount weights (radar-scoring-fp-4c06fd2d2d8c) → spec 119, which folded the earnings-read
        // model identity into the directional descriptor by value and built the ai= segment through the real
        // escaping (radar-scoring-fp-2ef5ef96cce2) → spec 122, the radar-formula-v8 structure bump + the new
        // CollapsedBreadthCredit magnitude, which re-stamps BOTH the AI-OFF and the AI-ON default
        // (radar-scoring-fp-c908f03a554a) → spec 127, the RuleSetVersion v3→v4 bump for the new PatentActivity
        // rule group (opt-in OFF) (radar-scoring-fp-63c096e531ec) → spec 129, the RuleSetVersion v4→v5 bump for
        // the new RegulatoryApproval rule group (opt-in-OFF openFDA collector) (radar-scoring-fp-2be98e738684)
        // → spec 130, the RuleSetVersion v5→v6 bump for the new TrademarkActivity rule group (opt-in-OFF USPTO
        // trademark collector), which folds into BOTH defaults automatically with scoring math byte-identical
        // (radar-scoring-fp-74c5e077f728) → spec 133, which promotes the openFDA collector `fda` into
        // scripts/run-profiles/default.json: a COLLECTOR-SET change (6 → 7 collectors) that re-stamped
        // automatically (radar-scoring-fp-57356123e09b) → spec 141, which removed the collector CSV from the
        // hashed identity entirely, so a re-stamp like spec 133's can never happen again
        // (radar-scoring-fp-3457da53489d) → SPEC 148, which folds in the recent-signal WINDOW and
        // ScoringWeights.TrajectoryCorroborationK — two output-affecting inputs that were hashed into nothing.
        // THE MOVE IS THE DELIVERABLE; scoring math is byte-identical, with no _formula.Version or
        // RuleSetVersion bump. → SPEC 146 deliberately did NOT move it: see the
        // AI-OFF pin above for why the per-strategy Formula/Channels addition folds in here as a no-op.
        // → SPEC 160 (radar-scoring-fp-28226897f97b → the value below): the comparability-aware confidence
        // cap on the AI filing read folded TWO new fields into the directional descriptor, appended after
        // model= — cmpscan=cmpscan-v1 (the deterministic comparability scan's rule-STRUCTURE identity,
        // parallel to RuleSetVersion) and cmpcap=0.65 (the cap magnitude by value, G29). The cap bounds the
        // persisted confidence of directional GuidanceChange signals when the release itself declares
        // comparability breaks (the CASS 2026-07-29 0.90 misread), so an AI-ON run with the cap and one
        // without must never share a ScoringConfigVersion. AI-OFF pins do NOT move (the descriptor is folded
        // only when the AI source is registered — asserted by the AI-OFF pin above staying put). No
        // _formula.Version bump, no KeywordSignalExtractor.RuleSetVersion bump; cmpscan-v1 is its own
        // parallel structure token.
        // → SPEC 191 (radar-scoring-fp-ebd7d11a58d0 → radar-scoring-fp-ef9104b7b2b9): the RuleSetVersion
        // v6 → v7 bump for the DIRECTIONAL NewsArticle branch, then SPEC 194 §1.1's v7 → v8 correction.
        // Unlike specs 127/129/130 those bumps change scores on the shipped baseline — the AI-ON path is
        // where it lands hardest, since default.json enables the spec-185 judgment step whose verdicts were
        // that direction's source. The AI directional-filing descriptor is untouched; only the rules=
        // segment moved.
        // → SPEC 194 §1.5 (radar-scoring-fp-ef9104b7b2b9 → radar-scoring-fp-fce77b299c76): the
        // media-collapse-v1 → media-collapse-v2 bump. It re-stamps BOTH the AI-OFF and the AI-ON default
        // automatically, because mediaCollapseDescriptor is its own hashed field and is folded whether or
        // not the AI source is registered.
        // → SPEC 194 §2 (radar-scoring-fp-fce77b299c76 → radar-scoring-fp-5ef6508adc5d): the news-read
        // scoring identity. On THIS side the segment carries the ENABLED form — the live baseline designates
        // the DeepInfra DeepSeek reader as both presentation judge and presentation stage-1 extractor — so a
        // judge-model or presentation-cohort change now moves this pin, exactly as the earnings-read model
        // has moved it since spec 119.
        // → SPEC 196 (radar-scoring-fp-5ef6508adc5d → radar-scoring-fp-420b31ba0753): the attention
        // publisher TIER MAP — the inverted unknown default (0.25 → 0.1) plus the four-tier policy and its
        // audited membership. It re-stamped BOTH the AI-OFF and the AI-ON default automatically, because
        // attnDesc is its own hashed field and is folded whether or not the AI source is registered. See
        // the AI-OFF pin above for the measured cause and the "not comparable across this boundary"
        // statement.
        // → SPEC 197 (radar-scoring-fp-420b31ba0753 → the value below): the news-read identity moves for
        // TWO reasons at once, both arriving through the ALREADY-HASHED spec-194 §2 `news=` segment, folded
        // into ONE recomputation (which is why §1 and §2 were deliberately specified together rather than
        // shipped as two slices with two operator resets):
        //   (a) §1.3 — news-judgment-signal-v1 → news-judgment-signal-v2. The observation→evidence join
        //       replaced its title-only key with a fail-closed ladder (exact URL + normalized headline +
        //       publication instant, then exact URL + headline, then the pre-197 unique headline; ambiguity
        //       STOPS and never falls through to a weaker key). That changes WHICH judgments can produce a
        //       scoring input at all — measured on the live store, the 2026-08-27 baseline's 9 eligible
        //       directional judgments went from 2 materializable to 9 — so it is not a silent fix under the
        //       v1 token. Accrued v1 signals stay valid, immutable and recognized by the ONE shared
        //       classifier; an existing valid v1 id is prior-version occupancy and mints no v2 duplicate.
        //   (b) §2.2 — news-judgment-prompt-v2/schema-v2 → v3. The accepted FactId GRAMMAR is part of the
        //       result schema (a unique 8–31-character hex prefix of exactly one SUPPLIED representative
        //       fact now expands deterministically; everything else fails by named reason), and both
        //       versions enter the resolved PRESENTATION COHORT KEY that this segment carries.
        // Consequence stated where the pin is: forking the stage-2 cohort key means every candidate company
        // is RE-JUDGED ONCE on the first post-197 run (≈19 hosted judge calls at the current candidate
        // count) and the five accrued ValidationFailed attempts are not reused — the intended effect, and
        // no budget or retry-count change was requested. No _formula.Version bump, no RuleSetVersion bump
        // (still radar-keyword-rules-v8), no media-collapse bump (still media-collapse-v2), no supersede or
        // neutralization rule bump, no attention tier edit, no weight edit; §3's warning aggregation is
        // transient and would move nothing on its own. THE THREE AI-OFF PINS ARE PROVEN UNCHANGED — see the
        // AI-OFF pin above for why, and why a move there would be scope leakage.
        //
        // OPERATOR ACTION AFTER SPEC 197 — the third close identity boundary in a row (194, 196, 197), and
        // the ORDER is load-bearing: (1) do not touch the ignored identity records while a pre-197 baseline
        // is running; (2) after merge and BEFORE the first post-197 baseline, consciously delete or
        // re-record every configured data/scoring-configs/strategies/{name}.json; (3) verify the first run
        // reports radar-scoring-fp-81a397434756 (the shipped profile is AI-ON at 60 days) before treating
        // later snapshots as the corrected series. That path is git-ignored, so those records cannot ride
        // in a PR and MUST NEVER be fabricated. If step 2 is missed, StrategyIdentityGuard halts the run
        // before collection — that halt is CORRECT and must not be bypassed.
        //
        // ⚠ THE DISCONTINUITY, stated precisely: post-194/v1 scores fail closed CORRECTLY but materially
        // UNDER-ADMIT grounded judgments, because the title-only join rejected stronger exact identity (2 of
        // 9 eligible directional judgments admitted on the measured baseline); post-197/v2 scores admit only
        // citations resolved by the stronger deterministic ladder. History is preserved and never
        // regenerated, rewritten or backfilled (AD-8/AD-1) — and the pre-197 sparse-join segment must NOT be
        // presented as equivalent judgment coverage when interpreting news-direction efficacy.
        // → SPEC 198 (radar-scoring-fp-e7317fd038ac → radar-scoring-fp-7d2b0cf537c4): the news-feed QUERY
        // identity, the trailing `newsquery=7d;` segment carrying Radar:News:RecencyWindowDays. Unlike spec
        // 197 this moves BOTH sides, because the segment is not judgment-gated — see the AI-OFF pin above
        // for the full reasoning, the live endpoint verification, the additivity control and the ordered
        // operator action.
        // → SPEC 214 MOVED IT (radar-scoring-fp-7d2b0cf537c4 → the value below), AI-ON side ONLY, the
        // spec-197 pattern: three causes folded into ONE recomputation, all arriving through the `news=`
        // segment — (a) comparison-basis-v1 joins the stage-2 cohort key (the per-family ComparisonBasis
        // line is an input the judge sees); (b) news-judgment-prompt-v3 → v4 (rule 11: a level is not a
        // trend); (c) news-judgment-signal-v2 → v3 (the materializer ALLOWLISTS TrajectoryBasis Supported
        // alone, so it changes WHICH judgments can produce a scoring input). No formula, RuleSetVersion,
        // media-collapse, supersede, neutralization, attention-tier, weight or news-query change. THE
        // THREE AI-OFF PINS ARE UNCHANGED — asserted by Compute_DefaultConfig_MatchesPinnedFingerprint and
        // Compute_LiveWindowAiOffStamps_ArePinned.
        // → SPEC 215 MOVED IT (radar-scoring-fp-fc2a32b1c2ac → the value below), AI-ON side ONLY, the same
        // pattern: three causes folded into ONE recomputation, all through the `news=` segment —
        // (a) news-judgment-prompt-v4 → v5 (rule 12: reference values are a comparison basis, cite both);
        // (b) news-judgment-schema-v3 → v4 (TrajectoryReferenceIds / per-finding ReferenceIds);
        // (c) reference-projection-v1 joins the stage-2 cohort key after comparison= (the metric-phrase
        // table and caps decide which reference values the model sees). The materializer identity did NOT
        // move (news-judgment-signal-v3 — the allowlist grew, the rule did not). No formula, RuleSetVersion,
        // media-collapse, supersede, neutralization, attention-tier, weight or news-query change. THE THREE
        // AI-OFF PINS ARE UNCHANGED — asserted by Compute_DefaultConfig_MatchesPinnedFingerprint and
        // Compute_LiveWindowAiOffStamps_ArePinned. The operator step (delete/re-record
        // data/scoring-configs/strategies/{name}.json, verify the first run's stamp against
        // Compute_LiveWindowAiOnStamps_ArePinned) is taken ONCE for 214 and 215 together.
        // → SPEC 216 MOVED IT (radar-scoring-fp-bd8135c65d98 → the value below), AI-ON side ONLY, with
        // THREE causes folded into ONE recomputation — and, for the first time in this arc, one of them
        // does NOT travel through `news=`:
        //   (a) §5 — the DIRECTIONAL-FILING `ai=` descriptor gains a trailing `rm=` field carrying the
        //       reported-metrics policy token (`disabled` or `reported-metrics-v<N>`). It belongs there,
        //       not in `news=`, because enabling metric extraction changes the FILING-ANALYSIS PROMPT
        //       ITSELF — the model is asked for the release's stated metrics as well as its direction —
        //       even when the news judgment is disabled, and the VERIFICATION policy decides which values
        //       exist at all. That is spec 119's reading-model argument applied to the read's other
        //       prompt-shaping input. The shipped token is the ENABLED one because §6 re-enables the
        //       ledger in this same PR; `rm=disabled` exists only in the mutation matrix
        //       (Compute_ReportedMetricsIdentityMatrix_IsPinned_AiOnMovesAiOffCannot) and in a profile
        //       that switches the ledger off — never a separate later regime.
        //   (b) §3 — reported-metrics-v1 → v2. The verification rule is part of the policy token, and v2
        //       verifies three things v1 did not: the quote must NAME the labelled metric, the period must
        //       be the release's own wording, and the value must be a WHOLE token ASSOCIATED with the
        //       metric inside one bounded fragment. Under v1 a cash figure labelled Backlog verified.
        //   (c) §1 — reference-projection-v1 → v2 and news-judgment-prompt-v5 → v6, both through the
        //       `news=` segment. The projection now EXCLUDES the newest accession per metric (a fact's own
        //       release could otherwise be its own "reference"), projects a verified StatedPrior pair
        //       separately, and excludes a filing later than the news; the prompt states the rule and the
        //       rendered reference line carries the kind.
        // The record tag moved to news-judgment-v7 and the response schema did NOT move (still
        // news-judgment-schema-v4 — the response shape is unchanged). No formula, RuleSetVersion,
        // media-collapse, supersede, neutralization, attention-tier, weight or news-query change. THE
        // THREE AI-OFF PINS ARE UNCHANGED — asserted by Compute_DefaultConfig_MatchesPinnedFingerprint and
        // Compute_LiveWindowAiOffStamps_ArePinned, and REQUIRED to be: with no AI read registered there is
        // no `ai=` segment to carry rm= and no `news=enabled:` segment to carry the projection version, so
        // a move there would be scope leakage (the spec-197 proof pattern).
        // → SPEC 219 §6 MOVES IT (radar-scoring-fp-3c91bc88a2e3 → the value below), AI-ON side ONLY, for
        // ONE cause and one only: the `news=enabled:…` segment gains a trailing COVERAGE-POLICY field
        // carrying NewsJudgmentCoveragePolicy.Version (news-judgment-coverage-v2). It is deliberately NOT a
        // budget: MaxCompaniesPerRun, MaxFamiliesPerJudgment, the new MaxFamiliesPerBreadthJudgment and
        // MaxJudgmentAttempts all stay excluded BY VALUE (asserted by
        // NewsJudgmentScoringIdentityModeTests.CostControlsAndBudgets_DoNotMoveTheFingerprint). What moved
        // is WHICH COMPANIES HAVE A DIRECTIONAL NEWS READ AT ALL — under the implicit v1 policy the
        // spec-179 §3 rank traversal supplied, ~19 of 102 companies reached scoring with a direction and
        // the rest reached it as undirected volume; under v2 the universe does. No formula, RuleSetVersion,
        // media-collapse, supersede, neutralization, attention-tier, weight, news-query, acq= or ai=
        // change; the prompt, the response schema and the stage-2 cohort key are UNCHANGED (only the record
        // tag moved, to news-judgment-v8). THE THREE AI-OFF PINS ARE UNCHANGED and REQUIRED to be: the
        // field is enabled-only, so the `news=disabled:…` segment has no place to carry it and no value of
        // the coverage policy can reach an AI-OFF fingerprint (the spec-197/214–216 proof pattern).
        // → SPEC 220 §1 MOVES IT (radar-scoring-fp-11b10caf36d8 → the value below), AI-ON side ONLY, for ONE
        // cause: the stage-2 judgment cohort key gains a trailing `ordering=family-ordering-v2` segment
        // (NewsJudgmentFamilyOrdering.Version). The family ORDER decides which facts fill a bounded judge's
        // budget — an input the model sees — so it is cohort identity, and the presentation cohort key rides
        // the `news=enabled:…` segment. Under the implicit family-ordering-v1 a five-family breadth read saw
        // the five most-SYNDICATED families; under v2 it sees the directional ones first. No formula,
        // RuleSetVersion, media-collapse, supersede, neutralization, attention-tier, weight, news-query, acq=,
        // ai= or coverage-policy change; the prompt and the response schema are UNCHANGED (the record tag
        // moved to news-judgment-v9). Spec 220 §2 (a blank ChallengeStrength beside surviving findings is
        // accepted as not recorded instead of failing validation) is NOT separately hashed, and it is NOT
        // scoring-inert: the judgment-signal materializer gates on Status == Judged, so a formerly-discarded
        // directional response with a Supported/ReferenceSupported basis now mints a signal. It needs no
        // identity of its own only because, in THIS slice, it shares its comparability boundary with the
        // `ordering=` cohort fork, which moves the cohort key and these AI-ON pins together — so no pre- and
        // post-relaxation judgments are pooled under one stamp. That is why it was not split into its own
        // spec; a later change to that rule ALONE would need its own identity. THE THREE AI-OFF PINS ARE
        // UNCHANGED and REQUIRED to be: the disabled segment
        // carries no cohort key, so no ordering version can reach an AI-OFF fingerprint.
        // → SPEC 221 MOVES IT (radar-scoring-fp-3001c04030e7 → the value below), AI-ON side ONLY, with FOUR
        // causes folded into ONE recomputation, all through the `news=enabled:…` segment:
        //   (a) §1 — the cohort key's `ordering=` token moves to family-ordering-v3: a family confined to the
        //       context-only event types (share-price moves, analyst actions, index mechanics, promotional
        //       coverage) now fills a bounded judge's budget after every business family, whatever its basis;
        //   (b) §2b — news-judgment-prompt-v6 → v7 (rule 2 splits NoBusinessSignal from Unknown, with the
        //       adverse-fact guard; rule 11 no longer forces abstention to Unknown);
        //   (c) §2b — news-judgment-schema-v4 → v5 (the BusinessTrajectory vocabulary widened);
        //   (d) the materializer-identity DIRECTION MAPPING gains a fifth token, `NoBusinessSignal>none`,
        //       because NewsJudgmentScoringIdentityFactory enumerates the trajectory enum — a new trajectory is
        //       a new mapping (it maps to no direction, so the materializer version news-judgment-signal-v3
        //       does NOT move: nothing it mints changed).
        // No formula, RuleSetVersion, media-collapse, supersede, neutralization, attention-tier, weight,
        // news-query, acq=, ai= or coverage-policy change; the record tag moved to news-judgment-v10. THE
        // THREE AI-OFF PINS ARE UNCHANGED and REQUIRED to be: the disabled segment carries no cohort key and no
        // mapping, so none of the four causes can reach an AI-OFF fingerprint.
        //
        // → SPEC 224 MOVES IT (radar-scoring-fp-9ee56ab1bbb3 → the value below), and this time the AI-OFF
        // side moves WITH it (the spec-217 shape, not the 219–221 one): the cause is the new unconditional
        // `insiderCollapse=` fingerprint field (insider-collapse-v1;window=30;), which sits outside the
        // source descriptor and therefore outside every AI gate. No news=, ai=, rules=, acq=, weight, tier,
        // media-collapse or window change. See Compute_DefaultConfig_MatchesPinnedFingerprint for the
        // measured basis and the single operator step owed.
        //
        // → SPEC 226 MOVES IT (radar-scoring-fp-a448254b38cc → the value below), AI-OFF side WITH it: the
        // rules= token radar-keyword-rules-v8 → v9 (the SEC item-heading rules become Neutral CorporateAction)
        // and acq-supersede-v1 → v2, both unconditional inside the source descriptor. No news=, ai=, weight,
        // tier, collapse or window change. See Compute_DefaultConfig_MatchesPinnedFingerprint.
        //
        // → SPEC 227 MOVES IT (radar-scoring-fp-fcb4a4593ff1 → the value below), AI-OFF side WITH it: the acq=
        // scan half acqscan-v1 → acqscan-v2, unconditional inside the source descriptor. No news=, ai=, rules=,
        // supersede, weight, tier, collapse or window change. See Compute_DefaultConfig_MatchesPinnedFingerprint.
        //
        // → SPEC 228 MOVES IT (radar-scoring-fp-b7e9623fd747 → the value below), AI-OFF side WITH it: the acq=
        // scan half acqscan-v2 → acqscan-v3 (the item-1.01 READ changed — the 8-K primary is now read — not the
        // rule), unconditional inside the source descriptor. No news=, ai=, rules=, supersede, weight, tier,
        // collapse or window change. See Compute_DefaultConfig_MatchesPinnedFingerprint.
        Assert.Equal(
            "radar-scoring-fp-4e7cec079dfe",
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor));
    }

    [Fact]
    public void Compute_ChangedComparabilityCap_ChangesFingerprint()
    {
        // Spec 160: the comparability confidence cap is folded by value (cmpcap=) — tuning it re-stamps the
        // fingerprint automatically, so runs under different caps are never falsely comparable (AD-10).
        var changed = AiOnSourceDescriptorWith(
            AiDirectionalDescriptor.Replace("cmpcap=0.65", "cmpcap=0.5", StringComparison.Ordinal));

        Assert.NotEqual(
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor),
            DefaultFingerprint(sourceDescriptor: changed));
    }

    [Fact]
    public void Compute_ReportedMetricsIdentityMatrix_IsPinned_AiOnMovesAiOffCannot()
    {
        // SPEC 216 §5's four-case matrix, in one place so the placement argument is checkable rather than
        // asserted in prose. The token rides the DIRECTIONAL-FILING `ai=` descriptor because enabling
        // extraction changes the FILING-ANALYSIS PROMPT ITSELF — the model is asked for the release's
        // stated metrics as well as its direction — and the VERIFICATION policy decides which values exist
        // at all. That is spec 119's argument for the reading model, applied to the read's other
        // prompt-shaping input.
        //
        // (i) JUDGMENT OFF, flag toggled ⇒ the AI-ON descriptor CHANGES. This is the case the round-2
        // review named: extraction changes the filing prompt even with no judge, so it cannot live in the
        // `news=` segment.
        var judgmentOffOn = DefaultFingerprint(
            sourceDescriptor: "rules=radar-keyword-rules-v9;"
                + $"ai={DescriptorEscaping.Escape(AiDirectionalDescriptor)};"
                + NewsJudgmentScoringIdentity.Disabled.Segment
                + NewsQueryScoringIdentity.Default.Segment);
        var judgmentOffOff = DefaultFingerprint(
            sourceDescriptor: "rules=radar-keyword-rules-v9;"
                + $"ai={DescriptorEscaping.Escape(AiDirectionalDescriptorWithoutReportedMetrics)};"
                + NewsJudgmentScoringIdentity.Disabled.Segment
                + NewsQueryScoringIdentity.Default.Segment);
        Assert.NotEqual(judgmentOffOn, judgmentOffOff);

        // (ii) JUDGMENT ON, flag toggled ⇒ changes.
        Assert.NotEqual(
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor),
            DefaultFingerprint(
                sourceDescriptor: AiOnSourceDescriptorWith(AiDirectionalDescriptorWithoutReportedMetrics)));

        // (iii) AI READ OFF, flag toggled ⇒ UNCHANGED — and the literal "toggle the flag with no AI read"
        // mutation is UNCONSTRUCTIBLE, which is the honest way to state this case. The option lives on the
        // DIRECTIONAL FILING SOURCE; with the AI read off that source is not registered, so no ai= segment
        // is folded and there is no field for the flag to change. The proof is therefore STRUCTURAL rather
        // than a comparison: the AI-OFF descriptor carries no `rm=` field at all, so no value of the flag
        // can reach the AI-OFF fingerprint — which is why the AI-OFF pins cannot move in this slice.
        // (An Assert.Equal of the AI-OFF fingerprint against ITSELF stood here until the spec-216 review:
        // it compared a value to itself, could never fail, and made this case look like it pinned more
        // than it did.)
        Assert.DoesNotContain("rm=", SourceDescriptor, StringComparison.Ordinal);

        // (iv) POLICY TOKEN v2 → a fake v3 with the flag on ⇒ changes. The verification rule decides which
        // values exist, so two policies are two different scorings.
        Assert.NotEqual(
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor),
            DefaultFingerprint(
                sourceDescriptor: AiOnSourceDescriptorWith(
                    AiDirectionalDescriptor.Replace(
                        ReportedMetricsPolicy.Version, "reported-metrics-v3", StringComparison.Ordinal))));

        // Non-vacuity: the two descriptors really do differ in exactly the rm= field.
        Assert.EndsWith($";rm={ReportedMetricsPolicy.Version}", AiDirectionalDescriptor, StringComparison.Ordinal);
        Assert.EndsWith(
            $";rm={ReportedMetricsPolicy.DisabledToken}",
            AiDirectionalDescriptorWithoutReportedMetrics,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compute_LiveWindowAiOnStamps_ArePinned()
    {
        // The OPERATOR-FACING live stamps at the two windows real runs use (spec 148 broke pin == live stamp:
        // the window is hashed, the unit pins above are computed at the 30-day CODE default the Worker never
        // uses). Recomputed here for spec 160 (the cmpscan/cmpcap descriptor fields), for SPEC 191 (the
        // RuleSetVersion v6 → v7 bump), for SPEC 194 §1.1 (v7 → v8, withdrawing that read), for SPEC 194
        // §1.5 (media-collapse-v1 → v2) and finally for SPEC 194 §2 (the news-read scoring identity), so the
        // values recorded in scripts/run-profiles/default.json's comment are asserted rather than
        // transcribed: 60 days is the live baseline (Radar:ScoringWindowDays=60), 120 days is
        // -Profile long-window.
        // Spec 191 lineage: 60d radar-scoring-fp-5ffa8c9e25f0 → radar-scoring-fp-3670cdb74652;
        // 120d radar-scoring-fp-19fecdb64e3a → radar-scoring-fp-c9fe86a19073. Spec 194 §1.1: 60d
        // → radar-scoring-fp-7a4cd9d409ed; 120d → radar-scoring-fp-759835b624ca. Spec 194 §1.5: 60d
        // → radar-scoring-fp-162df0f4c62b; 120d → radar-scoring-fp-b8ce14dea17a.
        // Spec 194 §2: 60d → radar-scoring-fp-b9543f441717;
        // 120d → radar-scoring-fp-901129153cd1. SPEC 196 moved them for the attention publisher tier map
        // (attnDesc) — the inverted 0.1 unknown default plus the four-tier audited membership: 60d
        // → radar-scoring-fp-65eb592d0354; 120d → radar-scoring-fp-a89b6d9ad0a5. The AI-OFF live values
        // moved at every one of those steps too (60d radar-scoring-fp-4eb2fe5d3cdf → 58c289cd0113 →
        // 06e4781f86bb → 61891b37e429 → 2cbbd056ffe5 → radar-scoring-fp-8daa662a57a6; 120d
        // radar-scoring-fp-0a7058d94582 → 5d89d6ce1668 → 5cb9dc71f309 → f160ee8faaa6 → f68e6481b136 →
        // radar-scoring-fp-f610244e23c6) — a rules=, media-collapse, news= or attnDesc change folds in with
        // or without the AI descriptor.
        //
        // SPEC 197 MOVES THEM TO THE VALUES BELOW — and, unlike every step named above, it moves the AI-ON
        // side ONLY: 60d radar-scoring-fp-65eb592d0354 → radar-scoring-fp-81a397434756; 120d
        // radar-scoring-fp-a89b6d9ad0a5 → radar-scoring-fp-e9d9819a2b41, while the AI-OFF live values
        // radar-scoring-fp-8daa662a57a6 / radar-scoring-fp-f610244e23c6 are UNCHANGED and asserted so by
        // Compute_LiveWindowAiOffStamps_ArePinned below. The move has two causes folded into one
        // recomputation — news-judgment-signal-v2 (§1.3) and news-judgment-prompt-v3/news-judgment-schema-v3
        // (§2.2) — both reaching the hash through the `news=` segment, which on the AI-OFF side renders its
        // DISABLED form carrying neither the presentation cohort nor the materializer version. See
        // Compute_AiOnDefault_MatchesPinnedFingerprint for the full lineage, the one-time re-judge and the
        // ordered operator action; radar-scoring-fp-81a397434756 is the value the first post-197 baseline
        // must report.
        // These are the FINAL post-197 values; everything named above them is history. The three window
        // pairs are three CORRECT answers at three windows — do NOT reconcile them onto one value; match an
        // accrued stamp against the pair for the window that run actually used.
        //
        // SPEC 198 MOVES THEM AGAIN, TO THE VALUES BELOW - and unlike spec 197 it moves the AI-OFF live
        // values too (Compute_LiveWindowAiOffStamps_ArePinned): 60d radar-scoring-fp-81a397434756 ->
        // radar-scoring-fp-11240da5aeb0; 120d radar-scoring-fp-e9d9819a2b41 ->
        // radar-scoring-fp-7eece22968a4. The cause is the trailing `newsquery=7d;` segment carrying
        // Radar:News:RecencyWindowDays, which is not judgment-gated and so folds in with or without the AI
        // descriptor. radar-scoring-fp-11240da5aeb0 was the value the first post-198 baseline reported
        // (2026-08-29, run 1) and the value stamped live through 2026-09-07.
        //
        // SPEC 214 MOVES THEM TO THE VALUES BELOW, AI-ON side ONLY (the spec-197 pattern): 60d
        // radar-scoring-fp-11240da5aeb0 → radar-scoring-fp-241097438af8; 120d radar-scoring-fp-7eece22968a4
        // → radar-scoring-fp-dc0f9b905f5b, while the AI-OFF live values radar-scoring-fp-0ff442a14c1b /
        // radar-scoring-fp-adf455313d35 are UNCHANGED and asserted so by
        // Compute_LiveWindowAiOffStamps_ArePinned below. Three causes in one recomputation, all through the
        // `news=` segment: comparison-basis-v1 in the cohort key, news-judgment-prompt-v4, and
        // news-judgment-signal-v3 (see Compute_AiOnDefault_MatchesPinnedFingerprint).
        //
        // SPEC 215 MOVED THEM TO THE VALUES BELOW, AI-ON side ONLY, the same pattern: 60d
        // radar-scoring-fp-241097438af8 → radar-scoring-fp-8590412af27c; 120d radar-scoring-fp-dc0f9b905f5b
        // → radar-scoring-fp-a32785c416a6, while the AI-OFF live values are again UNCHANGED and asserted so by
        // Compute_LiveWindowAiOffStamps_ArePinned. Three causes in one recomputation, all through the
        // `news=` segment: news-judgment-prompt-v5, news-judgment-schema-v4 and reference-projection-v1 in
        // the cohort key (see Compute_AiOnDefault_MatchesPinnedFingerprint); news-judgment-signal-v3 did not
        // move. Specs 214 and 215 merged back-to-back, so ONE operator step covered both — and it WAS
        // PERFORMED, on 2026-09-08. The first post-214/215 baseline then reported
        // radar-scoring-fp-8590412af27c, which stamped 102 companies (strategy 'default', earliest score
        // 2026-09-08T21:46:22.311Z) under prompt v5 / schema v4 / reference-projection-v1 with the
        // reported-metrics ledger DISABLED (766c925), so that cohort projected ZERO references. That value
        // is HISTORY, quoted so accrued snapshots stay reconcilable — spec 216 moved it again (below), and
        // the 60-day assertion at the end of this method is the only authority for the current value.
        // radar-scoring-fp-241097438af8 (the 214-only value) was never stamped by a live run, because 214
        // and 215 merged back-to-back.
        //
        // SPEC 216 MOVES THEM TO THE VALUES BELOW, AI-ON side ONLY, the same pattern: 60d
        // radar-scoring-fp-8590412af27c → radar-scoring-fp-d7dbbcf89304; 120d radar-scoring-fp-a32785c416a6
        // → radar-scoring-fp-cb9a65795fb3, while the AI-OFF live values are again UNCHANGED and asserted so
        // by Compute_LiveWindowAiOffStamps_ArePinned. Three causes in one recomputation — the trailing
        // `rm=reported-metrics-v2` field on the ai= descriptor (§5, the FIRST cause in this arc that does
        // not travel through news=), the reported-metrics verification policy v1 → v2 (§3), and
        // reference-projection-v2 + news-judgment-prompt-v6 in the cohort key (§1). See
        // Compute_AiOnDefault_MatchesPinnedFingerprint for the full reasoning.
        //
        // ⚠ THE OPERATOR STEP WAS OWED ONCE MORE — a SECOND, SEPARATE step, not one shared with 214/215,
        // whose step was already performed on 2026-09-08 and whose composition stamped a live run (above).
        // SINCE SATISFIED: run-20260909T234658242Z-5c6644f6 stamped the spec-219 value, so no 216/217/219
        // step remains outstanding (the 60-day assertion below has since moved on; the spec-216 value is
        // quoted above as history). The step was: delete or
        // re-record every configured data/scoring-configs/strategies/{name}.json BEFORE that run (the path
        // is git-ignored, so those records cannot ride in a PR and MUST NEVER be fabricated); if the step
        // is missed, StrategyIdentityGuard halts the run before collection — that halt is CORRECT.
        //
        // ⚠ DO NOT POOL ACROSS 214–216. It is treated as ONE COMPARABILITY boundary spanning the three
        // slices — the level-only gate, the reference values, and the correction that stops a reference
        // validating itself — but that is a deliberate CALL over TWO identity discontinuities, not a
        // shared identity move: radar-scoring-fp-8590412af27c stamped one 102-company run between them
        // (ledger off ⇒ zero references projected). Pooling 214–216 pools that cohort in knowingly. The
        // precommitted 2026-09-29 claim date is UNCHANGED — the boundary describes comparability, not the
        // claim.
        // SPEC 217 §2 MOVES THEM AGAIN, AND THIS TIME THE AI-OFF PAIR MOVES WITH THEM (see
        // Compute_LiveWindowAiOffStamps_ArePinned): 60d radar-scoring-fp-d7dbbcf89304 →
        // radar-scoring-fp-908659c0ba7e; 120d radar-scoring-fp-cb9a65795fb3 →
        // radar-scoring-fp-918f19bd6751. The cause is the UNCONDITIONAL trailing acq= segment — see
        // Compute_DefaultConfig_MatchesPinnedFingerprint for the full reasoning, the measured MarineMax
        // basis and the operator step it required (since satisfied: run-20260909T234658242Z-5c6644f6
        // stamped the spec-219 value). radar-scoring-fp-d7dbbcf89304 is the spec-216 value; whether a
        // live run stamped it depends on whether the 216 operator step was taken before this merge, and
        // this file does not assert that either way.
        //
        // SPEC 219 §6 MOVES THEM AGAIN, AI-ON side ONLY (back to the spec-197/214–216 pattern): 60d
        // radar-scoring-fp-908659c0ba7e → radar-scoring-fp-09c9db128480; 120d radar-scoring-fp-918f19bd6751
        // → radar-scoring-fp-cc5ac1f84de1, while the AI-OFF live values are UNCHANGED and asserted so by
        // Compute_LiveWindowAiOffStamps_ArePinned. ONE cause: the trailing enabled-only COVERAGE-POLICY
        // field on the `news=` segment (NewsJudgmentCoveragePolicy.Version, news-judgment-coverage-v2 —
        // universal judgment coverage with a capped depth). See
        // Compute_AiOnDefault_MatchesPinnedFingerprint for why a coverage policy is an identity input where
        // a budget is not.
        //
        // ⚠ THE OPERATOR STEP WAS OWED ONCE MORE — a FOURTH, SEPARATE step, distinct from the 214/215 step
        // (performed 2026-09-08) and from the 216 and 217 ones. SINCE SATISFIED:
        // run-20260909T234658242Z-5c6644f6 stamped the spec-219 60-day value (radar-scoring-fp-09c9db128480,
        // quoted above as history), so no 216/217/219 step remains outstanding. The step was: delete or
        // re-record every configured
        // data/scoring-configs/strategies/{name}.json BEFORE that run (the path is git-ignored, so those
        // records cannot ride in a PR and MUST NEVER be fabricated); if the step is missed,
        // StrategyIdentityGuard halts the run before collection — that halt is CORRECT.
        //
        // ⚠ SPEC 219 IS ALSO A COMPARABILITY BOUNDARY IN ITS OWN RIGHT, and a bigger one than a pin move
        // usually implies: it is not a re-weighting, it is a new SENSE ORGAN. Before it, ~83 of 102
        // companies reached scoring with news as undirected volume; after it they reach it with a judged
        // direction. A step change in the efficacy series across this date is that, not an improvement in
        // the scoring — the date is recorded in docs/architecture-history.md for exactly that reason.
        //
        // SPEC 220 §1 MOVES THEM AGAIN, AI-ON side ONLY (the spec-219 pattern): 60d
        // radar-scoring-fp-09c9db128480 → the 60-day value below; the 120-day value likewise (see the
        // assertion), while the AI-OFF live values are UNCHANGED and asserted so by
        // Compute_LiveWindowAiOffStamps_ArePinned. ONE cause: the judgment cohort key gains a trailing
        // `ordering=family-ordering-v2` segment (NewsJudgmentFamilyOrdering.Version — the judge's family
        // budget is now filled comparison-basis-first rather than by syndication volume), which the
        // presentation cohort key carries into `news=enabled:…`. See Compute_AiOnDefault_MatchesPinnedFingerprint.
        // radar-scoring-fp-09c9db128480 is the spec-219 value and DID stamp a live run
        // (run-20260909T234658242Z-5c6644f6, the spec-220 §4 baseline).
        //
        // ⚠ THE OPERATOR STEP WAS OWED ONCE MORE — a FIFTH, SEPARATE step, distinct from the 214/215 step
        // (performed 2026-09-08) and from the 216, 217 and 219 ones. This file does not assert whether it has
        // been taken, nor whether a live run stamped the spec-220 60-day value (radar-scoring-fp-59a1064a7ad6,
        // quoted here as history).
        //
        // SPEC 221 MOVES THEM AGAIN, AI-ON side ONLY (the spec-219/220 pattern): 60d
        // radar-scoring-fp-59a1064a7ad6 → the 60-day value below; 120d radar-scoring-fp-8c72c2fbdf0a → the
        // 120-day value below, while the AI-OFF live values are UNCHANGED and asserted so by
        // Compute_LiveWindowAiOffStamps_ArePinned. Four causes, all through `news=enabled:…`:
        // family-ordering-v3, news-judgment-prompt-v7, news-judgment-schema-v5 and the NoBusinessSignal
        // mapping token. See Compute_AiOnDefault_MatchesPinnedFingerprint.
        //
        // ⚠ THE OPERATOR STEP IS OWED AGAIN — a SIXTH step. If no baseline ran between the spec-220 and
        // spec-221 merges, the two collapse into ONE step (both fork only this AI-ON side); otherwise they are
        // two. Either way: whatever the 60-day assertion below says is the value the first post-221 baseline
        // must report. Delete or re-record every configured data/scoring-configs/strategies/{name}.json BEFORE
        // that run (the path is git-ignored, so those records cannot ride in a PR and MUST NEVER be
        // fabricated); if the step is missed, StrategyIdentityGuard halts the run before collection — that
        // halt is CORRECT.
        //
        // SPEC 224 MOVES THEM AGAIN, AND THE AI-OFF PAIR MOVES WITH THEM (see
        // Compute_LiveWindowAiOffStamps_ArePinned — the spec-217 shape): 60d radar-scoring-fp-741269b4384b
        // → radar-scoring-fp-692055768db8; 120d radar-scoring-fp-e4aaf21c09a9 → radar-scoring-fp-60f898fd0d1f.
        // ONE cause: the new unconditional `insiderCollapse=` field (insider-collapse-v1;window=30;), its
        // own fixed-position fingerprint field after mediaCollapse. radar-scoring-fp-741269b4384b is the
        // spec-221 value; whether a live run stamped it depends on whether the 220/221 operator step was
        // taken before this merge, and this file does not assert that either way.
        //
        // ⚠ THE OPERATOR STEP IS OWED — and if no baseline ran between the spec-220/221 merges and this one,
        // the three collapse into ONE step (224 forks both sides; 220/221 fork only this AI-ON side, so the
        // union is still "delete or re-record every configured data/scoring-configs/strategies/{name}.json
        // BEFORE the first post-merge run"). Whatever the 60-day assertion below says is the value the first
        // post-224 baseline must report; a StrategyIdentityGuard halt before that step is CORRECT.
        //
        // SPEC 226 MOVES THEM AGAIN, AND THE AI-OFF PAIR MOVES WITH THEM (the spec-217/224 shape): 60d
        // radar-scoring-fp-692055768db8 → radar-scoring-fp-b8872cce9666; 120d radar-scoring-fp-60f898fd0d1f →
        // radar-scoring-fp-7b917a3d3d26. Two causes in one recomputation, both unconditional: the rules= token
        // radar-keyword-rules-v8 → v9 and acq-supersede-v1 → v2. radar-scoring-fp-692055768db8 is the spec-224
        // value, quoted as history. ONE OPERATOR STEP WAS OWED after merge (TAKEN 2026-09-14): delete or re-record every configured
        // data/scoring-configs/strategies/{name}.json BEFORE the first post-226 run (git-ignored, never
        // fabricated). Spec 227 has since moved both values again — see below.
        //
        // SPEC 227 MOVES THEM AGAIN, AND THE AI-OFF PAIR MOVES WITH THEM (the spec-217/224/226 shape): 60d
        // radar-scoring-fp-b8872cce9666 → radar-scoring-fp-f8c4a612502c; 120d radar-scoring-fp-7b917a3d3d26 →
        // radar-scoring-fp-18a020b22eac. One cause, unconditional: the acq= scan half acqscan-v1 → acqscan-v2.
        // radar-scoring-fp-b8872cce9666 is the spec-226 value, quoted as history. ONE OPERATOR STEP WAS OWED
        // after merge (TAKEN 2026-09-15): delete or re-record every configured
        // data/scoring-configs/strategies/{name}.json BEFORE the first post-227 run (git-ignored, never fabricated).
        //
        // SPEC 228 MOVES THEM AGAIN, AND THE AI-OFF PAIR MOVES WITH THEM (the spec-217/224/226/227 shape): 60d
        // radar-scoring-fp-f8c4a612502c → radar-scoring-fp-376fec1cc260; 120d radar-scoring-fp-18a020b22eac →
        // radar-scoring-fp-afd955beba0f. One cause, unconditional: the acq= scan half acqscan-v2 → acqscan-v3 (the
        // read, not the rule). radar-scoring-fp-f8c4a612502c is the spec-227 value, quoted as history. ⚠ ONE
        // OPERATOR STEP IS OWED after merge: delete or re-record every configured
        // data/scoring-configs/strategies/{name}.json BEFORE the first post-228 run (git-ignored, never
        // fabricated); whatever the 60-day assertion below says is the value that run must report.
        Assert.Equal(
            "radar-scoring-fp-376fec1cc260",
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor, window: TimeSpan.FromDays(60)));
        Assert.Equal(
            "radar-scoring-fp-afd955beba0f",
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor, window: TimeSpan.FromDays(120)));
    }

    [Fact]
    public void Compute_LiveWindowAiOffStamps_ArePinned()
    {
        // The AI-OFF counterparts of the two live-window stamps above. Until spec 194 §2 they lived ONLY in
        // prose — in this file's pin comments and in scripts/run-profiles/default.json's operator comment —
        // so nothing asserted them and a transcription error could survive indefinitely. They are what a run
        // with Radar:Ai unconfigured stamps at the two windows real runs use; pinning them makes all six
        // recorded values change-detected instead of four of them.
        //
        // ⚠ SPEC 197 LEFT BOTH OF THESE EXACTLY AS SPEC 196 SET THEM, AND THAT IS AN ASSERTED DELIVERABLE.
        // §4 predicted the split in advance: the three AI-ON pins move because their `news=enabled:…`
        // segment carries the presentation cohort (news-judgment-prompt-v3/news-judgment-schema-v3, §2.2)
        // and the materializer identity (news-judgment-signal-v2, §1.3); the disabled segment
        // `news=disabled:legacy-news-inheritance-v1:news-judgment-supersede-v1;` carries neither, so it
        // cannot see either move. If either value below ever changes in a slice that touches only the
        // judgment read, the finding is SCOPE LEAKAGE, not a deliverable.
        //
        // SPEC 198 MOVES BOTH OF THESE, AND THAT MOVE IS THE DELIVERABLE - the mirror image of spec 197's
        // non-move. The `newsquery=7d;` segment is appended UNCONDITIONALLY of the judgment read (it carries
        // Radar:News:RecencyWindowDays, which governs the newssearch collector whether or not any AI or
        // judgment seam is registered), so if either value below had stayed put the window would not
        // actually be hashed and a query change could still move every score silently. 60d
        // radar-scoring-fp-8daa662a57a6 → radar-scoring-fp-0ff442a14c1b; 120d
        // radar-scoring-fp-f610244e23c6 → radar-scoring-fp-adf455313d35. See the AI-OFF unit pin for the
        // measured basis, the additivity control and the operator action.
        //
        // SPEC 214 MOVED NEITHER OF THESE, AND THAT NON-MOVE IS AN ASSERTED DELIVERABLE (the spec-197
        // pattern): comparison-basis-v1, news-judgment-prompt-v4 and news-judgment-signal-v3 all travel
        // inside the `news=enabled:…` segment, which the disabled descriptor never renders. NEITHER DID
        // SPEC 215, on the same reasoning.
        //
        // SPEC 216 MOVED NEITHER EITHER, AND THAT NON-MOVE IS AN ASSERTED DELIVERABLE — this time for TWO
        // independent reasons, because spec 216 has an input that does NOT ride the news= segment. Its
        // `rm=` field rides the DIRECTIONAL-FILING `ai=` descriptor, which SignalSourceDescriptor folds in
        // ONLY when the AI filing source is registered: with no AI read there is nothing to extract, so
        // there is nothing to hash. Its reference-projection-v2 and news-judgment-prompt-v6 ride
        // `news=enabled:…`, which the disabled descriptor never renders. If either value below ever moves
        // in a slice that touches only the filing read or the judgment read, the finding is SCOPE LEAKAGE,
        // not a deliverable.
        // ⚠ SPEC 217 §2 MOVES BOTH OF THESE, AND THAT MOVE IS THE DELIVERABLE — the mirror image of the
        // 197 / 214 / 215 / 216 non-moves, and the same shape as spec 198's. The
        // `acq=acqscan-v1;supersede=acq-supersede-v1;` segment is appended UNCONDITIONALLY: the
        // corporate-action supersede is pure assembly code that runs whether or not any AI or judgment seam
        // is registered, so if either value below had stayed put the rule would not actually be hashed and a
        // recognition could move every affected score silently. 60d radar-scoring-fp-0ff442a14c1b →
        // radar-scoring-fp-7b0758e7eede; 120d radar-scoring-fp-adf455313d35 →
        // radar-scoring-fp-5b36883c1b3a. See the AI-OFF unit pin for the measured basis (MarineMax
        // 2026-08-10) and the operator step it required (since satisfied: run-20260909T234658242Z-5c6644f6
        // stamped the spec-219 value).
        //
        // SPECS 219, 220 AND 221 MOVED NEITHER OF THESE, AND EACH NON-MOVE IS AN ASSERTED DELIVERABLE: the
        // coverage policy (219), the `ordering=` token (220, then v3 in 221), prompt v7 / schema v5 and the
        // `NoBusinessSignal>none` mapping token (221) all travel inside `news=enabled:…`, which the disabled
        // descriptor never renders. If either value below moves in a slice that touches only the judgment
        // read, the finding is SCOPE LEAKAGE.
        //
        // ⚠ SPEC 224 MOVES BOTH OF THESE, AND THAT MOVE IS THE DELIVERABLE — the same shape as specs 198 and
        // 217. The `insiderCollapse=` field is appended UNCONDITIONALLY (the same-insider collapse is pure
        // assembly code that runs whether or not any AI or judgment seam is registered), so if either value
        // below had stayed put the collapse window would not actually be hashed and a window change could
        // move every insider-heavy score silently. 60d radar-scoring-fp-7b0758e7eede →
        // radar-scoring-fp-0e09edc016da; 120d radar-scoring-fp-5b36883c1b3a → radar-scoring-fp-9cb00807c3cf.
        // See the AI-OFF unit pin for the measured basis and the operator step.
        //
        // ⚠ SPEC 226 MOVES BOTH OF THESE, AND THAT MOVE IS THE DELIVERABLE: the rules= token
        // (radar-keyword-rules-v8 → v9) and the acq= segment (acq-supersede-v1 → v2) are both rendered with or
        // without any AI or judgment seam, so an unchanged AI-OFF pin would mean the rule change is not hashed.
        // 60d radar-scoring-fp-0e09edc016da → radar-scoring-fp-9d1665dcebbb; 120d radar-scoring-fp-9cb00807c3cf
        // → radar-scoring-fp-ba32db581757. See the AI-OFF unit pin for the measured basis and the operator step.
        //
        // ⚠ SPEC 227 MOVES BOTH OF THESE, AND THAT MOVE IS THE DELIVERABLE: the acq= segment's scan half
        // (acqscan-v1 → acqscan-v2) is rendered with or without any AI or judgment seam, so an unchanged AI-OFF
        // pin would mean the recognition-rule change is not hashed. 60d radar-scoring-fp-9d1665dcebbb →
        // radar-scoring-fp-cfc2fb4370f4; 120d radar-scoring-fp-ba32db581757 → radar-scoring-fp-e878916856dc. See
        // the AI-OFF unit pin for the measured basis and the operator step.
        //
        // ⚠ SPEC 228 MOVES BOTH OF THESE, AND THAT MOVE IS THE DELIVERABLE: the acq= segment's scan half
        // (acqscan-v2 → acqscan-v3, the version now covering the item-1.01 READ) is rendered with or without any
        // AI or judgment seam, so an unchanged AI-OFF pin would mean the read change is not hashed. 60d
        // radar-scoring-fp-cfc2fb4370f4 → radar-scoring-fp-219d216de6b2; 120d radar-scoring-fp-e878916856dc →
        // radar-scoring-fp-ef8f67469ab8. See the AI-OFF unit pin for the measured basis and the operator step.
        Assert.Equal("radar-scoring-fp-219d216de6b2", DefaultFingerprint(window: TimeSpan.FromDays(60)));
        Assert.Equal("radar-scoring-fp-ef8f67469ab8", DefaultFingerprint(window: TimeSpan.FromDays(120)));
    }

    [Fact]
    public void Compute_ChangedAiStrength_ChangesFingerprint()
    {
        // Tuning the AI signal's Strength re-stamps the fingerprint by value (spec 106) — the deferred Strength
        // recalibration cannot silently produce falsely-comparable snapshots.
        var changed = AiOnSourceDescriptorWith(
            AiDirectionalDescriptor.Replace("str=8", "str=9", StringComparison.Ordinal));

        Assert.NotEqual(
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor),
            DefaultFingerprint(sourceDescriptor: changed));
    }

    [Fact]
    public void Compute_ChangedAiModel_ChangesFingerprint()
    {
        // Spec 119: the earnings-read MODEL is folded in by value because it changes signal DIRECTION (the
        // 2026-07-21 A/B: llama3.1 read EOSE Improving 0.90 where DeepSeek-V4-Flash read the same release
        // Mixed 0.85). Two runs on different models must therefore never share a ScoringConfigVersion —
        // otherwise the efficacy line would be drawn as continuous across a real change.
        const string previousModel =
            "directional-filing:str=8;nov=6;minconf=0.6;model=ollama:llama3.1;cmpscan=cmpscan-v1;cmpcap=0.65";

        Assert.NotEqual(
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor),
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptorWith(previousModel)));
    }

    [Fact]
    public void Compute_ChangedFormulaVersion_ChangesFingerprint()
    {
        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(formulaVersion: "radar-formula-v7"));
    }

    [Fact]
    public void Compute_ChangedFollowingTierDiscount_ChangesFingerprint()
    {
        // The spec-117 following-discount magnitudes are hashed by value: tuning a tier discount (a config
        // edit, no formula bump) must re-stamp the fingerprint so runs stay comparable (AD-10).
        Assert.NotEqual(
            DefaultFingerprint(),
            DefaultFingerprint(weights: new ScoringWeights { FollowingTierDiscountMega = 0.6 }));
    }

    [Fact]
    public void Compute_ChangedMediaCollapseWindow_ChangesFingerprint()
    {
        // Changing the same-event media-collapse window changes how many MediaAttention signals feed the
        // formula, so the fingerprint must re-stamp automatically by value (spec 109 — no _formula.Version /
        // RuleSetVersion bump; the window magnitude is hashed via the media-collapse descriptor).
        var changedWindow =
            new MediaAttentionCollapse(new MediaCollapseOptions { EventWindowDays = 7.0 }).CanonicalDescriptor();

        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(mediaCollapseDescriptor: changedWindow));
    }

    [Fact]
    public void Compute_ChangedInsiderCollapseWindow_ChangesFingerprint()
    {
        // Spec 224: changing the same-insider collapse window changes how many InsiderBuying signals feed the
        // formula (and at what aggregate Strength), so the fingerprint must re-stamp automatically by value —
        // no _formula.Version / RuleSetVersion bump; the window magnitude is hashed via the insider-collapse
        // descriptor exactly as the media window is via the media one.
        var changedWindow = new InsiderActivityCollapse(
                new InsiderCollapseOptions { EventWindowDays = 7.0 }, new InsiderMaterialityWeights())
            .CanonicalDescriptor();

        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(insiderCollapseDescriptor: changedWindow));

        // ...on BOTH pin families, because the field is unconditional (the spec-198/217 shape).
        Assert.NotEqual(
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor),
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor, insiderCollapseDescriptor: changedWindow));
    }

    [Fact]
    public void InsiderCollapseDescriptor_IsTheExactHashedString()
    {
        // The literal a live run hashes for the code default: structure version + window, trailing ';'.
        // Pinned as a string so a silent change to the descriptor's shape (not just its value) fails here.
        Assert.Equal("insider-collapse-v1;window=30;", InsiderCollapseDescriptor);
        Assert.Equal("insider-collapse-v1", InsiderActivityCollapse.Version);
    }

    [Fact]
    public void Compute_ChangedCollapsedBreadthCredit_ChangesFingerprint()
    {
        // The spec-122 breadth-preserving-collapse credit is hashed by value: dialling it (a config edit, no
        // formula bump) changes the Attention reach, so runs on different credits must never be falsely
        // comparable (AD-10). Credit 0.0 is the radar-formula-v7-equivalent setting.
        Assert.NotEqual(
            DefaultFingerprint(),
            DefaultFingerprint(weights: new ScoringWeights { CollapsedBreadthCredit = 0.0 }));
    }

    // ---- spec 148: the two inputs that used to be hashed into nothing -----------------------------------

    [Fact]
    public void Compute_ChangedScoringWindow_ChangesFingerprint()
    {
        // THE spec-148 acceptance criterion. Radar:ScoringWindowDays selects ScoringOptions.Window, which
        // bounds BOTH the current window and the previous/velocity window — so a 14-day and a 30-day run over
        // the same evidence produce materially different Trajectory, SignalVelocity and Attention. Before this
        // slice they stamped the SAME ScoringConfigVersion, which is exactly the "silently continue one series
        // while measuring something else" failure StrategyIdentityGuard's own error message describes (and,
        // being an in-place edit to a named strategy, the one category the guard structurally could not see).
        Assert.NotEqual(
            DefaultFingerprint(window: TimeSpan.FromDays(30)),
            DefaultFingerprint(window: TimeSpan.FromDays(14)));
    }

    [Fact]
    public void Compute_WindowEncoding_IsInjective_NotTruncatedToWholeDays()
    {
        // The window is hashed as TICKS, deliberately. Whole-days would be lossy: these two windows differ by
        // 12 hours and would collide under a day-truncating encoding, silently making two different scorings
        // share one stamp — the precise failure the field exists to prevent (AD-3 determinism does not permit
        // a lossy identity).
        Assert.NotEqual(
            DefaultFingerprint(window: TimeSpan.FromHours(24)),
            DefaultFingerprint(window: TimeSpan.FromHours(36)));

        // …and sub-day precision survives all the way down to a single tick.
        Assert.NotEqual(
            DefaultFingerprint(window: TimeSpan.FromDays(30)),
            DefaultFingerprint(window: TimeSpan.FromDays(30) + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void Compute_ChangedTrajectoryCorroborationK_ChangesFingerprint()
    {
        // THE other spec-148 acceptance criterion, and the last ScoringWeights field the fold had missed
        // (recorded as a known gap by the spec-146 hand-back). k is the denominator smoother in
        // radar-formula-v8's T_raw = 10·(Mpos−Mneg)/(Mpos+Mneg+k) AND — since spec 146 — in v9's per-channel
        // direction factor, so tuning it moves scores in both formulas.
        Assert.NotEqual(
            DefaultFingerprint(),
            DefaultFingerprint(weights: new ScoringWeights { TrajectoryCorroborationK = 4.0 }));
    }

    [Fact]
    public void Compute_EveryScoringWeightsProperty_IsFoldedIntoTheFingerprint()
    {
        // A COMPLETENESS GUARD, not a spot check. Spec 148 exists because TrajectoryCorroborationK sat
        // unfolded for seven slices while every review read the fold as exhaustive. Enumerating the record's
        // public properties by reflection makes the NEXT unfolded weight fail loudly here — the day it is
        // added — instead of silently producing falsely-comparable snapshots.
        //
        // Perturbation only has to change the hashed string: Compute() does not call Validate(), so a value
        // that would be rejected at startup is still a legitimate probe of whether the field is read at all.
        var baseline = DefaultFingerprint();
        var properties = typeof(ScoringWeights)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToList();

        // NO CanWrite FILTER. Filtering on settability would silently EXCLUDE a future
        // `public double Foo { get; }` from the very guard that exists to catch the next unfolded weight —
        // exhaustiveness is the whole point, so an unperturbable property must fail here and force a
        // conscious decision rather than disappear from the enumeration.
        Assert.All(
            properties,
            p => Assert.True(
                p.CanRead && p.CanWrite,
                $"ScoringWeights.{p.Name} is not both readable and settable, so this completeness guard "
                    + "cannot perturb it. Give it an init accessor, or fold it in and extend this test "
                    + "deliberately — do not let it fall out of the enumeration."));

        // Not vacuous: if the reflection query ever returns nothing (a refactor to fields, say) the loop
        // would pass by doing nothing at all.
        Assert.Equal(27, properties.Count);

        foreach (var property in properties)
        {
            Assert.Equal(typeof(double), property.PropertyType);

            var perturbed = new ScoringWeights();
            var current = (double)property.GetValue(perturbed)!;
            // A distinct, finite sentinel for every field regardless of its default (0.0 included).
            property.SetValue(perturbed, current + 0.123456789);

            Assert.NotEqual(baseline, DefaultFingerprint(weights: perturbed));
        }
    }

    [Fact]
    public void ScoringOptions_ExposesExactlyOneKnob_AndItIsFolded()
    {
        // The ScoringOptions equivalent of the ScoringWeights completeness guard above. ScoringOptions is a
        // plain class rather than a record of doubles, so instead of perturbing every property generically
        // this pins the SET of properties: exactly one, named Window, of type TimeSpan — and the test above
        // proves that one is folded. A second operational knob therefore cannot be added without a conscious
        // decision about whether it is output-affecting, which is precisely the decision that was skipped for
        // Window itself between spec 89 and spec 148.
        var properties = typeof(ScoringOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p.Name, p.PropertyType))
            .ToList();

        Assert.Equal([(nameof(ScoringOptions.Window), typeof(TimeSpan))], properties);
    }
}
