using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 214 §1 — <see cref="StatementComparisonClassifier"/> (<c>comparison-basis-v1</c>): pure,
/// closed-table, PRECEDENCE-ordered, whole-word. Every acceptance-criterion example is pinned, the four
/// substring negatives are pinned, and the version token's place in the cohort key is asserted — because
/// the tables and the boundary rule ARE the classifier's identity and a change to any of them is v2.
/// </summary>
public sealed class StatementComparisonClassifierTests
{
    private static readonly IReadOnlyList<NewsEventType> Earnings = [NewsEventType.EarningsOrGuidance];
    private static readonly IReadOnlyList<NewsEventType> Contract = [NewsEventType.ContractOrCustomerWin];
    private static readonly IReadOnlyList<NewsEventType> Regulatory = [NewsEventType.RegulatoryOrLegal];
    private static readonly IReadOnlyList<NewsEventType> Other = [NewsEventType.OtherSpecified];

    // ---- the acceptance-criterion examples, verbatim -------------------------------------------------

    [Fact]
    public void TheArganBacklogStatement_IsLevelOnly()
    {
        // The statement the 2026-09-07 judgment cited as trajectory support: a LEVEL typed
        // ContractOrCustomerWin + OtherSpecified. No comparison phrase, a figure attached to "backlog".
        var basis = StatementComparisonClassifier.Classify(
            "Power projects lift Argan (NYSE: AGX) as backlog hits $2.5B",
            [NewsEventType.ContractOrCustomerWin, NewsEventType.OtherSpecified]);

        Assert.Equal(NewsFactComparisonBasis.LevelOnly, basis);
    }

    [Fact]
    public void RecordRevenue_IsAStatedComparison()
    {
        Assert.Equal(
            NewsFactComparisonBasis.StatedComparison,
            StatementComparisonClassifier.Classify("record revenue of $384 million", Earnings));
    }

    [Fact]
    public void ANumberlessComparison_IsAStatedComparison()
    {
        // Review round 1: a number is NOT required for the directional classes.
        Assert.Equal(
            NewsFactComparisonBasis.StatedComparison,
            StatementComparisonClassifier.Classify("Backlog declined during the quarter", Contract));
    }

    [Fact]
    public void ANumberlessContractWin_IsAnEvent()
    {
        Assert.Equal(
            NewsFactComparisonBasis.Event,
            StatementComparisonClassifier.Classify(
                "The company won a major government contract", Contract));
    }

    [Fact]
    public void AnFdaApproval_IsAnEvent()
    {
        Assert.Equal(
            NewsFactComparisonBasis.Event,
            StatementComparisonClassifier.Classify("The FDA approved the product", Regulatory));
    }

    [Fact]
    public void AQuantifiedFollowOnOrder_IsAnEvent_NotALevel()
    {
        // A figure with no metric noun attached: "order" is the event, $22 million is its size.
        Assert.Equal(
            NewsFactComparisonBasis.Event,
            StatementComparisonClassifier.Classify("$22 Million Follow-On Order", Contract));
    }

    [Fact]
    public void ABarePercentage_IsALevel_NeverAComparison()
    {
        Assert.Equal(
            NewsFactComparisonBasis.LevelOnly,
            StatementComparisonClassifier.Classify("gross margin was 24%", Earnings));
        // …and the same statement with no earnings typing still attaches the figure to "margin".
        Assert.Equal(
            NewsFactComparisonBasis.LevelOnly,
            StatementComparisonClassifier.Classify("gross margin was 24%", Other));
    }

    [Fact]
    public void ABareFigureOnAnEarningsStatement_IsALevel_EvenWithoutAMetricNoun()
    {
        Assert.Equal(
            NewsFactComparisonBasis.LevelOnly,
            StatementComparisonClassifier.Classify(
                "The company reported $53.3 million for the quarter", Earnings));
        // Without the earnings typing there is no metric noun to attach to, and no event term: not quantified.
        Assert.Equal(
            NewsFactComparisonBasis.NotQuantified,
            StatementComparisonClassifier.Classify(
                "The company reported $53.3 million for the quarter", Other));
    }

