using System.Globalization;
using System.Text;

using Radar.Application.Filings;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 192 §1/§4 — an over-long rationale must not discard a judgment's findings.
/// <para>
/// The pre-192 validator returned its <c>rationale-too-long</c> failure BEFORE the findings loop, so the
/// findings were not judged invalid: they were never examined at all — no citation check, no
/// attribution-caveat rule, no context-only gate. Measured on the live store for 2026-08-25: four of
/// eighteen judgments failed validation (22 %), three for length alone, with rationales clustered at
/// 1,095–1,228 characters; CVLT lost three findings and LBRT two, and the rationale text itself was nulled
/// rather than persisted, so only the response hash survived.
/// </para>
/// <para>
/// <b>Mutation proof.</b>
/// <see cref="CvltShaped_OverLongRationaleWithThreeValidFindings_IsJudgedWithAllThree"/>
/// is the test that fails if the pre-192 ordering is restored: put the length check back ahead of the
/// findings loop (or make <see cref="NewsJudgmentValidator.MaxRationaleLength"/> fail the response again)
/// and it reports <see cref="NewsJudgmentStatus.ValidationFailed"/> with zero findings and a null
/// rationale. Likewise, moving the advice-language scrub back after the length measurement fails
/// <see cref="AdviceLanguageInALongRationale_IsScrubbedFirst_ThenFailsAsRationaleMissing"/>.
/// </para>
/// Fixtures are built from the shared <see cref="NewsJudgmentTestData"/> builders rather than re-pasted.
/// </summary>
public sealed class NewsJudgmentRationaleLengthTests
{
    /// <summary>
    /// The measured live CVLT rationale length (2026-08-25) — the shape this slice exists for.
    /// </summary>
    private const int CvltRationaleLength = 1_228;

    /// <summary>
    /// Ordinary factual prose deterministically extended to an EXACT length. The live over-long rationales
    /// were not filler: they were normal explanations that simply ran long, so the fixture is prose too.
    /// A trailing space would be removed by the validator's <c>Trim()</c> and the pinned length would then
    /// be a lie, so the final character is forced to a full stop.
    /// </summary>
    private static string RationaleOfLength(int length)
    {
        const string Prose =
            "The supplied filings describe an opened regulatory review, a disclosed customer-concentration "
            + "change and a delayed product milestone; each is stated factually by the filer and none rests "
            + "on speculation. ";

        var text = new StringBuilder();
        while (text.Length < length)
        {
            text.Append(Prose);
        }

        var exact = text.ToString(0, length);
        return char.IsWhiteSpace(exact[^1]) ? string.Concat(exact.AsSpan(0, length - 1), ".") : exact;
    }

    private static NewsJudgmentInputFamily Business(Guid factId) => NewsJudgmentTestData.Family(
        factId: factId, assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);

    [Fact]
    public void TheFixtureProse_IsExactlyAsLongAsItClaims_AndCarriesNoAdviceLanguage()
    {
        var rationale = RationaleOfLength(CvltRationaleLength);

        Assert.Equal(CvltRationaleLength, rationale.Length);
        Assert.Equal(rationale, rationale.Trim());
        Assert.False(AdviceLanguageGuard.ContainsAdviceLanguage(rationale));
    }

    /// <summary>
    /// The CVLT shape: a 1,228-character rationale carrying THREE valid findings. All three are accepted,
    /// the rationale is persisted IN FULL (never truncated — a shortened rationale is a fabricated
    /// explanation), and the soft bound records itself instead of destroying the work it measures.
    /// <b>This is the mutation-proof test</b>: restoring the pre-192 ordering turns it red.
    /// </summary>
    [Fact]
    public void CvltShaped_OverLongRationaleWithThreeValidFindings_IsJudgedWithAllThree()
    {
        var first = Business(Guid.Parse("cf000000-0000-4000-8000-000000000001"));
        var second = Business(Guid.Parse("cf000000-0000-4000-8000-000000000002"));
        var third = Business(Guid.Parse("cf000000-0000-4000-8000-000000000003"));
        var rationale = RationaleOfLength(CvltRationaleLength);
        var response = NewsJudgmentTestData.Response(
            rationale: rationale,
            findings:
            [
                NewsJudgmentTestData.Finding(first.RepresentativeFactId),
                NewsJudgmentTestData.Finding(
                    second.RepresentativeFactId, category: "ExecutionOrMissedMilestone", severity: "Medium"),
                NewsJudgmentTestData.Finding(
                    third.RepresentativeFactId, category: "CustomerOrRevenueConcentration", severity: "Low"),
            ]);

        var result = NewsJudgmentValidator.Validate(
            response, NewsJudgmentTestData.Supplied(first, second, third));

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(3, result.Findings.Count);
        Assert.Equal(3, result.FindingsTotal);
        Assert.Equal(3, result.FindingsAccepted);
        Assert.Equal(0, result.FindingsDropped);
        Assert.Empty(result.FindingDropReasons);

        // The full text, character for character — the whole complaint was that it became unrecoverable.
        Assert.Equal(rationale, result.Rationale);
        Assert.Equal(CvltRationaleLength, result.Rationale!.Length);

        // …and the bound still MEANS something: it is measured and flagged, not enforced by deletion.
        Assert.Equal(CvltRationaleLength, result.RationaleLength);
        Assert.True(result.RationaleOverSoftLimit);
    }

