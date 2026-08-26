using System.Globalization;

using Radar.Application.Identity;
using Radar.Application.News;

namespace Radar.Application.NewsTyping;

/// <summary>
/// The versioned, deterministic fact-family builder (spec 181 §4, spec 186 §4) — NEVER a model call (AD-3).
/// Groups validated facts asserting the SAME claim about the same company so syndication reaches stage 2 as
/// one family with size metadata, instead of N copies of one claim (the 40-outlets problem must not be
/// reborn at the judgment seam).
///
/// <para>
/// <b><c>fact-family-v2</c> runs in TWO STAGES, because durable identity and the window representative are
/// DIFFERENT jobs.</b> Stage 1 SEGMENTS every qualifying validated fact in the store — the whole accrued
/// history, not the checkpoint window — into episodes. Stage 2 PROJECTS each episode that has at least one
/// IN-WINDOW member onto the checkpoint window: the projected record carries the episode's DURABLE
/// <see cref="FactFamilyRecord.FamilyId"/> from stage 1, while its representative, members, counts,
/// publishers, event types, statement, canonical claim key and earliest instant are derived from the
/// IN-WINDOW members ALONE. What a checkpoint MEANS — window families, window metadata — is unchanged from
/// v1; only the id survives from full history. Collapsing the two stages back into one would make the
/// first-ever member double as the representative, and <c>NewsJudgmentInputBuilder</c> drops any family
/// whose representative is absent from the current window's fact index — so once an anchor fact aged out, a
/// family carrying FRESH news would silently vanish from judgment. Do not collapse it.
/// </para>
///
/// Rules:
/// <list type="bullet">
/// <item><b>Membership (stage 1) is BYTE-COMPATIBLE with v1</b>: company + capture mode (capture-mode
/// cohorts never pool, so a family never mixes prospective and legacy epistemics) + overlapping event types
/// + normalized-statement similarity + temporal proximity — all evaluated against the family's
/// REPRESENTATIVE (its first-ever member), greedy first fit over facts ordered by (instant, fact id). NOT
/// exact-canonical-key grouping and NOT transitive/proximity chaining. Spec 186 §4 changes identity and
/// projection only.</item>
/// <item><b>Contradiction never merges</b>: if both statements contain numbers and the number multisets
/// differ, or exactly one carries a negation token, the facts never share a family however similar their
/// text — a family is one claim, and merging a contradiction would erase exactly what stage 2 needs.</item>
/// <item><b>Representative</b>: earliest <c>FirstObservedAtUtc</c>, then lowest <c>FactId</c> — applied to
/// the stage-1 episode for the identity anchor, and applied AGAIN to the stage-2 projection for the
/// snapshot's <see cref="FactFamilyRecord.RepresentativeFactId"/>.</item>
/// <item><b>Family ids carry a TEMPORAL ANCHOR and are stable under window expiry</b>: derived from builder
/// version + company + capture mode + the FIRST-EVER member's <c>FirstObservedAtUtc</c> UTC date + that
/// member's sorted event types + that member's normalized statement (the anchor canonical-claim key) —
/// never from the member list, and never from the window. Two temporally separate episodes asserting a
/// byte-identical recurring claim (the quarterly dividend/buyback shape) therefore get DISTINCT ids, and an
/// episode whose earliest member ages out of the checkpoint window keeps the SAME id.</item>
/// <item><b>Disjoint event types are different families</b>: the anchor folds the first-ever member's
/// sorted event types, so two same-statement episodes with disjoint types can never collide.</item>
/// <item><b>The full definition is the identity</b>: builder version, normalization version, similarity
/// metric AND threshold, the temporal window, the segmentation scope, the anchor rule and the projection
/// rule compose <see cref="IdentityString"/> (pinned by test) — changing any of them is
/// <c>fact-family-v3</c>, a new cohort dimension, never an edit.</item>
/// </list>
///
/// <para>
/// <b>Documented, accepted caveat — the ONLY id-shift case left.</b> A late-arriving member that is
/// temporally EARLIER than every member of its episode ever observed shifts the anchor, so that episode's
/// family id moves at the next checkpoint. Rare (facts arrive roughly in time order, and the syndication
/// tail lands inside the window and changes nothing) and honest: already-written snapshots are immutable
/// values on disk and are never rewritten (AD-8), so the shift is visible as a snapshot-to-snapshot
/// difference rather than hidden.
/// </para>
///
/// The existing media same-event collapse is deliberately NOT reused: it is a time-window bucket that can
/// merge unrelated same-day stories; family membership means <i>same claim</i>, not <i>same day</i>.
/// </summary>
public static class FactFamilyBuilder
{
    public const string BuilderVersion = "fact-family-v2";

