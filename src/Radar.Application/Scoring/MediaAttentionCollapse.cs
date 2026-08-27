using System.Globalization;

using Radar.Application.SignalExtraction;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// Deterministic (AD-3: no clock, IO, or randomness) same-event collapse of
/// <see cref="SignalType.MediaAttention"/> signals (spec 109). Many near-simultaneous outlets covering ONE
/// real-world event each emit a separate <c>MediaAttention</c> signal; this transform greedily buckets those
/// signals by observation-time proximity (within <see cref="MediaCollapseOptions.EventWindow"/> of the
/// bucket's earliest signal) and keeps ONE representative per bucket. No synthetic signal is ever fabricated:
/// the representative is always a real persisted signal keeping its own evidence link. Non-<c>MediaAttention</c>
/// signals pass through untouched. This de-noises the media channel so one event counts as ~one attention unit
/// rather than N duplicates — it is a general de-noising transform, not a ticker-specific rule.
///
/// <para>
/// <b>SPEC 194 §1.5 — <c>media-collapse-v2</c>: a grounded direction must survive the collapse.</b> v1 kept
/// the bucket's EARLIEST-observed member, which is direction-blind. Spec 191 recorded that as a known gap;
/// §1.2 made it live. The judgment-derived signal (§1.2) is anchored to the article a judgment actually
/// cited, and that article's own ordinary Neutral event was very often observed minutes or hours earlier from
/// another outlet — so under v1 the one signal in the bucket that carries a grounded direction could be
/// de-noised away by an unread duplicate, and the company's news would score Neutral while Radar held a
/// validated read of it. v2 changes ONLY the choice of representative INSIDE each completed bucket:
/// <list type="number">
///   <item>a structurally valid <c>news-judgment-signal-v1</c> signal beats an ordinary media signal;</item>
///   <item>among materialized signals, the latest <c>CreatedAtUtc</c>, then the lowest <c>Id</c>; and</item>
///   <item>when the bucket holds no materialized signal, the EXACT v1 rule — earliest observed, then lowest
///     <c>Id</c> — so an all-ordinary bucket produces a byte-identical result to v1.</item>
/// </list>
/// </para>
/// <para>
/// <b>The bucket BOUNDARIES are unchanged, and that separation is load-bearing.</b> Buckets are still formed
/// greedily against the EARLIEST signal of each bucket, never against the chosen representative. Measuring
/// the window from a later representative would let the choice of representative widen or shrink the event
/// bucket — the collapsed counts, and therefore the media signal count, would silently depend on whether a
/// judgment happened to exist. v2 widens and shrinks nothing: for any input, v1 and v2 produce the same
/// buckets and the same collapsed counts, and differ only in WHICH real member of a bucket carries them.
/// </para>
/// <para>
/// The collapse STRUCTURE (greedy same-window bucketing + the representative rule) is versioned here
/// (<see cref="Version"/> = <c>media-collapse-v2</c>) and folded into the scoring-config fingerprint via
/// <see cref="CanonicalDescriptor"/>; the tunable window MAGNITUDE lives in
/// <see cref="MediaCollapseOptions"/> (config). Changing the window re-stamps the fingerprint by value; no
/// formula-version bump is needed because the formula math is untouched — only its media input set changes.
/// The v1 → v2 bump moves every recorded fingerprint pin, deliberately and consciously (see
/// <c>ScoringConfigFingerprintTests</c>).
/// </para>
/// </summary>
public sealed class MediaAttentionCollapse
{
    /// <summary>The versioned collapse-structure identity (bumped only if the bucketing shape changes).</summary>
    public const string Version = "media-collapse-v2";

    private readonly MediaCollapseOptions _options;

