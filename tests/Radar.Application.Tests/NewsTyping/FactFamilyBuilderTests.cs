using Radar.Application.News;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsTyping;

/// <summary>
/// Spec 181 §4 — the mandatory pinned fixtures: syndicated duplicates collapse to one family; unrelated
/// same-day stories do NOT merge; contradictory quantities do NOT merge; a rerun is byte-deterministic; a
/// later-arriving member joins the EXISTING family id. Plus the identity-string pin: every membership-shaping
/// parameter is part of <c>fact-family-v1</c>'s identity — changing one is <c>fact-family-v2</c>.
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
            "fact-family-v1|normalization=statement-normalization-v1|similarity=token-set-jaccard"
                + "|threshold=0.6|temporalWindowDays=7",
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
                Company, NewsObservationCaptureMode.ProspectiveRss, family.CanonicalClaimKey),
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
