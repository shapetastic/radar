using System.Globalization;

namespace Radar.Application.Scoring;

/// <summary>
/// SPEC 194 §2 — the canonical, hashed identity of Radar's NEWS READ, folded into
/// <see cref="SignalSourceDescriptor.CanonicalDescriptor"/> and therefore into every
/// <c>ScoringConfigVersion</c>.
/// <para>
/// <b>The hole this closes.</b> The AI filing read has carried an <c>ai=</c> segment since spec 106/119
/// precisely because its per-signal magnitudes and its READING MODEL change signal direction. The news read
/// had no analogue: two runs differing only in <c>Radar:NewsResearch:Judgment:Enabled</c>, in the judge
/// MODEL, in the prospectively designated presentation cohort, or in the news-trajectory strength constants
/// stamped the IDENTICAL fingerprint. So <c>StrategyIdentityGuard</c> could not see the difference and
/// <c>ScoreSeriesKey</c> pooled both cohorts into one series — two materially different scorings drawn as
/// one continuous line. Spec 194 §1.2 made that live by giving a validated judgment its own durable
/// directional signal; this type makes the change visible.
/// </para>
/// <para>
/// <b>It holds STRINGS AND NUMBERS, and that is structural, not stylistic</b> (the spec-147
/// <c>EnabledCollectorVocabulary</c> precedent). It cannot call a judge, cannot construct a provider client
/// and references nothing that can — which is what lets a spec-144 <c>score</c> pass, and a spec-139 replay,
/// compose the SAME identity a <c>full</c> run composes from the same configuration, without registering the
/// judgment step at all. It is also what keeps the spec-177/179 architecture guards intact — and those
/// guards enforce a TYPE-GRAPH constraint, which is narrower than "Scoring never mentions News": they walk
/// each <c>Radar.Application.Scoring</c> type's base type, interfaces, ALL fields (private and static
/// included), property/method/constructor signatures and every generic argument, and fail if any of them
/// lands in <c>Radar.Application.News</c> or <c>Radar.Application.NewsRisk</c>. They are structurally blind
/// to a <c>const</c> or method-body reference, and <see cref="LegacyNewsInheritanceNeutralization"/> relies
/// on exactly that: it reads the <c>const</c> <c>NewsTrajectorySignalRules.BaseStrength</c> inside a method
/// body so the neutral strength it substitutes cannot drift from the one the extractor emits. That is
/// deliberate and does not violate the guard, because no field, signature or generic argument carries the
/// type. What this type must never do is the thing the guard DOES see — carry a News/NewsRisk type in its
/// shape — so the trajectory→direction mapping and the strength constants arrive here already rendered,
/// composed by <c>NewsJudgmentScoringIdentityFactory</c> on the far side of that boundary. Do not "simplify"
/// this by taking the trajectory enum or the rules type as a parameter — that inverts the dependency those
/// guards exist to enforce.
/// </para>
/// <para>
/// <b>The two rule versions are recorded even when judgment is DISABLED, and that is deliberate.</b>
/// <see cref="LegacyNewsInheritanceNeutralization"/> (§1.4) and <see cref="NewsJudgmentSignalSupersede"/>
/// (§1.3) are applied by <c>ScoringEngine</c> unconditionally — they act on signals ALREADY on disk, so
/// their rules change what a judgment-disabled run scores. The materializer identity, the presentation
/// cohort and the trajectory magnitudes are recorded only when judgment is enabled, because only an enabled
/// judgment can mint a signal that carries them; recording them anyway would re-stamp operators who never
/// enable the step, for constants they cannot reach.
/// </para>
/// <para>
/// <b>The segment is ALWAYS present</b>, including the disabled form. A disabled state that rendered nothing
/// would be byte-identical to a pre-194 composition, so "judgment off" and "a Radar that predates the
/// judgment read entirely" would share a stamp — the exact ambiguity spec 147 removed from
/// <c>collectors=;</c>. It is not free: because the segment is unconditional, every pin moves once, which is
/// this slice's declared and intended cost.
/// </para>
/// <para>
/// <b>What is deliberately NOT here:</b> reader API keys, call budgets, retry caps and every other cost
/// control (<c>MaxCompaniesPerRun</c>, <c>MaxFamiliesPerJudgment</c>, <c>MaxJudgmentAttempts</c>, the typing
/// budgets). They change how much Radar spends discovering a judgment, never what a judgment MEANS, and
/// folding them in would re-stamp a series for a throttle change — the spec-141 rule that a fingerprint
/// records identity, not operational posture.
/// </para>
/// </summary>
public sealed class NewsJudgmentScoringIdentity
{
    /// <summary>The token an enabled judgment renders as the segment's FIRST field.</summary>
    private const string EnabledToken = "enabled";

    /// <summary>The token a disabled (or entirely unconfigured) judgment renders instead.</summary>
    private const string DisabledToken = "disabled";

    /// <summary>
    /// The identity of a composition where the stage-2 judgment is off — or was never configured at all, a
    /// library composition that never heard of it scoring exactly as a disabled one does.
    /// </summary>
    public static NewsJudgmentScoringIdentity Disabled { get; } = new();

    private readonly string _segment;

