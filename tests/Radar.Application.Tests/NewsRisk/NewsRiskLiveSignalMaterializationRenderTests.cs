using System.Collections.Frozen;
using System.Text.Json;

using Radar.Application.News;
using Radar.Application.NewsRisk;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// SPEC 194 §1.2 — the judgment-signal materialization summary in the live news-risk artifact: rendered
/// when the step ran, and STRICTLY additive when it did not.
/// <para>
/// The additive claim is asserted two ways, because "trailing and nullable" is only a compatibility
/// guarantee if both halves hold: a <c>null</c> summary must render a BYTE-IDENTICAL markdown document,
/// and a consumer reading the pre-194 JSON fields BY NAME must be unaffected by the new property.
/// </para>
/// </summary>
public sealed class NewsRiskLiveSignalMaterializationRenderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANullSummary_RendersAByteIdenticalDocument()
    {
        var withoutSummary = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(summary: null));
        var withSummary = NewsRiskLiveArtifactRenderer.RenderMarkdown(
            Document(NewsJudgmentSignalMaterializationSummary.Empty));

        // The pin: a document whose materialization member is `null` renders exactly what a pre-194
        // document rendered — nothing is appended, and no header, blank line or trailing whitespace moves.
        // Asserted against the SAME document carrying a summary, because "trailing and nullable" is only a
        // compatibility guarantee if the section is genuinely appended at the END: the summary render must
        // begin with the null render byte for byte, and the null render must stop there.
        Assert.DoesNotContain("Judgment-derived news signals", withoutSummary, StringComparison.Ordinal);
        Assert.StartsWith(withoutSummary, withSummary, StringComparison.Ordinal);
        Assert.Contains("Judgment-derived news signals", withSummary, StringComparison.Ordinal);
        Assert.NotEqual(withoutSummary, withSummary);
    }

    [Fact]
    public void ASummary_RendersEveryCounterAndTheNamedSkipReasons()
    {
        var summary = new NewsJudgmentSignalMaterializationSummary(
            JudgmentsConsidered: 9,
            Eligible: 4,
            Materialized: 2,
            AlreadyMaterialized: 1,
            ValidationRejected: 0,
            WriteFailed: 0,
            Skips: new Dictionary<NewsJudgmentSignalSkipReason, int>
            {
                [NewsJudgmentSignalSkipReason.NonDirectionalTrajectory] = 3,
                [NewsJudgmentSignalSkipReason.NotPresentationCohort] = 2,
                [NewsJudgmentSignalSkipReason.UnresolvedObservation] = 1,
            });

        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(summary));

        Assert.Contains("## Judgment-derived news signals (spec 194 §1.2)", markdown, StringComparison.Ordinal);
        Assert.Contains("Judgments considered: 9", markdown, StringComparison.Ordinal);
        Assert.Contains("eligible: 4", markdown, StringComparison.Ordinal);
        Assert.Contains("materialized: 2", markdown, StringComparison.Ordinal);
        Assert.Contains("already materialized: 1", markdown, StringComparison.Ordinal);

        // The named reasons ARE the finding: "0 grounded" must never be readable as "the judge found
        // nothing" when it means "the provenance chain was incomplete". Order is the enum's declaration
        // order, so the line is deterministic (AD-3).
        Assert.Contains(
            "Not materialized, by reason: not-presentation-cohort 2, non-directional-trajectory 3, "
                + "unresolved-observation 1",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheJoinMeasurement_RendersEveryRoute_AndSaysSoWhenItWasNotAttempted()
    {
        // SPEC 197 §1.2 — the run's observation→evidence ladder measurement, rendered beside the
        // materialization it explains. The three joined ROUTES are named separately: pooling them would
        // hide a regression in the strong tiers behind the weak one's count.
        var measured = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(
            new NewsJudgmentSignalMaterializationSummary(
                JudgmentsConsidered: 9,
                Eligible: 9,
                Materialized: 9,
                AlreadyMaterialized: 0,
                ValidationRejected: 0,
                WriteFailed: 0,
                Skips: new Dictionary<NewsJudgmentSignalSkipReason, int>(),
                PriorVersionOccupied: 0,
                JoinCounts: new NewsObservationEvidenceJoinCounts(3185, 0, 0, 0, 9))));

        Assert.Contains("Observations: 3194", measured, StringComparison.Ordinal);
        Assert.Contains("joined: 3185", measured, StringComparison.Ordinal);
        Assert.Contains("exact article + instant: 3185", measured, StringComparison.Ordinal);
        Assert.Contains("exact article URL: 0", measured, StringComparison.Ordinal);
        Assert.Contains("unique-headline fallback: 0", measured, StringComparison.Ordinal);
        Assert.Contains("no match: 0", measured, StringComparison.Ordinal);
        Assert.Contains(
            "ambiguous (identity refused, never guessed): 9", measured, StringComparison.Ordinal);

        // NOT ATTEMPTED is a different fact from a measured zero, and the artifact says which one it is —
        // a defaulted zero must never render as a measured zero.
        var notAttempted = NewsRiskLiveArtifactRenderer.RenderMarkdown(
            Document(NewsJudgmentSignalMaterializationSummary.Empty));

        Assert.Contains("Not attempted this run", notAttempted, StringComparison.Ordinal);
        Assert.Contains("This is NOT a measured zero.", notAttempted, StringComparison.Ordinal);
        Assert.DoesNotContain("Observations: 0", notAttempted, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeasuredZeroJoin_RendersItsZeros_RatherThanTheNotAttemptedSentence()
    {
        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(
            new NewsJudgmentSignalMaterializationSummary(
                JudgmentsConsidered: 1,
                Eligible: 1,
                Materialized: 0,
                AlreadyMaterialized: 0,
                ValidationRejected: 0,
                WriteFailed: 0,
                Skips: new Dictionary<NewsJudgmentSignalSkipReason, int>
                {
                    [NewsJudgmentSignalSkipReason.ObservationNoMatch] = 1,
                },
                PriorVersionOccupied: 0,
                JoinCounts: NewsObservationEvidenceJoinCounts.Empty)));

        Assert.Contains("Observations: 0", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Not attempted this run", markdown, StringComparison.Ordinal);

        // The split reason is rendered by its own name, so a future run can tell missing evidence from a
        // deliberately refused identity.
        Assert.Contains(
            "Not materialized, by reason: observation-no-match 1", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePriorVersionOccupancyCount_IsRendered_OnItsOwnAxis()
    {
        // SPEC 197 §1.3 — the one-time migration across the news-judgment-signal-v1 → v2 fork must be
        // visible enough to be seen draining, not hidden inside AlreadyMaterialized.
        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(
            new NewsJudgmentSignalMaterializationSummary(
                JudgmentsConsidered: 3,
                Eligible: 3,
                Materialized: 1,
                AlreadyMaterialized: 0,
                ValidationRejected: 0,
                WriteFailed: 0,
                Skips: new Dictionary<NewsJudgmentSignalSkipReason, int>(),
                PriorVersionOccupied: 2,
                JoinCounts: NewsObservationEvidenceJoinCounts.Empty)));

        Assert.Contains(
            "already held under the retired v1 identity: 2", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySkipMapRendersNoneRatherThanAnEmptyList()
    {
        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(
            Document(NewsJudgmentSignalMaterializationSummary.Empty));

        Assert.Contains("Not materialized, by reason: none.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmptySummarysSkipMapCannotBeMutatedThroughACast()
    {
        // Shared instance, so it is frozen: a consumer casting the interface back to Dictionary must not be
        // able to poison every past and future Empty.
        Assert.IsAssignableFrom<FrozenDictionary<NewsJudgmentSignalSkipReason, int>>(
            NewsJudgmentSignalMaterializationSummary.Empty.Skips);
    }

    [Fact]
    public void AJsonConsumerReadingThePreSpec194FieldsByName_IsUnaffected()
    {
        var withSummary = JsonSerializer.Serialize(Document(new NewsJudgmentSignalMaterializationSummary(
            1, 1, 1, 0, 0, 0, new Dictionary<NewsJudgmentSignalSkipReason, int>())));

        using var document = JsonDocument.Parse(withSummary);
        var root = document.RootElement;

        Assert.Equal(
            NewsRiskLiveDocument.CurrentSchemaVersion, root.GetProperty("SchemaVersion").GetString());
        Assert.Equal(NewsRiskLiveDocument.LiveCaveat, root.GetProperty("Caveat").GetString());
        Assert.Equal(1, root.GetProperty("Companies").GetArrayLength());
        Assert.True(root.TryGetProperty("SignalMaterialization", out var added));
        Assert.Equal(1, added.GetProperty("Materialized").GetInt32());
    }

    private static NewsRiskLiveDocument Document(
        NewsJudgmentSignalMaterializationSummary? summary) => new(
        SchemaVersion: NewsRiskLiveDocument.CurrentSchemaVersion,
        RunId: new Guid("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
        SelectionAsOfUtc: Now,
        Caveat: NewsRiskLiveDocument.LiveCaveat,
        Readers: ["reader-a (test-provider:model-a)"],
        Diagnostic: null,
        Companies:
        [
            new NewsRiskLiveCompany(
                CompanyId: new Guid("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
                CompanyName: "Test Co",
                Ticker: "TST",
                Selections: [new NewsRiskCandidateSelection("default", 1, Guid.Empty)],
                Articles: [],
                ArchiveCapture: NewsRiskArchiveCapture.Proven,
                SearchEnumeration: NewsRiskSearchEnumeration.Complete,
                AssessmentBundle: NewsRiskAssessmentBundle.Complete,
                QualifyingArticleCount: 0,
                CoverageIssues: [],
                ReaderResults: []),
        ],
        GeneratedAtUtc: Now,
        SignalMaterialization: summary);
}
