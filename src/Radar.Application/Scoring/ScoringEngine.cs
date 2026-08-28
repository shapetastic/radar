using Microsoft.Extensions.Logging;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Domain.Companies;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// Deterministic Stage 6 orchestration. Selects the recent-signal window, loads the evidence behind
/// each reviewed signal, loads the company's curated <see cref="FollowingTier"/> (spec 117 — the
/// non-price notedness input the v7 Opportunity discount consumes; a missing company fail-safes to
/// <see cref="FollowingTier.Small"/>), delegates the actual scoring math to <see cref="IScoreFormula"/>,
/// maps the result onto a domain <see cref="CompanyScoreSnapshot"/>, builds one
/// <see cref="ScoreEvidenceLink"/> per contribution, and persists everything via
/// <see cref="IScoreRepository"/>.
///
/// <para>
/// HUMAN-OWNED BOUNDARY: this engine contains <b>no scoring formula</b>. It never inspects, computes,
/// or hard-codes weights — all scoring math lives behind <see cref="IScoreFormula"/>. The operational
/// knobs introduced here (the window length and the "only Approved signals" rule) are tunable pipeline
/// scaffolding, not formula weights.
/// </para>
/// <para>
/// GENERATION STAMP: the <c>ScoringConfigVersion</c> stamped on every snapshot is no longer a hand-bumped
/// code constant but a <b>deterministic content fingerprint</b> of the effective resolved scoring config —
/// the structure identity (<see cref="EngineVersion"/> + <c>_formula.Version</c>) plus every
/// <see cref="ScoringWeights"/> value plus the attention tier-map descriptor
/// (<see cref="IAttentionSourceWeights.CanonicalDescriptor"/>) plus the signal-source IDENTITY descriptor
/// (<see cref="ISignalSourceDescriptor.CanonicalDescriptor"/> — the extractor rule-set identity + the
/// optional AI directional-filing magnitudes; spec 95, narrowed by spec 141) plus the insider-materiality
/// descriptor
/// (<see cref="InsiderMaterialityWeights.CanonicalDescriptor"/> — the config-tunable buy/sell tiers +
/// cluster boost, spec 96) plus the media-collapse descriptor
/// (<see cref="MediaAttentionCollapse.CanonicalDescriptor"/> — the same-event media-attention collapse
/// structure + window, spec 109) plus the recent-signal WINDOW length
/// (<see cref="ScoringOptions.Window"/>, spec 148), computed once via
/// <see cref="ScoringConfigFingerprint"/> (AD-10 as amended). Any output-affecting change (formula shape
/// <b>as expressed by <c>_formula.Version</c></b>, any weight, the tier map, an insider materiality tier, the
/// media-collapse window, the scoring window) re-stamps automatically. <c>ScoringVersion</c> (structure
/// identity, <c>$"{EngineVersion}+{_formula.Version}"</c>) is unchanged.
/// </para>
/// <para>
/// ⚠ <b>THE "formula shape" TERM IS EXACTLY AS STRONG AS AD-6 COMPLIANCE, and spec 149 is the shipped
/// exception.</b> This engine hashes the formula's VERSION TOKEN, not its code, so a formula edited in place
/// without a <c>radar-formula-vN</c> bump re-stamps nothing. Spec 149 did that deliberately: it added the
/// notedness discount to <see cref="RadarScoreFormulaV9"/>'s composition while leaving the version at
/// <c>radar-formula-v9</c> and the default <see cref="ScoringWeights"/> untouched, so a v9 strategy's
/// <c>ScoringConfigVersion</c> does NOT move even though its output did — and
/// <see cref="StrategyIdentityGuard"/> does not trip. The remedy is a NEW strategy name; see the AD-6
/// paragraph on <see cref="RadarScoreFormulaV9"/>.
/// </para>
/// <para>
/// SPEC 153 CLOSES THAT HOLE FOR ANY FORMULA THAT OPTS IN. What this engine stamps is no longer
/// <c>_formula.Version</c> but the COMPOSED identity <see cref="FormulaIdentity.Of"/> returns —
/// <c>{Version}@{CompositionRevision}</c> when the formula declares a revision, the bare token otherwise. All
/// THREE stamping sites (the hashed <c>formulaVersion</c> field, <see cref="EffectiveScoringConfig.FormulaVersion"/>
/// and <c>ScoringVersion</c>) route through that one helper, so the hashed identity and the persisted record
/// cannot disagree — which is what keeps the scoring-config store's recompute-from-stored self-verification
/// true. v8 and v9 declare no revision, so their stamps are byte-identical to before; only
/// <c>radar-formula-v10</c> currently carries one.
/// </para>
/// <para>
/// COLLECTION PROVENANCE IS RECORDED, NOT HASHED (spec 141). The enabled-collector set is no longer a
/// fingerprint input: it is stamped verbatim on every snapshot as
/// <see cref="CompanyScoreSnapshot.CollectionProvenance"/> from
/// <see cref="ISignalSourceDescriptor.CollectionProvenance"/>. Enabling or disabling a collector therefore
/// changes THAT field and nothing else — not the fingerprint, not a single component score. The score series
/// is keyed by <see cref="CompanyScoreSnapshot.StrategyName"/> (see <see cref="ScoreSeriesKey"/>), and the
/// fingerprint is demoted from primary key to recorded provenance plus a startup drift tripwire
/// (<see cref="StrategyIdentityGuard"/>).
/// </para>
/// <para>
/// ONE ENGINE INSTANCE IS ONE STRATEGY (spec 137). Every scoring-affecting input is constructor-injected and
/// the effective config + fingerprint are resolved once here, so running N strategies over one collection
/// pass is purely a COMPOSITION concern (<see cref="IScoringStrategyFactory"/>) — the scoring core, the
/// <see cref="IScoringEngine"/> signature and the formula are all untouched. The engine additionally stamps
/// the strategy's human-readable name on each snapshot; that name is NOT a fingerprint input.
/// </para>
/// <para>
/// A strategy may additionally declare WHICH <see cref="SignalType"/>s it consumes (spec 138) via
/// <see cref="SignalTypeFilter"/>. That set — unlike the name — IS folded into the fingerprint (through the
/// signal-source descriptor), because two strategies scoring different signal sets are genuinely different
/// scorings. The default filter consumes everything and folds in as a no-op, so the pinned default
/// fingerprints are unmoved.
/// </para>
/// <para>
/// A strategy may further declare a <see cref="ScoringChannelSet"/> — the weighted channel budget
/// <c>radar-formula-v9</c> composes its score from (spec 146). Like the signal-type set it IS a fingerprint
/// input, folded in on the SAME <c>Describe</c> chain so the composition the formula performs and the hashed
/// identity of that composition cannot drift; and like it, the default (no channels) folds in as a no-op, so
/// the pinned default fingerprints stay put. The formula VERSION was already a hashed field, so nothing new
/// was needed for a v9 strategy to stamp differently from a v8 one. The engine additionally hands the formula
/// this run's enabled-collector VOCABULARY as <see cref="ScoringInput.EnabledCollectors"/> — recorded
/// provenance, hashed into nothing, never a scoring input. Since spec 147 that vocabulary is config-derived
/// and so is correct in every run mode, including a <c>score</c> pass that registers no collector instance;
/// read the note on <see cref="ISignalSourceDescriptor.EnabledCollectors"/> for what a channel's
/// "did not run" split does and does not tell you.
/// </para>
/// </summary>
public sealed class ScoringEngine : IScoringEngine
{
    private const string EngineVersion = "mvp-engine-v1";

