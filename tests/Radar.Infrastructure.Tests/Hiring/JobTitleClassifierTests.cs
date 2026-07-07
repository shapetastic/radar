using Radar.Infrastructure.Hiring;

namespace Radar.Infrastructure.Tests.Hiring;

public sealed class JobTitleClassifierTests
{
    [Theory]
    [InlineData("VP, Strategic Partnerships")]
    [InlineData("vice president of sales")]
    [InlineData("CHIEF Financial Officer")]
    [InlineData("Head of Talent")]
    [InlineData("Sales director, EMEA")]
    [InlineData("principal Consultant")]
    public void Classify_SeniorKeyword_CountsTowardSenior_CaseInsensitively(string title)
    {
        var (senior, _) = JobTitleClassifier.Classify([title]);

        Assert.Equal(1, senior);
    }

    [Theory]
    [InlineData("Software ENGINEER II")]
    [InlineData("Manager, engineering Operations")]
    [InlineData("r&d Technician")]
    [InlineData("Market research Analyst")]
    [InlineData("Staff scientist")]
    public void Classify_EngineeringKeyword_CountsTowardEngineering_CaseInsensitively(string title)
    {
        var (_, engineering) = JobTitleClassifier.Classify([title]);

        Assert.Equal(1, engineering);
    }

    [Fact]
    public void Classify_TitleMatchingBothSets_CountsTowardBothBuckets()
    {
        // The buckets are independent tallies, not a partition.
        var (senior, engineering) = JobTitleClassifier.Classify(["VP of Engineering"]);

        Assert.Equal(1, senior);
        Assert.Equal(1, engineering);
    }

    [Fact]
    public void Classify_TitleMatchingNeitherSet_CountsTowardNeitherBucket()
    {
        var (senior, engineering) = JobTitleClassifier.Classify(["Account Executive"]);

        Assert.Equal(0, senior);
        Assert.Equal(0, engineering);
    }

    [Fact]
    public void Classify_EmptyList_YieldsZeroZero()
    {
        var (senior, engineering) = JobTitleClassifier.Classify([]);

        Assert.Equal(0, senior);
        Assert.Equal(0, engineering);
    }

    [Fact]
    public void Classify_MixedList_TalliesIndependently()
    {
        var (senior, engineering) = JobTitleClassifier.Classify(
        [
            "VP of Engineering",          // both
            "Senior Software Engineer",   // engineering only ("Senior" alone is not a senior keyword)
            "Director of Marketing",      // senior only
            "Account Executive",          // neither
        ]);

        Assert.Equal(2, senior);
        Assert.Equal(2, engineering);
    }
}
