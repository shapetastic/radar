using System.Reflection;

using Radar.Application.NewsRisk.Judgment;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 185 — the boundaries, asserted on the TYPE GRAPH (the shared <see cref="TypeGraphClosure"/>
/// walker): scoring and the evidence/signal pipeline can never reach a judgment type; the judge subsystem
/// never reaches a price type (no price/future-return join, §1); and the STAGE-2 CONTRACT is structural —
/// the request carries no raw article text member and the wire/validated shapes carry no Radar
/// score/rank/label member.
/// </summary>
public sealed class NewsJudgmentArchitectureGuardTests
{
    private const string JudgmentNamespace = "Radar.Application.NewsRisk.Judgment";
    private const string PricesNamespace = "Radar.Application.Prices";

    private static readonly string[] GuardedNamespaces =
    [
        "Radar.Application.Scoring",
        "Radar.Application.SignalExtraction",
        "Radar.Application.SignalReview",
        "Radar.Application.EntityResolution",
        "Radar.Application.Evidence",
        "Radar.Application.Signals",
        "Radar.Application.Pipeline",
    ];

    [Fact]
    public void ScoringAndPipelineTypeGraphs_CanNeverReachAJudgmentType()
    {
        var assembly = typeof(ScoringInput).Assembly;
        var roots = assembly.GetTypes()
            .Where(t => t.Namespace is not null && GuardedNamespaces.Any(
                ns => t.Namespace.Equals(ns, StringComparison.Ordinal)
                    || t.Namespace.StartsWith(ns + ".", StringComparison.Ordinal)))
            .ToList();

        Assert.NotEmpty(roots);
        Assert.Contains(typeof(ScoringInput), roots);

        var leaks = TypeGraphClosure.TransitiveClosure(roots)
            .Where(t => t.Namespace is not null
                && t.Namespace.StartsWith(JudgmentNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "No scoring/evidence-pipeline type may reach the judgment subsystem (spec 185: read-side and "
                + "shadow — a judgment is never a scoring input), but these are reachable: "
                + string.Join(", ", leaks));
    }

    [Fact]
    public void JudgmentSubsystem_CanNeverReachAPriceType()
    {
        var judgmentTypes = typeof(NewsJudgmentGenerator).Assembly.GetTypes()
            .Where(t => t.Namespace == JudgmentNamespace)
            .ToList();

        Assert.NotEmpty(judgmentTypes);
        Assert.Contains(typeof(NewsJudgmentGenerator), judgmentTypes);
        Assert.Contains(typeof(NewsJudgmentAnalysisRequest), judgmentTypes);

        var leaks = TypeGraphClosure.TransitiveClosure(judgmentTypes)
            .Where(t => t.Namespace is not null
                && t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "The judge weighs typed facts only — no price series or future return may be reachable "
                + "(spec 185 §1), but these are: " + string.Join(", ", leaks));
    }

    [Fact]
    public void NewsRiskEvaluator_StillReachesPrice_SoThePriceGuardIsNotVacuous()
    {
        // The positive control (the spec-181 guard's pattern): the spec-179 §9 evaluator legitimately reads
        // price, so a closure walk that went blind fails HERE before the judgment guard silently passes.
        var evaluationTypes = typeof(NewsJudgmentGenerator).Assembly.GetTypes()
            .Where(t => t.Namespace == "Radar.Application.NewsRisk.Evaluation")
            .ToList();

        Assert.NotEmpty(evaluationTypes);
        Assert.True(
            TypeGraphClosure.TransitiveClosure(evaluationTypes).Any(t => t.Namespace is not null
                && t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal)),
            "The spec-179 §9 evaluator is supposed to read price — the walk should see it.");
    }

    [Fact]
    public void JudgeRequestAndSchema_CarryNoRawTextScoreRankOrLabelMember()
    {
        // The stage-2 contract, enforced STRUCTURALLY (spec 185 §1/§2): the judge sees canonical fact
        // families only — no member may even exist to carry raw article prose, a headline, or a Radar
        // score/rank/label. "Statement"/"Citations" are the typed fact's own preserved content, so the
        // forbidden fragments target the article/prose and score vocabulary, not the fact fields.
        string[] forbiddenFragments =
            ["Headline", "Body", "Article", "Prose", "Score", "Rank", "Label", "Opportunity", "Price"];
        Type[] contractTypes =
        [
            typeof(NewsJudgmentAnalysisRequest),
            typeof(NewsJudgmentInputFamily),
            typeof(NewsJudgmentModelResponse),
            typeof(NewsJudgmentModelFinding),
            typeof(NewsJudgmentValidatedFinding),
        ];

        var violations = contractTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Type: t.Name, Property: p.Name)))
            .Where(p => forbiddenFragments.Any(
                f => p.Property.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.Type}.{p.Property}")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "The judge receives typed fact families only — no raw prose and no Radar score/rank/label — "
                + "but these members exist: " + string.Join(", ", violations));
    }

    [Fact]
    public void ExistingNewsTypingContract_IsUntouchedByTheJudgmentSlice()
    {
        // Spec 185's dispatch note: build against spec 181's shipped contract AS-IS. The stage-1 wire
        // schema still exposes no directional member (the spec-181 guard also pins this; restated here so
        // this file documents the dependency).
        var factProperties = typeof(Radar.Application.NewsTyping.NewsTypingValidatedFact)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain("Direction", factProperties);
        Assert.DoesNotContain("Severity", factProperties);
    }
}