    private readonly ISignalRepository _signalRepository;
    private readonly ISignalFileStore _signalFileStore;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly IScoreRepository _scoreRepository;
    private readonly ICompanyRepository _companyRepository;
    private readonly IScoreFormula _formula;
    private readonly MediaAttentionCollapse _mediaCollapse;
    private readonly ScoringOptions _options;
    private readonly ILogger<ScoringEngine> _logger;

    // The human-readable identity of the strategy this engine instance IS (spec 137), stamped on every
    // snapshot alongside the opaque fingerprint. Null when the engine is composed outside a strategy set
    // (⇒ primary/legacy). Deliberately NOT a fingerprint input and NOT part of EffectiveScoringConfig: two
    // strategies that resolve to the same effective config are genuinely comparable, and hashing the name
    // would move the pinned default fingerprints for no scoring-affecting reason (AD-10).
    private readonly string? _strategyName;

    // The SignalTypes this strategy consumes (spec 138). Defaults to SignalTypeFilter.All (consume
    // everything), which Describe() folds into the source descriptor as a NO-OP, so the default composition's
    // fingerprint is byte-identical to the pre-138 value. Unlike the strategy NAME this IS a fingerprint
    // input: two strategies scoring different signal sets are genuinely different scorings.
    private readonly SignalTypeFilter _signalTypes;

    // The weighted channel budget this strategy composes its score from (spec 146). Defaults to
    // ScoringChannelSet.Empty, which Describe() folds into the source descriptor as a NO-OP, so a strategy
    // that declares none hashes exactly what it hashed before this existed. Only radar-formula-v9 consumes
    // it; ScoringStrategySet rejects the two incoherent combinations (v9 with none, non-v9 with some).
    private readonly ScoringChannelSet _channels;

    // The whole scoring-generation stamp: a content fingerprint of the effective resolved scoring config
    // (structure + all weights + tier map), computed once and stamped on every snapshot's
    // ScoringConfigVersion (AD-10 amended, spec 89). Distinct from ScoringVersion. Since spec 141 it is
    // recorded provenance + the startup drift tripwire, NOT the comparability key — that is StrategyName
    // (ScoreSeriesKey).
    private readonly string _scoringConfigFingerprint;

    // WHAT WAS COLLECTED on this run (spec 141): the enabled-collector descriptor, captured once here and
    // stamped verbatim on every snapshot. Deliberately hashed into NOTHING — it is provenance, not identity,
    // so a collector toggle re-stamps this field alone and never the fingerprint or a score.
    private readonly string _collectionProvenance;

    // The same enabled-collector set as _collectionProvenance, as names, handed to the formula on every
    // ScoringInput (spec 146) so a v9 channel can distinguish "declared collector ran and found nothing"
    // from "declared collector did not run". Recorded provenance, hashed into nothing, never a score input.
    private readonly IReadOnlyList<string> _enabledCollectors;

    // The effective resolved scoring config projection (same tuple the fingerprint hashes), built once in
    // the constructor and exposed as a pure accessor for content-addressed persistence (spec 91). Additive:
    // it does not change scoring output or the stamped fingerprint value.
    private readonly EffectiveScoringConfig _effectiveConfig;

