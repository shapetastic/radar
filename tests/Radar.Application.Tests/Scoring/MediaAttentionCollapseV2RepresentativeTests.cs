using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// SPEC 194 §1.5 — <c>media-collapse-v2</c>: a grounded direction must survive the same-event collapse.
/// <para>
/// The gap spec 191 recorded and §1.2 made live: v1 kept the bucket's EARLIEST-observed member, which is
/// direction-blind. The judgment-derived signal is anchored to the article a judgment actually cited, and
/// that article's own ordinary event was very often observed earlier from another outlet — so under v1 the
/// one member carrying a validated direction could be de-noised away by an unread duplicate, and the
/// company's news would score Neutral while Radar held a grounded read of it.
/// </para>
/// <para>
/// <b>What did NOT change, and is asserted here rather than assumed:</b> the greedy event-window BOUNDARIES.
/// Buckets are still formed against each bucket's EARLIEST signal, never against the chosen representative —
/// otherwise the existence of a judgment could widen or shrink a bucket and silently move the collapsed
/// counts. And an all-ordinary bucket still yields v1's exact result, instance for instance.
/// </para>
/// <para>
/// <b>MUTATION PROOFS.</b> Revert the representative to <c>bucketFirst</c> and
/// <see cref="MaterializedSignal_RepresentsTheBucket_OverAnEarlierNeutralMember"/>,
/// <see cref="MaterializedRepresentative_CarriesTheExactCollapsedCount"/> and
/// <see cref="AmongMaterializedSignals_LatestCreatedThenLowestIdRepresents"/> turn red. Compute the greedy
/// window from the chosen representative instead of the bucket's earliest member and
/// <see cref="BucketBoundaries_AreMeasuredFromTheEarliestMember_NotTheRepresentative"/> turns red.
/// </para>
/// </summary>
public sealed class MediaAttentionCollapseV2RepresentativeTests
{
    private static readonly DateTimeOffset Base = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid JudgmentId = new("9c8f7e6d-3333-4c33-9333-cccccccccccc");

    private static MediaAttentionCollapse Collapse(double windowDays = 3.0) =>
        new(new MediaCollapseOptions { EventWindowDays = windowDays });

    // ---------------------------------------------------------------------------------------------
    // The v2 representative rule.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void MaterializedSignal_RepresentsTheBucket_OverAnEarlierNeutralMember()
    {
        // The live shape: two outlets covered one event, the earlier one was never read, and the judgment
        // was grounded in the later article. Under v1 the Neutral duplicate represented the bucket and the
        // grounded direction vanished.
        var earlierNeutral = Ordinary(SignalId(0xA1), Base);
        var grounded = Materialized(SignalId(0xB1), Base.AddHours(6), SignalDirection.Negative);

        var result = Collapse().Collapse([earlierNeutral, grounded]);

        var representative = Assert.Single(result.Signals);
        Assert.Equal(grounded.Signal.Id, representative.Signal.Id);
        Assert.Equal(SignalDirection.Negative, representative.Signal.Direction);

        // The representative is a REAL persisted signal — the same instance, never a synthesized composite.
        Assert.Same(grounded, representative);
    }

    [Fact]
    public void MaterializedRepresentative_CarriesTheExactCollapsedCount()
    {
        // Five outlets, one event, one of them grounded. The collapsed count is exact and unchanged by the
        // representative rule: every OTHER member of the bucket, whichever member represents it.
        var grounded = Materialized(SignalId(0xB1), Base.AddDays(2), SignalDirection.Positive);
        var members = new List<ScoringSignal>
        {
            Ordinary(SignalId(0xA1), Base),
            Ordinary(SignalId(0xA2), Base.AddHours(6)),
            grounded,
            Ordinary(SignalId(0xA3), Base.AddHours(30)),
            Ordinary(SignalId(0xA4), Base.AddDays(2).AddHours(12)),
        };

        var result = Collapse().Collapse(members);

        var representative = Assert.Single(result.Signals);
        Assert.Equal(grounded.Signal.Id, representative.Signal.Id);
        Assert.Equal(4, result.CollapsedCounts[grounded.Signal.Id]);

        // The count is keyed by the REPRESENTATIVE, so the engine can name it on that signal's contribution
        // reason. Nothing is keyed by the earliest member any more.
        Assert.Single(result.CollapsedCounts);
    }

