using System.Globalization;

namespace Radar.Application.Scoring;

/// <summary>
/// SPEC 198 §3 — the canonical, hashed identity of the NEWS FEED QUERY's recency window, folded into
/// <see cref="SignalSourceDescriptor.CanonicalDescriptor"/> and therefore into every
/// <c>ScoringConfigVersion</c>.
/// <para>
/// <b>The hole this closes.</b> The feed query decides WHICH evidence exists at all: a <c>when:{n}d</c> term
/// on the Google News RSS search bounds the response to the last <c>n</c> days, so it changes the
/// <c>NewsArticle</c> evidence Radar admits and therefore <c>AttentionReach</c>, <c>OpportunityScore</c> and
/// every rank. It was hashed into NOTHING, so narrowing or widening the window would have moved every score
/// while two materially different scorings kept sharing one stamp — <c>StrategyIdentityGuard</c> blind to
/// the change and <c>ScoreSeriesKey</c> pooling both cohorts into one series. This is the same comparability
/// hole spec 194 §2 closed for the judgment read.
/// </para>
/// <para>
/// <b>It holds a NUMBER, and that is structural, not stylistic</b> (the spec-147
/// <c>EnabledCollectorVocabulary</c> and spec-194 <see cref="NewsJudgmentScoringIdentity"/> precedent). It
/// references no <c>Radar.Application.News</c>, <c>Radar.Application.NewsRisk</c> or Infrastructure type,
/// cannot construct an <c>HttpClient</c> and cannot issue a request — which is what keeps the spec-177/179
/// architecture guards intact and what lets a spec-144 <c>score</c> pass and a spec-139 replay compose the
/// SAME identity a <c>full</c> run composes from the same configuration, without registering the news
/// collector at all.
/// </para>
/// <para>
/// <b>Why a window of 0 renders NOTHING, unlike spec 194's unconditional segment.</b> Spec 198 §3 requires
/// that the disabled configuration reproduce the post-197 pins EXACTLY — that is the additivity proof the
/// spec asks for, and it is what makes the shipped default's move unambiguously attributable to the window
/// and to nothing else. Spec 194 made the opposite choice for the judgment read because "judgment off" and
/// "a Radar that predates the judgment read" are different facts that would otherwise share a stamp; here
/// the fact is a plain magnitude with a code default, and rendering <c>newsquery=0d;</c> would have
/// re-stamped every composition for a filter that does nothing. A composition that wants no filter says
/// <c>0</c> and is byte-identical to a pre-198 Radar, which is exactly what it collects.
/// </para>
/// <para>
/// <b>What is deliberately NOT here:</b> the retention limit (<c>Radar:News:MaxRecordsPerCompany</c>), the
/// absolute parse ceiling, the inter-request pacing and the English-only locale flag. Spec 198 §5 holds all
/// of them fixed, and pacing/limits are the collector's operational posture rather than the identity of what
/// it asks for — the spec-141 rule that a fingerprint records identity, not throttle. The first-collection
/// exemption (§2) is likewise absent: it is a per-company PERSISTED-STATE fact that varies within one run,
/// not a configured magnitude, and it is recorded on the coverage diagnostic instead.
/// </para>
/// </summary>
public sealed class NewsQueryScoringIdentity
{
    /// <summary>
    /// THE one definition of the shipped recency window in days (spec 198 §1). Both
    /// <c>NewsCollectorOptions.RecencyWindowDays</c> (Infrastructure) and
    /// <c>NewsWorkerOptions.RecencyWindowDays</c> (Worker) default off this const — both projects reference
    /// Application — so the value a live run SENDS and the value the fingerprint HASHES cannot drift.
    /// <para>
    /// Seven, not one or two: the baseline runs daily, so a 1–2 day window has no margin and a single missed
    /// night would open a permanent gap (a skipped article never reappears in a narrower window). Seven
    /// tolerates several consecutive failures while still cutting a 100-item response to a handful, and the
    /// redundancy is free because cross-run dedupe already discards it.
    /// </para>
    /// </summary>
    public const int DefaultRecencyWindowDays = 7;

    /// <summary>
    /// The <c>when:</c> token's rendered unit inside the segment — days, matching the query term the reader
    /// appends. Declared here so the hashed rendering and the query rendering describe the same unit.
    /// </summary>
    private const string DayUnitSuffix = "d";

    private readonly int _windowDays;
    private readonly string _segment;

    private NewsQueryScoringIdentity(int windowDays)
    {
        _windowDays = windowDays;
        _segment = windowDays <= 0
            ? string.Empty
            : $"newsquery={DescriptorEscaping.Escape(windowDays.ToString(CultureInfo.InvariantCulture) + DayUnitSuffix)};";
    }

    /// <summary>The identity of a composition running the SHIPPED default window (<see cref="DefaultRecencyWindowDays"/>).</summary>
    public static NewsQueryScoringIdentity Default { get; } = ForWindowDays(DefaultRecencyWindowDays);

    /// <summary>
    /// The identity of a composition with the recency filter DISABLED. Its <see cref="Segment"/> is empty,
    /// so the composed descriptor is byte-identical to a pre-198 one — the spec-198 §3 additivity proof.
    /// </summary>
    public static NewsQueryScoringIdentity None { get; } = ForWindowDays(0);

    /// <summary>
    /// The identity for an explicit window in days. Zero (or any non-positive value reaching here through
    /// <see cref="None"/>) means the filter is disabled; a NEGATIVE value is rejected, because it is
    /// configuration nonsense rather than a disabled filter and silently reading it as "off" is how a typo
    /// becomes an invisible collection change.
    /// </summary>
    public static NewsQueryScoringIdentity ForWindowDays(int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        return new NewsQueryScoringIdentity(days);
    }

    /// <summary>The effective window in days; <c>0</c> means the recency filter is disabled.</summary>
    public int WindowDays => _windowDays;

    /// <summary>
    /// The canonical <c>newsquery={n}d;</c> segment, appended to the signal-source identity descriptor LAST
    /// — after <c>rules=</c>, the optional <c>ai=</c> and the spec-194 <c>news=</c> segment — so the whole
    /// post-197 prefix stays byte-stable and a pin move is unambiguously attributable. EMPTY when the filter
    /// is disabled; see the type remarks for why that differs from the judgment read's unconditional
    /// segment.
    /// </summary>
    public string Segment => _segment;
}