    // The formula's COMPOSED identity (spec 153): Version, plus @CompositionRevision when the formula
    // declares one. Resolved ONCE so the hashed field, the persisted EffectiveScoringConfig record and every
    // snapshot's ScoringVersion are the same string by construction (see FormulaIdentity).
    private readonly string _formulaIdentity;

    public ScoringEngine(
        ISignalRepository signalRepository,
        ISignalFileStore signalFileStore,
        IEvidenceRepository evidenceRepository,
        IScoreRepository scoreRepository,
        ICompanyRepository companyRepository,
        IScoreFormula formula,
        ScoringWeights weights,
        IAttentionSourceWeights sourceWeights,
        ISignalSourceDescriptor sourceDescriptor,
        InsiderMaterialityWeights insiderMaterialityWeights,
        MediaAttentionCollapse mediaCollapse,
        ScoringOptions options,
        ILogger<ScoringEngine> logger,
        string? strategyName = null,
        SignalTypeFilter? signalTypes = null,
        ScoringChannelSet? channels = null)
    {
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(signalFileStore);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(scoreRepository);
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(sourceWeights);
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        ArgumentNullException.ThrowIfNull(insiderMaterialityWeights);
        ArgumentNullException.ThrowIfNull(mediaCollapse);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _signalRepository = signalRepository;
        _signalFileStore = signalFileStore;
        _evidenceRepository = evidenceRepository;
        _scoreRepository = scoreRepository;
        _companyRepository = companyRepository;
        _formula = formula;
        _mediaCollapse = mediaCollapse;
        _options = options;
        _logger = logger;
        _strategyName = strategyName;
        _signalTypes = signalTypes ?? SignalTypeFilter.All;
        _channels = channels ?? ScoringChannelSet.Empty;

        var attentionDescriptor = sourceWeights.CanonicalDescriptor();

        // Spec 138: the strategy's declared signal-type set is folded into the SIGNAL-SOURCE descriptor here,
        // inside the engine that also applies the filter behaviourally, so the gate and the hashed identity
        // can never drift apart. ScoringConfigFingerprint and EffectiveScoringConfig are untouched — they
        // store/hash the composed descriptor verbatim, so the scoring-config store's self-verification
        // invariant (the persisted descriptor reproduces the persisted fingerprint) still holds. For the
        // default "all types" filter Describe() returns its input unchanged, so the pinned default
        // fingerprints do not move.
        //
        // Spec 146 extends the SAME chain with the channel budget, for the same reason: a strategy that
        // allocates its score differently is a different scoring, and the fold happens here — inside the
        // engine that also hands the budget to the formula — so the composition and its hashed identity can
        // never drift. An empty channel set returns its input verbatim, so the pinned fingerprints hold.
        var signalSourceDescriptor = _channels.Describe(
            _signalTypes.Describe(sourceDescriptor.CanonicalDescriptor()));

        // Spec 141: captured alongside the identity descriptor, from the SAME descriptor instance, so the
        // recorded collector set and the hashed identity are two projections of one composed graph rather
        // than two independently-resolved answers that could disagree. Spec 146 takes the third projection —
        // the bare names — from that same instance, for the same reason.
        _collectionProvenance = sourceDescriptor.CollectionProvenance();
        _enabledCollectors = sourceDescriptor.EnabledCollectors();

        var insiderMaterialityDescriptor = insiderMaterialityWeights.CanonicalDescriptor();
        var mediaCollapseDescriptor = mediaCollapse.CanonicalDescriptor();

        // Spec 153: the formula's COMPOSED identity — resolved ONCE here so the hashed field, the persisted
        // EffectiveScoringConfig record and the per-snapshot ScoringVersion are literally the same string.
        // A formula that declares no CompositionRevision (v8, v9) composes to its bare version token, so this
        // is byte-identical to the pre-153 `formula.Version` at every one of those three sites.
        _formulaIdentity = FormulaIdentity.Of(formula);

        // Spec 148: the recent-signal window is an output-affecting input (it decides which signals the
        // current AND previous/velocity windows contain), so it is hashed too. It is read from the SAME
        // _options instance ScoreCompanyAsync slices with, so the hashed value and the value actually used
        // cannot disagree — the same reasoning as the "SAME tuple" note below.
        _scoringConfigFingerprint = ScoringConfigFingerprint.Compute(
            EngineVersion, _formulaIdentity, weights, attentionDescriptor, signalSourceDescriptor,
            insiderMaterialityDescriptor, mediaCollapseDescriptor, _options.Window);

        // Build the effective-config projection from the SAME tuple the fingerprint hashes, so
        // EffectiveConfig.Fingerprint always equals the stamp on every snapshot this engine produces.
        _effectiveConfig = new EffectiveScoringConfig(
            Fingerprint: _scoringConfigFingerprint,
            EngineVersion: EngineVersion,
            FormulaVersion: _formulaIdentity,
            Weights: weights,
            AttentionDescriptor: attentionDescriptor,
            SignalSourceDescriptor: signalSourceDescriptor,
            InsiderMaterialityDescriptor: insiderMaterialityDescriptor,
            MediaCollapseDescriptor: mediaCollapseDescriptor,
            Window: _options.Window);
    }

    public EffectiveScoringConfig EffectiveConfig => _effectiveConfig;

