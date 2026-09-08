using Radar.Application.Filings;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 215 §2 — reference values through the validator, the family-set hash and the marker policy: a
/// reference citation resolves ONLY against the projected set (named failure otherwise, exactly as a
/// FactId); the basis becomes <see cref="NewsTrajectoryBasis.ReferenceSupported"/> only for a cited
/// LevelOnly fact whose statement names a cited reference's metric; a reference-free judgment's hash,
/// request and record are byte-identical to pre-215; and a grown ledger is a new hash.
/// </summary>
public sealed class NewsJudgmentReferenceValueTests
{
    private static readonly Guid BacklogReferenceId = Guid.Parse("1e5a0000-0000-4000-8000-000000000001");
    private static readonly Guid RevenueReferenceId = Guid.Parse("1e5a0000-0000-4000-8000-000000000002");

    private static NewsJudgmentReferenceValue Reference(
        Guid? id = null, ReportedMetric metric = ReportedMetric.Backlog) => new(
        ReferenceId: id ?? BacklogReferenceId,
        Metric: metric,
        Value: "2.929",
        Unit: "billion",
        Period: "as of January 31, 2026",
        PriorValue: null,
        PriorPeriod: null,
        FilingDateUtc: new DateTimeOffset(2026, 4, 9, 20, 0, 0, TimeSpan.Zero),
        Form: "8-K",
        Quote: "Project backlog of $2.929 billion as of January 31, 2026.");

    private static NewsJudgmentInputFamily Level(Guid? factId = null) => NewsJudgmentTestData.Family(
        factId: factId,
        assertionStatus: NewsFactAssertionStatus.Reported,
        statement: "Power projects lift Argan as backlog hits $2.5B");

    private static NewsJudgmentInputFamily CashLevel(Guid? factId = null) => NewsJudgmentTestData.Family(
        factId: factId,
        assertionStatus: NewsFactAssertionStatus.Reported,
        statement: "Cash of $671.6 million");

    private static NewsJudgmentInputFamily Comparison(Guid? factId = null) => NewsJudgmentTestData.Family(
        factId: factId,
        assertionStatus: NewsFactAssertionStatus.Reported,
        statement: "record revenue of $384 million");

    private static string[] Ids(params NewsJudgmentInputFamily[] families) =>
        [.. families.Select(f => f.RepresentativeFactId.ToString("D"))];

    private static NewsJudgmentModelResponse Response(
        NewsJudgmentInputFamily[] trajectoryFacts,
        string[]? trajectoryReferenceIds,
        string trajectory = "Deteriorating",
        IReadOnlyList<NewsJudgmentModelFinding>? findings = null) =>
        NewsJudgmentTestData.Response(
            trajectory: trajectory,
            strength: findings is { Count: > 0 } ? 60 : null,
            findings: findings ?? [],
            rationale: "Backlog of $2.5B is below the $2.929B the company reported as of January 31.",
            trajectoryFactIds: Ids(trajectoryFacts)) with
        {
            TrajectoryReferenceIds = trajectoryReferenceIds,
        };

    // ── the basis rule ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALevelBesideACitedReferenceForTheSameMetric_IsReferenceSupported_AndStillJudged()
    {
        // The case the pair of specs exists for: "backlog $2.5B vs $2.929B reported in January: down".
        var level = Level();
        var result = NewsJudgmentValidator.Validate(
            Response([level], [BacklogReferenceId.ToString("D")]), [level], [Reference()]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsTrajectoryBasis.ReferenceSupported, result.TrajectoryBasis);
        Assert.Equal([BacklogReferenceId], result.TrajectoryReferenceIds);
        Assert.Empty(result.FindingDropReasons);
    }

    [Fact]
    public void ACitedReferenceWhoseMetricNoCitedLevelNames_LeavesTheBasisLevelOnly()
    {
        // A revenue reference beside a backlog level is not a comparison of the same metric.
        var level = Level();
        var result = NewsJudgmentValidator.Validate(
            Response([level], [RevenueReferenceId.ToString("D")]),
            [level],
            [Reference(RevenueReferenceId, ReportedMetric.Revenue)]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsTrajectoryBasis.LevelOnly, result.TrajectoryBasis);
        Assert.Equal([RevenueReferenceId], result.TrajectoryReferenceIds);
    }

    [Fact]
    public void AReferenceNamedOnlyByAnUncitedFact_DoesNotSupportTheTrajectory()
    {
        // The backlog reference matches the backlog level, but the judge cited only the CASH level.
        var backlog = Level();
        var cash = CashLevel();
        var result = NewsJudgmentValidator.Validate(
            Response([cash], [BacklogReferenceId.ToString("D")]), [backlog, cash], [Reference()]);

        Assert.Equal(NewsTrajectoryBasis.LevelOnly, result.TrajectoryBasis);
    }

    [Fact]
    public void SupportedTakesPrecedence_OverReferenceSupported()
    {
        var level = Level();
        var comparison = Comparison();
        var result = NewsJudgmentValidator.Validate(
            Response([level, comparison], [BacklogReferenceId.ToString("D")]), [level, comparison], [Reference()]);

        Assert.Equal(NewsTrajectoryBasis.Supported, result.TrajectoryBasis);
    }

