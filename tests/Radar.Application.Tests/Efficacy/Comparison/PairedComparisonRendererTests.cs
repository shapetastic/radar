using System.Globalization;

using Radar.Application.Efficacy.Claims;
using Radar.Application.Efficacy.Comparison;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// The rendered paired comparison must state the model limitation BESIDE every interval, disclose the
/// boundary (or its absence), the supports, the dropped dates and the arms considered, stay AD-9-clean, be
/// byte-stable — and, since spec 170, render the COMPOSITE AD-15 gate: the "adding value" sentence only for a
/// qualifying composite verdict, an unmet AD-16 prerequisite named beside a price-side pass, and a
/// prerequisite met by Miss stated before the licence sentence.
/// </summary>
public sealed class PairedComparisonRendererTests
{
    private static readonly PairedComparisonRenderer Renderer = new();
    private static readonly PairedComparisonHarness Harness = new();

    /// <summary>The hard-rule forbidden terms (CLAUDE.md "Output language"), plus obvious near-misses.</summary>
    private static readonly string[] ForbiddenTerms =
    [
        "buy", "sell", "guaranteed upside", "safe bet", "guaranteed", "outperform", "price target",
    ];

    private static Ad15ClaimVerdict Verdict(PairedStrategyComparison result, Ad16ScreenOutcome outcome) =>
        Ad15ClaimGate.Evaluate(
            result.SatisfiesPriceGate, result.PriceGateReasons, Ad15AttentionPrerequisite.For(outcome));

    private static Ad15ClaimVerdict AbsentVerdict(PairedStrategyComparison result) =>
        Ad15ClaimGate.Evaluate(result.SatisfiesPriceGate, result.PriceGateReasons, attentionPrerequisite: null);

