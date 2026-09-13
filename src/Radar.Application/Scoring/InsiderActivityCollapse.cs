using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;

using Radar.Application.Collectors;
using Radar.Application.SignalExtraction;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// Deterministic (AD-3: no clock, IO, or randomness) same-insider collapse of directional
/// <see cref="SignalType.InsiderBuying"/> signals (spec 224) — the insider equivalent of
/// <see cref="MediaAttentionCollapse"/>, built to the same contract.
///
/// <para>
/// <b>The counting defect this closes.</b> Radar mints one <c>InsiderBuying</c> signal per Form 4, and each
/// discretionary sale adds its full weight to the trajectory's negative mass independently — so one
/// executive selling down a position over several weeks contributes one bearish event PER FILING. Measured on
/// 2026-09-12 over the live store: ATNI's 13 negative insider signals came from THREE people (4.3 filings
/// each); OOMA's five sale signals were three decisions across five weeks. Lowering the sale tier would
/// under-weight a single genuine sale while five repeats still outvote everything else — the error is that one
/// decision is counted N times, so the fix is a collapse, not a re-weighting.
/// </para>
/// <para>
/// <b>The rule (<c>insider-collapse-v1</c>).</b> Only <c>InsiderBuying</c> signals whose direction is
/// Positive or Negative are candidates; a Neutral insider signal (a 10b5-1 plan, a no-discretionary filing,
/// a mixed buy+sell) and every other signal type pass through untouched. A candidate's insider IDENTITY is
/// read ONLY through the shared <see cref="InsiderActivityRead"/> projection
/// (<see cref="InsiderActivityMetadata.TryRead"/>): the reporting owner's CIK when the collector captured one,
/// else the owner's name normalised (trimmed, upper-cased invariant, internal whitespace collapsed). That name
/// comes from the structured metadata key when present and otherwise — for every accrued pre-224 evidence
/// item, which carries the owner only inside its title — from the collector's fixed title shape, recovered at
/// read time inside <c>TryRead</c> (the ONE title parse; this type never touches a title). Every bucketed
/// candidate whose identity rests on that fallback is counted on
/// <see cref="InsiderCollapseResult.OwnerFromTitleCount"/>. So that a legacy filing (title name, no CIK) and a
/// post-224 filing by the same person (CIK + name) share ONE bucket across the boundary, a name-only candidate
/// whose normalised name the same company's input shows beside EXACTLY ONE CIK takes that CIK's identity; a
/// name shown beside two or more CIKs is ambiguous and the name-only candidate is not attributed to either.
/// Names match on exact normalised equality only, never on a surname or similarity. A filing whose owner
/// cannot be resolved (no readable Form 4 envelope; no CIK and no name — an unparseable title or the
/// collector's anonymous placeholder; or an ambiguous name) is UNRESOLVED: it passes through as its own
/// signal, never bucketed, and is counted on <see cref="InsiderCollapseResult.OwnerUnresolvedCount"/>.
/// Candidates are keyed by
/// <c>(CompanyId, identity, Direction)</c> — two different people selling in the same week are two decisions
/// and stay two signals; the same person buying and selling are two buckets — sorted by
/// <c>(ObservedAtUtc, Id)</c>, and bucketed greedily against the EARLIEST member of each bucket through the
/// shared <see cref="SameWindowBucketing"/> primitive within <see cref="InsiderCollapseOptions.EventWindow"/>.
/// The boundary is measured from the earliest member and never from the representative for the reason
/// <see cref="MediaAttentionCollapse"/> records: otherwise the representative choice could silently widen the
/// bucket and move the collapsed counts.
/// </para>
/// <para>
/// <b>The representative is the earliest member</b> (lowest <c>ObservedAtUtc</c>, then lowest <c>Id</c>) —
/// the v1 rule. No synthetic signal is ever fabricated: the representative is a real persisted signal keeping
/// its own id, evidence link, company, instants, confidence and novelty. A bucket of ONE returns the very
/// same instance, byte-identical. For a bucket of two or more, the members' recorded <c>insiderNetValue</c>s
/// are SUMMED and the representative's scoring-time <c>Strength</c> is re-derived from that AGGREGATE through
/// the SAME materiality tier walk the extractor applied to each filing
/// (<see cref="InsiderMaterialityWeights.StrengthForAmount"/>: <c>BuyTiers</c> for Positive,
/// <c>SellTiers</c> for Negative, plus the cluster boost when any member carried the cluster flag, capped at
/// 10) — so a person selling $500k four times is ONE signal at $2m, read by the tiers as a true position
/// size, never four partial ones (collapsing without aggregating would UNDER-count a large disposal). Summing
/// is safe because only <c>discretionary-buy</c> / <c>discretionary-sale</c> filings are directional; the
/// <c>mixed-buy-sell</c> figure that <see cref="InsiderActivityMetadata.NetValueKey"/> documents as
/// "never sum" rides a Neutral signal and is never a candidate. Members with no recorded value are counted
/// separately (<see cref="InsiderCollapsedBucket.MembersWithoutValue"/>); when NO member recorded one the
/// bucket still collapses and the representative's stored Strength is kept. The re-derivation is surfaced on
/// the persisted contribution reason by <c>ScoringEngine</c>, so a score can never silently disagree with
/// the signal on disk.
/// </para>
/// <para>
/// <b>What this does NOT change.</b> NO formula version bump: the math in every <c>IScoreFormula</c> is
/// untouched — only the insider INPUT SET changes, exactly the precedent the media collapse set. No signal
/// is written, superseded or rewritten on disk (AD-8): the collapse applies at scoring time to the window's
/// signals — accrued ones included, since their owner is recovered from the title at read time — so nothing
/// is backfilled and the stored history is untouched. The collapse STRUCTURE is versioned here
/// (<see cref="Version"/>) and folded into the scoring-config fingerprint via
/// <see cref="CanonicalDescriptor"/> together with the tunable window MAGNITUDE
/// (<see cref="InsiderCollapseOptions"/>); introducing it moved every pin, deliberately, and any structural
/// change (the key, the boundary rule, the representative rule, the aggregation) bumps <see cref="Version"/>
/// and moves every pin again (see <c>ScoringConfigFingerprintTests</c>).
/// </para>
/// </summary>
public sealed class InsiderActivityCollapse
{
    /// <summary>The versioned collapse-structure identity (bumped only if the bucketing/aggregation shape changes).</summary>
    public const string Version = "insider-collapse-v1";

