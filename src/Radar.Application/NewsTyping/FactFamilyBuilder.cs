using System.Globalization;

using Radar.Application.Identity;
using Radar.Application.News;

namespace Radar.Application.NewsTyping;

/// <summary>
/// The versioned, deterministic fact-family builder (spec 181 §4) — NEVER a model call (AD-3). Groups
/// validated facts asserting the SAME claim about the same company so syndication reaches stage 2 as one
/// family with size metadata, instead of N copies of one claim (the 40-outlets problem must not be reborn at
/// the judgment seam). Rules:
/// <list type="bullet">
/// <item><b>Key</b>: company + capture mode (capture-mode cohorts never pool, so a family never mixes
/// prospective and legacy epistemics) + overlapping event types + normalized-statement similarity + temporal
/// proximity — all evaluated against the family's REPRESENTATIVE (earliest member), so membership is a pure
/// function of the input.</item>
/// <item><b>Contradiction never merges</b>: if both statements contain numbers and the number multisets
/// differ, or exactly one carries a negation token, the facts never share a family however similar their
/// text — a family is one claim, and merging a contradiction would erase exactly what stage 2 needs.</item>
/// <item><b>Representative</b>: earliest <c>FirstObservedAtUtc</c>, then lowest <c>FactId</c>.</item>
/// <item><b>Family ids are stable under changing membership</b>: derived from builder version + company +
/// capture mode + the representative's normalized statement (the canonical-claim key) — never from the
/// member list.</item>
/// <item><b>The full definition is the identity</b>: builder version, normalization version, similarity
/// metric AND threshold, and the temporal window compose <see cref="IdentityString"/> (pinned by test) —
/// changing any of them is <c>fact-family-v2</c>, a new cohort dimension, never an edit.</item>
/// </list>
/// The existing media same-event collapse is deliberately NOT reused: it is a time-window bucket that can
/// merge unrelated same-day stories; family membership means <i>same claim</i>, not <i>same day</i>.
/// </summary>
public static class FactFamilyBuilder
{
    public const string BuilderVersion = "fact-family-v1";
    public const string NormalizationVersion = "statement-normalization-v1";
    public const string SimilarityMetric = "token-set-jaccard";
    public const double SimilarityThreshold = 0.6;
    public const int TemporalWindowDays = 7;

    /// <summary>The complete builder identity — every parameter that shapes membership, pinned by test.</summary>
    public static readonly string IdentityString = string.Create(
        CultureInfo.InvariantCulture,
        $"{BuilderVersion}|normalization={NormalizationVersion}|similarity={SimilarityMetric}"
            + $"|threshold={SimilarityThreshold}|temporalWindowDays={TemporalWindowDays}");

    /// <summary>Negation tokens preserved by normalization and consulted by the contradiction rule.</summary>
    private static readonly HashSet<string> NegationTokens =
        new(StringComparer.Ordinal) { "not", "no", "denies", "denied" };

    /// <summary>
    /// Builds the deterministic family set over the supplied facts. Byte-deterministic: a rerun over
    /// identical facts produces an identical list; families are ordered by (earliest member instant, family
    /// id) and members by (instant, fact id).
    /// </summary>
    public static IReadOnlyList<FactFamilyRecord> Build(IReadOnlyList<FactFamilyInputFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var ordered = facts
            .OrderBy(f => f.FirstObservedAtUtc)
            .ThenBy(f => f.FactId)
            .ToList();

        var families = new List<MutableFamily>();
        foreach (var fact in ordered)
        {
            var tokens = NormalizedTokens(fact.Statement);
            var joined = false;
            foreach (var family in families)
            {
                if (CanJoin(family, fact, tokens))
                {
                    family.Members.Add(fact);
                    joined = true;
                    break;
                }
            }

            if (!joined)
            {
                families.Add(new MutableFamily(fact, tokens));
            }
        }

        return families
            .Select(ToRecord)
            .OrderBy(f => f.EarliestObservedAtUtc)
            .ThenBy(f => f.FamilyId)
            .ToList();
    }

    /// <summary>
    /// The versioned statement normalization (<see cref="NormalizationVersion"/>): lowercase invariant,
    /// every non-letter/digit character becomes a space, whitespace collapsed. Negation tokens and numbers
    /// survive BY CONSTRUCTION — they are letters/digits — because stripping them would erase exactly the
    /// distinctions the contradiction rule protects.
    /// </summary>
    public static string NormalizeStatement(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return string.Join(' ', NormalizedTokens(statement));
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

    private static HashSet<string> NormalizedTokens(string statement)
    {
        var chars = statement.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ');
        return new string([.. chars])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static FactFamilyRecord ToRecord(MutableFamily family)
    {
        var members = family.Members
            .OrderBy(m => m.FirstObservedAtUtc)
            .ThenBy(m => m.FactId)
            .ToList();
        var representative = members[0];
        var canonicalClaimKey = NormalizeStatement(representative.Statement);
        return new FactFamilyRecord(
            FamilyId: FamilyIdFor(representative.CompanyId, representative.CaptureMode, canonicalClaimKey),
            CompanyId: representative.CompanyId,
            CaptureMode: representative.CaptureMode,
            RepresentativeFactId: representative.FactId,
            RepresentativeStatement: representative.Statement,
            CanonicalClaimKey: canonicalClaimKey,
            EventTypes: members
                .SelectMany(m => m.EventTypes)
                .Distinct()
                .OrderBy(t => (int)t)
                .ToList(),
            MemberFactIds: members.Select(m => m.FactId).ToList(),
            MemberCount: members.Count,
            DistinctPublisherCount: members.Select(m => m.Publisher).Distinct(StringComparer.Ordinal).Count(),
            EarliestObservedAtUtc: representative.FirstObservedAtUtc);
    }

    /// <summary>The stable family identity: builder version + company + capture mode + canonical-claim key. Never the member list.</summary>
    public static Guid FamilyIdFor(
        Guid companyId, NewsObservationCaptureMode captureMode, string canonicalClaimKey) =>
        DeterministicGuid.FromCanonicalString(
            $"radar:fact-family:{BuilderVersion}:{companyId:D}:{captureMode}:{canonicalClaimKey}");

    private sealed class MutableFamily(FactFamilyInputFact representative, HashSet<string> tokens)
    {
        /// <summary>The FIRST fact (input is pre-sorted by instant then id), i.e. the deterministic representative.</summary>
        public FactFamilyInputFact Representative { get; } = representative;

        public HashSet<string> RepresentativeTokens { get; } = tokens;

        public List<FactFamilyInputFact> Members { get; } = [representative];
    }
}