    private static PairedStrategyComparison PriceGateTrue() => Harness.Compare(
        [
            PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(7)),
            PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Spaced(7)),
            PairedFixtures.Series("baseline-b", PairedFixtures.AntiAligned, PairedFixtures.Spaced(7)),
        ],
        "primary",
        primaryWasPredeclared: true,
        PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

    private static PairedStrategyComparison Exploratory() => Harness.Compare(
        [
            PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Daily(30)),
            PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Daily(30)),
        ],
        "primary",
        primaryWasPredeclared: false,
        PairedFixtures.Options(configuredPrimary: ""));

    private static PairedStrategyComparison NoBaselines() => Harness.Compare(
        [PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(3))],
        "primary",
        primaryWasPredeclared: true,
        PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

    [Fact]
    public void RenderMarkdown_StatesTheLimitationBesideEveryInterval_NotOnlyInAFootnote()
    {
        var result = PriceGateTrue();
        var markdown = Renderer.RenderMarkdown(result, Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen));
        var lines = markdown.Split('\n');

        // Every line that prints an order-statistic interval carries the conditional-model limitation ON
        // THAT LINE — quoting the interval without it is impossible.
        var intervalLines = lines
            .Where(l => l.Contains("order-statistic interval", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("- ", StringComparison.Ordinal))   // exclude the how-to-read bullet
            .ToList();
        Assert.Equal(2, intervalLines.Count);                            // one per baseline
        Assert.All(intervalLines, l =>
            Assert.Contains(
                "cannot prove independence or stationarity across market regimes",
                l,
                StringComparison.Ordinal));
        Assert.All(intervalLines, l =>
            Assert.Contains("ties make the order-statistic interval conservative", l, StringComparison.Ordinal));
    }

    [Fact]
    public void RenderMarkdown_DisclosesBoundarySupportsDroppedDatesBlockCountAndArmsConsidered()
    {
        var result = PriceGateTrue();
        var markdown = Renderer.RenderMarkdown(result, Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen));

        Assert.Contains("Precommitted first eligible as-of date: **2026-01-01**", markdown, StringComparison.Ordinal);
        Assert.Contains("Arms considered: 3", markdown, StringComparison.Ordinal);
        Assert.Contains("baselines compared: 2", markdown, StringComparison.Ordinal);
        Assert.Contains("Joint intersection across the primary and every baseline", markdown, StringComparison.Ordinal);
        Assert.Contains("## Purged blocks (7 admitted)", markdown, StringComparison.Ordinal);
        Assert.Contains("| primary |", markdown, StringComparison.Ordinal);        // marginal support table
        Assert.Contains("Pairwise primary∩baseline intersections", markdown, StringComparison.Ordinal);
        Assert.Contains("Companies are never pooled across dates", markdown, StringComparison.Ordinal);
        Assert.Contains("Daily candidate dates are NOT independent", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_RendersBothSupports_AndTheInstantExclusionCountsWithTheirUnits()
    {
        var result = PriceGateTrue();
        var markdown = Renderer.RenderMarkdown(result, Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen));

        // All-history vs eligible: two labelled numbers, never one figure under two meanings (spec 170 §3).
        Assert.Contains("ALL history — this figure describes the dataset, never the claim", markdown, StringComparison.Ordinal);
        Assert.Contains("Eligible joint support (the CLAIM's support", markdown, StringComparison.Ordinal);

        // The two instant counters, with their DIFFERENT units labelled (observations vs keys).
        Assert.Contains("unit: de-duped company-day observations", markdown, StringComparison.Ordinal);
        Assert.Contains("unit: keys, not observations", markdown, StringComparison.Ordinal);

        // Per-block company N is rendered for the admitted blocks.
        Assert.Contains("| block date | companies | observed entry | observed exit |", markdown, StringComparison.Ordinal);
        Assert.Contains("| block date | companies | primary rho | baseline rho | paired delta |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_NoBoundary_RendersTheEligibleSupportAsEmpty_NeverTheAllHistoryNumber()
    {
        var result = Exploratory();
        var markdown = Renderer.RenderMarkdown(result, AbsentVerdict(result));

        Assert.Contains("Eligible joint support (the CLAIM's support): **empty**", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_MissingBoundaryAndPredeclaration_AreNamedAndTheResultIsLabelledExploratory()
    {
        var result = Exploratory();
        var markdown = Renderer.RenderMarkdown(result, AbsentVerdict(result));

        Assert.Contains("Status: EXPLORATORY", markdown, StringComparison.Ordinal);
        Assert.Contains("No primary was predeclared", markdown, StringComparison.Ordinal);
        Assert.Contains("no-precommitted-evaluation-boundary", markdown, StringComparison.Ordinal);
        Assert.Contains("Qualifies under AD-15's amended gate: no.", markdown, StringComparison.Ordinal);

        // Dropped dates are rendered with their machine tokens (the dense fixture purges most dates).
        Assert.Contains("overlapping-outcome-window", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_NoBaselines_SaysSoAndClaimsNothing()
    {
        var result = NoBaselines();
        var markdown = Renderer.RenderMarkdown(result, AbsentVerdict(result));

        Assert.Contains("no-baselines", markdown, StringComparison.Ordinal);
        Assert.Contains("nothing is being claimed", markdown, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ spec 170: the composite gate

    [Fact]
    public void RenderMarkdown_QualifyingCompositeVerdict_IsAboutRadarScoringNeverAboutAnyAction()
    {
        var result = PriceGateTrue();
        var markdown = Renderer.RenderMarkdown(result, Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen));

        Assert.Contains("Qualifies under AD-15's amended gate: yes.", markdown, StringComparison.Ordinal);
        Assert.Contains("adding value relative to these baselines under AD-15's gate", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "a statement about Radar's scoring, never about any company, security or action",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_PriceGatePassWithNoPrerequisite_RendersNoAddingValueSentence_AndNamesIt()
    {
        // The attention generator is disabled ⇒ the Worker passes null ⇒ the gate reads not-calculated.
        var result = PriceGateTrue();
        Assert.True(result.SatisfiesPriceGate);

        var markdown = Renderer.RenderMarkdown(result, AbsentVerdict(result));

        Assert.DoesNotContain("adding value", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Qualifies under AD-15's amended gate: yes", markdown, StringComparison.Ordinal);
        Assert.Contains("satisfies the price gate: **yes**", markdown, StringComparison.Ordinal);
        Assert.Contains("ad16-screen-not-calculated", markdown, StringComparison.Ordinal);
        Assert.Contains("NO claim is licensed", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_PrerequisiteMetByMiss_StatesTheMissBeforeTheLicenceSentence()
    {
        var result = PriceGateTrue();
        var verdict = Verdict(result, Ad16ScreenOutcome.Miss);
        Assert.True(verdict.Qualifies);   // Miss SATISFIES the prerequisite (calculated, not passed)

        var markdown = Renderer.RenderMarkdown(result, verdict);

        var missIndex = markdown.IndexOf(
            "attention-arrival screen returned `Miss`", StringComparison.Ordinal);
        var licenceIndex = markdown.IndexOf(
            "Qualifies under AD-15's amended gate: yes.", StringComparison.Ordinal);
        Assert.True(missIndex >= 0, "the Miss must be stated in the claim block");
        Assert.True(licenceIndex >= 0, "the licence sentence must exist for a qualifying composite verdict");
        Assert.True(
            missIndex < licenceIndex,
            "the Miss must be stated BEFORE the licence sentence, in the same block");
    }

    [Fact]
    public void RenderMarkdown_PendingAndUnavailableAndInvalid_AllRefuseTheClaimNamingTheirCode()
    {
        var result = PriceGateTrue();
        foreach (var (outcome, code) in new[]
        {
            (Ad16ScreenOutcome.Pending, "ad16-screen-pending"),
            (Ad16ScreenOutcome.Unavailable, "ad16-screen-unavailable"),
            (Ad16ScreenOutcome.Invalid, "ad16-screen-invalid"),
        })
        {
            var markdown = Renderer.RenderMarkdown(result, Verdict(result, outcome));
            Assert.DoesNotContain("adding value", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(code, markdown, StringComparison.Ordinal);
            Assert.Contains("NO claim is licensed", markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RenderMarkdown_VerdictFromAnotherResult_IsRefused()
    {
        var result = PriceGateTrue();
        var exploratory = Exploratory();
        var foreignVerdict = Verdict(exploratory, Ad16ScreenOutcome.ClearsNecessaryScreen);

        Assert.Throws<InvalidOperationException>(() => Renderer.RenderMarkdown(result, foreignVerdict));
        Assert.Throws<InvalidOperationException>(() => Renderer.RenderCsv(result, foreignVerdict));
    }

    [Fact]
    public void RenderMarkdown_PointsAtTheDescriptiveMarginalLeaderboard()
    {
        var result = PriceGateTrue();
        var markdown = Renderer.RenderMarkdown(result, Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen));

        Assert.Contains("strategy-leaderboard.md", markdown, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTIVE", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "only result that can support the amended AD-15 claim", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCsv_OneRowPerBaselineWithAlignedColumns_AndSignTestLabelledByItsOwnColumns()
    {
        var result = PriceGateTrue();
        var csv = Renderer.RenderCsv(result, Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen));
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("status,primaryStrategy,primaryPredeclared,firstEligibleAsOf,", lines[0], StringComparison.Ordinal);
        Assert.Equal(3, lines.Length);                                   // header + 2 baselines

        var header = lines[0].Split(',');
        foreach (var line in lines)
        {
            Assert.Equal(header.Length, SplitCsv(line));
        }

        Assert.Contains("signTestP", lines[0], StringComparison.Ordinal);
        Assert.Contains("signTestZeroDeltasDropped", lines[0], StringComparison.Ordinal);

        // The rename plus the composite columns (spec 170): the price half is satisfiesPriceGate; the claim
        // is qualifiesUnderAd15 with the prerequisite outcome beside it; the support/instant columns are
        // additive at the end, their units carried by their names.
        Assert.Contains("satisfiesPriceGate", lines[0], StringComparison.Ordinal);
        Assert.Contains("qualifiesUnderAd15", lines[0], StringComparison.Ordinal);
        Assert.Contains("ad16ScreenOutcome", lines[0], StringComparison.Ordinal);
        Assert.Contains("eligibleJointObservations", lines[0], StringComparison.Ordinal);
        Assert.Contains("observationsWithoutAsOfInstant", lines[0], StringComparison.Ordinal);
        Assert.Contains("mismatchedAsOfInstantKeys", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("qualifiesUnderAd15,gateReasons", lines[0], StringComparison.Ordinal);

        Assert.All(lines.Skip(1), l => Assert.StartsWith("baseline,", l, StringComparison.Ordinal));
        Assert.All(lines.Skip(1), l => Assert.Contains(",clears-necessary-screen,", l, StringComparison.Ordinal));
    }

    [Fact]
    public void RenderCsv_PriceGatePassWithoutPrerequisite_SatisfiesPriceGateTrueButQualifiesFalse()
    {
        var result = PriceGateTrue();
        var csv = Renderer.RenderCsv(result, AbsentVerdict(result));
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var header = lines[0].Split(',');
        var satisfiesIndex = Array.IndexOf(header, "satisfiesPriceGate");
        var qualifiesIndex = Array.IndexOf(header, "qualifiesUnderAd15");
        var outcomeIndex = Array.IndexOf(header, "ad16ScreenOutcome");
        Assert.True(satisfiesIndex >= 0 && qualifiesIndex >= 0 && outcomeIndex >= 0);

        foreach (var line in lines.Skip(1))
        {
            var fields = SplitCsvFields(line);
            Assert.Equal("true", fields[satisfiesIndex]);
            Assert.Equal("false", fields[qualifiesIndex]);
            Assert.Equal("not-calculated", fields[outcomeIndex]);
            Assert.Contains("ad16-screen-not-calculated", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RenderCsv_CarriesTheGateVerdictIdAsAnAdditiveTrailingRunLevelColumn()
    {
        // Spec 186 §3. The paired CSV carries NO schemaVersion column of its own (unlike the spec-183
        // leaderboard), so there is no tag to bump: the identity column is appended, and every reader
        // resolves columns BY HEADER NAME — asserted here by pinning that no pre-186 column moved.
        var result = PriceGateTrue();
        var verdict = Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen);
        var csv = Renderer.RenderCsv(result, verdict);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(',');

        Assert.DoesNotContain("schemaVersion", header);
        Assert.Equal("gateVerdictId", header[^1]);
        Assert.Equal(PreSpec186Columns.Length + 1, header.Length);
        for (var i = 0; i < PreSpec186Columns.Length; i++)
        {
            Assert.Equal(PreSpec186Columns[i], header[i]);   // no by-name reader can shift
        }

        var expected = GateVerdictIdentity.Compute(result, verdict);
        Assert.NotEmpty(expected);
        Assert.All(lines.Skip(1), l => Assert.Equal(expected, SplitCsvFields(l)[^1]));
    }

    [Fact]
    public void RenderCsv_ExploratoryArtifact_LeavesTheGateVerdictIdEmpty()
    {
        // No predeclared primary and no boundary ⇒ no verdict ⇒ nothing to override.
        var result = Exploratory();
        var csv = Renderer.RenderCsv(result, AbsentVerdict(result));
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var idIndex = Array.IndexOf(lines[0].Split(','), "gateVerdictId");

        Assert.All(lines.Skip(1), l => Assert.Equal(string.Empty, SplitCsvFields(l)[idIndex]));
    }

    [Fact]
    public void RenderMarkdown_StatesTheGateVerdictId_SoAnOverrideCanBindToIt()
    {
        var result = PriceGateTrue();
        var verdict = Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen);

        Assert.Contains(
            "Gate verdict id: `" + GateVerdictIdentity.Compute(result, verdict) + "`",
            Renderer.RenderMarkdown(result, verdict),
            StringComparison.Ordinal);

        var exploratory = Exploratory();
        Assert.Contains(
            "Gate verdict id: **none**",
            Renderer.RenderMarkdown(exploratory, AbsentVerdict(exploratory)),
            StringComparison.Ordinal);
    }

    /// <summary>The pre-186 column set, in order — the by-header-name contract this slice must not disturb.</summary>
    private static readonly string[] PreSpec186Columns =
    [
        "status", "primaryStrategy", "primaryPredeclared", "firstEligibleAsOf", "armsConsidered",
        "baselinesCompared", "baseline", "jointObservations", "jointCompanies", "jointDates",
        "candidateDates", "droppedDates", "developmentDates", "inconsistentOutcomeObservations",
        "purgedBlocks", "medianPairedDelta", "intervalLower95", "intervalUpper95", "intervalCoverage",
        "intervalReason", "signTestP", "signTestEffectiveN", "signTestZeroDeltasDropped", "baselineClears",
        "satisfiesPriceGate", "gateReasons", "qualifiesUnderAd15", "ad16ScreenOutcome",
        "eligibleJointObservations", "eligibleJointCompanies", "eligibleJointDates",
        "observationsWithoutAsOfInstant", "mismatchedAsOfInstantKeys",
    ];

    [Fact]
    public void RenderBlocksCsv_OneRowPerBaselinePerAdmittedBlock_WithCompanyNAndBothRhos()
    {
        var result = PriceGateTrue();
        var blocksCsv = Renderer.RenderBlocksCsv(result);
        var lines = blocksCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("baseline,blockDate,companies,primaryRho,baselineRho,pairedDelta", lines[0]);
        // 2 baselines × 7 admitted blocks.
        Assert.Equal(1 + (2 * 7), lines.Length);
        Assert.Contains("baseline-a,2026-01-01,4,1.0000,-1.0000,2.0000", lines.Skip(1));
    }

    [Fact]
    public void RenderBlocksCsv_NoBaselines_IsHeaderOnly()
    {
        var blocksCsv = Renderer.RenderBlocksCsv(NoBaselines());
        Assert.Equal("baseline,blockDate,companies,primaryRho,baselineRho,pairedDelta\n", blocksCsv);
    }

    [Fact]
    public void RenderCsv_NoBaselines_StillOneParseableRowWithItsStatus()
    {
        var result = NoBaselines();
        var csv = Renderer.RenderCsv(result, AbsentVerdict(result));
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("no-baselines,", lines[1], StringComparison.Ordinal);
        Assert.Equal(lines[0].Split(',').Length, SplitCsv(lines[1]));
    }

    [Fact]
    public void RenderedOutput_ContainsNoFinancialAdviceLanguage()
    {
        var outcomes = new[]
        {
            Ad16ScreenOutcome.NotCalculated,
            Ad16ScreenOutcome.Unavailable,
            Ad16ScreenOutcome.Pending,
            Ad16ScreenOutcome.Miss,
            Ad16ScreenOutcome.ClearsNecessaryScreen,
            Ad16ScreenOutcome.Invalid,
        };

        foreach (var result in new[] { PriceGateTrue(), Exploratory(), NoBaselines() })
        {
            foreach (var outcome in outcomes)
            {
                var verdict = Verdict(result, outcome);
                foreach (var text in new[]
                {
                    Renderer.RenderMarkdown(result, verdict),
                    Renderer.RenderCsv(result, verdict),
                    Renderer.RenderBlocksCsv(result),
                })
                {
                    foreach (var term in ForbiddenTerms)
                    {
                        Assert.DoesNotContain(term, text, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }

        var gateTrue = PriceGateTrue();
        Assert.Contains(
            PairedComparisonRenderer.Framing,
            Renderer.RenderMarkdown(gateTrue, Verdict(gateTrue, Ad16ScreenOutcome.ClearsNecessaryScreen)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Render_IsCultureInvariantAndByteStable()
    {
        var result = PriceGateTrue();
        var verdict = Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen);

        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var deCsv = Renderer.RenderCsv(result, verdict);
            var deMarkdown = Renderer.RenderMarkdown(result, verdict);
            var deBlocks = Renderer.RenderBlocksCsv(result);

            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(Renderer.RenderCsv(result, verdict), deCsv);
            Assert.Equal(Renderer.RenderMarkdown(result, verdict), deMarkdown);
            Assert.Equal(Renderer.RenderBlocksCsv(result), deBlocks);

            Assert.DoesNotContain(";", deCsv.Split('\n')[1], StringComparison.Ordinal);
            Assert.Contains(".", deCsv, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    /// <summary>Column count of one CSV line, respecting quoted fields (the shared CsvField rule).</summary>
    private static int SplitCsv(string line) => SplitCsvFields(line).Count;

    /// <summary>
    /// The fields of one CSV line, respecting quoted fields and unescaping CsvField's doubled-quote
    /// escape ("" inside a quoted field yields one literal quote).
    /// </summary>
    private static List<string> SplitCsvFields(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