    /// <summary>The domain maximum Strength; the cluster boost is capped here exactly as the extractor caps it.</summary>
    private const int MaxStrength = 10;

    private static readonly Regex InternalWhitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly InsiderCollapseOptions _options;
    private readonly InsiderMaterialityWeights _materiality;

    public InsiderActivityCollapse(InsiderCollapseOptions options, InsiderMaterialityWeights materiality)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(materiality);
        options.Validate();
        materiality.Validate();
        _options = options;
        _materiality = materiality;
    }

    /// <summary>
    /// Deterministic, culture-invariant (AD-3) serialization hashed by the scoring-config fingerprint:
    /// <c>insider-collapse-v1;window={days};</c> — the structure version + the tunable window magnitude,
    /// with a trailing ';' to match the <c>mediaCollapse</c>/<c>insiderDesc</c> style. Round-trip ("R")
    /// invariant-culture number formatting so a comma-decimal locale cannot corrupt it. The materiality
    /// tiers the re-derivation walks are NOT repeated here: they are already hashed by value through the
    /// <c>insiderDesc</c> field, from the same <see cref="InsiderMaterialityWeights"/> instance.
    /// </summary>
    public string CanonicalDescriptor() =>
        $"{Version};window={_options.EventWindowDays.ToString("R", CultureInfo.InvariantCulture)};";

    /// <summary>
    /// Collapses same-insider directional <see cref="SignalType.InsiderBuying"/> signals in
    /// <paramref name="signals"/> to one representative per (company, insider, direction) bucket, leaving
    /// every other signal untouched. Returns the collapsed signal list (representatives ∪ untouched, stably
    /// ordered by <c>ObservedAtUtc</c> then <c>Id</c>), per representative that absorbed at least one other
    /// filing the bucket's accounting, and the count of candidates whose owner could not be resolved.
    /// Empty input is a no-op.
    /// </summary>
    public InsiderCollapseResult Collapse(IReadOnlyList<ScoringSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var untouched = new List<ScoringSignal>();
        var ownerUnresolved = 0;
        var ownerAmbiguous = 0;
        var ownerFromTitle = 0;

        // Pass 1: every directional insider candidate with SOME owner (a CIK, or a name from metadata or the
        // title), plus — per company — which CIKs each normalised name was seen with. The association is a
        // property of the whole input set, never of its order (AD-3).
        var resolvable = new List<Candidate>();
        var ciksByName = new Dictionary<(Guid? CompanyId, string Name), SortedSet<string>>();

        foreach (var s in signals)
        {
            var signal = s.Signal;
            if (signal.Type != SignalType.InsiderBuying
                || (signal.Direction != SignalDirection.Positive && signal.Direction != SignalDirection.Negative))
            {
                // Neutral insider signals (plan-10b5-1, no-discretionary, mixed) and every other type pass
                // through byte-identical (spec 224 §2).
                untouched.Add(s);
                continue;
            }

            // Identity through the ONE shared read contract (which alone may fall back to the title).
            var read = InsiderActivityMetadata.TryRead(s.Evidence);
            if (read is null || (read.OwnerCik is null && read.OwnerName is null))
            {
                // UNRESOLVED: its own signal, never bucketed, counted on a named axis.
                ownerUnresolved++;
                untouched.Add(s);
                continue;
            }

            resolvable.Add(new Candidate(s, read));
            if (read.OwnerCik is { } cik && read.OwnerName is { } name)
            {
                var nameKey = (signal.CompanyId, NormaliseName(name));
                if (!ciksByName.TryGetValue(nameKey, out var ciks))
                {
                    ciks = new SortedSet<string>(StringComparer.Ordinal);
                    ciksByName[nameKey] = ciks;
                }

                ciks.Add(cik);
            }
        }

        // Pass 2: the bucket key. Candidates grouped by it; the dictionary's enumeration order never reaches
        // the output: representatives are re-sorted with everything else below, and the accounting map is
        // keyed by signal id — so group order cannot affect any result (AD-3).
        var groups = new Dictionary<(Guid? CompanyId, string Identity, SignalDirection Direction), List<Candidate>>();

        foreach (var candidate in resolvable)
        {
            var signal = candidate.Signal.Signal;
            var identity = ResolveIdentity(signal.CompanyId, candidate.Read, ciksByName);
            if (identity is null)
            {
                // A name-only filing whose name this company's window associates with TWO OR MORE CIKs: two
                // different people share it, so it cannot be attributed to either. Unresolved, counted on the
                // same axis (with its own sub-count) — never guessed into one of them.
                ownerUnresolved++;
                ownerAmbiguous++;
                untouched.Add(candidate.Signal);
                continue;
            }

            if (candidate.Read.OwnerCik is null && candidate.Read.OwnerSource == InsiderOwnerSource.Title)
            {
                // The identity rests on the title fallback (accrued pre-224 evidence) — counted so it is visible
                // how much of the collapse depends on it.
                ownerFromTitle++;
            }

            var key = (signal.CompanyId, identity, signal.Direction);
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
            }

            group.Add(candidate);
        }

        var representatives = new List<ScoringSignal>();
        var collapsed = new Dictionary<Guid, InsiderCollapsedBucket>();

        foreach (var group in groups.Values)
        {
            // Deterministic ordering inside the key so bucketing and the earliest-member rule never depend
            // on caller order (AD-3).
            group.Sort(static (a, b) => SameWindowBucketing.CompareObservedThenId(a.Signal, b.Signal));
            var sorted = group.Select(c => c.Signal).ToList();

            foreach (var (start, end) in SameWindowBucketing.GreedyWindows(sorted, _options.EventWindow))
            {
                var representative = group[start];
                var count = end - start;
                if (count == 1)
                {
                    // A bucket of one: the SAME instance, byte-identical (no `with`).
                    representatives.Add(representative.Signal);
                    continue;
                }

                representatives.Add(CollapseBucket(group, start, end, representative, collapsed));
            }
        }

        var result = new List<ScoringSignal>(representatives.Count + untouched.Count);
        result.AddRange(representatives);
        result.AddRange(untouched);
        result.Sort(SameWindowBucketing.CompareObservedThenId);

        return new InsiderCollapseResult(
            result,
            collapsed.Count == 0 ? InsiderCollapseResult.NoBuckets : collapsed,
            ownerUnresolved,
            ownerFromTitle,
            ownerAmbiguous);
    }

    /// <summary>
    /// Collapses the completed bucket <c>[start, end)</c> onto its earliest member: sums the members'
    /// recorded values, re-derives the representative's Strength from the aggregate when at least one
    /// member recorded a value, and records the bucket's accounting under the representative's id.
    /// </summary>
    private ScoringSignal CollapseBucket(
        List<Candidate> group,
        int start,
        int end,
        Candidate representative,
        Dictionary<Guid, InsiderCollapsedBucket> collapsed)
    {
        decimal? aggregate = null;
        var withoutValue = 0;
        var anyCluster = false;
        for (var k = start; k < end; k++)
        {
            var member = group[k];
            if (member.Read.NetValue is { } value)
            {
                aggregate = (aggregate ?? 0m) + value;
            }
            else
            {
                withoutValue++;
            }

            anyCluster |= member.Read.HasCluster;
        }

        var before = representative.Signal.Signal.Strength;
        var after = before;
        if (aggregate is { } total)
        {
            // The SAME tier walk the extractor applied per filing, over the AGGREGATE: BuyTiers for a
            // Positive bucket, SellTiers for a Negative one, then the cluster boost if ANY member was a
            // multi-insider filing — capped at the domain max exactly as the extractor caps it.
            var tiers = representative.Signal.Signal.Direction == SignalDirection.Positive
                ? _materiality.BuyTiers
                : _materiality.SellTiers;
            after = InsiderMaterialityWeights.StrengthForAmount(total, tiers);
            if (anyCluster)
            {
                after = Math.Min(MaxStrength, after + _materiality.ClusterBoost);
            }
        }

        var count = end - start;
        collapsed[representative.Signal.Signal.Id] = new InsiderCollapsedBucket(
            CollapsedCount: count - 1,
            AggregateValue: aggregate,
            MembersWithoutValue: withoutValue,
            StrengthBefore: before,
            StrengthAfter: after);

        // Same Id, EvidenceId, CompanyId, instants, confidence, novelty — only the scoring-time Strength
        // moves, and only when the aggregate actually moved it (an unchanged Strength returns the same
        // instance; a `with` copy would be value-equal anyway).
        return after == before
            ? representative.Signal
            : representative.Signal with
            {
                Signal = representative.Signal.Signal with { Strength = after },
            };
    }

    /// <summary>
    /// The insider identity of one candidate that carries a CIK or a name:
    /// <list type="bullet">
    /// <item>the CIK when captured;</item>
    /// <item>else, when the candidate's normalised name was seen beside EXACTLY ONE CIK for the same company in
    /// this input, that CIK — so a legacy filing (name recovered from the title, no CIK) and a post-224 filing
    /// (CIK + name from metadata) by the same person share ONE bucket across the boundary;</item>
    /// <item>else, when the name was seen beside NO CIK, the normalised name;</item>
    /// <item>else (the name was seen beside two or more CIKs — two people share it) <c>null</c>: ambiguous.</item>
    /// </list>
    /// Names match only on EXACT normalised equality (trimmed, internal whitespace collapsed, upper-cased
    /// invariant) — never on a surname, prefix or similarity.
    /// </summary>
    private static string? ResolveIdentity(
        Guid? companyId,
        InsiderActivityRead read,
        Dictionary<(Guid? CompanyId, string Name), SortedSet<string>> ciksByName)
    {
        if (read.OwnerCik is { } cik)
        {
            // Prefixed so a CIK can never collide with a name that happens to be digits.
            return CikIdentity(cik);
        }

        var name = NormaliseName(read.OwnerName!);
        if (!ciksByName.TryGetValue((companyId, name), out var ciks))
        {
            return "name:" + name;
        }

        return ciks.Count == 1 ? CikIdentity(ciks.Min!) : null;
    }

    private static string CikIdentity(string cik) => "cik:" + cik;

    private static string NormaliseName(string name) =>
        InternalWhitespace.Replace(name.Trim(), " ").ToUpperInvariant();

    private readonly record struct Candidate(ScoringSignal Signal, InsiderActivityRead Read);
}