    /// <summary>
    /// The versioned statement normalization, owned by the shared <see cref="NewsTextNormalization"/>
    /// (extracted by spec 191 §1 so the join key and the claim key cannot drift). Re-exported here because
    /// it is a documented part of this builder's identity; the VALUE is the shared one, never a second
    /// literal.
    /// </summary>
    public const string NormalizationVersion = NewsTextNormalization.Version;

    public const string SimilarityMetric = "token-set-jaccard";
    public const double SimilarityThreshold = 0.6;
    public const int TemporalWindowDays = 7;

    /// <summary>Stage 1 segments the FULL accrued fact history, never the checkpoint window (spec 186 §4).</summary>
    public const string SegmentationScope = "full-history";

    /// <summary>The durable identity anchor: the first-ever member's UTC date + that member's sorted event types.</summary>
    public const string IdentityAnchor = "first-member-utc-date+event-types";

    /// <summary>Stage 2 projects each episode onto the checkpoint window; only the id survives from history.</summary>
    public const string ProjectionRule = "window-members";

    /// <summary>The complete builder identity — every parameter that shapes membership, identity and projection, pinned by test.</summary>
    public static readonly string IdentityString = string.Create(
        CultureInfo.InvariantCulture,
        $"{BuilderVersion}|normalization={NormalizationVersion}|similarity={SimilarityMetric}"
            + $"|threshold={SimilarityThreshold}|temporalWindowDays={TemporalWindowDays}"
            + $"|segmentation={SegmentationScope}|anchor={IdentityAnchor}|projection={ProjectionRule}");

    /// <summary>Negation tokens preserved by normalization and consulted by the contradiction rule.</summary>
    private static readonly HashSet<string> NegationTokens =
        new(StringComparer.Ordinal) { "not", "no", "denies", "denied" };

    /// <summary>
    /// Builds the deterministic family set over the supplied facts, treating EVERY supplied fact as
    /// in-window (stage 2 projects the whole segmentation). Byte-deterministic: a rerun over identical
    /// facts produces an identical list; families are ordered by (earliest projected member instant, family
    /// id) and members by (instant, fact id).
    /// </summary>
    public static IReadOnlyList<FactFamilyRecord> Build(IReadOnlyList<FactFamilyInputFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return BuildCore(facts, static _ => true);
    }

    /// <summary>
    /// The two-stage checkpoint build (spec 186 §4). <paramref name="allFacts"/> is EVERY qualifying
    /// validated fact in the store — stage 1 segments all of them so each episode's identity anchor is
    /// durable under window expiry — and the window bounds select which episodes are PROJECTED into the
    /// checkpoint (an episode enters iff it has ≥ 1 member with
    /// <c>windowStartUtc &lt; FirstObservedAtUtc ≤ windowEndUtc</c>, matching the generator's own window
    /// predicate exactly).
    /// </summary>
    public static IReadOnlyList<FactFamilyRecord> Build(
        IReadOnlyList<FactFamilyInputFact> allFacts,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc)
    {
        ArgumentNullException.ThrowIfNull(allFacts);
        return BuildCore(
            allFacts,
            f => f.FirstObservedAtUtc > windowStartUtc && f.FirstObservedAtUtc <= windowEndUtc);
    }

