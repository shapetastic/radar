using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// The judgment record tag, the judge CONTRACT versions and the stage-2 cohort key, pinned together so a
/// move to any of them is a conscious act.
/// <para>
/// Spec 189 §2 moved the RECORD tag alone (the persisted typing-completeness vocabulary widened) and
/// deliberately moved neither contract version nor the cohort key — typing completeness is run provenance
/// the judge never sees. Spec 192 §2 moved NOTHING (two appended trailing nullable fields).
/// <b>Spec 197 §2.2 moves BOTH sides, deliberately and for different reasons</b>: the FactId grammar the
/// validator accepts is part of the result contract, so <c>prompt-v3</c>/<c>schema-v3</c> fork the stage-2
/// cohort (earning the accrued v2 validation failures a fresh budget and guaranteeing no v2 attempt is
/// reused as a v3 one), and the record gained <c>FactIdPrefixExpansionCount</c>, whose presence changes what
/// a persisted citation set MEANS — hence <c>news-judgment-v4</c>.
/// </para>
/// </summary>
public sealed class NewsJudgmentCompletenessSchemaTests
{
    [Fact]
    public void TheRecordTagAndTheContractVersions_ArePinned_AndTheTagIsNeverACohortKeyInput()
    {
        Assert.Equal("news-judgment-v4", NewsJudgmentRecord.CurrentSchemaVersion);
        Assert.Equal("news-judgment-prompt-v3", NewsJudgmentContract.PromptVersion);
        Assert.Equal("news-judgment-schema-v3", NewsJudgmentContract.SchemaVersion);

        // The stage-2 cohort key, asserted against the literal composition rather than against itself: the
        // record tag is deliberately NOT one of its inputs, so widening a persisted field can never fork a
        // cohort or invalidate a cached verdict.
        const string Stage1 = "openai:extractor-model|p|s|news-event-taxonomy-v1";
        var cohortKey = NewsJudgmentContract.CohortKey("openai", "judge-model", Stage1);

        Assert.Equal(
            "openai:judge-model|news-judgment-prompt-v3|news-judgment-schema-v3|"
                + $"stage1={Stage1}|families={FactFamilyBuilder.IdentityString}",
            cohortKey);
        Assert.DoesNotContain("news-judgment-v4", cohortKey, StringComparison.Ordinal);
        Assert.DoesNotContain("news-judgment-v3", cohortKey, StringComparison.Ordinal);
        Assert.DoesNotContain("news-judgment-v2", cohortKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec 192 §2's two fields, and spec 197 §2.2's one, are all TRAILING and NULLABLE — so every pre-192
    /// and pre-197 file on disk hydrates losslessly with "not recorded" rather than a fabricated 0/false
    /// (AD-8). The ORDER matters as much as the nullability: an appended field must never displace an
    /// existing positional parameter.
    /// </summary>
    [Fact]
    public void TheAppendedProvenanceFields_AreTrailingAndNullable_SoOldFilesHydrateAsNotRecorded()
    {
        var trailing = typeof(NewsJudgmentRecord)
            .GetConstructors()
            .Single()
            .GetParameters()
            .TakeLast(3)
            .ToList();
        Assert.Equal(
            [
                nameof(NewsJudgmentRecord.RationaleLength),
                nameof(NewsJudgmentRecord.RationaleOverSoftLimit),
                nameof(NewsJudgmentRecord.FactIdPrefixExpansionCount),
            ],
            trailing.Select(p => p.Name).ToList());
        Assert.All(trailing, p => Assert.True(p.IsOptional));
        Assert.All(trailing, p => Assert.Null(p.DefaultValue));
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