/// <summary>
/// The result of an <see cref="InsiderActivityCollapse.Collapse"/>: the de-noised signal list
/// (representatives ∪ untouched, stably ordered), per representative <c>Signal.Id</c> the accounting of the
/// bucket it stands for (only buckets that absorbed at least one other filing are present), and the number
/// of directional insider candidates whose owner could not be resolved and so were passed through unbucketed.
/// </summary>
/// <param name="Signals">The de-noised signal list.</param>
/// <param name="Collapsed">Per representative id, the accounting of a bucket that absorbed at least one other filing.</param>
/// <param name="OwnerUnresolvedCount">Directional insider candidates passed through UNBUCKETED for want of a
/// resolvable owner: not a readable Form 4 envelope, no CIK and no name (no metadata key, and a title in no
/// known collector shape or naming only the anonymous placeholder), or a name-only filing whose name is
/// ambiguous (see <paramref name="OwnerNameAmbiguousCount"/>).</param>
/// <param name="OwnerFromTitleCount">Directional insider candidates that WERE bucketed on an identity resting
/// on an owner name recovered from the evidence title (<see cref="InsiderOwnerSource.Title"/>, no CIK) —
/// accrued pre-224 evidence. Its own axis, so how much of the collapse rests on the title fallback is visible.
/// Includes a title-named filing joined to a CIK bucket through its name.</param>
/// <param name="OwnerNameAmbiguousCount">The subset of <paramref name="OwnerUnresolvedCount"/> that carried a
/// name but no CIK, where that name was seen beside two or more different CIKs for the same company.</param>
public sealed record InsiderCollapseResult(
    IReadOnlyList<ScoringSignal> Signals,
    IReadOnlyDictionary<Guid, InsiderCollapsedBucket> Collapsed,
    int OwnerUnresolvedCount,
    int OwnerFromTitleCount,
    int OwnerNameAmbiguousCount)
{
    /// <summary>The shared empty map, frozen so the healthy path allocates nothing per company.</summary>
    internal static readonly IReadOnlyDictionary<Guid, InsiderCollapsedBucket> NoBuckets =
        FrozenDictionary<Guid, InsiderCollapsedBucket>.Empty;

    /// <summary>How many filings this collapse removed from the scored set — the per-company log count.</summary>
    public int TotalCollapsed => Collapsed.Values.Sum(b => b.CollapsedCount);

    /// <summary>How many buckets absorbed at least one other filing.</summary>
    public int BucketCount => Collapsed.Count;
}

/// <summary>
/// One collapsed bucket's accounting, carried on its representative so the contribution reason and the
/// report line can name what the surviving signal stands for.
/// </summary>
/// <param name="CollapsedCount">Members other than the representative (the bucket holds <c>CollapsedCount + 1</c> filings).</param>
/// <param name="AggregateValue">The SUM of the members' recorded values, or <c>null</c> when NO member recorded one — never a defaulted zero.</param>
/// <param name="MembersWithoutValue">Members that recorded no value (counted, never silently summed as zero).</param>
/// <param name="StrengthBefore">The representative's stored Strength.</param>
/// <param name="StrengthAfter">The Strength scored — re-derived from the aggregate, or equal to <paramref name="StrengthBefore"/> when no value was recorded.</param>
public sealed record InsiderCollapsedBucket(
    int CollapsedCount,
    decimal? AggregateValue,
    int MembersWithoutValue,
    int StrengthBefore,
    int StrengthAfter);
