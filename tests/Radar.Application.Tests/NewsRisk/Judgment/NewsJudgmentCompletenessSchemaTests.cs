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
        // Spec 214 §2: the record tag moved to v5 (TrajectoryBasis + per-family ComparisonBasis change what
        // a record MEANS — whether it can become a signal) and the prompt forked to v4 (rule 11). Spec 215
        // §2: the tag moved to v6 (the reference fields; ReferenceSupported widens the basis vocabulary),
        // the prompt forked to v5 (rule 12) and the response schema to v4 (the reference citation lists).
        // Spec 219 §2: the tag moves to v8 (ReadDepth + the family accounting: a v8 record may be a
        // BOUNDED five-family read where every v7 record was a full-budget one, so "no challenge found"
        // means something different). The prompt, the response schema and the cohort key do NOT move — the
        // judge sees exactly the same contract, just fewer families.
        // Spec 220: the tag moves to v9 (FamiliesAvailableByBasis, and a Judged record may now carry accepted
        // findings with a NULL ChallengeStrength — a shape no v8 record could hold). The prompt and the
        // response schema do NOT move; the cohort key DOES, for the family ORDER (ordering=, below) — which
        // decides which facts a bounded judge sees — never for the record tag.
        // Spec 221: the tag moves to v10 (the trajectory vocabulary widened — NoBusinessSignal — and the
        // families were selected business-first); the prompt forks to v7 and the response schema to v5 (the
        // new token); the cohort key moves through those two AND ordering=family-ordering-v3. The tag is
        // still never a cohort-key input.
        Assert.Equal("news-judgment-v10", NewsJudgmentRecord.CurrentSchemaVersion);
        Assert.Equal("news-judgment-prompt-v7", NewsJudgmentContract.PromptVersion);
        Assert.Equal("news-judgment-schema-v5", NewsJudgmentContract.SchemaVersion);
        Assert.Equal("family-ordering-v3", NewsJudgmentFamilyOrdering.Version);

        // The stage-2 cohort key, asserted against the literal composition rather than against itself: the
        // record tag is deliberately NOT one of its inputs, so widening a persisted field can never fork a
        // cohort or invalidate a cached verdict.
        const string Stage1 = "openai:extractor-model|p|s|news-event-taxonomy-v1";
        var cohortKey = NewsJudgmentContract.CohortKey("openai", "judge-model", Stage1);

        Assert.Equal(
            "openai:judge-model|news-judgment-prompt-v7|news-judgment-schema-v5|"
                + $"stage1={Stage1}|families={FactFamilyBuilder.IdentityString}"
                + $"|comparison={StatementComparisonClassifier.Version}"
                + $"|references={ReferenceValueProjector.Version}"
                + "|ordering=family-ordering-v3",
            cohortKey);
        Assert.DoesNotContain("news-judgment-v10", cohortKey, StringComparison.Ordinal);
        Assert.DoesNotContain("news-judgment-v9", cohortKey, StringComparison.Ordinal);
        Assert.DoesNotContain("news-judgment-v8", cohortKey, StringComparison.Ordinal);
        Assert.DoesNotContain("news-judgment-v7", cohortKey, StringComparison.Ordinal);
        Assert.DoesNotContain("news-judgment-v5", cohortKey, StringComparison.Ordinal);
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
            .TakeLast(21)
            .ToList();
        Assert.Equal(
            [
                nameof(NewsJudgmentRecord.RationaleLength),
                nameof(NewsJudgmentRecord.RationaleOverSoftLimit),
                nameof(NewsJudgmentRecord.FactIdPrefixExpansionCount),
                nameof(NewsJudgmentRecord.TrajectoryBasis), // spec 214 §2
                nameof(NewsJudgmentRecord.ReferenceIds), // spec 215 §2
                nameof(NewsJudgmentRecord.ReferenceValuesOmitted), // spec 215 §2
                nameof(NewsJudgmentRecord.TrajectoryReferenceIds), // spec 215 §2
                nameof(NewsJudgmentRecord.ReferencePolicy), // spec 216 §5
                nameof(NewsJudgmentRecord.ReferenceKinds), // spec 216 §5
                nameof(NewsJudgmentRecord.TrajectoryReferenceKinds), // spec 216 §5
                nameof(NewsJudgmentRecord.ReferencesExcludedNewest), // spec 216 §1
                nameof(NewsJudgmentRecord.ReferencesExcludedLaterThanFact), // spec 216 §1
                nameof(NewsJudgmentRecord.ReferencesSkippedSupersededPolicy), // spec 216 §5
                nameof(NewsJudgmentRecord.ReadDepth), // spec 219 §2
                nameof(NewsJudgmentRecord.FamiliesAvailable), // spec 219 §2
                nameof(NewsJudgmentRecord.FamiliesWithheldByBudget), // spec 219 §2
                nameof(NewsJudgmentRecord.FamiliesAvailableByBasis), // spec 220 §3
                nameof(NewsJudgmentRecord.SuppliedBasisProfile), // spec 221 §2a
                nameof(NewsJudgmentRecord.FamiliesNonBusinessAvailable), // spec 221 §3
                nameof(NewsJudgmentRecord.FamiliesNonBusinessDemotedBySelection), // spec 221 §3
                nameof(NewsJudgmentRecord.FamiliesWithNoEventTypesAvailable), // spec 221 §3
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