    [Fact]
    public void JustOverTheSoftBound_IsFlaggedButStillJudged()
    {
        var family = Business(Guid.NewGuid());
        var response = NewsJudgmentTestData.Response(
            rationale: RationaleOfLength(NewsJudgmentValidator.MaxRationaleLength + 1),
            findings: [NewsJudgmentTestData.Finding(family.RepresentativeFactId)]);

        var result = NewsJudgmentValidator.Validate(response, NewsJudgmentTestData.Supplied(family));

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Single(result.Findings);
        Assert.True(result.RationaleOverSoftLimit);
        Assert.Equal(NewsJudgmentValidator.MaxRationaleLength + 1, result.RationaleLength);
    }

    [Fact]
    public void OverTheHardCeiling_FailsWithItsOwnNamedReason_WhichStatesTheLength()
    {
        var family = Business(Guid.NewGuid());
        var length = NewsJudgmentValidator.MaxRationaleHardLimit + 1;
        var rationale = RationaleOfLength(length);
        var response = NewsJudgmentTestData.Response(
            rationale: rationale,
            findings: [NewsJudgmentTestData.Finding(family.RepresentativeFactId)]);

        var result = NewsJudgmentValidator.Validate(response, NewsJudgmentTestData.Supplied(family));

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        var reason = Assert.Single(
            result.FindingDropReasons,
            r => r.StartsWith(
                NewsJudgmentValidator.RationaleExceedsHardLimitReason, StringComparison.Ordinal));
        Assert.Contains(
            length.ToString(CultureInfo.InvariantCulture), reason, StringComparison.Ordinal);
        Assert.Contains(
            NewsJudgmentValidator.MaxRationaleHardLimit.ToString(CultureInfo.InvariantCulture),
            reason,
            StringComparison.Ordinal);

        // The malformed text is still PERSISTED, never nulled: spec 192's complaint is that nulling made
        // the four live rationales unrecoverable, leaving only a response hash.
        Assert.Equal(rationale, result.Rationale);
        Assert.Equal(length, result.RationaleLength);
        Assert.True(result.RationaleOverSoftLimit);
    }

    [Fact]
    public void ExactlyAtTheHardCeiling_IsStillJudged_TheCeilingIsInclusive()
    {
        var family = Business(Guid.NewGuid());
        var response = NewsJudgmentTestData.Response(
            rationale: RationaleOfLength(NewsJudgmentValidator.MaxRationaleHardLimit),
            findings: [NewsJudgmentTestData.Finding(family.RepresentativeFactId)]);

        var result = NewsJudgmentValidator.Validate(response, NewsJudgmentTestData.Supplied(family));

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Single(result.Findings);
    }