    /// <summary>
    /// The versioned statement normalization (<see cref="NormalizationVersion"/>): lowercase invariant,
    /// every non-letter/digit character becomes a space, whitespace collapsed. Negation tokens and numbers
    /// survive BY CONSTRUCTION — they are letters/digits — because stripping them would erase exactly the
    /// distinctions the contradiction rule protects.
    /// <para>
    /// Spec 191 §1 EXTRACTED the rule into <see cref="NewsTextNormalization"/> so the news
    /// observation ↔ evidence join uses the same primitive rather than a second copy. This member survives
    /// as the fact layer's name for it and is byte-identical in behaviour (asserted by test) — the
    /// builder's <see cref="IdentityString"/> must not move.
    /// </para>
    /// </summary>
    public static string NormalizeStatement(string statement) =>
        NewsTextNormalization.Normalize(statement);

    /// <summary>
    /// The durable family identity (spec 186 §4): builder version + company + capture mode + the
    /// FIRST-EVER member's UTC date + that member's sorted event types + that member's canonical-claim key.
    /// Never the member list, never the checkpoint window. Canonical composition:
    /// <c>radar:fact-family:{builderVersion}:{companyId:D}:{captureMode}:{anchorDate:yyyy-MM-dd}:{eventTypes}:{canonicalClaimKey}</c>
    /// where <c>eventTypes</c> is the distinct member names ordered by enum value and joined with <c>,</c>.
    /// </summary>
    public static Guid FamilyIdFor(
        Guid companyId,
        NewsObservationCaptureMode captureMode,
        DateOnly anchorDateUtc,
        IReadOnlyList<NewsEventType> anchorEventTypes,
        string anchorCanonicalClaimKey)
    {
        ArgumentNullException.ThrowIfNull(anchorEventTypes);
        ArgumentNullException.ThrowIfNull(anchorCanonicalClaimKey);

        var types = string.Join(',', anchorEventTypes.Distinct().OrderBy(t => (int)t));
        var date = anchorDateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return DeterministicGuid.FromCanonicalString(
            $"radar:fact-family:{BuilderVersion}:{companyId:D}:{captureMode}:{date}:{types}"
                + $":{anchorCanonicalClaimKey}");
    }

    private static IReadOnlyList<FactFamilyRecord> BuildCore(
        IReadOnlyList<FactFamilyInputFact> allFacts, Func<FactFamilyInputFact, bool> inWindow)
    {
        // ---- Stage 1: SEGMENTATION over the full accrued history (v1's membership algorithm, verbatim).
        var ordered = allFacts
            .OrderBy(f => f.FirstObservedAtUtc)
            .ThenBy(f => f.FactId)
            .ToList();

        // The greedy first-fit scan is v1's, unchanged — but stage 1 reads the FULL accrued history, so the
        // candidate set is BUCKETED by (company, capture mode) and pruned once an episode's representative
        // falls more than the temporal window behind the fact being placed. Both are exact: CanJoin already
        // rejects a different company/capture mode outright, facts are processed in ascending instant order
        // (so an expired episode can never match a later fact), and each bucket preserves episode CREATION
        // order — so first fit picks the identical episode it would have picked scanning the whole list.
        var episodes = new List<MutableFamily>();
        var active = new Dictionary<(Guid CompanyId, NewsObservationCaptureMode CaptureMode),
            List<MutableFamily>>();
        var window = TimeSpan.FromDays(TemporalWindowDays);
        foreach (var fact in ordered)
        {
            var tokens = NormalizedTokens(fact.Statement);
            var bucketKey = (fact.CompanyId, fact.CaptureMode);
            if (!active.TryGetValue(bucketKey, out var bucket))
            {
                bucket = [];
                active[bucketKey] = bucket;
            }

            bucket.RemoveAll(e => fact.FirstObservedAtUtc - e.Representative.FirstObservedAtUtc > window);

            var joined = false;
            foreach (var episode in bucket)
            {
                if (CanJoin(episode, fact, tokens))
                {
                    episode.Members.Add(fact);
                    joined = true;
                    break;
                }
            }

            if (!joined)
            {
                var episode = new MutableFamily(fact, tokens);
                episodes.Add(episode);
                bucket.Add(episode);
            }
        }

        // ---- Stage 2: PROJECTION onto the checkpoint window (durable id, in-window metadata).
        var projected = new List<FactFamilyRecord>(episodes.Count);
        foreach (var episode in episodes)
        {
            var members = episode.Members
                .Where(inWindow)
                .OrderBy(m => m.FirstObservedAtUtc)
                .ThenBy(m => m.FactId)
                .ToList();
            if (members.Count == 0)
            {
                continue;
            }

            projected.Add(ToRecord(episode.Representative, members));
        }

        return projected
            .OrderBy(f => f.EarliestObservedAtUtc)
            .ThenBy(f => f.FamilyId)
            .ToList();
    }

