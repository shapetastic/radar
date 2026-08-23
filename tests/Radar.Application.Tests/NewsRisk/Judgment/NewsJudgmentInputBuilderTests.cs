using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 185 §1/§3/§5 — deterministic judge-input assembly: family selection/ordering, the family cap with
/// its recorded Capped dimension, the representative-fact join, and the ordered family-set hash the
/// judgment cache keys on. Plus the cohort-key composition: a stage-1 change forks a new stage-2 cohort by
/// construction.
/// </summary>
public sealed class NewsJudgmentInputBuilderTests
{
    private static readonly Guid Company = Guid.NewGuid();

    private static (FactFamilyRecord Family, NewsTypingFactRef Fact) FamilyWithFact(
        string statement, int memberCount = 1)
    {
        var factId = Guid.NewGuid();
        return (
            NewsJudgmentTestData.FamilyRecord(Company, factId, statement, memberCount),
            NewsJudgmentTestData.FactRef(Company, factId, statement));
    }

    [Fact]
    public void Build_JoinsRepresentativeFacts_AndOrdersByMemberCountDescThenFamilyId()
    {
        var (small, smallFact) = FamilyWithFact("Company faces one small claim.", memberCount: 1);
        var (big, bigFact) = FamilyWithFact("Company faces a large syndicated claim.", memberCount: 40);
        var facts = new Dictionary<Guid, NewsTypingFactRef>
        {
            [smallFact.Fact.FactId] = smallFact,
            [bigFact.Fact.FactId] = bigFact,
        };

        var bundle = NewsJudgmentInputBuilder.Build(Company, [small, big], facts, 50);

        Assert.Equal(2, bundle.Families.Count);
        Assert.Equal(big.RepresentativeFactId, bundle.Families[0].RepresentativeFactId);
        Assert.Equal(40, bundle.Families[0].MemberCount);
        Assert.Equal(NewsJudgmentFamilyBundle.Complete, bundle.FamilyBundle);
        Assert.Equal(2, bundle.FamiliesAvailable);
    }

    [Fact]
    public void FamilyCap_TruncatesDeterministically_AndRecordsCapped()
    {
        var pairs = Enumerable.Range(0, 5)
            .Select(i => FamilyWithFact($"Distinct claim number {i} about the company.", memberCount: i + 1))
            .ToList();
        var facts = pairs.ToDictionary(p => p.Fact.Fact.FactId, p => p.Fact);

        var bundle = NewsJudgmentInputBuilder.Build(
            Company, pairs.Select(p => p.Family).ToList(), facts, maxFamiliesPerJudgment: 2);

        Assert.Equal(2, bundle.Families.Count);
        Assert.Equal(NewsJudgmentFamilyBundle.Capped, bundle.FamilyBundle);
        Assert.Equal(5, bundle.FamiliesAvailable);
        // Biggest families survive the cap (MemberCount desc).
        Assert.Equal(5, bundle.Families[0].MemberCount);
        Assert.Equal(4, bundle.Families[1].MemberCount);
    }

    [Fact]
    public void AnotherCompanysFamilies_AreNeverSupplied()
    {
        var (mine, myFact) = FamilyWithFact("A claim about this company.");
        var otherCompany = Guid.NewGuid();
        var otherFactId = Guid.NewGuid();
        var other = NewsJudgmentTestData.FamilyRecord(otherCompany, otherFactId, "Another company's claim.");
        var facts = new Dictionary<Guid, NewsTypingFactRef>
        {
            [myFact.Fact.FactId] = myFact,
            [otherFactId] = NewsJudgmentTestData.FactRef(otherCompany, otherFactId, "Another company's claim."),
        };

        var bundle = NewsJudgmentInputBuilder.Build(Company, [mine, other], facts, 50);

        Assert.Equal(mine.FamilyId, Assert.Single(bundle.Families).FamilyId);
    }

    [Fact]
    public void FamilySetHash_IsStableForIdenticalInput_AndMovesWithContent()
    {
        var (family, fact) = FamilyWithFact("Company faces securities-fraud scrutiny.");
        var facts = new Dictionary<Guid, NewsTypingFactRef> { [fact.Fact.FactId] = fact };

        var first = NewsJudgmentInputBuilder.Build(Company, [family], facts, 50);
        var second = NewsJudgmentInputBuilder.Build(Company, [family], facts, 50);
        Assert.Equal(first.FamilySetHash, second.FamilySetHash);

        // A grown family (more syndication) is a DIFFERENT cache entry — never a silent reuse.
        var grown = NewsJudgmentInputBuilder.Build(
            Company, [family with { MemberCount = 7 }], facts, 50);
        Assert.NotEqual(first.FamilySetHash, grown.FamilySetHash);

        // Citations are part of what the judge sees, so a citations-only change moves the hash too.
        var supplied = Assert.Single(first.Families);
        var recited = supplied with { Citations = [.. supplied.Citations, "an added citation"] };
        Assert.NotEqual(
            NewsJudgmentInputBuilder.ComputeFamilySetHash([supplied]),
            NewsJudgmentInputBuilder.ComputeFamilySetHash([recited]));
    }

    [Fact]
    public void ZeroFamilies_ProducesAnEmptyBundle_NeverAnInventedOne()
    {
        var bundle = NewsJudgmentInputBuilder.Build(
            Company, [], new Dictionary<Guid, NewsTypingFactRef>(), 50);

        Assert.Empty(bundle.Families);
        Assert.Equal(0, bundle.FamiliesAvailable);
        Assert.Equal(NewsJudgmentFamilyBundle.Complete, bundle.FamilyBundle);
    }

    [Fact]
    public void Stage2CohortKey_ComposesJudgeContractStage1CohortAndFamilyBuilderIdentity()
    {
        var stage1 = NewsTypingContract.CohortKey("openai", "deepseek-ai/DeepSeek-V4-Flash");
        var key = NewsJudgmentContract.CohortKey("openai", "judge-model", stage1);

        Assert.StartsWith("openai:judge-model|news-judgment-prompt-v1|news-judgment-schema-v1|", key);
        Assert.Contains("stage1=" + stage1, key);
        Assert.Contains("families=" + FactFamilyBuilder.IdentityString, key);
        // The stage-1 cohort key carries the extractor model, prompt/schema AND taxonomy version — so a
        // stage-1 change of any of them forks a NEW stage-2 cohort by construction.
        Assert.Contains(NewsTypingContract.TaxonomyVersion, key);
        Assert.NotEqual(
            key,
            NewsJudgmentContract.CohortKey(
                "openai", "judge-model", NewsTypingContract.CohortKey("ollama", "llama3.1")));
    }
}