    private NewsJudgmentScoringIdentity()
    {
        _segment = Compose(DisabledToken, []);
    }

    private NewsJudgmentScoringIdentity(
        string presentationCohortKey,
        string materializerVersion,
        IReadOnlyList<string> directionMapping,
        int baseStrength,
        int maxFindingContribution,
        int completeTypingBonus,
        int novelty,
        decimal confidence)
    {
        // Ordered EXACTLY as listed: cohort, materializer identity, direction mapping, then the strength and
        // magnitude constants. A fixed field order is what makes the encoding injective (AD-3); the mapping
        // is rendered by the factory in enum order for the same reason. Numbers are formatted
        // culture-invariantly so a comma-decimal locale cannot corrupt the stamp.
        _segment = Compose(
            EnabledToken,
            [
                DescriptorEscaping.EscapeNested(presentationCohortKey),
                DescriptorEscaping.EscapeNested(materializerVersion),
                string.Join('|', directionMapping.Select(DescriptorEscaping.EscapeNested)),
                baseStrength.ToString(CultureInfo.InvariantCulture),
                maxFindingContribution.ToString(CultureInfo.InvariantCulture),
                completeTypingBonus.ToString(CultureInfo.InvariantCulture),
                novelty.ToString(CultureInfo.InvariantCulture),
                confidence.ToString(CultureInfo.InvariantCulture),
            ]);
    }

    /// <summary>
    /// The identity of a composition with the stage-2 judgment ENABLED, carrying the resolved presentation
    /// cohort key (provider + exact model id + judge prompt/schema versions + the whole stage-1 extractor
    /// cohort identity + the fact-family builder identity) and the magnitudes a materialized judgment signal
    /// would carry.
    /// <para>
    /// Every parameter is a primitive on purpose — see the type remarks. Production has exactly ONE caller,
    /// <c>NewsJudgmentScoringIdentityFactory.ForPresentationCohort</c>, which supplies the shipped constants;
    /// the wide signature exists so a test can perturb ONE constant without any of them becoming
    /// configurable.
    /// </para>
    /// </summary>
    /// <param name="presentationCohortKey">The resolved stage-2 presentation cohort key. Must be non-blank.</param>
    /// <param name="materializerVersion">The judgment-derived signal's versioned identity (<c>news-judgment-signal-v1</c>).</param>
    /// <param name="directionMapping">The trajectory→direction mapping, one rendered token per trajectory, in a fixed order.</param>
    /// <param name="baseStrength">The base strength a judgment-derived news signal carries.</param>
    /// <param name="maxFindingContribution">The cap on the judge-finding strength contribution.</param>
    /// <param name="completeTypingBonus">The strength bonus for a COMPLETE stage-1 typing.</param>
    /// <param name="novelty">The declared novelty.</param>
    /// <param name="confidence">The declared confidence.</param>
    public static NewsJudgmentScoringIdentity ForPresentationCohort(
        string presentationCohortKey,
        string materializerVersion,
        IReadOnlyList<string> directionMapping,
        int baseStrength,
        int maxFindingContribution,
        int completeTypingBonus,
        int novelty,
        decimal confidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationCohortKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(materializerVersion);
        ArgumentNullException.ThrowIfNull(directionMapping);

        return new NewsJudgmentScoringIdentity(
            presentationCohortKey,
            materializerVersion,
            directionMapping,
            baseStrength,
            maxFindingContribution,
            completeTypingBonus,
            novelty,
            confidence);
    }

    /// <summary>
    /// The canonical <c>news=…;</c> segment, appended to the signal-source identity descriptor AFTER the
    /// existing <c>rules=</c> and optional <c>ai=</c> segments — so the pre-194 prefix stays byte-stable and
    /// the reason a pin moved is unambiguous.
    /// </summary>
    public string Segment => _segment;

    /// <summary>
    /// Composes <c>news={state}[:{field}]*;</c>. The two rule versions come FIRST because they apply in both
    /// states (see the type remarks); the enabled-only fields follow.
    /// <para>
    /// Values are spliced through <see cref="DescriptorEscaping.EscapeNested"/> rather than
    /// <see cref="DescriptorEscaping.Escape"/> because this segment has INTERNAL structure (<c>:</c> between
    /// fields, <c>|</c> inside the mapping list) and the presentation cohort key legitimately contains
    /// <c>:</c>, <c>|</c> and <c>=</c> (<c>openai:model|prompt|schema|stage1=…|families=…</c>). Escaping only
    /// the outer delimiters would let two different cohort keys collide with this segment's own field
    /// structure; widening the shared <see cref="DescriptorEscaping.Escape"/> instead would silently move the
    /// AI-ON pin, whose descriptor legitimately contains <c>:</c> — the spec-146 reasoning, applied verbatim.
    /// </para>
    /// </summary>
    private static string Compose(string state, IReadOnlyList<string> enabledFields)
    {
        var fields = new List<string>(3 + enabledFields.Count)
        {
            state,
            DescriptorEscaping.EscapeNested(LegacyNewsInheritanceNeutralization.Version),
            DescriptorEscaping.EscapeNested(NewsJudgmentSignalSupersede.Version),
        };
        fields.AddRange(enabledFields);

        return $"news={string.Join(':', fields)};";
    }
}
