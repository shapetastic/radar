using System.Globalization;

using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// Deterministic (AD-3: no clock, IO, or randomness) same-event collapse of
/// <see cref="SignalType.MediaAttention"/> signals (spec 109). Many near-simultaneous outlets covering ONE
/// real-world event each emit a separate <c>MediaAttention</c> signal; this transform greedily buckets those
/// signals by observation-time proximity (within <see cref="MediaCollapseOptions.EventWindow"/> of the
/// bucket's earliest signal) and keeps ONE representative per bucket — the earliest-observed real signal (no
/// synthetic signal is ever fabricated; provenance is preserved). Non-<c>MediaAttention</c> signals pass
/// through untouched. This de-noises the media channel so one event counts as ~one attention unit rather than
/// N duplicates — it is a general de-noising transform, not a ticker-specific rule.
///
/// <para>
/// The collapse STRUCTURE (greedy same-window bucketing, earliest representative) is versioned here
/// (<see cref="Version"/> = <c>media-collapse-v1</c>) and folded into the scoring-config fingerprint via
/// <see cref="CanonicalDescriptor"/>; the tunable window MAGNITUDE lives in
/// <see cref="MediaCollapseOptions"/> (config). Changing the window re-stamps the fingerprint by value; no
/// formula-version bump is needed because the formula math is untouched — only its media input set changes.
/// </para>
/// </summary>
public sealed class MediaAttentionCollapse
{
    /// <summary>The versioned collapse-structure identity (bumped only if the bucketing shape changes).</summary>
    public const string Version = "media-collapse-v1";

    private readonly MediaCollapseOptions _options;

    public MediaAttentionCollapse(MediaCollapseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <summary>
    /// Deterministic, culture-invariant (AD-3) serialization hashed by the scoring-config fingerprint:
    /// <c>media-collapse-v1;window={days};</c> — the structure version + the tunable window magnitude, with a
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

        // Deterministic ordering so bucketing (and the earliest-representative choice) never depends on caller
        // order (AD-3): ObservedAtUtc, then Id as tiebreak.
        media.Sort(CompareSignals);

        var representatives = new List<ScoringSignal>();
        var collapsedCounts = new Dictionary<Guid, int>();

        var i = 0;
        while (i < media.Count)
        {
            var bucketFirst = media[i];
            var count = 1;

            // Greedy: each subsequent media signal within EventWindow of the bucket's FIRST/earliest signal
            // joins this bucket; the first one outside opens the next bucket.
            var j = i + 1;
            while (j < media.Count
                && media[j].Signal.ObservedAtUtc - bucketFirst.Signal.ObservedAtUtc <= _options.EventWindow)
            {
                count++;
                j++;
            }

            representatives.Add(bucketFirst);
            if (count > 1)
            {
                collapsedCounts[bucketFirst.Signal.Id] = count - 1;
            }

            i = j;
        }

        var result = new List<ScoringSignal>(representatives.Count + nonMedia.Count);
        result.AddRange(representatives);
        result.AddRange(nonMedia);
        result.Sort(CompareSignals);

        return new MediaCollapseResult(result, collapsedCounts);
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
