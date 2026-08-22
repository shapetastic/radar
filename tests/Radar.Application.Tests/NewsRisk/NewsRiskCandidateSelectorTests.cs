using Radar.Application.NewsRisk;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// Spec 179 §3: deterministic candidate traversal — primary then Research order, five rows per arm,
/// deduped by company while retaining every selecting strategy/rank/snapshot, capped by the cost budget;
/// Comparators never enter, and no consensus/merged rank of any kind is created.
/// </summary>
public sealed class NewsRiskCandidateSelectorTests
{
    [Fact]
    public void Traversal_IsPrimaryFirst_ThenResearchOrder_FiveRowsPerSection()
    {
        var primaryCompanies = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        var secondCompanies = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();

        var sections = new[]
        {
            // The primary appears AFTER the other section in the list, but must still be traversed first.
            NewsRiskTestData.Section(
                "arm-b", isPrimary: false, StrategyPurpose.Research,
                secondCompanies.Select((id, i) => NewsRiskTestData.Row(i + 1, id, $"B{i}")).ToArray()),
            NewsRiskTestData.Section(
                "default", isPrimary: true, StrategyPurpose.Research,
                primaryCompanies.Select((id, i) => NewsRiskTestData.Row(i + 1, id, $"P{i}")).ToArray()),
        };

        var candidates = NewsRiskCandidateSelector.Select(sections, maxCompaniesPerRun: 30);

        // Five (not seven) from the primary, then the second arm's three, in rank order.
        Assert.Equal(8, candidates.Count);
        Assert.Equal(primaryCompanies.Take(5), candidates.Take(5).Select(c => c.CompanyId));
        Assert.Equal(secondCompanies, candidates.Skip(5).Select(c => c.CompanyId));
    }

    [Fact]
    public void Comparators_NeverEnter()
    {
        var researchCompany = Guid.NewGuid();
        var comparatorCompany = Guid.NewGuid();
        var sections = new[]
        {
            NewsRiskTestData.Section(
                "default", isPrimary: true, StrategyPurpose.Research,
                NewsRiskTestData.Row(1, researchCompany, "R")),
            NewsRiskTestData.Section(
                "baseline-activity-only", isPrimary: false, StrategyPurpose.Comparator,
                NewsRiskTestData.Row(1, comparatorCompany, "C")),
        };

        var candidates = NewsRiskCandidateSelector.Select(sections, maxCompaniesPerRun: 30);

        var candidate = Assert.Single(candidates);
        Assert.Equal(researchCompany, candidate.CompanyId);
    }

    [Fact]
    public void Dedupe_RetainsEverySelectingStrategyRankAndSnapshot_WithoutAnyConsensusArtifact()
    {
        var shared = Guid.NewGuid();
        var primaryRow = NewsRiskTestData.Row(2, shared, "Shared");
        var secondRow = NewsRiskTestData.Row(4, shared, "Shared");
        var sections = new[]
        {
            NewsRiskTestData.Section(
                "default", isPrimary: true, StrategyPurpose.Research,
                NewsRiskTestData.Row(1, Guid.NewGuid(), "A"), primaryRow),
            NewsRiskTestData.Section(
                "arm-b", isPrimary: false, StrategyPurpose.Research, secondRow),
        };

        var candidates = NewsRiskCandidateSelector.Select(sections, maxCompaniesPerRun: 30);

        var sharedCandidate = Assert.Single(candidates, c => c.CompanyId == shared);
        // Every selection fact is retained — shared ancestry, not corroboration; there is no count,
        // average rank, Borda score or any other merged artifact on the type at all.
        Assert.Equal(2, sharedCandidate.Selections.Count);
        Assert.Contains(sharedCandidate.Selections,
            s => s.StrategyName == "default" && s.Rank == 2 && s.ScoreSnapshotId == primaryRow.ScoreSnapshotId);
        Assert.Contains(sharedCandidate.Selections,
            s => s.StrategyName == "arm-b" && s.Rank == 4 && s.ScoreSnapshotId == secondRow.ScoreSnapshotId);
    }

    [Fact]
    public void Budget_CapsNewCompanies_InTraversalOrder_ButStillAttachesProvenanceToAdmittedOnes()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var sections = new[]
        {
            NewsRiskTestData.Section(
                "default", isPrimary: true, StrategyPurpose.Research,
                NewsRiskTestData.Row(1, first, "A"), NewsRiskTestData.Row(2, second, "B")),
            NewsRiskTestData.Section(
                "arm-b", isPrimary: false, StrategyPurpose.Research,
                NewsRiskTestData.Row(1, third, "C"), NewsRiskTestData.Row(2, first, "A")),
        };

        var candidates = NewsRiskCandidateSelector.Select(sections, maxCompaniesPerRun: 2);

        Assert.Equal([first, second], candidates.Select(c => c.CompanyId));
        // Third never entered (budget), but the already-admitted first still gained arm-b's provenance.
        Assert.Equal(2, candidates[0].Selections.Count);
    }

    [Fact]
    public void Selection_IsDeterministic()
    {
        var sections = new[]
        {
            NewsRiskTestData.Section(
                "default", isPrimary: true, StrategyPurpose.Research,
                NewsRiskTestData.Row(1, Guid.NewGuid(), "A"), NewsRiskTestData.Row(2, Guid.NewGuid(), "B")),
        };

        var first = NewsRiskCandidateSelector.Select(sections, 30);
        var second = NewsRiskCandidateSelector.Select(sections, 30);

        Assert.Equal(first.Select(c => c.CompanyId), second.Select(c => c.CompanyId));
        Assert.Equal(first.SelectMany(c => c.Selections), second.SelectMany(c => c.Selections));
    }
}