    [Fact]
    public void AmongMaterializedSignals_LatestCreatedThenLowestIdRepresents()
    {
        // Two grounded reads inside one bucket: the newest one is the current read. ObservedAtUtc is the
        // article instant, so only CreatedAtUtc separates them — the same rule the §1.3 supersede applies,
        // deliberately, so the two steps can never disagree about which grounded read is current.
        var older = Materialized(
            SignalId(0xB1), Base, SignalDirection.Positive, createdAt: Base.AddDays(1));
        var newer = Materialized(
            SignalId(0xB2), Base.AddHours(1), SignalDirection.Negative, createdAt: Base.AddDays(2));

        var result = Collapse().Collapse([older, newer]);

        Assert.Equal(newer.Signal.Id, Assert.Single(result.Signals).Signal.Id);

        // Same CreatedAtUtc ⇒ lowest id wins.
        var lowId = Materialized(
            SignalId(0x01), Base.AddHours(2), SignalDirection.Positive, createdAt: Base.AddDays(3));
        var highId = Materialized(
            SignalId(0x02), Base, SignalDirection.Negative, createdAt: Base.AddDays(3));

        var tie = Collapse().Collapse([highId, lowId]);

        Assert.Equal(lowId.Signal.Id, Assert.Single(tie.Signals).Signal.Id);
    }

    [Fact]
    public void MalformedV1Envelope_DoesNotRepresentTheBucket()
    {
        // An unverifiable claim must not displace the honest earliest-observed member: the collapse asks the
        // SAME shared predicate the supersede and the §1.4 neutralization ask, so a signal cannot be
        // "grounded enough to represent" while being "unverifiable enough to neutralize".
        var earliest = Ordinary(SignalId(0xA1), Base);
        var malformed = new ScoringSignal(
            NewsSignal(SignalId(0xB9), Base.AddHours(6), SignalDirection.Neutral, "{not json"),
            EvidenceFor(SignalId(0xB9)));

        var result = Collapse().Collapse([earliest, malformed]);

        Assert.Same(earliest, Assert.Single(result.Signals));
    }

    // ---------------------------------------------------------------------------------------------
    // What did NOT change: boundaries, and the all-ordinary result.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void BucketBoundaries_AreMeasuredFromTheEarliestMember_NotTheRepresentative()
    {
        // Window 3 days. Members at t0 (ordinary), t0+2d (grounded) and t0+4d (ordinary).
        //   * From the bucket's EARLIEST member (t0): t0+4d is 4 days out ⇒ it opens its OWN bucket. Two
        //     buckets, the first collapsing one duplicate.
        //   * From the REPRESENTATIVE (t0+2d): t0+4d would be only 2 days out ⇒ it would JOIN, giving one
        //     bucket collapsing two. That is the wrong answer, and it is wrong in a way that depends on
        //     whether a judgment happened to exist.
        var earliest = Ordinary(SignalId(0xA1), Base);
        var grounded = Materialized(SignalId(0xB1), Base.AddDays(2), SignalDirection.Positive);
        var separateEvent = Ordinary(SignalId(0xA2), Base.AddDays(4));

        var result = Collapse().Collapse([earliest, grounded, separateEvent]);

        Assert.Equal(2, result.Signals.Count);
        Assert.Equal(
            [grounded.Signal.Id, separateEvent.Signal.Id],
            result.Signals.Select(s => s.Signal.Id).ToList());
        Assert.Equal(1, result.CollapsedCounts[grounded.Signal.Id]);
        Assert.DoesNotContain(separateEvent.Signal.Id, result.CollapsedCounts.Keys);
    }

