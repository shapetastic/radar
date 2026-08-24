using Radar.Application.News;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsTyping;

/// <summary>
/// Spec 181 §4's mandatory pinned fixtures, RE-PINNED under <c>fact-family-v2</c> (spec 186 §4): syndicated
/// duplicates collapse to one family; unrelated same-day stories do NOT merge; contradictory quantities do
/// NOT merge; a rerun is byte-deterministic; a later-arriving member joins the EXISTING family id. Plus the
/// identity-string pin: every membership-, identity- and projection-shaping parameter is part of
/// <c>fact-family-v2</c>'s identity — changing one is <c>fact-family-v3</c>.
/// <para>
/// And the spec-186 §4 additions: temporally separate episodes get DISTINCT ids; an episode keeps its id
/// when its earliest member ages out of the checkpoint window; disjoint event types never collide; the
/// stage-2 projection carries an IN-WINDOW representative; and membership is byte-compatible with v1.
/// </para>
/// </summary>
public sealed class FactFamilyBuilderTests
{
    private static readonly Guid Company = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Day1 = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private static Guid FactId(int n) => new($"bbbbbbbb-0000-0000-0000-{n:D12}");

    [Fact]
    public void BuilderIdentityString_IsPinned()
    {
        Assert.Equal(
            "fact-family-v2|normalization=statement-normalization-v1|similarity=token-set-jaccard"
                + "|threshold=0.6|temporalWindowDays=7|segmentation=full-history"
                + "|anchor=first-member-utc-date+event-types|projection=window-members",
            FactFamilyBuilder.IdentityString);
    }