    private static bool CanJoin(MutableFamily family, FactFamilyInputFact fact, HashSet<string> tokens)
    {
        var representative = family.Representative;
        if (fact.CompanyId != representative.CompanyId || fact.CaptureMode != representative.CaptureMode)
        {
            return false;
        }

        if (!fact.EventTypes.Intersect(representative.EventTypes).Any())
        {
            return false;
        }

        var delta = fact.FirstObservedAtUtc - representative.FirstObservedAtUtc;
        if (delta < TimeSpan.Zero)
        {
            delta = -delta;
        }

        if (delta > TimeSpan.FromDays(TemporalWindowDays))
        {
            return false;
        }

        if (Contradicts(tokens, family.RepresentativeTokens))
        {
            return false;
        }

        return Jaccard(tokens, family.RepresentativeTokens) >= SimilarityThreshold;
    }

    /// <summary>
    /// The conservative contradiction rule: different number multisets (when both sides carry numbers) or a
    /// negation token on exactly one side means these are DIFFERENT claims, never one family.
    /// </summary>
    private static bool Contradicts(HashSet<string> a, HashSet<string> b)
    {
        var numbersA = a.Where(t => t.All(char.IsAsciiDigit)).Order(StringComparer.Ordinal).ToList();
        var numbersB = b.Where(t => t.All(char.IsAsciiDigit)).Order(StringComparer.Ordinal).ToList();
        if (numbersA.Count > 0 && numbersB.Count > 0 && !numbersA.SequenceEqual(numbersB))
        {
            return true;
        }

        var negatedA = a.Overlaps(NegationTokens);
        var negatedB = b.Overlaps(NegationTokens);
        return negatedA != negatedB;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static HashSet<string> NormalizedTokens(string statement) =>
        NewsTextNormalization.Tokens(statement);

    /// <summary>
    /// Projects one episode onto its in-window members: the id comes from the episode's durable
    /// <paramref name="anchor"/> (its first-ever member), everything else from
    /// <paramref name="windowMembers"/> (already ordered by instant then fact id, non-empty).
    /// </summary>
    private static FactFamilyRecord ToRecord(
        FactFamilyInputFact anchor, IReadOnlyList<FactFamilyInputFact> windowMembers)
    {
        var representative = windowMembers[0];
        return new FactFamilyRecord(
            FamilyId: FamilyIdFor(
                anchor.CompanyId,
                anchor.CaptureMode,
                DateOnly.FromDateTime(anchor.FirstObservedAtUtc.UtcDateTime),
                anchor.EventTypes,
                NormalizeStatement(anchor.Statement)),
            CompanyId: representative.CompanyId,
            CaptureMode: representative.CaptureMode,
            RepresentativeFactId: representative.FactId,
            RepresentativeStatement: representative.Statement,
            CanonicalClaimKey: NormalizeStatement(representative.Statement),
            EventTypes: windowMembers
                .SelectMany(m => m.EventTypes)
                .Distinct()
                .OrderBy(t => (int)t)
                .ToList(),
            MemberFactIds: windowMembers.Select(m => m.FactId).ToList(),
            MemberCount: windowMembers.Count,
            DistinctPublisherCount: windowMembers
                .Select(m => m.Publisher)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            EarliestObservedAtUtc: representative.FirstObservedAtUtc);
    }

    private sealed class MutableFamily(FactFamilyInputFact representative, HashSet<string> tokens)
    {
        /// <summary>
        /// The episode's FIRST-EVER member (input is pre-sorted by instant then id) — the membership
        /// yardstick AND the durable identity anchor. Immutable under window expiry by construction: facts
        /// are append-only, and stage 1 sees the whole history.
        /// </summary>
        public FactFamilyInputFact Representative { get; } = representative;

        public HashSet<string> RepresentativeTokens { get; } = tokens;

        public List<FactFamilyInputFact> Members { get; } = [representative];
    }
}