    [Fact]
    public void AStatementWithNoComparisonLevelOrEvent_IsNotQuantified()
    {
        Assert.Equal(
            NewsFactComparisonBasis.NotQuantified,
            StatementComparisonClassifier.Classify(
                "The company will present at an investor conference in September", Other));
        Assert.Equal(
            NewsFactComparisonBasis.NotQuantified,
            StatementComparisonClassifier.Classify(string.Empty, Other));
        Assert.Equal(
            NewsFactComparisonBasis.NotQuantified,
            StatementComparisonClassifier.Classify(null, Other));
    }

    // ---- precedence -----------------------------------------------------------------------------------

    [Fact]
    public void AComparisonMarkerBesideALevel_IsAStatedComparison()
    {
        Assert.Equal(
            NewsFactComparisonBasis.StatedComparison,
            StatementComparisonClassifier.Classify(
                "Backlog rose to $2.5B, up from $2.1B a year ago", Contract));
    }

    [Fact]
    public void ALevelBesideAnEventTerm_IsALevel()
    {
        // Precedence rule 2 before rule 3: the level is what the judge is most likely to misread.
        Assert.Equal(
            NewsFactComparisonBasis.LevelOnly,
            StatementComparisonClassifier.Classify(
                "Argan wins a $50M contract; backlog now stands at $2.5B", Contract));
    }

    [Fact]
    public void AnUpOrDownFollowedByAFigure_IsAComparison_ButABareUpIsNot()
    {
        Assert.Equal(
            NewsFactComparisonBasis.StatedComparison,
            StatementComparisonClassifier.Classify("Quarterly revenue up 12%", Earnings));
        Assert.Equal(
            NewsFactComparisonBasis.StatedComparison,
            StatementComparisonClassifier.Classify("Net income down by $3 million", Earnings));
        // "up to" is a bound, not a change: a level of the metric it qualifies.
        Assert.Equal(
            NewsFactComparisonBasis.LevelOnly,
            StatementComparisonClassifier.Classify("Revenue of up to $5 million is expected", Earnings));
    }

    [Fact]
    public void FromXToY_IsAComparison()
    {
        Assert.Equal(
            NewsFactComparisonBasis.StatedComparison,
            StatementComparisonClassifier.Classify(
                "Gross margin moved from 24% to 27% over the period", Earnings));
    }

    // ---- the tie-breaker rule, pinned as v1 identity ----------------------------------------------------

    [Fact]
    public void AnEventTerm_WithoutAnEventBearingType_IsNotAnEvent()
    {
        // "filed" names an event, but the typing says the statement is about earnings — the type is the
        // tie-breaker, and both are required.
        Assert.Equal(
            NewsFactComparisonBasis.NotQuantified,
            StatementComparisonClassifier.Classify("The company filed its quarterly report", Earnings));
        Assert.Equal(
            NewsFactComparisonBasis.Event,
            StatementComparisonClassifier.Classify("The company filed a lawsuit", Regulatory));
    }

    [Fact]
    public void TheEventBearingTypes_AreExactlyTheFiveTheSpecNames()
    {
        Assert.Equal(
            new HashSet<NewsEventType>
            {
                NewsEventType.ContractOrCustomerWin,
                NewsEventType.RegulatoryOrLegal,
                NewsEventType.ProductOrTechnology,
                NewsEventType.MergerAcquisitionOrStake,
                NewsEventType.FinancingOrDilution,
            },
            StatementComparisonClassifier.EventBearingTypes);
    }

    // ---- whole-word matching: the four pinned negatives --------------------------------------------------

    [Theory]
    [InlineData("Investors welcomed the news")] // "vs" — the spec's pinned negative
    [InlineData("The company recorded a charge")] // "record"
    [InlineData("A cutting-edge platform")] // "cut"
    [InlineData("Rosetta Stone announced a product")] // "rose"
    [InlineData("The company reversed its earlier position")] // "vs" inside "reversed"
    public void AComparisonTerm_NeverMatchesInsideAnotherWord(string statement)
    {
        Assert.Equal(
            NewsFactComparisonBasis.NotQuantified,
            StatementComparisonClassifier.Classify(statement, Other));
    }