    [Fact]
    public void NoReferenceCited_KeepsThe214Result_WithAnEmptyReferenceSet()
    {
        var level = Level();
        var result = NewsJudgmentValidator.Validate(Response([level], null), [level], [Reference()]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsTrajectoryBasis.LevelOnly, result.TrajectoryBasis);
        Assert.Empty(result.TrajectoryReferenceIds);
    }

    [Fact]
    public void TheRuleItself_IsOneStaticFunction_WithThePrecedencePinned()
    {
        var level = Level();
        var comparison = Comparison();
        var byId = new Dictionary<Guid, NewsJudgmentInputFamily>
        {
            [level.RepresentativeFactId] = level,
            [comparison.RepresentativeFactId] = comparison,
        };
        var references = new Dictionary<Guid, NewsJudgmentReferenceValue> { [BacklogReferenceId] = Reference() };

        Assert.Equal(
            NewsTrajectoryBasis.ReferenceSupported,
            NewsJudgmentValidator.TrajectoryBasisFor(
                NewsJudgmentTrajectory.Deteriorating, [level.RepresentativeFactId], byId, [BacklogReferenceId], references));
        Assert.Equal(
            NewsTrajectoryBasis.Supported,
            NewsJudgmentValidator.TrajectoryBasisFor(
                NewsJudgmentTrajectory.Deteriorating,
                [level.RepresentativeFactId, comparison.RepresentativeFactId],
                byId,
                [BacklogReferenceId],
                references));
        Assert.Equal(
            NewsTrajectoryBasis.LevelOnly,
            NewsJudgmentValidator.TrajectoryBasisFor(
                NewsJudgmentTrajectory.Deteriorating, [level.RepresentativeFactId], byId, [], references));
        Assert.Null(NewsJudgmentValidator.TrajectoryBasisFor(
            NewsJudgmentTrajectory.Mixed, [level.RepresentativeFactId], byId, [BacklogReferenceId], references));
    }

    // ── reference citations resolve like FactIds, against the PROJECTED set only ──────────────────────

