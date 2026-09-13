using System.Globalization;
using System.Text.Json;

using Radar.Application.Efficacy.EvidenceConfidence;

namespace Radar.Application.Tests.Efficacy.EvidenceConfidence;

/// <summary>
/// The three renderings of one report: the markdown carries the verdict sentence and renders every
/// not-recorded value as <c>(not recorded)</c>; the CSV has one row per seeded company with empty cells for
/// nulls; the JSON is camelCase with <c>null</c>s; and every number is invariant-culture.
/// </summary>
public sealed class EvidenceConfidenceDistributionRendererTests
{
    private static async Task<EvidenceConfidenceDistributionReport> ReportAsync()
    {
        var u = new EvidenceConfidenceTestUniverse();
        u.AddCompany("AAA", 80, 0, [EvidenceConfidenceTestUniverse.MediumPressRelease]);
        u.AddCompany("BBB", 60, 0, [EvidenceConfidenceTestUniverse.HighFiling, EvidenceConfidenceTestUniverse.HighNews, EvidenceConfidenceTestUniverse.MediumInsider]);
        u.AddCompany("CCC", 52, 0, [EvidenceConfidenceTestUniverse.HighFiling]);
        u.AddCompany("DDD", 45, 0, [EvidenceConfidenceTestUniverse.MediumPressRelease, EvidenceConfidenceTestUniverse.MediumInsider]);
        u.AddCompany("EEE", 0, 0, []);            // zero links → NoSignalsInWindow, everything not recorded
        u.AddCompanyWithoutSnapshot("FFF");        // never scored → NoSnapshot
        return await u.BuildAsync();
    }