    public async Task<CompanyScoreResult> ScoreCompanyAsync(
        Guid companyId, DateTimeOffset windowEndUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Operational scaffolding (tunable, NOT a formula weight): a fixed-length recent-signal window.
        var windowStartUtc = windowEndUtc - _options.Window;

        var allSignals = await _signalRepository.GetByCompanyAsync(companyId, ct).ConfigureAwait(false);

        // Window + known-at + review filter — all three are tunable pipeline scaffolding, NOT formula:
        //   * window rule: ObservedAtUtc in (windowStartUtc, windowEndUtc] — exclusive start, inclusive end;
        //   * known-at rule (spec 136, point-in-time honesty): CreatedAtUtc <= windowEndUtc — score only
        //     what Radar KNEW by asOf, so a historical replay at asOf = T never sees a signal that entered
        //     the store after T. A forward run is provably unaffected (AD-7, one run one instant): this
        //     run's signals carry CreatedAtUtc == asOfUtc == windowEndUtc exactly, satisfied by equality;
        //   * review rule: scoring consumes only Approved (human/deterministically reviewed) signals;
        //   * signal-type rule (spec 138): this strategy scores only the SignalTypes it declared. Applied
        //     LAST, after the window / known-at / review predicates, because it is a pure "does this strategy
        //     consume this type" gate and not a provenance change — an out-of-set signal is not deleted, its
        //     evidence chain is untouched, and every other strategy still scores it. Default is all types.
        var windowedApproved = allSignals
            .Where(s => s.ObservedAtUtc > windowStartUtc && s.ObservedAtUtc <= windowEndUtc)
            .Where(s => s.CreatedAtUtc <= windowEndUtc)
            .Where(s => s.ReviewStatus == SignalReviewStatus.Approved)
            .Where(s => _signalTypes.Includes(s.Type));

        var pairs = new List<ScoringSignal>();

        // Dropped-signal accounting (spec 145, RE-HOMED BY SPEC 197 §3). Spec 145 replaced a per-signal
        // Warning (~9,500 per run PER STRATEGY on the live store) with ONE Warning per company carrying the
        // dropped count and the distinct-evidence-id count. But this engine IS one strategy, so "per company"
        // was really per strategy × company: the live baseline run 0b48b865 still emitted 397 of these lines
        // and buried two genuine RSS transport failures. Silencing it is not an option — an unresolvable
        // evidence chain is a real provenance defect, and spec 145 heals evidence identity FORWARD only, so
        // the accrued residue does not go away. So the counts are RETURNED on ScoreAssemblyDiagnostics and
        // the Warning is emitted ONCE per operation by the caller that can see the whole strategy × company
        // grid (ScoringPass / ReplayRunner, both through ScoreAssemblyDiagnosticsAggregator). Nothing is
        // discarded without being counted; the per-signal detail stays at Debug exactly as spec 145 left it.
        // The HashSet is allocated only if something actually drops, so the healthy path costs nothing.
        var droppedSignalCount = 0;
        HashSet<Guid>? droppedEvidenceIds = null;

        foreach (var signal in windowedApproved)
        {
            // Provenance cannot be established without the source evidence; drop the signal.
            var evidence = await _evidenceRepository.GetByIdAsync(signal.EvidenceId, ct).ConfigureAwait(false);
            if (evidence is null)
            {
                droppedSignalCount++;
                (droppedEvidenceIds ??= []).Add(signal.EvidenceId);
                _logger.LogDebug(
                    "Dropping signal {SignalId} for company {CompanyId}: evidence {EvidenceId} not found.",
                    signal.Id, companyId, signal.EvidenceId);
                continue;
            }

            pairs.Add(new ScoringSignal(signal, evidence));
        }

        // Deterministic ordering so the formula input and resulting links are stable across runs.
        pairs.Sort(static (a, b) =>
        {
            var byObserved = a.Signal.ObservedAtUtc.CompareTo(b.Signal.ObservedAtUtc);
            return byObserved != 0 ? byObserved : a.Signal.Id.CompareTo(b.Signal.Id);
        });

        // Legacy news-inheritance neutralization (spec 194 §1.4), FIRST of the four assembly transforms.
        // Spec 191 persisted directional MediaAttention signals whose direction was taken from the company's
        // latest judgment — a judgment produced from earlier articles that had never read the one being
        // extracted. That producer is deleted (§1.1), but its signals are on disk, the stores are
        // append-only (AD-8) and already-seen evidence is never re-extracted, so they would otherwise keep
        // asserting an ungrounded direction on every future run. This transform admits them with the exact
        // pre-191 Neutral media-attention direction/strength instead. It runs BEFORE both supersedes and the
        // collapse deliberately: everything downstream — including PreCollapseSignals below, which is the
        // POST-supersede/PRE-collapse set and therefore derives from this one — then sees a single,
        // consistent admitted set, and no step can score a direction a later step has already disowned.
        // Read-side only: nothing is written, and the persisted signal/review/file stay byte-identical.
        var legacyNews = LegacyNewsInheritanceNeutralization.Apply(pairs);
        var admitted = legacyNews.Signals;

        // GuidanceChange supersede (spec 113): a filing first collected while the directional earnings read
        // failed has its deterministic Neutral GuidanceChange already persisted; the signal stores are
        // append-only (AD-8), so we supersede at read/assembly time instead of deleting — when the set
        // carries both a directional and a Neutral GuidanceChange over the SAME filing evidence, only the
        // directional one is scored (at most one GuidanceChange per filing; no double-count, ever). The
        // Neutral stays on disk for provenance but gets no contribution/ScoreEvidenceLink. Pure and
        // deterministic (AD-3), and deliberately NOT a fingerprint input (a correctness fix, not a
        // scoring-config change — the stamp must not move).
        // Spec 193 §2: the supersede now RETURNS what it removed (survivors + a per-survivor count), in the
        // same shape the media collapse below returns. The filter itself is unchanged.
        var supersede = GuidanceChangeSupersede.Apply(admitted);
        var superseded = supersede.Signals;

        // Judgment-derived news supersede (spec 194 §1.3): §1.2 materializes ONE grounded MediaAttention
        // signal per validated judgment, anchored to the evidence that judgment actually cited — but that
        // article's ordinary Neutral attention signal is already on disk, so without this step the cited
        // article would contribute TWO attention signals over ONE evidence id and the media channel would
        // grow purely because Radar formed a judgment. The grounded signal REPLACES the ordinary (and any
        // accrued spec-191 v7 directional) signal over the same evidence: one attention event in, one out.
        //
        // PLACEMENT, deliberately:
        //   * AFTER LegacyNewsInheritanceNeutralization (§1.4), whose OUTPUT this reads. A neutralized legacy
        //     signal must arrive here as a LOSING ordinary signal, not as a rival direction — running the two
        //     in the other order would let an ungrounded inherited direction contend with a grounded one.
        //   * BEFORE the media collapse, so the collapse buckets the post-supersede media set: the duplicate
        //     the supersede removed must not first be counted as a collapsed same-event item, and the
        //     surviving grounded signal is what media-collapse-v2 then prefers as its bucket representative.
        //   * Relative to GuidanceChangeSupersede the order is behaviourally IRRELEVANT, and that is a
        //     checked fact rather than an assumption: the guidance supersede only ever removes
        //     SignalType.GuidanceChange signals and passes every other type through untouched, while this one
        //     only ever removes SignalType.MediaAttention signals. The two operate on disjoint types, so
        //     neither can see the other's removals. It is placed second simply to keep the pre-194 steps
        //     adjacent and their diffs small.
        var newsSupersede = NewsJudgmentSignalSupersede.Apply(superseded);
        var newsSuperseded = newsSupersede.Signals;

        // Same-event media collapse (spec 109): many near-simultaneous outlets covering ONE event each emit a
        // MediaAttention signal, inflating the media contribution and the signal count with duplication (not
        // breadth). Collapse those to one representative per event window BEFORE the formula sees them (a
        // signal-count de-noising transform, not a formula change). Provenance is preserved: the representative
        // is a real signal keeping its evidence link, and the collapsed count is surfaced on its contribution
        // reason below. Non-MediaAttention signals and the activity-only previousSignals are untouched.
        var collapse = _mediaCollapse.Collapse(newsSuperseded);
        var scoredSignals = collapse.Signals.ToList();

        // The immediately-preceding window of the same length, now sourced from the ON-DISK signal store
        // (cross-run) rather than the in-memory repo — the in-memory repo starts empty every process and
        // holds only THIS run's signals, so slicing the previous window from it left previous-window
        // activity at 0 on every fresh run. Velocity then collapsed to its no-previous behaviour: exactly
        // 50 only on a quiet current window, and above 50 whenever the current window had any activity.
        // It is carried as activity-only input for velocity measurement:
        //   * window rule: ObservedAtUtc in (previousWindowStartUtc, windowStartUtc] — note the shared
        //     boundary with the current window means a signal exactly at windowStartUtc belongs here (AD-6);
        //   * known-at rule (spec 136): the knowledge threshold is windowEndUtc — the scoring instant —
        //     NOT windowStartUtc. The previous window's OBSERVATION range ends at windowStartUtc, but the
        //     question is "what did Radar know at asOf about the preceding period"; passing windowStartUtc
        //     would under-count previous-window activity and silently shift velocity;
        //   * review rule: same Approved-only filter as the current window.
        // The read returns Approved-only, window-filtered, deterministically-ordered signals (AD-3). No
        // evidence is loaded for it and it never builds contributions / ScoreEvidenceLinks — provenance is
        // only the current-window signals (AD-6). A failed/empty read degrades to an empty previous window
        // (the safe no-previous velocity); the store swallows per-file failures, but OperationCanceledException
        // still propagates (no broad catch here).
        var previousWindowStartUtc = windowStartUtc - _options.Window;

        var previousSignals = await _signalFileStore
            .ReadApprovedInWindowAsync(companyId, previousWindowStartUtc, windowStartUtc, windowEndUtc, ct)
            .ConfigureAwait(false);

        // Spec 194 §1.4, previous window too, and in the SAME relative position (before the supersede): an
        // accrued inherited direction must not be allowed to misdirect the velocity comparison either. The
        // previous window is activity-only and builds no contributions or evidence links (AD-6), so the
        // suppression there is reported through the result and the aggregated log line rather than through a
        // contribution reason.
        var previousLegacyNews = LegacyNewsInheritanceNeutralization.Apply(previousSignals);
        previousSignals = previousLegacyNews.Signals;

        // Spec 113, previous window too (no double-count, ever): the read's cross-run dedupe key includes
        // Direction (spec 85), so a filing whose stale Neutral AND directional GuidanceChange both persist
        // on disk comes back as TWO signals — the same filing must not count twice as activity for
        // velocity. On the healthy spec-78 path only one GuidanceChange per filing ever persists, so this
        // is behaviour-identical there.
        var previousSupersede = GuidanceChangeSupersede.Apply(previousSignals);
        previousSignals = previousSupersede.Signals;

        // Spec 194 §1.3, previous window too, and in the SAME relative position (after §1.4's neutralization
        // and the guidance supersede, before the signal-type filter): if the cited article's ordinary signal
        // AND its grounded companion both counted as previous-window activity, velocity would read the
        // company as accelerating purely because a judgment was formed in the earlier window. The previous
        // window is activity-only and builds no contributions or evidence links (AD-6), so its removals are
        // reported through the aggregated log line rather than through a contribution reason.
        var previousNewsSupersede = NewsJudgmentSignalSupersede.Apply(previousSignals);
        previousSignals = previousNewsSupersede.Signals;

        // Spec 138, previous window too: the velocity comparison must be like-for-like. If a strategy does not
        // consume a SignalType in the CURRENT window, prior activity of that type is not this strategy's prior
        // activity either — otherwise a filtered strategy would measure its own (narrow) current activity
        // against the FULL previous window and read as decelerating for a reason that has nothing to do with
        // the company. Applied AFTER the spec-85 cross-run dedupe/read predicate and the spec-113 supersede,
        // for the same reason as the current window: a pure membership gate, applied last.
        if (!_signalTypes.IsAll)
        {
            previousSignals = [.. previousSignals.Where(s => _signalTypes.Includes(s.Type))];
        }

        // The company's curated following tier (spec 117): the non-price notedness input the v7 Opportunity
        // discount consumes (AD-14 — seed-curated, never price-derived). A missing company degrades to
        // Small (no extra discount) — the fail-safe; never a throw.
        var company = await _companyRepository.GetByIdAsync(companyId, ct).ConfigureAwait(false);
        var followingTier = company?.FollowingTier ?? FollowingTier.Small;

        // Spec 122 (radar-formula-v8): the collapse above discards the distinct-publisher BREADTH of the
        // outlets it dropped alongside the duplicate volume it is meant to remove. Hand the formula the
        // PRE-collapse set as well so it can credit those collapsed-away publishers back into the Attention
        // breadth term (tier-weighted, scaled by ScoringWeights.CollapsedBreadthCredit) while the media COUNT
        // it consumes stays post-collapse. The collapse transform's bucket boundaries are untouched by spec
        // 194 §1.5 — only which real member of a bucket represents it changed (media-collapse-v2).
        // Spec 194, verified rather than assumed: `newsSuperseded` is the output of BOTH news transforms —
        // §1.4 neutralized, then §1.3's judgment-derived supersede — so the pre-collapse breadth set the
        // formula reads here is ALREADY corrected. No accrued inherited direction can re-enter scoring
        // through this back door, and the ordinary signal a grounded judgment replaced cannot be credited
        // back as extra breadth for the very article it was replaced over.
        var input = new ScoringInput(
            companyId, windowStartUtc, windowEndUtc, scoredSignals, previousSignals, followingTier)
        {
            PreCollapseSignals = newsSuperseded,
            // Spec 146: what RAN this process, as provenance the formula may record but must never score on.
            EnabledCollectors = _enabledCollectors,
        };
        var computation = _formula.Compute(input);

        // Record both identities so snapshots remain reproducible and auditable. The formula side is the
        // spec-153 COMPOSED identity, resolved once in the constructor — the same string the fingerprint
        // hashed and the effective-config record persists.
        var scoringVersion = $"{EngineVersion}+{_formulaIdentity}";

        // ZERO CONSUMED SIGNALS (spec 138), stated explicitly because it is a deliberate choice: a strategy
        // whose signal-type filter excludes every one of a company's signals gets EXACTLY what a company with
        // no signals already gets today — a neutral snapshot carrying zero ScoreEvidenceLinks. There is no
        // early return and no special case, so the two are indistinguishable by construction. That is not a
        // phantom: a snapshot with zero evidence links IS how Radar already represents "no evidence for this
        // company in this window", and suppressing it instead would make a filtered strategy's series
        // silently discontinuous (a missing company reads as "not scored", not as "nothing to score"),
        // breaking the spec-140 strategy-vs-price comparison the filter exists to enable.

        var snapshot = new CompanyScoreSnapshot(
            Id: Guid.NewGuid(),
            CompanyId: companyId,
            ScoringVersion: scoringVersion,
            TrajectoryScore: computation.Components.TrajectoryScore,
            OpportunityScore: computation.Components.OpportunityScore,
            AttentionScore: computation.Components.AttentionScore,
            EvidenceConfidenceScore: computation.Components.EvidenceConfidenceScore,
            SignalVelocityScore: computation.Components.SignalVelocityScore,
            Explanation: computation.Explanation,
            ComponentJson: computation.ComponentJson,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            // CreatedAtUtc IS the single run instant (windowEndUtc / asOfUtc), NOT a separate
            // wall-clock read. Using the run instant keeps the snapshot deterministic/reproducible
            // and AD-7-consistent: a fresh GetUtcNow() lands a few ms after asOfUtc, so the snapshot
            // would have CreatedAtUtc > periodEndUtc and be excluded by the report's inclusive
            // upper-bound window — the run could never report the snapshots it just created.
            CreatedAtUtc: windowEndUtc,
            ScoringConfigVersion: _scoringConfigFingerprint,
            // Human-readable strategy identity (spec 137), additive alongside the opaque fingerprint. Null
            // ⇒ primary/legacy composition; it never affects the scores or the fingerprint. Since spec 141 it
            // IS the series key (ScoreSeriesKey), so comparability is decided by this field, not the hash.
            StrategyName: _strategyName,
            // What was collected on this run (spec 141): recorded verbatim, hashed into nothing.
            CollectionProvenance: _collectionProvenance);

        var links = new List<ScoreEvidenceLink>(computation.Contributions.Count);
        foreach (var contribution in computation.Contributions)
        {
            // If this contribution's signal was the representative of a collapsed same-event media bucket,
            // surface the collapsed count on its reason so the report shows ONE line naming the coverage
            // breadth rather than N duplicate lines (provenance for the dropped duplicates; spec 109). The
            // formula itself is untouched — only the persisted ScoreEvidenceLink text is enriched.
            var reason = contribution.ContributionReason;
            if (collapse.CollapsedCounts.TryGetValue(contribution.SignalId, out var collapsedN) && collapsedN > 0)
            {
                reason = $"{reason} (collapsed {collapsedN} same-event media items)";
            }

            // Spec 193 §2: the mirror of the line above for the OTHER removal step. If this contribution's
            // signal superseded a stale GuidanceChange over the same filing evidence, say so on its reason,
            // so the persisted ScoreEvidenceLink records that this contribution REPLACED something rather
            // than leaving the removal untraceable. Media collapse and supersede can never both apply to one
            // signal (MediaAttention and GuidanceChange are different types), so the two blocks cannot
            // interact and their order is immaterial. The formula is untouched — only the link text.
            if (supersede.SupersededCounts.TryGetValue(contribution.SignalId, out var supersededN)
                && supersededN > 0)
            {
                reason = $"{reason} (superseded {supersededN} stale GuidanceChange signal(s) for this evidence)";
            }

            // Spec 194 §1.3: the third member of the same accounting block. If this contribution's signal is
            // a grounded judgment-derived news signal that replaced the ordinary attention event(s) over the
            // same article evidence, say so on its reason — otherwise the snapshot would show one attention
            // contribution where the store holds two signals, with nothing anywhere explaining which one was
            // scored or why. A MediaAttention signal can carry BOTH this note and the collapse note above (it
            // may also represent an event bucket), but never the GuidanceChange one, since those are
            // different types. Appended after the collapse note so that text stays byte-identical.
            if (newsSupersede.SupersededCounts.TryGetValue(contribution.SignalId, out var newsSupersededN)
                && newsSupersededN > 0)
            {
                reason =
                    $"{reason} (superseded {newsSupersededN} ordinary media attention signal(s) for this "
                    + "evidence: the judgment-derived direction replaces the attention event)";
            }

            // Spec 194 §1.4: if this contribution's signal had its persisted direction suppressed, SAY SO on
            // the link. Without this the snapshot would score a MediaAttention signal as Neutral while the
            // record on disk reads Positive/Negative, and nothing anywhere would explain the difference — a
            // score must never silently disagree with its own provenance. Appended last so the two blocks
            // above keep byte-identical text; a neutralized signal is a MediaAttention one, so it CAN also
            // carry the collapse note (it may be a bucket representative) but never the GuidanceChange one.
            if (legacyNews.NeutralizedKinds.TryGetValue(contribution.SignalId, out var neutralizedKind))
            {
                reason =
                    $"{reason} ({LegacyNewsInheritanceNeutralization.ProvenanceNoteFor(neutralizedKind)})";
            }

            links.Add(new ScoreEvidenceLink(
                Id: Guid.NewGuid(),
                ScoreSnapshotId: snapshot.Id,
                SignalId: contribution.SignalId,
                EvidenceId: contribution.EvidenceId,
                ContributionReason: reason,
                ContributionWeight: contribution.ContributionWeight));
        }

        await _scoreRepository.AddSnapshotAsync(snapshot, ct).ConfigureAwait(false);
        foreach (var link in links)
        {
            await _scoreRepository.AddEvidenceLinkAsync(link, ct).ConfigureAwait(false);
        }

        // _signalTypes.ToString() is a precomputed-free join over at most a handful of members ("all types"
        // for the default), so logging the strategy's declared signal set stays cheap while making a filtered
        // strategy's narrower signal count self-explanatory in the run log.
        _logger.LogInformation(
            "Scored company {CompanyId} from {SignalCount} signal(s) using {ScoringVersion} " +
            "(strategy {StrategyName}, signal types {SignalTypes}).",
            companyId, scoredSignals.Count, scoringVersion, _strategyName ?? "(none)", _signalTypes.ToString());

        // Spec 193 §2: one aggregated per-company line, at the SAME Information level as the line above, when
        // the supersede actually removed something. Emitted separately rather than folded into that template
        // so the healthy path's log line stays byte-identical, and aggregated per company rather than per
        // removed signal (the spec-145 precedent). Both windows are reported: the current window's removals
        // are the ones the contributions/evidence links reflect, while the previous window's are
        // activity-only (velocity) and carry no provenance by design (AD-6).
        if (supersede.TotalSuperseded > 0 || previousSupersede.TotalSuperseded > 0)
        {
            _logger.LogInformation(
                "Superseded {SupersededCount} stale GuidanceChange signal(s) for company {CompanyId} in the "
                    + "current window (and {PreviousSupersededCount} in the previous/velocity window): a "
                    + "directional read over the same filing evidence replaced them. They stay on disk for "
                    + "provenance and are named on the surviving signal's contribution reason.",
                supersede.TotalSuperseded, companyId, previousSupersede.TotalSuperseded);
        }

        // Spec 194 §1.3: ONE aggregated per-company line, beside the guidance-supersede line above and at the
        // same Information level, when a grounded judgment-derived news signal actually replaced an ordinary
        // attention event. Emitted separately rather than folded into the line above so the healthy path's
        // log stays byte-identical and the two removals are never pooled into one number — they mean
        // different things. Aggregated per company rather than per removed signal (the spec-145 precedent).
        // Both windows are reported: the current window's removals are the ones the contributions/evidence
        // links reflect, while the previous window's are activity-only (velocity) and carry no provenance by
        // design (AD-6). Information, not Warning: replacing the ordinary event with the grounded one is the
        // intended healthy behaviour, not a fault — it is how the attention count stays flat when judgment
        // is added.
        if (newsSupersede.TotalSuperseded > 0 || previousNewsSupersede.TotalSuperseded > 0)
        {
            _logger.LogInformation(
                "Superseded {NewsSupersededCount} ordinary media attention signal(s) for company {CompanyId} "
                    + "in the current window (and {PreviousNewsSupersededCount} in the previous/velocity "
                    + "window): a judgment-derived news signal grounded in the same article evidence replaced "
                    + "them, so the attention count does not grow because a judgment exists. They stay on "
                    + "disk for provenance and are named on the surviving signal's contribution reason.",
                newsSupersede.TotalSuperseded, companyId, previousNewsSupersede.TotalSuperseded);
        }

        // Spec 194 §1.4's neutralization counts and spec 145's dropped-signal counts, RETURNED rather than
        // logged as Warnings (spec 197 §3). This engine is ONE STRATEGY: a per-company Warning here is a
        // per-strategy × per-company Warning in a multi-strategy run, and on the live baseline the two
        // categories together produced ~460 lines. Ownership of the operator-facing Warning therefore moves
        // to the caller — ScoringPass for a forward/standalone pass, ReplayRunner for a replay invocation —
        // which is the only place that can see the whole grid and label the population honestly (incidences,
        // not globally distinct signals). Nothing is lost: every number that used to be logged is on this
        // record, and the four neutralization axes stay separate so a malformed CURRENT envelope can never
        // disappear inside the expected spec-191 residue.
        var diagnostics = new ScoreAssemblyDiagnostics(
            UnresolvedEvidenceSignalCount: droppedSignalCount,
            // The `?? 0` here is a MEASURED zero, not a defaulted one: the set is allocated lazily on the
            // first drop, so `null` means "nothing dropped" and can only ever coincide with
            // droppedSignalCount == 0. There is no state in which a real distinct-evidence count is
            // rendered as zero.
            UnresolvedEvidenceDistinctEvidenceCount: droppedEvidenceIds?.Count ?? 0,
            CurrentWindowLegacyInheritanceNeutralized: legacyNews.LegacyInheritanceCount,
            CurrentWindowMalformedEnvelopeNeutralized: legacyNews.MalformedEnvelopeCount,
            PreviousWindowLegacyInheritanceNeutralized: previousLegacyNews.LegacyInheritanceCount,
            PreviousWindowMalformedEnvelopeNeutralized: previousLegacyNews.MalformedEnvelopeCount);

        // ONE bounded Debug line per AFFECTED strategy-company evaluation, so the per-cell detail the
        // aggregate necessarily pools is still recoverable by raising this category to Debug. Debug, not
        // Warning: the operator-facing statement about the whole operation is the caller's aggregate, and two
        // levels reporting the same fact would reintroduce exactly the flood this slice removes. An
        // unaffected evaluation logs nothing at all.
        if (diagnostics.HasAny)
        {
            _logger.LogDebug(
                "Score assembly diagnostics for company {CompanyId} (strategy {StrategyName}) at "
                    + "{WindowEndUtc:o}: {DroppedSignalCount} signal(s) dropped for unresolved evidence "
                    + "across {DistinctEvidenceCount} distinct evidence id(s); neutralized {LegacyCount} "
                    + "accrued spec-191 inherited news direction(s) and {MalformedCount} unverifiable "
                    + "judgment-signal envelope(s) in the current window (and {PreviousLegacyCount} / "
                    + "{PreviousMalformedCount} in the previous/velocity window). Reported to the operator "
                    + "as one aggregated Warning per category at the pass boundary.",
                companyId,
                _strategyName ?? "(none)",
                windowEndUtc,
                diagnostics.UnresolvedEvidenceSignalCount,
                diagnostics.UnresolvedEvidenceDistinctEvidenceCount,
                diagnostics.CurrentWindowLegacyInheritanceNeutralized,
                diagnostics.CurrentWindowMalformedEnvelopeNeutralized,
                diagnostics.PreviousWindowLegacyInheritanceNeutralized,
                diagnostics.PreviousWindowMalformedEnvelopeNeutralized);
        }

        return new CompanyScoreResult(snapshot, links, diagnostics);
    }
}