    public MediaAttentionCollapse(MediaCollapseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <summary>
    /// Deterministic, culture-invariant (AD-3) serialization hashed by the scoring-config fingerprint:
    /// <c>media-collapse-v2;window={days};</c> — the structure version + the tunable window magnitude, with a
    /// trailing ';' to match the <c>insiderDesc</c>/<c>srcDesc</c> style. Round-trip ("R") invariant-culture
    /// number formatting so a comma-decimal locale cannot corrupt it.
    /// </summary>
    public string CanonicalDescriptor() =>
        $"{Version};window={_options.EventWindowDays.ToString("R", CultureInfo.InvariantCulture)};";

    /// <summary>
    /// Collapses same-event <see cref="SignalType.MediaAttention"/> signals in <paramref name="signals"/> to
    /// one representative per event bucket, leaving all other signals untouched. Returns the collapsed signal
    /// list (representatives ∪ non-media, stably ordered by <c>ObservedAtUtc</c> then <c>Id</c>) plus, for each
    /// representative that absorbed at least one duplicate, the number of duplicates it collapsed (for
    /// provenance). Empty or single-media input is a no-op (all pass through, empty counts map).
    /// </summary>
    public MediaCollapseResult Collapse(IReadOnlyList<ScoringSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var media = new List<ScoringSignal>();
        var nonMedia = new List<ScoringSignal>();
        foreach (var s in signals)
        {
            if (s.Signal.Type == SignalType.MediaAttention)
            {
                media.Add(s);
            }
            else
            {
                nonMedia.Add(s);
            }
        }

        // Deterministic ordering so bucketing (and the fallback earliest-representative choice) never depends
        // on caller order (AD-3): ObservedAtUtc, then Id as tiebreak.
        media.Sort(CompareSignals);

        // Classify each media signal ONCE, up front (spec 194 §1.5). The judgment-provenance classification
        // parses the metadata envelope, and a bucket scan would otherwise re-parse the same signal for every
        // member it is compared against. It uses the SHARED predicate — the same one §1.3's supersede and
        // §1.4's neutralization consult — so the collapse can never regard a signal as grounded that another
        // step regards as unverifiable.
        var isJudgmentDerived = new bool[media.Count];
        for (var k = 0; k < media.Count; k++)
        {
            isJudgmentDerived[k] = NewsDirectionalSignalMetadata.IsJudgmentDerived(media[k].Signal);
        }

        var representatives = new List<ScoringSignal>();
        var collapsedCounts = new Dictionary<Guid, int>();

        var i = 0;
        while (i < media.Count)
        {
            var bucketFirst = media[i];
            var count = 1;

            // Greedy: each subsequent media signal within EventWindow of the bucket's FIRST/earliest signal
            // joins this bucket; the first one outside opens the next bucket. UNCHANGED from v1 — and the
            // window is measured from bucketFirst, never from the representative chosen below, so the
            // representative rule cannot move a bucket boundary.
            var j = i + 1;
            while (j < media.Count
                && media[j].Signal.ObservedAtUtc - bucketFirst.Signal.ObservedAtUtc <= _options.EventWindow)
            {
                count++;
                j++;
            }

            // Spec 194 §1.5: choose the representative from the COMPLETED bucket [i, j). The scan starts at
            // bucketFirst and only ever replaces it on a strict Beats, so with no materialized signal present
            // the winner is bucketFirst itself — the media list is sorted by (ObservedAtUtc, Id), which IS
            // v1's earliest-observed/lowest-id rule, and the very same instance is returned.
            var representative = bucketFirst;
            var representativeIsDerived = isJudgmentDerived[i];
            for (var k = i + 1; k < j; k++)
            {
                if (BeatsAsRepresentative(
                        media[k], isJudgmentDerived[k], representative, representativeIsDerived))
                {
                    representative = media[k];
                    representativeIsDerived = isJudgmentDerived[k];
                }
            }

            representatives.Add(representative);
            if (count > 1)
            {
                // The collapsed count is exact and unchanged: every other member of the bucket, whichever
                // member ended up representing it.
                collapsedCounts[representative.Signal.Id] = count - 1;
            }

            i = j;
        }

        var result = new List<ScoringSignal>(representatives.Count + nonMedia.Count);
        result.AddRange(representatives);
        result.AddRange(nonMedia);
        result.Sort(CompareSignals);

        return new MediaCollapseResult(result, collapsedCounts);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> should represent the bucket instead of
    /// <paramref name="incumbent"/> (spec 194 §1.5). A grounded judgment-derived direction beats an ordinary
    /// media signal; between two grounded ones the latest <c>CreatedAtUtc</c> then lowest <c>Id</c> wins
    /// (matching <c>NewsJudgmentSignalSupersede</c>'s rule, so the two steps never disagree about which
    /// grounded read is current); between two ordinary ones nothing beats the incumbent, because the caller
    /// scans in sorted (ObservedAtUtc, Id) order and the incumbent is therefore already v1's winner. Strict,
    /// so the outcome is independent of scan order (AD-3).
    /// </summary>
    private static bool BeatsAsRepresentative(
        ScoringSignal candidate, bool candidateIsDerived, ScoringSignal incumbent, bool incumbentIsDerived)
    {
        if (candidateIsDerived != incumbentIsDerived)
        {
            return candidateIsDerived;
        }

        if (!candidateIsDerived)
        {
            return false;
        }

        var byCreated = candidate.Signal.CreatedAtUtc.CompareTo(incumbent.Signal.CreatedAtUtc);
        return byCreated != 0 ? byCreated > 0 : candidate.Signal.Id.CompareTo(incumbent.Signal.Id) < 0;
    }

    private static int CompareSignals(ScoringSignal a, ScoringSignal b)
    {
        var byObserved = a.Signal.ObservedAtUtc.CompareTo(b.Signal.ObservedAtUtc);
        return byObserved != 0 ? byObserved : a.Signal.Id.CompareTo(b.Signal.Id);
    }
}

/// <summary>
/// The result of a <see cref="MediaAttentionCollapse.Collapse"/>: the de-noised signal list (media
/// representatives ∪ untouched non-media, stably ordered) and, per representative <c>Signal.Id</c>, the number
/// of same-event media duplicates it collapsed (only entries with a positive count are present).
/// </summary>
public sealed record MediaCollapseResult(
    IReadOnlyList<ScoringSignal> Signals,
    IReadOnlyDictionary<Guid, int> CollapsedCounts);