    [Fact]
    public async Task Markdown_CarriesTheVerdictSentence_AndRendersNullsAsNotRecorded_NeverZero()
    {
        var report = await ReportAsync();
        var md = new EvidenceConfidenceDistributionRenderer().RenderMarkdown(report);

        Assert.Contains("`" + EvidenceConfidenceDistributionReporter.ArtifactVersion + "`", md, StringComparison.Ordinal);
        Assert.Contains(report.Verdict.Sentence, md, StringComparison.Ordinal);
        Assert.Contains("it changes no score", md, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(not recorded)", md, StringComparison.Ordinal);

        // The zero-link row renders NOT RECORDED in every component cell, and the no-snapshot row likewise.
        var eee = md.Split('\n').Single(l => l.StartsWith("| EEE |", StringComparison.Ordinal));
        Assert.Contains("NoSignalsInWindow", eee, StringComparison.Ordinal);
        Assert.DoesNotContain("| 0 |", eee, StringComparison.Ordinal);
        var fff = md.Split('\n').Single(l => l.StartsWith("| FFF |", StringComparison.Ordinal));
        Assert.Contains("NoSnapshot", fff, StringComparison.Ordinal);

        Assert.Contains("## 6. Counted axes", md, StringComparison.Ordinal);
        Assert.Contains("| NoSignalsInWindow (defaulted zero, NOT RECORDED) | 1 |", md, StringComparison.Ordinal);
        Assert.Contains("| NoSnapshot | 1 |", md, StringComparison.Ordinal);
        Assert.Contains("reconciles", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_HasOneRowPerSeededCompany_WithEmptyCellsForNulls()
    {
        var report = await ReportAsync();
        var csv = new EvidenceConfidenceDistributionRenderer().RenderCsv(report);

        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal(1 + report.Rows.Count, lines.Length);
        Assert.StartsWith("ticker,name,companyId,state,", lines[0], StringComparison.Ordinal);

        var eee = lines.Single(l => l.StartsWith("EEE,", StringComparison.Ordinal));
        Assert.Contains(",NoSignalsInWindow,", eee, StringComparison.Ordinal);
        // evidenceConfidencePersisted .. rankDelta are all empty for a not-recorded row.
        Assert.Contains(",,,false,,,,,,,,false,,,,,", eee, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_IsCamelCase_WithNullsForNotRecorded_AndRoundTripsKeyFields()
    {
        var report = await ReportAsync();
        var json = new EvidenceConfidenceDistributionRenderer().RenderJson(report);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(EvidenceConfidenceDistributionReporter.ArtifactVersion, root.GetProperty("artifactVersion").GetString());
        Assert.Equal(report.Counts.CompaniesIncluded, root.GetProperty("counts").GetProperty("companiesIncluded").GetInt32());
        Assert.Equal(report.Verdict.Verdict.ToString(), root.GetProperty("verdict").GetProperty("verdict").GetString());
        Assert.Equal(report.Rows.Count, root.GetProperty("rows").GetArrayLength());

        var eee = root.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("ticker").GetString() == "EEE");
        Assert.Equal(JsonValueKind.Null, eee.GetProperty("evidenceConfidencePersisted").ValueKind);
        Assert.Equal(JsonValueKind.Null, eee.GetProperty("opportunity").ValueKind);
        Assert.Equal("NoSignalsInWindow", eee.GetProperty("state").GetString());
    }

    [Fact]
    public async Task EveryRendering_IsInvariantCulture_UnderACommaDecimalCulture()
    {
        var report = await ReportAsync();
        var renderer = new EvidenceConfidenceDistributionRenderer();

        var invariantMd = renderer.RenderMarkdown(report);
        var invariantCsv = renderer.RenderCsv(report);
        var invariantJson = renderer.RenderJson(report);

        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Assert.Equal(invariantMd, renderer.RenderMarkdown(report));
            Assert.Equal(invariantCsv, renderer.RenderCsv(report));
            Assert.Equal(invariantJson, renderer.RenderJson(report));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.DoesNotContain("0,5", invariantMd, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Markdown_CarriesTheAttributionBestSignalAboveMedianAndMoverSections_WithTheInferenceStated()
    {
        var report = await ReportAsync();
        var md = new EvidenceConfidenceDistributionRenderer().RenderMarkdown(report);

        Assert.Contains("## 2a. Attribution — which term the spread of EvidenceConfidence comes from", md, StringComparison.Ordinal);
        Assert.Contains("## 2b. Which signal sets `bestConfidence`", md, StringComparison.Ordinal);
        Assert.Contains("## 2c. The above-median companies", md, StringComparison.Ordinal);
        Assert.Contains("## 4a. Largest movers under the held-EvidenceConfidence counterfactual", md, StringComparison.Ordinal);
        Assert.Contains("**What is measured, what is inferred, what is not established.**", md, StringComparison.Ordinal);
        Assert.Contains("`" + SignalProducerRule.Version + "`", md, StringComparison.Ordinal);
        Assert.Contains("recognised by elimination", md, StringComparison.Ordinal);
        Assert.Contains("Dominant term (largest log-variance share, a measured fact): `" + report.TermAttribution.DominantTerm + "`", md, StringComparison.Ordinal);
        Assert.Contains("Axes fired:", md, StringComparison.Ordinal);
        Assert.Contains("when it is Negative, the same number raises EvidenceConfidence while pushing Trajectory down", md, StringComparison.Ordinal);

        // The excluded rows render the best-confidence columns NOT RECORDED, never a guessed producer.
        var eee = md.Split('\n').Single(l => l.StartsWith("| EEE |", StringComparison.Ordinal));
        Assert.DoesNotContain("Unclassified", eee, StringComparison.Ordinal);
        Assert.EndsWith("| (not recorded) | (not recorded) | (not recorded) | (not recorded) | (not recorded) |", eee.TrimEnd('\r'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_EveryRowHasTheHeadersFieldCount_IncludingTheBestConfidenceColumns()
    {
        var report = await ReportAsync();
        var csv = new EvidenceConfidenceDistributionRenderer().RenderCsv(report);

        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.EndsWith(",bestConfidenceTiedSignalTypes,bestConfidenceTiedProducers", lines[0], StringComparison.Ordinal);
        var expected = FieldCount(lines[0]);
        Assert.All(lines, l => Assert.Equal(expected, FieldCount(l)));

        var bbb = lines.Single(l => l.StartsWith("BBB,", StringComparison.Ordinal));
        Assert.Contains(",Unclassified,", bbb, StringComparison.Ordinal);
    }

    /// <summary>Counts CSV fields, honouring RFC-4180 quoting (a comma inside quotes is not a separator).</summary>
    private static int FieldCount(string line)
    {
        var count = 1;
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public async Task AnIdleReport_RendersTheIdleState_AndTheNotDeterminedVerdict()
    {
        var u = new EvidenceConfidenceTestUniverse
        {
            Strategies = new Radar.Application.Scoring.ScoringStrategySet(
            [
                new Radar.Application.Scoring.ScoringStrategyDefinition(
                    "other", "default", new Radar.Application.Scoring.ScoringWeights(), IsPrimary: true),
            ]),
        };
        var report = await u.BuildAsync();
        var md = new EvidenceConfidenceDistributionRenderer().RenderMarkdown(report);

        Assert.Contains("Idle: nothing was measured", md, StringComparison.Ordinal);
        Assert.Contains("NOT DETERMINED", md, StringComparison.Ordinal);
    }
}