    [Fact]
    public void SyndicatedDuplicateVariants_CollapseToOneFamily()
    {
        // The live EOSE shape: two near-identical legal-scrutiny headlines from different outlets.
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(
                FactId(1), Company,
                "Eos Energy faces legal scrutiny after investor complaint filed",
                Day1, publisher: "StocksToTrade"),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company,
                "Eos Energy faces legal scrutiny after an investor complaint was filed",
                Day1.AddHours(5), publisher: "MarketBeat"),
        };

        var families = FactFamilyBuilder.Build(facts);

        var family = Assert.Single(families);
        Assert.Equal(2, family.MemberCount);
        Assert.Equal([FactId(1), FactId(2)], family.MemberFactIds);
        Assert.Equal(FactId(1), family.RepresentativeFactId);
        Assert.Equal(2, family.DistinctPublisherCount);
    }

    [Fact]
    public void UnrelatedSameDayStories_DoNotMerge()
    {
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(
                FactId(1), Company, "Eos Energy faces legal scrutiny after investor complaint", Day1),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company, "Eos Energy announces new battery production milestone", Day1,
                eventTypes: [NewsEventType.ProductOrTechnology]),
        };

        Assert.Equal(2, FactFamilyBuilder.Build(facts).Count);
    }

    [Fact]
    public void ContradictoryQuantities_NeverMerge_HoweverSimilarTheText()
    {
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(
                FactId(1), Company, "Test Co reported a quarterly loss of 5 million dollars", Day1,
                eventTypes: [NewsEventType.EarningsOrGuidance]),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company, "Test Co reported a quarterly loss of 6 million dollars", Day1,
                eventTypes: [NewsEventType.EarningsOrGuidance]),
        };

        Assert.Equal(2, FactFamilyBuilder.Build(facts).Count);
    }

    [Fact]
    public void NegatedVersusAsserted_NeverMerge()
    {
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(
                FactId(1), Company, "Test Co faces an SEC investigation", Day1),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company, "Test Co denies it faces an SEC investigation", Day1.AddHours(2)),
        };

        Assert.Equal(2, FactFamilyBuilder.Build(facts).Count);
    }

    [Fact]
    public void Rerun_OverIdenticalFacts_IsByteDeterministic()
    {
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(FactId(1), Company, "legal scrutiny after investor complaint filed", Day1),
            NewsTypingTestData.FamilyFact(FactId(2), Company, "legal scrutiny after an investor complaint filed", Day1.AddHours(1)),
            NewsTypingTestData.FamilyFact(FactId(3), Company, "announces new battery production milestone", Day1,
                eventTypes: [NewsEventType.ProductOrTechnology]),
        };

        // Same input in a DIFFERENT order must also produce the identical family set — asserted at the
        // byte level via serialization, which is exactly what a persisted snapshot would carry.
        var first = System.Text.Json.JsonSerializer.Serialize(FactFamilyBuilder.Build(facts));
        var second = System.Text.Json.JsonSerializer.Serialize(
            FactFamilyBuilder.Build([facts[2], facts[1], facts[0]]));

        Assert.Equal(first, second);
    }

    [Fact]
    public void LaterArrivingMember_JoinsTheExistingFamilyId_InTheNextSnapshot()
    {
        var original = new[]
        {
            NewsTypingTestData.FamilyFact(FactId(1), Company, "faces legal scrutiny after investor complaint filed", Day1),
            NewsTypingTestData.FamilyFact(FactId(2), Company, "faces legal scrutiny after an investor complaint filed", Day1.AddHours(1)),
        };
        var withLateArrival = original.Append(
            NewsTypingTestData.FamilyFact(
                FactId(3), Company, "faces legal scrutiny after investor complaint being filed",
                Day1.AddDays(2), publisher: "Third Outlet"))
            .ToArray();

        var checkpoint1 = Assert.Single(FactFamilyBuilder.Build(original));
        var checkpoint2 = Assert.Single(FactFamilyBuilder.Build(withLateArrival));

        // Same id (representative unchanged — earliest member), grown membership; checkpoint1's record is a
        // separate immutable value, untouched by construction.
        Assert.Equal(checkpoint1.FamilyId, checkpoint2.FamilyId);
        Assert.Equal(2, checkpoint1.MemberCount);
        Assert.Equal(3, checkpoint2.MemberCount);
        Assert.Contains(FactId(3), checkpoint2.MemberFactIds);
    }

    [Fact]
    public void FamilyId_DerivesFromTheCanonicalClaimKey_NotTheMemberList()
    {
        var family = Assert.Single(FactFamilyBuilder.Build(
        [
            NewsTypingTestData.FamilyFact(FactId(1), Company, "Faces legal scrutiny, complaint filed!", Day1),
        ]));

        Assert.Equal("faces legal scrutiny complaint filed", family.CanonicalClaimKey);
        Assert.Equal(
            FactFamilyBuilder.FamilyIdFor(
                Company,
                NewsObservationCaptureMode.ProspectiveRss,
                new DateOnly(2026, 8, 20),
                [NewsEventType.RegulatoryOrLegal],
                family.CanonicalClaimKey),
            family.FamilyId);
    }

    [Fact]
    public void FactsOutsideTheTemporalWindow_DoNotJoin()
    {
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(FactId(1), Company, "faces legal scrutiny after complaint filed", Day1),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company, "faces legal scrutiny after complaint filed", Day1.AddDays(8)),
        };

        Assert.Equal(2, FactFamilyBuilder.Build(facts).Count);
    }

    [Fact]
    public void CaptureModes_NeverPoolInOneFamily()
    {
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(FactId(1), Company, "faces legal scrutiny after complaint filed", Day1),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company, "faces legal scrutiny after complaint filed", Day1.AddHours(1),
                captureMode: NewsObservationCaptureMode.LegacyHeadlineOnly),
        };

        var families = FactFamilyBuilder.Build(facts);

        Assert.Equal(2, families.Count);
        Assert.NotEqual(families[0].FamilyId, families[1].FamilyId);
    }

    // ---- Spec 186 section 4: the temporal anchor, the window projection, membership parity with v1. ----

    [Fact]
    public void RecurringClaim_MoreThanSevenDaysApart_ProducesTwoFamiliesWithDistinctIds()
    {
        // The quarterly dividend/buyback shape: byte-identical normalized statements, months apart. Under
        // fact-family-v1 these two separate EPISODES collided on one id (no temporal component).
        const string Statement = "The board declared a quarterly cash dividend of 25 cents per share";
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(
                FactId(1), Company, Statement, Day1,
                eventTypes: [NewsEventType.DividendOrBuyback]),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company, Statement, Day1.AddDays(91),
                eventTypes: [NewsEventType.DividendOrBuyback]),
        };

        var families = FactFamilyBuilder.Build(facts);

        Assert.Equal(2, families.Count);
        Assert.Equal(families[0].CanonicalClaimKey, families[1].CanonicalClaimKey);
        Assert.NotEqual(families[0].FamilyId, families[1].FamilyId);
    }

    [Fact]
    public void WindowExpiry_KeepsTheSameFamilyId_AndProjectsAnInWindowRepresentative()
    {
        // One episode: anchor on Day1, syndicated follow-up two days later.
        var all = new[]
        {
            NewsTypingTestData.FamilyFact(
                FactId(1), Company, "faces legal scrutiny after investor complaint filed", Day1),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company, "faces legal scrutiny after an investor complaint filed",
                Day1.AddDays(2), publisher: "Second Outlet"),
        };

        // Checkpoint 1: the whole episode is in-window.
        var first = Assert.Single(FactFamilyBuilder.Build(all, Day1.AddDays(-30), Day1.AddDays(3)));

        // Checkpoint 2: the window has rolled PAST the anchor fact — stage 1 still segments the FULL
        // history, so the durable id must not churn, while the projection is window-only.
        var second = Assert.Single(FactFamilyBuilder.Build(all, Day1.AddDays(1), Day1.AddDays(30)));

        Assert.Equal(first.FamilyId, second.FamilyId);
        Assert.Equal(FactId(1), first.RepresentativeFactId);
        Assert.Equal(2, first.MemberCount);

        // Projection: in-window members ALONE — including the representative, so it stays resolvable in
        // this checkpoint's own fact index (and therefore reaches the stage-2 judge).
        Assert.Equal(FactId(2), second.RepresentativeFactId);
        Assert.Equal([FactId(2)], second.MemberFactIds);
        Assert.Equal(1, second.MemberCount);
        Assert.Equal(1, second.DistinctPublisherCount);
        Assert.Equal(Day1.AddDays(2), second.EarliestObservedAtUtc);
    }

    [Fact]
    public void AnEpisodeWithNoInWindowMember_IsNotProjectedIntoTheCheckpoint()
    {
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(
                FactId(1), Company, "faces legal scrutiny after complaint filed", Day1),
        };

        Assert.Empty(FactFamilyBuilder.Build(facts, Day1.AddDays(10), Day1.AddDays(40)));
    }

    [Fact]
    public void SameStatement_DisjointEventTypes_NeverShareAnId()
    {
        const string Statement = "The company announced a major restructuring of its operations";
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(
                FactId(1), Company, Statement, Day1, eventTypes: [NewsEventType.RegulatoryOrLegal]),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company, Statement, Day1.AddHours(1),
                eventTypes: [NewsEventType.ProductOrTechnology]),
        };

        var families = FactFamilyBuilder.Build(facts);

        // Same company, capture mode, anchor DATE and canonical claim key — only the anchor's event types
        // differ, and the id must still separate them.
        Assert.Equal(2, families.Count);
        Assert.Equal(families[0].CanonicalClaimKey, families[1].CanonicalClaimKey);
        Assert.NotEqual(families[0].FamilyId, families[1].FamilyId);
    }

    [Fact]
    public void MembershipParity_V2GroupsTheV1FixtureSet_IntoTheSameMemberPartitions()
    {
        // Every membership shape the spec-181 v1 fixtures pin, in ONE set: syndicated duplicates collapse;
        // an unrelated same-day story stays apart; contradictory quantities stay apart; a negation stays
        // apart; a >7-day repeat stays apart; another capture mode never pools. v2 changes identity and
        // projection ONLY — the partitions below are v1's, member for member.
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(
                FactId(1), Company, "Eos Energy faces legal scrutiny after investor complaint filed", Day1),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company, "Eos Energy faces legal scrutiny after an investor complaint was filed",
                Day1.AddHours(5), publisher: "MarketBeat"),
            NewsTypingTestData.FamilyFact(
                FactId(3), Company, "Eos Energy announces new battery production milestone", Day1,
                eventTypes: [NewsEventType.ProductOrTechnology]),
            NewsTypingTestData.FamilyFact(
                FactId(4), Company, "Test Co reported a quarterly loss of 5 million dollars", Day1,
                eventTypes: [NewsEventType.EarningsOrGuidance]),
            NewsTypingTestData.FamilyFact(
                FactId(5), Company, "Test Co reported a quarterly loss of 6 million dollars",
                Day1.AddHours(1), eventTypes: [NewsEventType.EarningsOrGuidance]),
            NewsTypingTestData.FamilyFact(
                FactId(6), Company, "Eos Energy denies it faces legal scrutiny after investor complaint filed",
                Day1.AddHours(2)),
            NewsTypingTestData.FamilyFact(
                FactId(7), Company, "Eos Energy faces legal scrutiny after investor complaint filed",
                Day1.AddDays(9)),
            NewsTypingTestData.FamilyFact(
                FactId(8), Company, "Eos Energy faces legal scrutiny after investor complaint filed",
                Day1.AddHours(3), captureMode: NewsObservationCaptureMode.LegacyHeadlineOnly),
        };

        // The PARTITION is the claim (family-list order is a separate, id-dependent concern), so compare
        // the member sets ordered by their first member.
        static List<List<Guid>> Partitions(IReadOnlyList<FactFamilyRecord> families) => families
            .Select(f => f.MemberFactIds.ToList())
            .OrderBy(m => m[0])
            .ToList();

        var partitions = Partitions(FactFamilyBuilder.Build(facts));

        Assert.Equal(
            [
                [FactId(1), FactId(2)],
                [FactId(3)],
                [FactId(4)],
                [FactId(5)],
                [FactId(6)],
                [FactId(7)],
                [FactId(8)],
            ],
            partitions);

        // The windowed overload must partition identically when the window admits every fact — the two
        // stages differ in PROJECTION, never in membership.
        Assert.Equal(
            partitions, Partitions(FactFamilyBuilder.Build(facts, Day1.AddDays(-1), Day1.AddDays(30))));
    }

    [Fact]
    public void FamilyId_IsIndependentOfTheCheckpointWindow_AndOfTheMemberList()
    {
        var all = new[]
        {
            NewsTypingTestData.FamilyFact(
                FactId(1), Company, "faces legal scrutiny after investor complaint filed", Day1),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company, "faces legal scrutiny after an investor complaint filed",
                Day1.AddDays(2), publisher: "Second Outlet"),
        };

        var expected = FactFamilyBuilder.FamilyIdFor(
            Company,
            NewsObservationCaptureMode.ProspectiveRss,
            new DateOnly(2026, 8, 20),
            [NewsEventType.RegulatoryOrLegal],
            "faces legal scrutiny after investor complaint filed");

        Assert.Equal(
            expected,
            Assert.Single(FactFamilyBuilder.Build(all, Day1.AddDays(-30), Day1.AddDays(3))).FamilyId);
        Assert.Equal(
            expected,
            Assert.Single(FactFamilyBuilder.Build(all, Day1.AddDays(1), Day1.AddDays(30))).FamilyId);
        Assert.Equal(expected, Assert.Single(FactFamilyBuilder.Build([all[0]])).FamilyId);
    }

    [Fact]
    public void WindowedRerun_OverIdenticalFacts_IsByteDeterministic()
    {
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(
                FactId(1), Company, "legal scrutiny after investor complaint filed", Day1),
            NewsTypingTestData.FamilyFact(
                FactId(2), Company, "legal scrutiny after an investor complaint filed", Day1.AddDays(2)),
            NewsTypingTestData.FamilyFact(
                FactId(3), Company, "announces new battery production milestone", Day1.AddDays(3),
                eventTypes: [NewsEventType.ProductOrTechnology]),
        };

        var first = System.Text.Json.JsonSerializer.Serialize(
            FactFamilyBuilder.Build(facts, Day1.AddDays(1), Day1.AddDays(30)));
        var second = System.Text.Json.JsonSerializer.Serialize(
            FactFamilyBuilder.Build([facts[2], facts[0], facts[1]], Day1.AddDays(1), Day1.AddDays(30)));

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentCompanies_NeverShareAFamily()
    {
        var otherCompany = new Guid("aaaaaaaa-0000-0000-0000-000000000002");
        var facts = new[]
        {
            NewsTypingTestData.FamilyFact(FactId(1), Company, "faces legal scrutiny after complaint filed", Day1),
            NewsTypingTestData.FamilyFact(FactId(2), otherCompany, "faces legal scrutiny after complaint filed", Day1),
        };

        Assert.Equal(2, FactFamilyBuilder.Build(facts).Count);
    }
}