    [Theory]
    [InlineData("Revenue of $10M vs $8M")]
    [InlineData("A record quarter")]
    [InlineData("Guidance was cut")]
    [InlineData("Sales rose")]
    [InlineData("Backlog grew year-over-year")]
    [InlineData("Backlog grew year over year")]
    [InlineData("An all-time high in bookings")]
    [InlineData("Bookings were flat")]
    public void AWholeComparisonTerm_Matches_IncludingHyphenatedAndMultiWordPhrases(string statement)
    {
        Assert.Equal(
            NewsFactComparisonBasis.StatedComparison,
            StatementComparisonClassifier.Classify(statement, Other));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.Equal(
            NewsFactComparisonBasis.StatedComparison,
            StatementComparisonClassifier.Classify("RECORD REVENUE", Earnings));
        Assert.Equal(
            NewsFactComparisonBasis.Event,
            StatementComparisonClassifier.Classify("FDA APPROVED THE DEVICE", Regulatory));
    }

    [Theory]
    [InlineData("Cryoport to Report Second Quarter 2026 Financial Results on August 6, 2026")]
    [InlineData("Hormel Foods to Report Financial Results Pre-market on Aug. 27")]
    [InlineData("Results for the Six Months Ended June 30, 2026")]
    public void ADayOfMonth_IsNotAFigure_SoAnEarningsCalendarStatementIsNotALevel(string statement)
    {
        // Found in the live inspection sample: the "6" of "August 6" on an EarningsOrGuidance statement
        // was a bare figure, so an earnings-calendar notice read as a stated level. A date is not a quantity.
        Assert.Equal(
            NewsFactComparisonBasis.NotQuantified,
            StatementComparisonClassifier.Classify(statement, Earnings));
    }

    [Theory]
    [InlineData("Energy Recovery (ERII) Stock Faces Profit Squeeze As Revenue Slumps 57%")]
    [InlineData("ATN International swings to $167M profit on tower sale")]
    [InlineData("Debt was reduced by $20 million during the quarter")]
    public void TheDirectionalVerbsTheLiveSampleExposed_AreComparisons(string statement)
    {
        // "slumps", "swings to" and "reduced" were absent from the first draft of the table and the live
        // sample showed them classifying as levels — a missing comparison verb is a table defect, because
        // a judgment citing only such a fact would have been refused a signal it genuinely earned.
        Assert.Equal(
            NewsFactComparisonBasis.StatedComparison,
            StatementComparisonClassifier.Classify(statement, Earnings));
    }

    [Fact]
    public void ADivestitureOrSaleOfAssets_IsAnEvent()
    {
        Assert.Equal(
            NewsFactComparisonBasis.Event,
            StatementComparisonClassifier.Classify(
                "Cogent Communications Announces Closing of Sale of 10 Data Center Facilities",
                [NewsEventType.MergerAcquisitionOrStake]));
    }

    [Theory]
    [InlineData("Revenue guidance for fiscal 2026 was discussed")]
    [InlineData("Revenue guidance for fiscal 2026, the company said, was discussed")]
    [InlineData("In 2026, the company plans to expand")]
    public void ABareCalendarYear_IsNotAFigure_EvenWhenFollowedByAComma(string statement)
    {
        // "in 2026" beside "revenue" must not read as a quantified level — and a year followed by a comma
        // is still a year: the figure grammar takes thousands GROUPS only, never a trailing comma (review
        // round 1 caught "2026," swallowing the comma and escaping the year exclusion).
        Assert.Equal(
            NewsFactComparisonBasis.NotQuantified,
            StatementComparisonClassifier.Classify(statement, Other));
        Assert.Equal(
            NewsFactComparisonBasis.NotQuantified,
            StatementComparisonClassifier.Classify(statement, Earnings));
    }

    [Fact]
    public void AThousandsSeparatedFigure_IsStillAFigure()
    {
        Assert.Equal(
            NewsFactComparisonBasis.LevelOnly,
            StatementComparisonClassifier.Classify("Headcount stood at 1,200 employees", Other));
    }

    [Theory]
    [InlineData("Flat-panel display maker announces a product")]
    [InlineData("Growth strategy outlined at the investor day")]
    [InlineData("Power projects lift Argan (NYSE: AGX) as backlog hits $2.5B")]
    public void OverInclusiveEntriesFromTheLiveSample_NoLongerStateAComparison(string statement)
    {
        // A hyphenated compound is ONE token (flat-panel, above-average); "growth" needs a figure; "lift"
        // needs a metric noun right after it — so the AGX backlog statement stays a level.
        Assert.NotEqual(
            NewsFactComparisonBasis.StatedComparison,
            StatementComparisonClassifier.Classify(statement, Other));
    }