    /// <summary>
    /// Spec 192 §1: "Findings are still validated first and their count still reported." Even the hard
    /// ceiling runs AFTER the loop, so a maintainer reading the failed record can still see what the
    /// findings were and why each was dropped.
    /// </summary>
    [Fact]
    public void TheHardCeilingFailure_StillReportsTheFindingsItValidated()
    {
        var family = Business(Guid.NewGuid());
        var response = NewsJudgmentTestData.Response(
            rationale: RationaleOfLength(NewsJudgmentValidator.MaxRationaleHardLimit + 500),
            findings:
            [
                NewsJudgmentTestData.Finding(family.RepresentativeFactId),
                NewsJudgmentTestData.Finding(Guid.NewGuid()), // not supplied
            ]);

        var result = NewsJudgmentValidator.Validate(response, NewsJudgmentTestData.Supplied(family));

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Equal(2, result.FindingsTotal);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("cited-fact-not-supplied"));
        Assert.Contains(
            result.FindingDropReasons,
            r => r.StartsWith(
                NewsJudgmentValidator.RationaleExceedsHardLimitReason, StringComparison.Ordinal));
    }

    /// <summary>
    /// The ordering proof (spec 192 §1): the advice-language scrub used to run AFTER the length check, so
    /// an over-long rationale was returned unscrubbed — the rationale most in need of the house rule was
    /// the one exempt from it. It is now scrubbed FIRST, blanking it, and the (now absent) rationale fails
    /// as <c>rationale-missing</c>: an unchanged, deliberately unweakened rule.
    /// </summary>
    [Fact]
    public void AdviceLanguageInALongRationale_IsScrubbedFirst_ThenFailsAsRationaleMissing()
    {
        var family = Business(Guid.NewGuid());
        var rationale = "You should buy this stock now. " + RationaleOfLength(CvltRationaleLength);
        Assert.True(rationale.Length > NewsJudgmentValidator.MaxRationaleLength);

        var response = NewsJudgmentTestData.Response(
            rationale: rationale,
            findings: [NewsJudgmentTestData.Finding(family.RepresentativeFactId)]);

        var result = NewsJudgmentValidator.Validate(response, NewsJudgmentTestData.Supplied(family));

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(
            "rationale-advice-language: rationale contained advice language and was dropped",
            result.FindingDropReasons);
        Assert.Contains(
            "rationale-missing: a judged response requires a non-blank factual rationale",
            result.FindingDropReasons);
        // The advice text is NEVER surfaced, whatever its length.
        Assert.Null(result.Rationale);
        // Nothing survived the scrub, so there is nothing to have exceeded the bound.
        Assert.Equal(0, result.RationaleLength);
        Assert.False(result.RationaleOverSoftLimit);
    }

    /// <summary>
    /// Spec 192 §4: findings beneath an over-long rationale are still subject to EVERY finding-level rule
    /// individually — citation, the attribution-caveat rule and the context-only gate — and an invalid one
    /// is still dropped BY NAME while its valid siblings survive.
    /// </summary>
    [Fact]
    public void FindingsBeneathAnOverLongRationale_AreStillIndividuallyValidated()
    {
        var valid = Business(Guid.Parse("cf000000-0000-4000-8000-00000000000a"));
        var alleged = NewsJudgmentTestData.Family(
            factId: Guid.Parse("cf000000-0000-4000-8000-00000000000b"),
            assertionStatus: NewsFactAssertionStatus.Alleged);
        var contextOnly = NewsJudgmentTestData.Family(
            factId: Guid.Parse("cf000000-0000-4000-8000-00000000000c"),
            assertionStatus: NewsFactAssertionStatus.Reported) with
        {
            EventTypes = [NewsEventType.MarketReaction],
        };

        var response = NewsJudgmentTestData.Response(
            rationale: RationaleOfLength(CvltRationaleLength),
            findings:
            [
                NewsJudgmentTestData.Finding(valid.RepresentativeFactId),
                NewsJudgmentTestData.Finding(alleged.RepresentativeFactId, caveat: null),
                NewsJudgmentTestData.Finding(contextOnly.RepresentativeFactId),
                NewsJudgmentTestData.Finding(Guid.NewGuid()),
            ]);

        var result = NewsJudgmentValidator.Validate(
            response, NewsJudgmentTestData.Supplied(valid, alleged, contextOnly));

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(valid.RepresentativeFactId, Assert.Single(Assert.Single(result.Findings).FactIds));
        Assert.Equal(4, result.FindingsTotal);
        Assert.Equal(3, result.FindingsDropped);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("missing-attribution-caveat"));
        Assert.Contains(
            result.FindingDropReasons,
            r => r.Contains(NewsJudgmentValidator.NonBusinessContextOnlyReason));
        Assert.Contains(result.FindingDropReasons, r => r.Contains("cited-fact-not-supplied"));
        Assert.True(result.RationaleOverSoftLimit);
    }

    /// <summary>
    /// Spec 185's fail-closed rule is untouched: findings that fail ON THEIR OWN MERITS still produce
    /// <see cref="NewsJudgmentStatus.ValidationFailed"/>, never the supportive "no challenge found" read —
    /// the length of the rationale beside them is irrelevant either way.
    /// </summary>
    [Fact]
    public void AllFindingsInvalidBeneathAnOverLongRationale_IsValidationFailed_NeverNoChallengeFound()
    {
        var family = Business(Guid.NewGuid());
        var response = NewsJudgmentTestData.Response(
            rationale: RationaleOfLength(CvltRationaleLength),
            findings:
            [
                NewsJudgmentTestData.Finding(family.RepresentativeFactId, category: "NotACategory"),
                NewsJudgmentTestData.Finding(family.RepresentativeFactId, confidence: 1.5),
            ]);

        var result = NewsJudgmentValidator.Validate(response, NewsJudgmentTestData.Supplied(family));

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Empty(result.Findings);
        Assert.Equal(2, result.FindingsTotal);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("category-invalid"));
        Assert.Contains(result.FindingDropReasons, r => r.Contains("confidence-out-of-range"));
        // The failure is about the FINDINGS, so no rationale reason appears at all …
        Assert.DoesNotContain(result.FindingDropReasons, r => r.Contains("rationale"));
        // … and the rationale is still recoverable on the failed record.
        Assert.Equal(CvltRationaleLength, result.Rationale!.Length);
    }

    /// <summary>
    /// The two bounds are distinct constants with the soft one strictly below the hard one — otherwise the
    /// "flag, do not fail" behaviour would be unreachable.
    /// </summary>
    [Fact]
    public void TheSoftBoundSitsStrictlyBelowTheHardCeiling()
    {
        Assert.Equal(1_000, NewsJudgmentValidator.MaxRationaleLength);
        Assert.Equal(4_000, NewsJudgmentValidator.MaxRationaleHardLimit);
        Assert.True(NewsJudgmentValidator.MaxRationaleLength < NewsJudgmentValidator.MaxRationaleHardLimit);
        Assert.Equal("rationale-exceeds-hard-limit", NewsJudgmentValidator.RationaleExceedsHardLimitReason);
    }
}
