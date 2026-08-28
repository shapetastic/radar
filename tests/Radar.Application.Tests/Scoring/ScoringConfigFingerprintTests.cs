using System.Globalization;
using System.Reflection;
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
    // PatentActivity; spec 103 added HiringActivity.)
    //
    // SPEC 194 §2 APPENDS A SECOND SEGMENT: the news-read identity, ALWAYS present, rendered here in its
    // DISABLED form because this constant is the code-default composition — nothing optional registered, the
    // same reason it carries no ai= segment. Built through the real NewsJudgmentScoringIdentity rather than
    // written as a literal, so it cannot drift from what SignalSourceDescriptor actually emits.
    private static readonly string SourceDescriptor =
        "rules=radar-keyword-rules-v8;" + NewsJudgmentScoringIdentity.Disabled.Segment;

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
        TimeSpan? window = null) =>
        ScoringConfigFingerprint.Compute(
            "mvp-engine-v1",
            formulaVersion ?? "radar-formula-v8",
            weights ?? new ScoringWeights(),
            attentionDescriptor ?? DefaultTierDescriptor(),
            sourceDescriptor ?? SourceDescriptor,
            insiderDescriptor ?? InsiderDescriptor,
            mediaCollapseDescriptor ?? MediaCollapseDescriptor,
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
        Assert.Equal("radar-scoring-fp-54e845330f96", DefaultFingerprint());
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
        // RuleSetVersion (spec 194 §1.1 moved the default to v8, so this perturbs to v9) — a perturbation
        // equal to the default would make this test VACUOUS, which is exactly what happened when the shipped
        // version caught up with a perturbation literal that was not moved with it. Whoever bumps
        // KeywordSignalExtractor.RuleSetVersion next must move this literal in the same slice.
        // SPEC 194 §2: the perturbation carries the SAME news segment as the default, so the only thing that
        // differs is the rules= token — otherwise this would prove that two descriptors differing in two
        // places hash differently, which is a weaker claim.
        var perturbed = "rules=radar-keyword-rules-v9;" + NewsJudgmentScoringIdentity.Disabled.Segment;

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
    private const string AiDirectionalDescriptor =
        "directional-filing:str=8;nov=6;minconf=0.6;model=openai:deepseek-ai/DeepSeek-V4-Flash;cmpscan=cmpscan-v1;cmpcap=0.65";

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
    private static string AiOnSourceDescriptorWith(string aiDirectionalDescriptor) =>
        "rules=radar-keyword-rules-v8;"
            + $"ai={DescriptorEscaping.Escape(aiDirectionalDescriptor)};"
            + LiveNewsJudgmentSegment;

    private static readonly string AiOnSourceDescriptor =
        AiOnSourceDescriptorWith(AiDirectionalDescriptor);

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
        Assert.Equal(
            "radar-scoring-fp-e7317fd038ac",
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
        Assert.Equal(
            "radar-scoring-fp-81a397434756",
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor, window: TimeSpan.FromDays(60)));
        Assert.Equal(
            "radar-scoring-fp-e9d9819a2b41",
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
        Assert.Equal("radar-scoring-fp-8daa662a57a6", DefaultFingerprint(window: TimeSpan.FromDays(60)));
        Assert.Equal("radar-scoring-fp-f610244e23c6", DefaultFingerprint(window: TimeSpan.FromDays(120)));
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