    [Theory]
    [InlineData("Middlesex Water lifts revenue and profit on rate hikes")]
    [InlineData("Cryoport lifts Q2 revenue while posting continuing loss")]
    [InlineData("Napco Security Technologies Crushes Q4 2026 Profit Estimates by 31.6%")]
    [InlineData("Revenue growth of 12% in the quarter")]
    [InlineData("The company posted 12% revenue growth")]
    [InlineData("A record-breaking quarter for bookings")]
    [InlineData("Above-average demand lifted the outlook")] // the explicit hyphenated phrase
    [InlineData("Record-high revenue of $384 million")]
    [InlineData("Wider-than-Expected Loss")]
    [InlineData("Better-than-expected results")]
    [InlineData("Lifts its full-year outlook")] // up to two qualifiers between lift(s) and the metric noun
    public void TheScopedEntries_StillCatchTheDirectionalFormsTheLiveSampleShowed(string statement)
    {
        Assert.Equal(
            NewsFactComparisonBasis.StatedComparison,
            StatementComparisonClassifier.Classify(statement, Earnings));
    }

    // ---- determinism and identity -------------------------------------------------------------------------

    [Fact]
    public void ClassificationIsDeterministic()
    {
        const string Statement = "Power projects lift Argan (NYSE: AGX) as backlog hits $2.5B";
        Assert.Equal(
            StatementComparisonClassifier.Classify(Statement, Contract),
            StatementComparisonClassifier.Classify(Statement, Contract));
    }

    [Fact]
    public void TheVersionToken_IsV1_AndJoinsTheJudgmentCohortKey()
    {
        Assert.Equal("comparison-basis-v1", StatementComparisonClassifier.Version);

        var key = NewsJudgmentContract.CohortKey("openai", "judge-model", "stage1-key");
        Assert.EndsWith("|comparison=comparison-basis-v1", key, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEnumValues_AreExplicit_AndZeroIsUndefined()
    {
        // A defaulted zero must never be a silently meaningful basis.
        Assert.False(Enum.IsDefined(default(NewsFactComparisonBasis)));
        Assert.Equal(1, (int)NewsFactComparisonBasis.StatedComparison);
        Assert.Equal(2, (int)NewsFactComparisonBasis.LevelOnly);
        Assert.Equal(3, (int)NewsFactComparisonBasis.Event);
        Assert.Equal(4, (int)NewsFactComparisonBasis.NotQuantified);
    }

    [Fact]
    public void TheInputBuilder_ClassifiesEachSuppliedFamily_FromItsRepresentativeFact()
    {
        // The classifier is applied at judge-INPUT time (never typing time), from exactly the statement
        // and event types the judge will see.
        var company = Guid.NewGuid();
        var levelFact = Guid.NewGuid();
        var eventFact = Guid.NewGuid();
        var families = new[]
        {
            NewsJudgmentTestData.FamilyRecord(company, levelFact, "Backlog hits $2.5B"),
            NewsJudgmentTestData.FamilyRecord(company, eventFact, "The company won a major contract"),
        };
        var facts = new Dictionary<Guid, NewsTypingFactRef>
        {
            [levelFact] = NewsJudgmentTestData.FactRef(company, levelFact, "Backlog hits $2.5B") with
            {
                Fact = NewsJudgmentTestData.FactRef(company, levelFact, "Backlog hits $2.5B").Fact with
                {
                    EventTypes = [NewsEventType.ContractOrCustomerWin],
                },
            },
            [eventFact] = NewsJudgmentTestData.FactRef(company, eventFact, "The company won a major contract") with
            {
                Fact = NewsJudgmentTestData.FactRef(company, eventFact, "The company won a major contract").Fact with
                {
                    EventTypes = [NewsEventType.ContractOrCustomerWin],
                },
            },
        };

        var bundle = NewsJudgmentInputBuilder.Build(company, families, facts, 50);

        Assert.Equal(
            NewsFactComparisonBasis.LevelOnly,
            bundle.Families.Single(f => f.RepresentativeFactId == levelFact).ComparisonBasis);
        Assert.Equal(
            NewsFactComparisonBasis.Event,
            bundle.Families.Single(f => f.RepresentativeFactId == eventFact).ComparisonBasis);
    }
}