    [Fact]
    public void AllOrdinaryBucket_ProducesTheExactV1Result()
    {
        // THE V1 PIN. Every member is an ordinary Neutral media signal, so v2's rule 3 applies and the
        // outcome must be byte-for-byte what media-collapse-v1 produced: the earliest-observed member, ties
        // broken by lowest id, carrying N-1.
        var idA = SignalId(0x0A);
        var idB = SignalId(0x0B);

        var later = Ordinary(SignalId(0xA9), Base.AddDays(1));
        var earliestHighId = Ordinary(idB, Base);
        var earliestLowId = Ordinary(idA, Base);
        var nonMedia = new ScoringSignal(
            new SignalBuilder()
                .WithId(SignalId(0xC1))
                .WithEvidenceId(SignalId(0xC1))
                .WithType(SignalType.GuidanceChange)
                .WithDirection(SignalDirection.Positive)
                .WithObservedAtUtc(Base.AddHours(3))
                .WithCreatedAtUtc(Base.AddHours(3))
                .Build(),
            EvidenceFor(SignalId(0xC1)));

        var result = Collapse().Collapse([later, earliestHighId, nonMedia, earliestLowId]);

        // Field for field: the representative INSTANCE, the full ordered survivor list (media
        // representatives ∪ untouched non-media, sorted by ObservedAtUtc then Id) and the single count.
        Assert.Equal(2, result.Signals.Count);
        Assert.Same(earliestLowId, result.Signals[0]);
        Assert.Same(nonMedia, result.Signals[1]);
        Assert.Equal(2, result.CollapsedCounts[idA]);
        Assert.Single(result.CollapsedCounts);
        Assert.DoesNotContain(idB, result.CollapsedCounts.Keys);
    }

    [Fact]
    public void Version_IsMediaCollapseV2_AndIsCarriedIntoTheCanonicalDescriptor()
    {
        // The structure version is a hashed fingerprint field: the bump is what makes the pins move, and
        // pinning it here is what makes an accidental re-edit visible where the rule lives.
        Assert.Equal("media-collapse-v2", MediaAttentionCollapse.Version);
        Assert.Equal("media-collapse-v2;window=3;", Collapse().CanonicalDescriptor());
    }

    [Fact]
    public void RepresentativeChoice_IsOrderIndependent()
    {
        var earliest = Ordinary(SignalId(0xA1), Base);
        var grounded = Materialized(SignalId(0xB1), Base.AddHours(6), SignalDirection.Negative);
        var another = Ordinary(SignalId(0xA2), Base.AddHours(12));

        var forward = Collapse().Collapse([earliest, grounded, another]);
        var reversed = Collapse().Collapse([another, grounded, earliest]);

        Assert.Equal(
            forward.Signals.Select(s => s.Signal.Id).ToList(),
            reversed.Signals.Select(s => s.Signal.Id).ToList());
        Assert.Equal(
            forward.CollapsedCounts.OrderBy(kv => kv.Key).ToList(),
            reversed.CollapsedCounts.OrderBy(kv => kv.Key).ToList());
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture builders — every one constructed here, never copied from live data.
    // ---------------------------------------------------------------------------------------------

    private static Guid SignalId(byte discriminator) =>
        new([.. Enumerable.Repeat(discriminator, 16)]);

    private static ScoringSignal Ordinary(Guid id, DateTimeOffset observedAt) =>
        new(NewsSignal(id, observedAt, SignalDirection.Neutral, metadataJson: null), EvidenceFor(id));

    private static ScoringSignal Materialized(
        Guid id, DateTimeOffset observedAt, SignalDirection direction, DateTimeOffset? createdAt = null)
    {
        var metadata = NewsDirectionalSignalMetadata.ComposeJudgmentSignal(
            JudgmentId,
            "deepseek|p2|s2|stage1|families",
            direction == SignalDirection.Positive ? "Improving" : "Deteriorating",
            [new Guid("aaaaaaaa-0000-4000-8000-000000000001")],
            [new Guid("bbbbbbbb-0000-4000-8000-000000000001")],
            [id]);

        var signal = NewsSignal(id, observedAt, direction, metadata) with
        {
            CreatedAtUtc = createdAt ?? observedAt,
        };

        return new ScoringSignal(signal, EvidenceFor(id));
    }

    private static Signal NewsSignal(
        Guid id, DateTimeOffset observedAt, SignalDirection direction, string? metadataJson) =>
        new SignalBuilder()
            .WithId(id)
            // The evidence id is deliberately the signal's own id here: this file is about BUCKETING, and
            // giving every member its own evidence keeps the §1.3 same-evidence supersede out of the picture.
            .WithEvidenceId(id)
            .WithType(SignalType.MediaAttention)
            .WithDirection(direction)
            .WithStrength(4)
            .WithNovelty(4)
            .WithConfidence(0.5m)
            .WithObservedAtUtc(observedAt)
            .WithCreatedAtUtc(observedAt)
            .WithMetadataJson(metadataJson)
            .Build();

    private static Radar.Domain.Evidence.EvidenceItem EvidenceFor(Guid id) =>
        new EvidenceBuilder().WithId(id).WithContentHash(id.ToString("N")).Build();
}
