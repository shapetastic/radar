using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radar.Application.Efficacy.EvidenceConfidence;

/// <summary>
/// Renders the spec-225 measurement as JSON, CSV and Markdown. Pure string production — it touches no disk
/// (<see cref="IEvidenceConfidenceArtifactStore"/> owns that, AD-5).
/// <para>
/// <b>Byte-identical re-runs.</b> No wall-clock timestamp, no machine path, no unordered collection, invariant
/// culture and fixed precision everywhere. <b>Language.</b> Descriptive only (AD-9): no advice vocabulary, no
/// promotion. A value that was not recorded renders as <c>(not recorded)</c> in markdown, an empty cell in
/// CSV and <c>null</c> in JSON — never as <c>0</c>.
/// </para>
/// </summary>
public sealed class EvidenceConfidenceDistributionRenderer
{
    private const string NotRecorded = "(not recorded)";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Enums by NAME: the state tokens ARE the contract a later reader consumes.
            Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>The machine-readable source of truth: every row, every named count, every null.</summary>
    public string RenderJson(EvidenceConfidenceDistributionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    /// <summary>One row per seeded company. Not-recorded values are EMPTY cells.</summary>
    public string RenderCsv(EvidenceConfidenceDistributionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.Append("ticker,name,companyId,state,followingTier,snapshotWindowEndUtc,scoringConfigVersion,")
            .Append("linkCount,linkSignalUnresolvable,linkEvidenceUnresolvable,")
            .Append("evidenceConfidencePersisted,evidenceConfidenceRecomputed,termsDisagreeWithSnapshot,")
            .Append("bestConfidence,bestQualityWeight,distinctSourceTypes,diversityFactor,")
            .Append("trajectory,attention,opportunity,opportunityRecompositionMismatch,")
            .Append("counterfactualOpportunity,actualRank,counterfactualRank,rankDelta,flags,")
            .Append("bestConfidenceSignalId,bestConfidenceSignalType,bestConfidenceSignalDirection,bestConfidenceSignalStrength,")
            .Append("bestConfidenceProducer,bestConfidenceEvidenceSourceType,bestConfidenceCollector,bestConfidenceEvidenceTitle,")
            .Append("bestConfidenceObservedAtUtc,bestConfidenceComparabilityCapNoted,bestConfidenceTieCount,")
            .Append("bestConfidenceTiedSignalTypes,bestConfidenceTiedProducers")
            .Append('\n');

        foreach (var row in report.Rows)
        {
            sb.Append(CsvField.Escape(row.Ticker)).Append(',')
                .Append(CsvField.Escape(row.Name)).Append(',')
                .Append(row.CompanyId.ToString("D")).Append(',')
                .Append(row.State.ToString()).Append(',')
                .Append(row.FollowingTier.ToString()).Append(',')
                .Append(row.SnapshotWindowEndUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(CsvField.Escape(row.ScoringConfigVersion)).Append(',')
                .Append(Int(row.LinkCount)).Append(',')
                .Append(Int(row.LinkSignalUnresolvable)).Append(',')
                .Append(Int(row.LinkEvidenceUnresolvable)).Append(',')
                .Append(Int(row.EvidenceConfidencePersisted)).Append(',')
                .Append(Int(row.EvidenceConfidenceRecomputed)).Append(',')
                .Append(Bool(row.TermsDisagreeWithSnapshot)).Append(',')
                .Append(Dbl(row.BestConfidence)).Append(',')
                .Append(Dbl(row.BestQualityWeight)).Append(',')
                .Append(Int(row.DistinctSourceTypes)).Append(',')
                .Append(Dbl(row.DiversityFactor)).Append(',')
                .Append(Int(row.Trajectory)).Append(',')
                .Append(Int(row.Attention)).Append(',')
                .Append(Int(row.Opportunity)).Append(',')
                .Append(Bool(row.OpportunityRecompositionMismatch)).Append(',')
                .Append(Int(row.CounterfactualOpportunity)).Append(',')
                .Append(Int(row.ActualRank)).Append(',')
                .Append(Int(row.CounterfactualRank)).Append(',')
                .Append(Int(row.RankDelta)).Append(',')
                .Append(CsvField.Escape(string.Join("|", row.Flags))).Append(',')
                .Append(row.BestConfidenceSignalId?.ToString("D") ?? string.Empty).Append(',')
                .Append(row.BestConfidenceSignalType?.ToString() ?? string.Empty).Append(',')
                .Append(row.BestConfidenceSignalDirection?.ToString() ?? string.Empty).Append(',')
                .Append(Int(row.BestConfidenceSignalStrength)).Append(',')
                .Append(row.BestConfidenceProducer?.ToString() ?? string.Empty).Append(',')
                .Append(row.BestConfidenceEvidenceSourceType?.ToString() ?? string.Empty).Append(',')
                .Append(CsvField.Escape(row.BestConfidenceCollector)).Append(',')
                .Append(CsvField.Escape(row.BestConfidenceEvidenceTitle)).Append(',')
                .Append(row.BestConfidenceObservedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(row.BestConfidenceComparabilityCapNoted is { } cap ? Bool(cap) : string.Empty).Append(',')
                .Append(Int(row.BestConfidenceTieCount)).Append(',')
                .Append(row.BestConfidenceTiedSignalTypes is { } types ? CsvField.Escape(string.Join("|", types)) : string.Empty).Append(',')
                .Append(row.BestConfidenceTiedProducers is { } producers ? CsvField.Escape(string.Join("|", producers)) : string.Empty)
                .Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The human-readable report: caveat, header, the one-sentence verdict, then every table.</summary>
    public string RenderMarkdown(EvidenceConfidenceDistributionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("# Does EvidenceConfidence discriminate anything? (`" + report.ArtifactVersion + "`)");
        sb.AppendLine();
        sb.AppendLine(
            "> A MEASUREMENT over persisted score snapshots and their stored evidence links at ONE instant, for "
                + "ONE strategy. It changes no score, weight, formula, fingerprint input or operator step; the "
                + "counterfactual below is computed and NOT applied. A value that was not recorded renders as "
                + "`" + NotRecorded + "`, never as 0.");
        sb.AppendLine();

        if (!report.StrategyConfigured)
        {
            sb.AppendLine("**Idle: nothing was measured.** " + report.StrategyNotConfiguredReason + ".");
            sb.AppendLine();
            sb.AppendLine(report.Verdict.Sentence);
            sb.AppendLine();
            return sb.ToString();
        }

        RenderHeader(sb, report);
        sb.AppendLine("## Verdict (`" + report.Verdict.RuleVersion + "`)");
        sb.AppendLine();
        sb.AppendLine("**" + report.Verdict.Sentence + "**");
        sb.AppendLine();
        var v = report.Verdict;
        sb.AppendLine(
            "The thresholds above are the RULE's choices, not measured facts: flat tax when rank-change share ≤ "
                + Dbl(v.RankChangedShareAtOrBelowIsFlatTax) + " or modal share ≥ "
                + Dbl(v.ModalShareAtOrAboveIsFlatTax) + "; the wrong thing on the collector-count axis when "
                + "|ρ(EvidenceConfidence, DistinctSourceTypes)| ≥ " + Dbl(v.AbsRhoDistinctSourceTypesAtOrAboveIsWrongThing)
                + ", and on the evidence-type axis when the dominant term is `"
                + EvidenceConfidenceDistributionReporter.TermBestConfidence + "` at a log-variance share ≥ "
                + Dbl(v.DominantTermLogVarianceShareAtOrAboveIsSingleTerm)
                + " AND one signal type sets the best confidence of ≥ "
                + Dbl(v.AboveMedianModalSignalTypeShareAtOrAboveIsSingleType)
                + " of the above-median companies; (a) otherwise. Inputs: rank-change share " + DblOr(v.RankChangedShare)
                + ", modal share " + DblOr(v.ModalShare) + ", ρ(EC, DistinctSourceTypes) "
                + DblOr(v.RhoEvidenceConfidenceVsDistinctSourceTypes) + ", dominant term "
                + (v.DominantTerm ?? NotRecorded) + " at " + DblOr(v.DominantTermLogVarianceShare)
                + ", above-median modal type " + (v.AboveMedianModalSignalType ?? NotRecorded) + " "
                + IntOr(v.AboveMedianModalSignalTypeCompanies) + "/" + v.AboveMedianCompanies + " = "
                + DblOr(v.AboveMedianModalSignalTypeShare) + ", ρ(EC, Trajectory) "
                + DblOr(v.RhoEvidenceConfidenceVsTrajectory) + ". Axes fired: "
                + (v.WrongThingAxes.Count == 0 ? "none" : string.Join(", ", v.WrongThingAxes)) + ".");
        sb.AppendLine();
        RenderMeasuredInferredNotEstablished(sb);

        RenderDistribution(sb, report);
        RenderTerms(sb, report);
        RenderTermAttribution(sb, report);
        RenderBestSignal(sb, report);
        RenderAboveMedian(sb, report);
        RenderCorrelations(sb, report);
        RenderCounterfactual(sb, report);
        RenderMovers(sb, report);
        RenderRows(sb, report);
        RenderCounts(sb, report);
        return sb.ToString();
    }

    /// <summary>What this artifact can and cannot say — rendered on every run, because the verdict names a producer.</summary>
    private static void RenderMeasuredInferredNotEstablished(StringBuilder sb)
    {
        sb.AppendLine("**What is measured, what is inferred, what is not established.**");
        sb.AppendLine();
        sb.AppendLine("- **Measured** (through the production code over the store): every distribution, the term "
            + "decomposition, the log-variance shares, every ρ, the held-constant rank changes, and — per company — the "
            + "signal type, direction, strength, evidence source type, recorded collector and evidence title of the "
            + "signal that sets `" + EvidenceConfidenceDistributionReporter.TermBestConfidence + "`.");
        sb.AppendLine("- **Inferred** (`" + SignalProducerRule.Version + "`): the PRODUCER of that signal. Every member is "
            + "read from a producer's own constant or envelope except `" + nameof(SignalProducer.AiEarningsReadDirectional)
            + "`, which the directional read does not stamp and is recognised by elimination (a GuidanceChange over "
            + "Filing evidence with no keyword-rule Reason, no read-outcome envelope and no news-judgment envelope). "
            + "\"Cap noted\" is read from the comparability-cap annotation on the Reason.");
        sb.AppendLine("- **Not established**: whether a higher self-reported confidence marks a more accurate read, or "
            + "whether ranking on it helps or hurts any outcome. That needs reads graded against the filing body (which "
            + "is not persisted — spec 218) and a forward-outcome comparison. Nothing here changes a score; any change — "
            + "for example decoupling EvidenceConfidence from the read that also supplies direction — is a separate spec.");
        sb.AppendLine();
    }

    private static void RenderTermAttribution(StringBuilder sb, EvidenceConfidenceDistributionReport report)
    {
        var a = report.TermAttribution;
        sb.AppendLine("## 2a. Attribution — which term the spread of EvidenceConfidence comes from");
        sb.AppendLine();
        sb.AppendLine("Log-variance share: " + a.LogVarianceRule + "."
            + (a.LogVarianceUndefinedReason is null ? string.Empty : " **Undefined: " + a.LogVarianceUndefinedReason + ".**"));
        sb.AppendLine();
        sb.AppendLine("Held term: " + a.HeldTermRule + "."
            + (a.HeldTermNotAvailableReason is null
                ? " Companies ranked: " + a.HeldTermRankedCount + "."
                : " **Not available: " + a.HeldTermNotAvailableReason + ".**"));
        sb.AppendLine();
        sb.AppendLine("| term | median | ρ(EvidenceConfidence, term) | log-variance share | EC changed when held | rank changed when held | share | max \\|Δrank\\| |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var t in a.Terms)
        {
            sb.AppendLine("| " + t.Term
                + " | " + DblOr(t.Median)
                + " | " + (t.RhoWithEvidenceConfidence is { } rho ? Dbl(rho) : "(" + (t.RhoUndefinedReason ?? "undefined") + ")")
                + " | " + DblOr(t.LogVarianceShare)
                + " | " + IntOr(t.EvidenceConfidenceChangedWhenHeld)
                + " | " + IntOr(t.RankChangedWhenHeld)
                + " | " + DblOr(t.RankChangedShareWhenHeld)
                + " | " + IntOr(t.MaxAbsRankDeltaWhenHeld) + " |");
        }

        sb.AppendLine();
        sb.AppendLine("Dominant term (largest log-variance share, a measured fact): "
            + (a.DominantTerm is null ? NotRecorded : "`" + a.DominantTerm + "` at " + DblOr(a.DominantTermLogVarianceShare)) + ".");
        sb.AppendLine();
    }

    private static void RenderBestSignal(StringBuilder sb, EvidenceConfidenceDistributionReport report)
    {
        var b = report.BestSignal;
        sb.AppendLine("## 2b. Which signal sets `" + EvidenceConfidenceDistributionReporter.TermBestConfidence + "`");
        sb.AppendLine();
        sb.AppendLine("Over the " + report.Distributions.TermRowCount + " term rows (" + b.CompaniesWithBestSignal
            + " with a best-confidence signal recorded). Producer rule `" + b.ProducerRuleVersion + "`. Tie-break: "
            + b.TieBreakRule + ". Companies with more than one signal at the maximum: " + b.CompaniesTiedAtBestConfidence
            + "; of those, spanning more than one signal type: " + b.TiesSpanningSignalTypes
            + ", more than one producer: " + b.TiesSpanningProducers + ". Unclassified producer: "
            + b.UnclassifiedProducer + ".");
        sb.AppendLine();
        sb.AppendLine("| bestConfidence | signal type | producer | companies | share | cap noted |");
        sb.AppendLine("| ---: | --- | --- | ---: | ---: | ---: |");
        if (b.ByValueTypeProducer.Count == 0)
        {
            sb.AppendLine("| " + NotRecorded + " | | | 0 | " + NotRecorded + " | |");
        }

        foreach (var s in b.ByValueTypeProducer)
        {
            sb.AppendLine("| " + Dbl(s.BestConfidence) + " | " + s.SignalType + " | " + s.Producer + " | "
                + s.Companies + " | " + Dbl(s.Share) + " | " + s.ComparabilityCapNoted + " |");
        }

        sb.AppendLine();
        AppendCategories(sb, "producer / direction of the best-confidence signal", b.ByProducerAndDirection);
    }

    private static void RenderAboveMedian(StringBuilder sb, EvidenceConfidenceDistributionReport report)
    {
        var m = report.AboveMedian;
        sb.AppendLine("## 2c. The above-median companies — is the upper half one kind of signal?");
        sb.AppendLine();
        sb.AppendLine("Rule: " + m.Rule + ". Median EvidenceConfidence " + DblOr(m.MedianEvidenceConfidence) + "; "
            + m.Companies + " companies above it. Modal signal type " + (m.ModalSignalType ?? NotRecorded) + " ("
            + IntOr(m.ModalSignalTypeCompanies) + ", share " + DblOr(m.ModalSignalTypeShare) + "); modal producer "
            + (m.ModalProducer ?? NotRecorded) + " (" + IntOr(m.ModalProducerCompanies) + ", share "
            + DblOr(m.ModalProducerShare) + ").");
        sb.AppendLine();
        AppendCategories(sb, "signal type", m.BySignalType);
        AppendCategories(sb, "producer", m.ByProducer);
        AppendCategories(sb, "direction", m.ByDirection);
    }

    private static void RenderMovers(StringBuilder sb, EvidenceConfidenceDistributionReport report)
    {
        var ranked = report.Rows.Where(r => r.RankDelta is not null).ToList();
        sb.AppendLine("## 4a. Largest movers under the held-EvidenceConfidence counterfactual (" + MoverCount
            + " each way, the rule's choice)");
        sb.AppendLine();
        if (ranked.Count == 0)
        {
            sb.AppendLine("Not available: no ranked company.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("A positive Δrank means the company FALLS when EvidenceConfidence is held at the median — its actual "
            + "rank was lifted by its EvidenceConfidence; a negative one means it was held down.");
        sb.AppendLine();
        var fallers = ranked.Where(r => r.RankDelta > 0)
            .OrderByDescending(r => r.RankDelta).ThenBy(r => r.ActualRank).Take(MoverCount).ToList();
        var risers = ranked.Where(r => r.RankDelta < 0)
            .OrderBy(r => r.RankDelta).ThenBy(r => r.ActualRank).Take(MoverCount).ToList();
        AppendMoverTable(sb, "Lifted by EvidenceConfidence (fall when it is held)", fallers);
        AppendMoverTable(sb, "Held down by EvidenceConfidence (rise when it is held)", risers);
    }

    /// <summary>How many movers each way the markdown lists; the rule's choice, stated in the heading.</summary>
    public const int MoverCount = 10;

    /// <summary>The worked-case line for one company row — shared by the movers table and the live harness.</summary>
    public static string MoverCells(EvidenceConfidenceCompanyRow r) =>
        "| " + (r.Ticker ?? NotRecorded)
            + " | " + IntOr(r.ActualRank) + " → " + IntOr(r.CounterfactualRank)
            + " | " + (r.RankDelta is { } d ? (d > 0 ? "+" : string.Empty) + d.ToString(CultureInfo.InvariantCulture) : NotRecorded)
            + " | " + IntOr(r.EvidenceConfidencePersisted)
            + " | " + DblOr(r.BestConfidence)
            + " | " + (r.BestConfidenceSignalType?.ToString() ?? NotRecorded)
            + " (" + (r.BestConfidenceSignalDirection?.ToString() ?? NotRecorded) + ", strength "
            + IntOr(r.BestConfidenceSignalStrength) + ")"
            + " | " + (r.BestConfidenceProducer?.ToString() ?? NotRecorded)
            + " | " + BoolOr(r.BestConfidenceComparabilityCapNoted)
            + " | " + IntOr(r.BestConfidenceTieCount)
            + " | " + Cell(r.BestConfidenceEvidenceTitle)
            + " | " + IntOr(r.Trajectory) + " |";

    public const string MoverHeader =
        "| ticker | rank → cf rank | Δrank | EC | bestConf | best-confidence signal | producer | cap noted | ties | evidence | Trajectory |\n"
            + "| --- | --- | ---: | ---: | ---: | --- | --- | --- | ---: | --- | ---: |";

    private static void AppendMoverTable(StringBuilder sb, string title, IReadOnlyList<EvidenceConfidenceCompanyRow> rows)
    {
        sb.AppendLine("**" + title + "**");
        sb.AppendLine();
        sb.AppendLine(MoverHeader);
        if (rows.Count == 0)
        {
            sb.AppendLine("| (none) | | | | | | | | | | |");
        }

        foreach (var r in rows)
        {
            sb.AppendLine(MoverCells(r));
        }

        sb.AppendLine();
    }

    private static void AppendCategories(
        StringBuilder sb, string name, IReadOnlyList<EvidenceConfidenceCategoryCount> categories)
    {
        sb.AppendLine("| " + name + " | companies | share |");
        sb.AppendLine("| --- | ---: | ---: |");
        if (categories.Count == 0)
        {
            sb.AppendLine("| " + NotRecorded + " | 0 | " + NotRecorded + " |");
        }

        foreach (var c in categories)
        {
            sb.AppendLine("| " + c.Category + " | " + c.Companies + " | " + Dbl(c.Share) + " |");
        }

        sb.AppendLine();
    }

    private static void RenderHeader(StringBuilder sb, EvidenceConfidenceDistributionReport report)
    {
        sb.AppendLine("## What was measured");
        sb.AppendLine();
        sb.AppendLine("| item | value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine("| series | " + report.SeriesDescription + " |");
        sb.AppendLine("| strategy | `" + report.StrategyName + "` (formula `" + report.Formula + "`"
            + (report.FormulaIsV8 ? ", composes Opportunity from EvidenceConfidence" : ", does NOT compose Opportunity from EvidenceConfidence")
            + "; scoring profile `" + report.ScoringProfile + "`) |");
        sb.AppendLine("| instant (`WindowEndUtc`) | " + (report.InstantUtc?.ToString("O", CultureInfo.InvariantCulture) ?? NotRecorded) + " |");
        sb.AppendLine("| window start | " + (report.WindowStartUtc?.ToString("O", CultureInfo.InvariantCulture) ?? NotRecorded) + " |");
        sb.AppendLine("| `ScoringConfigVersion` stamp(s) seen at the instant | "
            + (report.ScoringConfigVersionsSeen.Count == 0 ? NotRecorded : string.Join(", ", report.ScoringConfigVersionsSeen.Select(v => "`" + v + "`")))
            + (report.Counts.MixedScoringConfigVersion > 0 ? " (MIXED — counted)" : string.Empty) + " |");
        sb.AppendLine("| EvidenceConfidence weights (config, as measured under) | "
            + string.Join(", ", report.EvidenceConfidenceWeights.Select(kv => kv.Key + "=" + Dbl(kv.Value))) + " |");
        sb.AppendLine("| companies seeded / included / ranked | " + report.Counts.CompaniesSeeded + " / "
            + report.Counts.CompaniesIncluded + " / " + report.Counts.CompaniesRanked + " |");
        sb.AppendLine();
    }

    private static void RenderDistribution(StringBuilder sb, EvidenceConfidenceDistributionReport report)
    {
        var d = report.Distributions;
        sb.AppendLine("## 1. The live distribution of the persisted `EvidenceConfidenceScore`");
        sb.AppendLine();
        sb.AppendLine("Shares are of the " + d.IncludedCount + " INCLUDED companies. " + d.DistinctEvidenceConfidenceValues
            + " distinct value(s); modal value " + IntOr(d.ModalEvidenceConfidence) + " at share "
            + DblOr(d.ModalEvidenceConfidenceShare) + "; median " + DblOr(d.MedianEvidenceConfidence)
            + " (mean of the two central values on an even count).");
        sb.AppendLine();
        AppendShares(sb, "EvidenceConfidence", d.EvidenceConfidence, v => v.ToString(CultureInfo.InvariantCulture));
    }

    private static void RenderTerms(StringBuilder sb, EvidenceConfidenceDistributionReport report)
    {
        var d = report.Distributions;
        sb.AppendLine("## 2. Term decomposition — which term saturates");
        sb.AppendLine();
        sb.AppendLine("Recomputed through `ScoreSignalMath.EvidenceConfidenceDecomposition` (the production body) from "
            + "each included company's stored links; shares are of the " + d.TermRowCount
            + " included companies whose recomputed score agrees with the persisted one ("
            + report.Counts.TermsDisagreeWithSnapshot + " disagree and are excluded from these tables only).");
        sb.AppendLine();
        AppendShares(sb, "bestConfidence", d.BestConfidence, v => Dbl(v));
        AppendShares(sb, "bestQualityWeight", d.BestQualityWeight, v => Dbl(v));
        AppendShares(sb, "distinctSourceTypes", d.DistinctSourceTypes, v => v.ToString(CultureInfo.InvariantCulture));
        AppendShares(sb, "diversityFactor", d.DiversityFactor, v => Dbl(v));
    }

    private static void RenderCorrelations(StringBuilder sb, EvidenceConfidenceDistributionReport report)
    {
        sb.AppendLine("## 3. Discrimination — Spearman rank correlation (average ranks on ties)");
        sb.AppendLine();
        sb.AppendLine("| pair | n | ρ |");
        sb.AppendLine("| --- | ---: | ---: |");
        foreach (var c in report.Correlations)
        {
            sb.AppendLine("| " + c.Pair + " | " + c.N + " | " + (c.Rho is { } rho ? Dbl(rho) : "(" + (c.UndefinedReason ?? "undefined") + ")") + " |");
        }

        sb.AppendLine();
        sb.AppendLine("Why EvidenceConfidence and Trajectory can correlate without corroborating each other: a directional "
            + "signal's Trajectory mass is strength · Confidence · recency (`ScoreSignalMath.DirectionalMasses`), and the "
            + "largest Confidence in the set IS `" + EvidenceConfidenceDistributionReporter.TermBestConfidence + "`. When "
            + "the best-confidence signal is Positive, one number raises both multiplied terms of Opportunity; when it is "
            + "Negative, the same number raises EvidenceConfidence while pushing Trajectory down. The direction column "
            + "in §2b/§2c shows how often each is the case; the ρ above does not separate the shared input from any "
            + "independent agreement.");
        sb.AppendLine();
    }

    private static void RenderCounterfactual(StringBuilder sb, EvidenceConfidenceDistributionReport report)
    {
        var cf = report.Counterfactual;
        sb.AppendLine("## 4. Counterfactual — EvidenceConfidence held at the universe median (computed, NOT applied)");
        sb.AppendLine();
        if (!cf.Available)
        {
            sb.AppendLine("Not available: " + cf.NotAvailableReason + ".");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("Held value: " + IntOr(cf.HeldEvidenceConfidence) + " (" + cf.HeldValueRule + "). Ranking rule: "
            + cf.RankingRule + ". Recomposition through `ScoreSignalMath.OpportunityComposition` + `NotednessDiscount` "
            + "from the persisted components; a company whose persisted Opportunity does not recompose is excluded "
            + "and counted (" + report.Counts.OpportunityRecompositionMismatch + ").");
        sb.AppendLine();
        sb.AppendLine("| statistic | value |");
        sb.AppendLine("| --- | ---: |");
        sb.AppendLine("| companies ranked | " + cf.CompaniesRanked + " |");
        sb.AppendLine("| companies whose rank changed | " + IntOr(cf.RankChanged) + " (share " + DblOr(cf.RankChangedShare) + ") |");
        sb.AppendLine("| max \\|Δrank\\| | " + IntOr(cf.MaxAbsRankDelta) + (cf.MaxAbsRankDeltaCompany is null ? string.Empty : " (" + cf.MaxAbsRankDeltaCompany + ")") + " |");
        sb.AppendLine("| enter / leave the top " + cf.TopN + " (the rule's choice) | " + IntOr(cf.EnterTopN) + " / " + IntOr(cf.LeaveTopN) + " |");
        sb.AppendLine("| median Opportunity actual → counterfactual | " + DblOr(cf.MedianActualOpportunity) + " → " + DblOr(cf.MedianCounterfactualOpportunity) + " |");
        sb.AppendLine();
    }

    private static void RenderRows(StringBuilder sb, EvidenceConfidenceDistributionReport report)
    {
        sb.AppendLine("## 5. Per company (ordered by actual rank; unranked and excluded rows last, by ticker)");
        sb.AppendLine();
        sb.AppendLine("| ticker | name | EC persisted | EC recomputed | bestConf | bestQualWeight | distinctSourceTypes | divFactor | Trajectory | Attention | tier | Opportunity | Opportunity @ held EC | rank | cf rank | Δrank | flags | best-confidence signal | producer | collector | cap noted | ties |");
        sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | --- | --- | --- | --- | --- | ---: |");
        foreach (var r in report.Rows)
        {
            sb.Append("| ").Append(r.Ticker ?? NotRecorded)
                .Append(" | ").Append(r.Name.Replace("|", "\\|", StringComparison.Ordinal))
                .Append(" | ").Append(IntOr(r.EvidenceConfidencePersisted))
                .Append(" | ").Append(IntOr(r.EvidenceConfidenceRecomputed))
                .Append(" | ").Append(DblOr(r.BestConfidence))
                .Append(" | ").Append(DblOr(r.BestQualityWeight))
                .Append(" | ").Append(IntOr(r.DistinctSourceTypes))
                .Append(" | ").Append(DblOr(r.DiversityFactor))
                .Append(" | ").Append(IntOr(r.Trajectory))
                .Append(" | ").Append(IntOr(r.Attention))
                .Append(" | ").Append(r.FollowingTier.ToString())
                .Append(" | ").Append(IntOr(r.Opportunity))
                .Append(" | ").Append(IntOr(r.CounterfactualOpportunity))
                .Append(" | ").Append(IntOr(r.ActualRank))
                .Append(" | ").Append(IntOr(r.CounterfactualRank))
                .Append(" | ").Append(r.RankDelta is { } delta ? (delta > 0 ? "+" : string.Empty) + delta.ToString(CultureInfo.InvariantCulture) : NotRecorded)
                .Append(" | ").Append(r.Flags.Count == 0 ? "—" : string.Join(", ", r.Flags))
                .Append(" | ").Append(r.BestConfidenceSignalType is { } type
                    ? type + " (" + (r.BestConfidenceSignalDirection?.ToString() ?? NotRecorded) + ")"
                    : NotRecorded)
                .Append(" | ").Append(r.BestConfidenceProducer?.ToString() ?? NotRecorded)
                .Append(" | ").Append(Cell(r.BestConfidenceCollector))
                .Append(" | ").Append(BoolOr(r.BestConfidenceComparabilityCapNoted))
                .Append(" | ").Append(IntOr(r.BestConfidenceTieCount))
                .AppendLine(" |");
        }

        sb.AppendLine();
    }

    private static void RenderCounts(StringBuilder sb, EvidenceConfidenceDistributionReport report)
    {
        var c = report.Counts;
        sb.AppendLine("## 6. Counted axes");
        sb.AppendLine();
        sb.AppendLine("Seeded = included + NoSnapshot + SnapshotNotAtInstant + NoSignalsInWindow + CompanyWithUnresolvableLink: "
            + (c.Reconciles ? "reconciles" : "DOES NOT RECONCILE") + ".");
        sb.AppendLine();
        sb.AppendLine("| axis | count |");
        sb.AppendLine("| --- | ---: |");
        sb.AppendLine("| CompaniesSeeded | " + c.CompaniesSeeded + " |");
        sb.AppendLine("| CompaniesIncluded | " + c.CompaniesIncluded + " |");
        sb.AppendLine("| CompaniesRanked | " + c.CompaniesRanked + " |");
        sb.AppendLine("| NoSnapshot | " + c.NoSnapshot + " |");
        sb.AppendLine("| SnapshotNotAtInstant | " + c.SnapshotNotAtInstant + " |");
        sb.AppendLine("| NoSignalsInWindow (defaulted zero, NOT RECORDED) | " + c.NoSignalsInWindow + " |");
        sb.AppendLine("| LinkSignalUnresolvable (links) | " + c.LinkSignalUnresolvable + " |");
        sb.AppendLine("| LinkEvidenceUnresolvable (links) | " + c.LinkEvidenceUnresolvable + " |");
        sb.AppendLine("| CompanyWithUnresolvableLink | " + c.CompanyWithUnresolvableLink + " |");
        sb.AppendLine("| TermsDisagreeWithSnapshot | " + c.TermsDisagreeWithSnapshot + " |");
        sb.AppendLine("| OpportunityRecompositionMismatch | " + c.OpportunityRecompositionMismatch + " |");
        sb.AppendLine("| FormulaDoesNotComposeOpportunityFromEvidenceConfidence | " + c.FormulaDoesNotComposeOpportunityFromEvidenceConfidence + " |");
        sb.AppendLine("| MixedScoringConfigVersion (distinct stamps when > 1) | " + c.MixedScoringConfigVersion + " |");
        sb.AppendLine();
    }

    private static void AppendShares<T>(
        StringBuilder sb, string name, IReadOnlyList<EvidenceConfidenceValueShare<T>> shares, Func<T, string> format)
    {
        sb.AppendLine("| " + name + " | companies | share |");
        sb.AppendLine("| ---: | ---: | ---: |");
        if (shares.Count == 0)
        {
            sb.AppendLine("| " + NotRecorded + " | 0 | " + NotRecorded + " |");
        }

        foreach (var s in shares)
        {
            sb.AppendLine("| " + format(s.Value) + " | " + s.Companies + " | " + Dbl(s.Share) + " |");
        }

        sb.AppendLine();
    }

    private static string Int(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Dbl(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Bool(bool value) => value ? "true" : "false";

    private static string IntOr(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? NotRecorded;

    private static string BoolOr(bool? value) => value is { } b ? (b ? "yes" : "no") : NotRecorded;

    /// <summary>A free-text markdown cell: not recorded when null, pipes escaped, newlines flattened.</summary>
    private static string Cell(string? value) =>
        value is null
            ? NotRecorded
            : value.Replace("|", "\\|", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);

    private static string DblOr(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? NotRecorded;
}
