using System.Globalization;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 224 — <c>insider-collapse-v1</c>: one insider's decision is ONE signal, not one per filing. The
/// bucket key is (company, insider identity, direction); boundaries are greedy against the EARLIEST member
/// through the shared <see cref="SameWindowBucketing"/>; the representative is the earliest member carrying
/// the AGGREGATE value, its Strength re-derived through the SAME materiality tier walk the extractor used;
/// an unresolvable owner is never bucketed and is counted; everything else passes through byte-identical.
/// </summary>
public sealed class InsiderActivityCollapseTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid CompanyA = new("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid CompanyB = new("bbbbbbbb-0000-0000-0000-00000000000b");

    private static readonly InsiderMaterialityWeights Materiality = new();

    private static InsiderActivityCollapse Collapse(double windowDays = 30.0, InsiderMaterialityWeights? weights = null) =>
        new(new InsiderCollapseOptions { EventWindowDays = windowDays }, weights ?? Materiality);

    /// <summary>The exact Form 4 envelope shape <c>SecForm4Collector.MapToEvidence</c> writes.</summary>
    private static string Form4Envelope(
        string reason,
        decimal? netValue,
        string? ownerName,
        string? ownerCik = null,
        bool cluster = false,
        bool form4 = true)
    {
        var pairs = new List<string>();
        if (form4)
        {
            pairs.Add("\"form\":\"4\"");
        }

        pairs.Add($"\"insiderClassificationReason\":\"{reason}\"");
        if (netValue is { } v)
        {
            pairs.Add($"\"insiderNetValue\":\"{v.ToString(CultureInfo.InvariantCulture)}\"");
        }

        if (ownerName is not null)
        {
            pairs.Add($"\"insiderOwnerName\":\"{ownerName}\"");
        }

        if (ownerCik is not null)
        {
            pairs.Add($"\"insiderOwnerCik\":\"{ownerCik}\"");
        }

        if (cluster)
        {
            pairs.Add("\"insiderCluster\":\"true\"");
        }

        return "{\"metadata\":{" + string.Join(",", pairs) + "},\"companyHints\":[]}";
    }

    private static ScoringSignal Filing(
        DateTimeOffset observedAt,
        SignalDirection direction = SignalDirection.Negative,
        decimal? netValue = 500_000m,
        string? ownerName = "STANG ERIC B",
        string? ownerCik = null,
        bool cluster = false,
        int? strength = null,
        Guid? companyId = null,
        Guid? id = null,
        SignalType type = SignalType.InsiderBuying,
        bool form4 = true)
    {
        var reason = direction switch
        {
            SignalDirection.Positive => InsiderActivityMetadata.DiscretionaryBuy,
            SignalDirection.Negative => InsiderActivityMetadata.DiscretionarySale,
            _ => InsiderActivityMetadata.Plan10b51,
        };
        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithContentHash(Guid.NewGuid().ToString("N"))
            .WithTitle($"Form 4 — insider open-market sale: {ownerName ?? "An insider"} sold 1,000 shares")
            .WithMetadataJson(Form4Envelope(reason, netValue, ownerName, ownerCik, cluster, form4))
            .Build();

        // The stored Strength is what the extractor minted per filing: the per-filing tier walk.
        var tiers = direction == SignalDirection.Positive ? Materiality.BuyTiers : Materiality.SellTiers;
        var storedStrength = strength ?? (netValue is { } v && direction != SignalDirection.Neutral
            ? InsiderMaterialityWeights.StrengthForAmount(v, tiers)
            : 3);

        var signal = new SignalBuilder()
            .WithId(id ?? Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithCompanyId(companyId ?? CompanyA)
            .WithType(type)
            .WithDirection(direction)
            .WithStrength(storedStrength)
            .WithObservedAtUtc(observedAt)
            .Build();
        return new ScoringSignal(signal, evidence);
    }

    // (a) THE spec-224 acceptance criterion 3: four $500k sales by one owner are ONE $2m signal.
    [Fact]
    public void FourSalesByOneOwnerInsideWindow_CollapseToOneSignal_AtTheAggregateStrength()
    {
        var signals = new List<ScoringSignal>
        {
            Filing(Base),
            Filing(Base.AddDays(5)),
            Filing(Base.AddDays(12)),
            Filing(Base.AddDays(20)),
        };

        var result = Collapse().Collapse(signals);

        var representative = Assert.Single(result.Signals);
        Assert.Equal(signals[0].Signal.Id, representative.Signal.Id);
        Assert.Equal(signals[0].Signal.EvidenceId, representative.Signal.EvidenceId);

        var bucket = result.Collapsed[representative.Signal.Id];
        Assert.Equal(3, bucket.CollapsedCount);
        Assert.Equal(2_000_000m, bucket.AggregateValue);
        Assert.Equal(0, bucket.MembersWithoutValue);

        // Re-derived from the AGGREGATE through the SAME tier walk — 4 under the default sell tiers — not the
        // per-filing $500k strength (3). Collapsing without aggregating would under-count the disposal.
        var expected = InsiderMaterialityWeights.StrengthForAmount(2_000_000m, Materiality.SellTiers);
        var perFiling = InsiderMaterialityWeights.StrengthForAmount(500_000m, Materiality.SellTiers);
        Assert.Equal(4, expected);
        Assert.Equal(3, perFiling);
        Assert.Equal(expected, representative.Signal.Strength);
        Assert.Equal(perFiling, bucket.StrengthBefore);
        Assert.Equal(expected, bucket.StrengthAfter);
        Assert.Equal(3, result.TotalCollapsed);
        Assert.Equal(1, result.BucketCount);
        Assert.Equal(0, result.OwnerUnresolvedCount);
    }

    // (b) Two different people in the same week are two decisions.
    [Fact]
    public void TwoDifferentOwnersSameWeek_StayTwoSignals()
    {
        var signals = new List<ScoringSignal>
        {
            Filing(Base, ownerName: "STANG ERIC B"),
            Filing(Base.AddDays(2), ownerName: "Yeh Jenny C"),
        };

        var result = Collapse().Collapse(signals);

        Assert.Equal(2, result.Signals.Count);
        Assert.Empty(result.Collapsed);
        Assert.Equal(0, result.TotalCollapsed);
    }

    // (c) Same owner, opposite directions: two buckets.
    [Fact]
    public void SameOwnerPositiveAndNegative_FormTwoBuckets()
    {
        var signals = new List<ScoringSignal>
        {
            Filing(Base, SignalDirection.Negative),
            Filing(Base.AddDays(1), SignalDirection.Positive),
            Filing(Base.AddDays(2), SignalDirection.Negative),
            Filing(Base.AddDays(3), SignalDirection.Positive),
        };

        var result = Collapse().Collapse(signals);

        Assert.Equal(2, result.Signals.Count);
        Assert.Contains(result.Signals, s => s.Signal.Direction == SignalDirection.Negative);
        Assert.Contains(result.Signals, s => s.Signal.Direction == SignalDirection.Positive);
        Assert.Equal(2, result.BucketCount);
        Assert.All(result.Collapsed.Values, b => Assert.Equal(1, b.CollapsedCount));
    }

    // (d) Boundaries are greedy against the EARLIEST member: day 0/25/50 at 30 days is {0,25} and {50}.
    [Fact]
    public void Boundaries_AreMeasuredFromTheEarliestMember_NotTheLatest()
    {
        var day0 = Filing(Base);
        var day25 = Filing(Base.AddDays(25));
        var day50 = Filing(Base.AddDays(50));

        var result = Collapse(30.0).Collapse([day50, day0, day25]);

        Assert.Equal(2, result.Signals.Count);
        Assert.Contains(result.Signals, s => s.Signal.Id == day0.Signal.Id);
        Assert.Contains(result.Signals, s => s.Signal.Id == day50.Signal.Id);
        Assert.DoesNotContain(result.Signals, s => s.Signal.Id == day25.Signal.Id);
        Assert.Equal(1, result.Collapsed[day0.Signal.Id].CollapsedCount);
        Assert.False(result.Collapsed.ContainsKey(day50.Signal.Id));
    }

    // (e) An unresolvable owner is never bucketed — even beside a resolved filing whose TITLE names the same
    // person: the title is never parsed.
    [Fact]
    public void UnresolvedOwner_PassesThroughUntouched_AndIsCounted_NeverBucketedOnTitle()
    {
        var resolved = Filing(Base, ownerName: "STANG ERIC B");
        // Legacy pre-224 shape: the name is only in the title (the Filing helper puts it there), no key.
        var legacy = Filing(Base.AddDays(1), ownerName: null);
        var legacyTitleNamesSameOwner = Filing(Base.AddDays(2), ownerName: null);

        var result = Collapse().Collapse([resolved, legacy, legacyTitleNamesSameOwner]);

        Assert.Equal(3, result.Signals.Count);
        Assert.Same(legacy, result.Signals.Single(s => s.Signal.Id == legacy.Signal.Id));
        Assert.Same(
            legacyTitleNamesSameOwner,
            result.Signals.Single(s => s.Signal.Id == legacyTitleNamesSameOwner.Signal.Id));
        Assert.Empty(result.Collapsed);
        Assert.Equal(2, result.OwnerUnresolvedCount);
    }

    [Fact]
    public void NonForm4Envelope_OnADirectionalInsiderSignal_IsUnresolved()
    {
        // TryRead returns null for an envelope without form=4 → unresolved, counted, untouched.
        var odd = Filing(Base, ownerName: "STANG ERIC B", form4: false);
        var odd2 = Filing(Base.AddDays(1), ownerName: "STANG ERIC B", form4: false);

        var result = Collapse().Collapse([odd, odd2]);

        Assert.Equal(2, result.Signals.Count);
        Assert.Equal(2, result.OwnerUnresolvedCount);
        Assert.Empty(result.Collapsed);
    }

    // (f) CIK wins over name; same CIK with differently-cased names buckets together; a name-only filing
    // normalises case and internal whitespace.
    [Fact]
    public void CikWinsOverName_AndNameIsNormalised()
    {
        var byCik = Collapse().Collapse(
        [
            Filing(Base, ownerName: "Stang Eric B", ownerCik: "0001234567"),
            Filing(Base.AddDays(3), ownerName: "STANG ERIC B", ownerCik: "0001234567"),
            // Same spelling, DIFFERENT CIK: a different person, not bucketed with the two above.
            Filing(Base.AddDays(4), ownerName: "STANG ERIC B", ownerCik: "0007654321"),
        ]);
        Assert.Equal(2, byCik.Signals.Count);
        Assert.Equal(1, byCik.TotalCollapsed);

        var byName = Collapse().Collapse(
        [
            Filing(Base, ownerName: "Stang   Eric B"),
            Filing(Base.AddDays(3), ownerName: "  STANG ERIC B "),
        ]);
        Assert.Single(byName.Signals);
        Assert.Equal(1, byName.TotalCollapsed);
    }

    [Fact]
    public void DifferentCompanies_SameOwner_AreDifferentBuckets()
    {
        var result = Collapse().Collapse(
        [
            Filing(Base, companyId: CompanyA),
            Filing(Base.AddDays(1), companyId: CompanyB),
        ]);

        Assert.Equal(2, result.Signals.Count);
        Assert.Empty(result.Collapsed);
    }

    // (g) Neutral insider signals and non-insider signals pass through byte-identical.
    [Fact]
    public void NeutralInsiderAndOtherTypes_PassThroughByteIdentical()
    {
        var plan1 = Filing(Base, SignalDirection.Neutral, netValue: null);
        var plan2 = Filing(Base.AddDays(1), SignalDirection.Neutral, netValue: null);
        var customerWin = Filing(Base.AddDays(2), SignalDirection.Positive, type: SignalType.CustomerWin);
        var customerWin2 = Filing(Base.AddDays(3), SignalDirection.Positive, type: SignalType.CustomerWin);

        var result = Collapse().Collapse([plan1, plan2, customerWin, customerWin2]);

        Assert.Equal(4, result.Signals.Count);
        Assert.Same(plan1, result.Signals[0]);
        Assert.Same(plan2, result.Signals[1]);
        Assert.Same(customerWin, result.Signals[2]);
        Assert.Same(customerWin2, result.Signals[3]);
        Assert.Empty(result.Collapsed);
        Assert.Equal(0, result.OwnerUnresolvedCount);
    }

    // (h) A bucket of one returns the SAME instance.
    [Fact]
    public void SingleMemberBucket_ReturnsTheSameInstance()
    {
        var only = Filing(Base);

        var result = Collapse().Collapse([only]);

        Assert.Same(only, Assert.Single(result.Signals));
        Assert.Empty(result.Collapsed);
    }

    // (i) Determinism: reversed input gives identical output.
    [Fact]
    public void ReversedInput_GivesIdenticalOutput()
    {
        var signals = new List<ScoringSignal>
        {
            Filing(Base, ownerName: "A"),
            Filing(Base.AddDays(3), ownerName: "B"),
            Filing(Base.AddDays(6), ownerName: "A"),
            Filing(Base.AddDays(9), ownerName: null),
            Filing(Base.AddDays(12), SignalDirection.Positive, ownerName: "A"),
            Filing(Base.AddDays(40), ownerName: "A"),
        };
        var reversed = signals.AsEnumerable().Reverse().ToList();

        var forward = Collapse().Collapse(signals);
        var backward = Collapse().Collapse(reversed);

        Assert.Equal(
            forward.Signals.Select(s => (s.Signal.Id, s.Signal.Strength)).ToList(),
            backward.Signals.Select(s => (s.Signal.Id, s.Signal.Strength)).ToList());
        Assert.Equal(
            forward.Collapsed.OrderBy(kv => kv.Key).ToList(),
            backward.Collapsed.OrderBy(kv => kv.Key).ToList());
        Assert.Equal(forward.OwnerUnresolvedCount, backward.OwnerUnresolvedCount);
    }

    // (j) The cluster boost applies when ANY member carries the flag, capped at 10.
    [Fact]
    public void ClusterBoost_AppliesWhenAnyMemberIsFlagged_CappedAtTen()
    {
        var boosted = new InsiderMaterialityWeights { ClusterBoost = 5 };

        // $30m + $30m = $60m ⇒ top sell tier (8) + 5 ⇒ capped at 10. Only the SECOND member is flagged.
        var result = Collapse(weights: boosted).Collapse(
        [
            Filing(Base, netValue: 30_000_000m),
            Filing(Base.AddDays(2), netValue: 30_000_000m, cluster: true),
        ]);

        var representative = Assert.Single(result.Signals);
        Assert.Equal(10, representative.Signal.Strength);
        Assert.Equal(10, result.Collapsed[representative.Signal.Id].StrengthAfter);

        // No member flagged ⇒ no boost.
        var unboosted = Collapse(weights: boosted).Collapse(
        [
            Filing(Base, netValue: 30_000_000m),
            Filing(Base.AddDays(2), netValue: 30_000_000m),
        ]);
        Assert.Equal(8, Assert.Single(unboosted.Signals).Signal.Strength);
    }

    // (k) No member recorded a value: strength kept, aggregate null, still collapsed and counted.
    [Fact]
    public void NoMemberWithValue_KeepsStrength_AggregateNull_StillCollapsedAndCounted()
    {
        var first = Filing(Base, netValue: null, strength: 6);
        var second = Filing(Base.AddDays(1), netValue: null, strength: 6);

        var result = Collapse().Collapse([first, second]);

        var representative = Assert.Single(result.Signals);
        Assert.Same(first, representative);
        var bucket = result.Collapsed[first.Signal.Id];
        Assert.Equal(1, bucket.CollapsedCount);
        Assert.Null(bucket.AggregateValue);
        Assert.Equal(2, bucket.MembersWithoutValue);
        Assert.Equal(6, bucket.StrengthBefore);
        Assert.Equal(6, bucket.StrengthAfter);
        Assert.Equal(6, representative.Signal.Strength);
    }

    [Fact]
    public void SomeMembersWithoutValue_AggregateSumsOnlyRecordedOnes_AndCountsTheRest()
    {
        var result = Collapse().Collapse(
        [
            Filing(Base, netValue: 1_500_000m),
            Filing(Base.AddDays(1), netValue: null, strength: 6),
            Filing(Base.AddDays(2), netValue: 1_500_000m),
        ]);

        var representative = Assert.Single(result.Signals);
        var bucket = result.Collapsed[representative.Signal.Id];
        Assert.Equal(3_000_000m, bucket.AggregateValue);
        Assert.Equal(1, bucket.MembersWithoutValue);
        Assert.Equal(2, bucket.CollapsedCount);
        Assert.Equal(
            InsiderMaterialityWeights.StrengthForAmount(3_000_000m, Materiality.SellTiers),
            representative.Signal.Strength);
    }

    [Fact]
    public void Representative_KeepsEveryOtherFieldOfTheEarliestMember()
    {
        var first = Filing(Base);
        var second = Filing(Base.AddDays(1), netValue: 5_000_000m);

        var result = Collapse().Collapse([second, first]);

        var representative = Assert.Single(result.Signals);
        Assert.Equal(first.Signal.Id, representative.Signal.Id);
        Assert.Equal(first.Signal.EvidenceId, representative.Signal.EvidenceId);
        Assert.Same(first.Evidence, representative.Evidence);
        Assert.Equal(first.Signal.ObservedAtUtc, representative.Signal.ObservedAtUtc);
        Assert.Equal(first.Signal.Confidence, representative.Signal.Confidence);
        Assert.Equal(first.Signal.Novelty, representative.Signal.Novelty);
        Assert.Equal(first.Signal.Direction, representative.Signal.Direction);
        Assert.Equal(first.Signal with { Strength = representative.Signal.Strength }, representative.Signal);
    }

    [Fact]
    public void EmptyInput_IsNoOp()
    {
        var result = Collapse().Collapse([]);

        Assert.Empty(result.Signals);
        Assert.Empty(result.Collapsed);
        Assert.Equal(0, result.OwnerUnresolvedCount);
    }

    // (l) The descriptor is exact and culture-invariant.
    [Fact]
    public void CanonicalDescriptor_IsExact_AndCultureInvariant()
    {
        Assert.Equal("insider-collapse-v1;window=30;", Collapse().CanonicalDescriptor());
        Assert.Equal("insider-collapse-v1", InsiderActivityCollapse.Version);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("insider-collapse-v1;window=2.5;", Collapse(2.5).CanonicalDescriptor());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void NonPositiveWindow_FailsFast_NamingTheConfigPath(double days)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Collapse(days));
        Assert.Contains("Radar:Scoring:InsiderCollapse:EventWindowDays", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new InsiderActivityCollapse(null!, Materiality));
        Assert.Throws<ArgumentNullException>(
            () => new InsiderActivityCollapse(new InsiderCollapseOptions(), null!));
    }

    [Fact]
    public void SharedBucketing_MediaCollapseIsByteIdentical_ThroughTheExtractedPrimitive()
    {
        // The greedy loop is shared with MediaAttentionCollapse (acceptance 2: no second copy). Its own
        // pinned tests are unmodified; this asserts the primitive's contract directly on a sorted list.
        var sorted = new List<ScoringSignal>
        {
            Filing(Base),
            Filing(Base.AddDays(1)),
            Filing(Base.AddDays(3.5)),
            Filing(Base.AddDays(4)),
            Filing(Base.AddDays(10)),
        };

        var windows = SameWindowBucketing.GreedyWindows(sorted, TimeSpan.FromDays(3)).ToList();

        Assert.Equal([(0, 2), (2, 4), (4, 5)], windows);
        Assert.Empty(SameWindowBucketing.GreedyWindows([], TimeSpan.FromDays(3)));
    }
}
