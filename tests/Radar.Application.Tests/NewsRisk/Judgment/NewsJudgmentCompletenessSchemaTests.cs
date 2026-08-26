using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 189 §2: the persisted typing-completeness VOCABULARY widened, so the judgment RECORD tag moved to
/// <c>news-judgment-v3</c> — and NOTHING else did. Typing completeness is run provenance the judge never
/// sees, so widening it must not fork a stage-2 cohort, change a prompt, change a result schema or
/// invalidate a cached verdict. Pinned here so a future edit to either side is a conscious act.
/// </summary>
public sealed class NewsJudgmentCompletenessSchemaTests
{
    [Fact]
    public void OnlyTheRecordTagMoved_TheContractAndTheCohortKeyDidNot()
    {
        Assert.Equal("news-judgment-v3", NewsJudgmentRecord.CurrentSchemaVersion);

        // The judge's own contract is UNCHANGED by spec 189 — the model request is byte-identical.
        Assert.Equal("news-judgment-prompt-v2", NewsJudgmentContract.PromptVersion);
        Assert.Equal("news-judgment-schema-v2", NewsJudgmentContract.SchemaVersion);

        // …and so is the stage-2 cohort key it composes, asserted against the literal composition rather
        // than against itself: the record tag is deliberately NOT one of its inputs.
        const string Stage1 = "openai:extractor-model|p|s|news-event-taxonomy-v1";
        var cohortKey = NewsJudgmentContract.CohortKey("openai", "judge-model", Stage1);

        Assert.Equal(
            "openai:judge-model|news-judgment-prompt-v2|news-judgment-schema-v2|"
                + $"stage1={Stage1}|families={FactFamilyBuilder.IdentityString}",
            cohortKey);
        Assert.DoesNotContain("news-judgment-v3", cohortKey, StringComparison.Ordinal);
        Assert.DoesNotContain("news-judgment-v2", cohortKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec 189 §2: the two new values are APPENDED, so the existing ordinals are frozen and the ZERO value
    /// stays the degraded one (the spec-182 convention). Persistence is token-based, so nothing depends on
    /// the numbers — but a reordering would silently re-mean every defensively-defaulted value in memory.
    /// </summary>
    [Fact]
    public void TheCompletenessOrdinals_AreFrozen_AndZeroStaysTheDegradedValue()
    {
        Assert.Equal(0, (int)NewsTypingCompleteness.Failed);
        Assert.Equal(1, (int)NewsTypingCompleteness.Backlog);
        Assert.Equal(2, (int)NewsTypingCompleteness.Complete);
        Assert.Equal(3, (int)NewsTypingCompleteness.RetryableFailure);
        Assert.Equal(4, (int)NewsTypingCompleteness.RetryExhausted);

        Assert.Equal(NewsTypingCompleteness.Failed, default(NewsTypingCompleteness));
        Assert.NotEqual(NewsTypingCompleteness.Complete, default(NewsTypingCompleteness));

        Assert.Equal(
            [
                NewsTypingCompleteness.Failed,
                NewsTypingCompleteness.Backlog,
                NewsTypingCompleteness.Complete,
                NewsTypingCompleteness.RetryableFailure,
                NewsTypingCompleteness.RetryExhausted,
            ],
            Enum.GetValues<NewsTypingCompleteness>());
    }
}