    [Fact]
    public void ATrajectoryReferenceNotInTheProjectedSet_FailsTheResponse_Named()
    {
        var level = Level();
        var invented = Guid.NewGuid().ToString("D");
        var result = NewsJudgmentValidator.Validate(Response([level], [invented]), [level], [Reference()]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        var reason = Assert.Single(result.FindingDropReasons);
        Assert.StartsWith("trajectory-reference-not-supplied: '" + invented + "'", reason, StringComparison.Ordinal);
        Assert.Contains("supplied ReferenceId", reason, StringComparison.Ordinal);
        Assert.Null(result.TrajectoryBasis);
        Assert.Empty(result.TrajectoryReferenceIds);
    }

    [Fact]
    public void AFactIdCitedAsAReference_IsNotSupplied_BecauseTheTwoSetsNeverCross()
    {
        var level = Level();
        var result = NewsJudgmentValidator.Validate(
            Response([level], [level.RepresentativeFactId.ToString("D")]), [level], [Reference()]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.StartsWith("trajectory-reference-not-supplied", Assert.Single(result.FindingDropReasons), StringComparison.Ordinal);
    }

    [Fact]
    public void AUniquePrefix_Expands_AndADuplicateAfterExpansion_Fails()
    {
        var level = Level();
        var prefix = BacklogReferenceId.ToString("N")[..8];

        var expanded = NewsJudgmentValidator.Validate(Response([level], [prefix]), [level], [Reference()]);
        Assert.Equal(NewsJudgmentStatus.Judged, expanded.Status);
        Assert.Equal([BacklogReferenceId], expanded.TrajectoryReferenceIds);
        // Reference expansions are NOT folded into the FactId expansion count (spec 197's definition).
        Assert.Equal(0, expanded.FactIdPrefixExpansionCount);

        var duplicate = NewsJudgmentValidator.Validate(
            Response([level], [prefix, BacklogReferenceId.ToString("D")]), [level], [Reference()]);
        Assert.Equal(NewsJudgmentStatus.ValidationFailed, duplicate.Status);
        Assert.StartsWith("trajectory-reference-duplicate", Assert.Single(duplicate.FindingDropReasons), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1e5a000", "trajectory-reference-id-prefix-too-short")]
    [InlineData("deadbeef", "trajectory-reference-id-prefix-unmatched")]
    [InlineData("not-an-id", "trajectory-reference-id-malformed")]
    public void EveryOtherMalformedReference_FailsWithItsOwnNamedReason(string raw, string expectedPrefix)
    {
        var level = Level();
        var result = NewsJudgmentValidator.Validate(Response([level], [raw]), [level], [Reference()]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.StartsWith(expectedPrefix, Assert.Single(result.FindingDropReasons), StringComparison.Ordinal);
    }

    [Fact]
    public void AFindingsReferences_ResolveTheSameWay_AndAnUnsuppliedOneDropsOnlyThatFinding()
    {
        var level = Level();
        var good = NewsJudgmentTestData.Finding(level.RepresentativeFactId) with
        {
            ReferenceIds = [BacklogReferenceId.ToString("D")],
        };
        var bad = NewsJudgmentTestData.Finding(level.RepresentativeFactId) with
        {
            ReferenceIds = [Guid.NewGuid().ToString("D")],
        };
        var none = NewsJudgmentTestData.Finding(level.RepresentativeFactId);

        var result = NewsJudgmentValidator.Validate(
            Response([level], null, findings: [good, bad, none]), [level], [Reference()]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(2, result.Findings.Count);
        Assert.Equal([BacklogReferenceId], result.Findings[0].ReferenceIds);
        Assert.Equal([], result.Findings[1].ReferenceIds!); // a v6 finding always records its (empty) set
        var reason = Assert.Single(result.FindingDropReasons);
        Assert.StartsWith("finding[1] reference-not-supplied", reason, StringComparison.Ordinal);
        Assert.Equal(3, result.FindingsTotal);
        Assert.Equal(1, result.FindingsDropped);
    }

    [Fact]
    public void WithNoReferencesSupplied_AnyReferenceCitation_IsNotSupplied()
    {
        var level = Level();
        var result = NewsJudgmentValidator.Validate(
            Response([level], [BacklogReferenceId.ToString("D")]), [level]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.StartsWith("trajectory-reference-not-supplied", Assert.Single(result.FindingDropReasons), StringComparison.Ordinal);
    }

    // ── the family-set hash ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFamilySetHash_IsByteIdenticalWithoutReferences_AndDifferentWithThem()
    {
        var level = Level();
        var pre215 = NewsJudgmentInputBuilder.ComputeFamilySetHash([level]);

        Assert.Equal(pre215, NewsJudgmentInputBuilder.ComputeFamilySetHash([level], []));

        var withReference = NewsJudgmentInputBuilder.ComputeFamilySetHash([level], [Reference()]);
        Assert.NotEqual(pre215, withReference);
        // …and the reference ORDER/SET is part of it: a grown ledger is a new judge input.
        Assert.NotEqual(
            withReference,
            NewsJudgmentInputBuilder.ComputeFamilySetHash(
                [level], [Reference(), Reference(RevenueReferenceId, ReportedMetric.Revenue)]));
    }

    [Fact]
    public void TheBuilder_ProjectsTheLedgerAgainstTheSuppliedStatements_AndAnEmptyLedgerProjectsNothing()
    {
        var company = Guid.NewGuid();
        var factId = Guid.NewGuid();
        var families = new[]
        {
            NewsJudgmentTestData.FamilyRecord(company, factId, "Power projects lift Argan as backlog hits $2.5B"),
        };
        var facts = new Dictionary<Guid, NewsTypingFactRef>
        {
            [factId] = NewsJudgmentTestData.FactRef(company, factId, "Power projects lift Argan as backlog hits $2.5B"),
        };
        var ledger = new[]
        {
            new ReportedMetricRecord(
                Id: BacklogReferenceId, CompanyId: company, Accession: "0001049521-26-000005",
                EvidenceId: Guid.NewGuid(), FilingDateUtc: new DateTimeOffset(2026, 4, 9, 20, 0, 0, TimeSpan.Zero),
                Form: "8-K", Metric: ReportedMetric.Backlog, Value: "2.929", Unit: "billion",
                Period: "as of January 31, 2026", PriorValue: null, PriorPeriod: null,
                Quote: "Project backlog of $2.929 billion as of January 31, 2026.",
                ReaderIdentity: "openai:deepseek", Verification: ReportedMetricVerification.Verbatim,
                Policy: ReportedMetricsPolicy.Version),
            new ReportedMetricRecord(
                Id: RevenueReferenceId, CompanyId: company, Accession: "0001049521-26-000005",
                EvidenceId: Guid.NewGuid(), FilingDateUtc: new DateTimeOffset(2026, 4, 9, 20, 0, 0, TimeSpan.Zero),
                Form: "8-K", Metric: ReportedMetric.Revenue, Value: "612.7", Unit: "million",
                Period: "fourth quarter of fiscal 2026", PriorValue: null, PriorPeriod: null,
                Quote: "Revenues of $612.7 million.",
                ReaderIdentity: "openai:deepseek", Verification: ReportedMetricVerification.Verbatim,
                Policy: ReportedMetricsPolicy.Version),
        };

        var without = NewsJudgmentInputBuilder.Build(company, families, facts, 50);
        var withLedger = NewsJudgmentInputBuilder.Build(company, families, facts, 50, ledger);

        Assert.Empty(without.References);
        Assert.Equal(0, without.ReferenceValuesOmitted);
        Assert.Equal([BacklogReferenceId], withLedger.References.Select(r => r.ReferenceId).ToList()); // revenue is not named
        Assert.Equal(0, withLedger.ReferenceValuesOmitted);
        Assert.NotEqual(without.FamilySetHash, withLedger.FamilySetHash);
        Assert.Equal(without.FamilySetHash, NewsJudgmentInputBuilder.Build(company, families, facts, 50, []).FamilySetHash);
    }
}
