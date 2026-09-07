using Radar.Application.Collectors;
using Radar.Application.Efficacy.Claims;
using Radar.Application.Lifecycle;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Domain.Reports;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 212 — the three operating-call states, pinned through the real builder + renderer:
/// <list type="bullet">
/// <item>undeclared (no operating-calls file) ⇒ the storage primary's <c>Labels ?? Default</c>, stated as
/// defaults, differing from its captured pre-212 bytes by the ONE banner line;</item>
/// <item>effective Lead ⇒ the Lead's explicit lines reach the policy (test e) and the banner names them;
/// a declared Lead without lines fails the build with the reducer's message (the gate-promoted case, f2,
/// is a reducer-level fixture: <c>StrategyEvidenceStatusCalculator.GateVerdicts</c> emits a verdict only
/// for the ONE paired arm, so no facts source can demote one arm and promote another);</item>
/// <item>StopAll — declared (f3a) and gate-produced (f3b) ⇒ no narrative, no labels, no banner,
/// BYTE-IDENTICAL to the pre-212 output.</item>
/// </list>
/// The three pins below were captured from the unmodified pre-212 builder/renderer on the same fixtures
/// (fixed ids, fixed clock) immediately before this slice's production changes were applied.
/// </summary>
public sealed partial class WeeklyReportBuilderTests
{
    private const string LabelLinesHonestySentence =
        "These are fixed operating thresholds set by prevalence, not validated evidence of opportunity; a "
            + "label is comparable only across reports labelled on the same arm and lines.";

    private const string DefaultsBanner =
        "> Labels in this report follow default at Investigate ≥ 60 / Watch ≥ 40 (defaults (no operating "
            + "calls declared); weekly-report-action-v5). " + LabelLinesHonestySentence;

    private const string LeadBanner =
        "> Labels in this report follow filings-led at Investigate ≥ 20 / Watch ≥ 15 (explicit lines on the "
            + "Lead arm; weekly-report-action-v5). " + LabelLinesHonestySentence;

    // Captured pre-212: TwoStrategies (both unlabelled at the time), declared globalCall StopAll.
    private static readonly string Pre212DeclaredStopAll = string.Join("\n",
    [
        "# Radar Weekly — 2026-02-01 to 2026-02-08",
        "Period: 2026-02-01 → 2026-02-08 (UTC)",
        "Generated: 2026-02-08 12:00Z",
        "",
        "> Not financial advice.",
        "> For research only.",
        "> Human review required.",
        "> Notedness (measured Attention + curated following tier) discounts a company's Opportunity so already-followed names surface lower — a research signal, not a valuation.",
        "> \"GuidanceChange\" in evidence lines is a historical earnings-release signal type — either a deterministic Neutral earnings-filing marker or an AI earnings-trajectory read; it does not by itself mean the company issued or changed guidance.",
        "> \"InsiderActivity\" rows are SEC Form 4 insider filings of any kind; a Neutral row is a routine or planned filing, not a discretionary transaction.",
        "",
        "## Live strategy leaders",
        "",
        "### Operating call",
        "",
        "**No lead — StopAll.** declared: globalCall StopAll is present in data/strategy-operating-calls.json",
        "",
        "No arm holds reader-facing prominence: the sections below are a diagnostic view. The next Lead call is made explicitly by a human and journaled in docs/strategy-lifecycle.md.",
        "",
        "### Calls and evidence status",
        "",
        "Evidence status is computed, descriptive and never a verdict; the call is a recorded decision. The two are stated side by side and never merged.",
        "",
        "| strategy | operating call | evidence status |",
        "| --- | --- | --- |",
        "| default | DoNotLead (human, as of 2026-08-23 00:00Z) — declared for the fixture | Accruing (evidence unavailable) |",
        "| filings-led | Trial (no declared call) | Accruing (evidence unavailable) |",
        "",
        "### Research arms",
        "",
        "Live scores are shown immediately and are never gated on a future price. Forward outcomes are required only to evaluate the strategy later; these rankings are not efficacy results.",
        "",
        "Scores and score magnitudes are comparable only within the same strategy. Repeated company names across arms are not a consensus signal.",
        "",
        "Semantic read: '⚠ challenged' means the designated judgment cohort recorded at least one validated challenge finding; '? unassessed (reason)' means no completed validated judgment exists for that row; '· no challenge found in supplied facts' comes only from a completed validated judgment and is a statement about the supplied typed facts, never a clean bill for the company.",
        "",
        "| strategy | rank | company | ticker | Opportunity | as-of UTC | semantic read |",
        "| --- | ---: | --- | --- | ---: | --- | --- |",
        "| default (storage primary) | 1 | Acme Corp | ACME | 80 | 2026-01-31 00:00Z | ? unassessed (no-judgment) |",
        "| filings-led | — | No evidence-linked live scores in this report window. | — | — | — | — |",
        "",
        "## Highest opportunity",
        "",
        "No narrative entries: no lead — StopAll. See the operating-call banner above and the per-strategy diagnostic tables below.",
        "",
        "## Collection summary",
        "",
        "Radar checked 0 source(s) this run; 0 could not be read.",
        "",
        "## Strategy: default (radar-formula-v8) — storage primary (series identity only; no lead — StopAll)",
        "Evidence status: Accruing (evidence unavailable)",
        "Fingerprint: radar-scoring-fp-111111111111 · 1 company scored · 1 with linked evidence",
        "",
        "These are independent scorings of the SAME collection pass. Absolute scores are not comparable across strategies when the formulas differ, and a higher-looking table is not a better strategy. Ranking strategies against subsequent price movement is data/efficacy/strategy-leaderboard.md, not this table.",
        "",
        "| rank | company | ticker | Opportunity | Trajectory | Attention | Evidence | Velocity |",
        "| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |",
        "| 1 | Acme Corp | ACME | 80 | 50 | 50 | 50 | 50 |",
        "",
        "## Strategy: filings-led (radar-formula-v9)",
        "Evidence status: Accruing (evidence unavailable)",
        "Fingerprint: radar-scoring-fp-222222222222 · 0 companies scored · 0 with linked evidence",
        "",
        "| rank | company | ticker | Opportunity | Trajectory | Attention | Evidence | Velocity |",
        "| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |",
        "",
        "",
    ]);

    // Captured pre-212: filings-led declared Lead, demoted by a failing gate verdict — the zero-Lead fallback.
    private static readonly string Pre212GateProducedStopAll = string.Join("\n",
    [
        "# Radar Weekly — 2026-02-01 to 2026-02-08",
        "Period: 2026-02-01 → 2026-02-08 (UTC)",
        "Generated: 2026-02-08 12:00Z",
        "",
        "> Not financial advice.",
        "> For research only.",
        "> Human review required.",
        "> Notedness (measured Attention + curated following tier) discounts a company's Opportunity so already-followed names surface lower — a research signal, not a valuation.",
        "> \"GuidanceChange\" in evidence lines is a historical earnings-release signal type — either a deterministic Neutral earnings-filing marker or an AI earnings-trajectory read; it does not by itself mean the company issued or changed guidance.",
        "> \"InsiderActivity\" rows are SEC Form 4 insider filings of any kind; a Neutral row is a routine or planned filing, not a discretionary transaction.",
        "",
        "## Live strategy leaders",
        "",
        "### Operating call",
        "",
        "**No lead — StopAll.** fallback: zero Leads after reduction (the declared Lead arm was demoted by a persisted gate verdict) — no other arm has earned the front page by default; a human makes the next Lead call explicitly",
        "",
        "No arm holds reader-facing prominence: the sections below are a diagnostic view. The next Lead call is made explicitly by a human and journaled in docs/strategy-lifecycle.md.",
        "",
        "### Calls and evidence status",
        "",
        "Evidence status is computed, descriptive and never a verdict; the call is a recorded decision. The two are stated side by side and never merged.",
        "",
        "| strategy | operating call | evidence status |",
        "| --- | --- | --- |",
        "| default | Trial (no declared call) | Accruing (evidence unavailable) |",
        "| filings-led | Stop (gate default: the AD-15 composite gate failed for this arm) | Gate failed (AD-15 composite gate, evaluated on its merits) — baseline 'baseline-x': interval-lower-bound-not-positive |",
        "",
        "### Research arms",
        "",
        "Live scores are shown immediately and are never gated on a future price. Forward outcomes are required only to evaluate the strategy later; these rankings are not efficacy results.",
        "",
        "Scores and score magnitudes are comparable only within the same strategy. Repeated company names across arms are not a consensus signal.",
        "",
        "Semantic read: '⚠ challenged' means the designated judgment cohort recorded at least one validated challenge finding; '? unassessed (reason)' means no completed validated judgment exists for that row; '· no challenge found in supplied facts' comes only from a completed validated judgment and is a statement about the supplied typed facts, never a clean bill for the company.",
        "",
        "| strategy | rank | company | ticker | Opportunity | as-of UTC | semantic read |",
        "| --- | ---: | --- | --- | ---: | --- | --- |",
        "| default (storage primary) | 1 | Acme Corp | ACME | 80 | 2026-01-31 00:00Z | ? unassessed (no-judgment) |",
        "| filings-led | — | No evidence-linked live scores in this report window. | — | — | — | — |",
        "",
        "## Highest opportunity",
        "",
        "No narrative entries: no lead — StopAll. See the operating-call banner above and the per-strategy diagnostic tables below.",
        "",
        "## Collection summary",
        "",
        "Radar checked 0 source(s) this run; 0 could not be read.",
        "",
        "## Strategy: default (radar-formula-v8) — storage primary (series identity only; no lead — StopAll)",
        "Evidence status: Accruing (evidence unavailable)",
        "Fingerprint: radar-scoring-fp-111111111111 · 1 company scored · 1 with linked evidence",
        "",
        "These are independent scorings of the SAME collection pass. Absolute scores are not comparable across strategies when the formulas differ, and a higher-looking table is not a better strategy. Ranking strategies against subsequent price movement is data/efficacy/strategy-leaderboard.md, not this table.",
        "",
        "| rank | company | ticker | Opportunity | Trajectory | Attention | Evidence | Velocity |",
        "| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |",
        "| 1 | Acme Corp | ACME | 80 | 50 | 50 | 50 | 50 |",
        "",
        "## Strategy: filings-led (radar-formula-v9)",
        "Evidence status: Gate failed (AD-15 composite gate, evaluated on its merits) — baseline 'baseline-x': interval-lower-bound-not-positive",
        "Fingerprint: radar-scoring-fp-222222222222 · 0 companies scored · 0 with linked evidence",
        "",
        "| rank | company | ticker | Opportunity | Trajectory | Attention | Evidence | Velocity |",
        "| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |",
        "",
        "",
    ]);

    // Captured pre-212: TwoStrategies, no operating-calls file (the undeclared state).
    private static readonly string Pre212Undeclared = string.Join("\n",
    [
        "# Radar Weekly — 2026-02-01 to 2026-02-08",
        "Period: 2026-02-01 → 2026-02-08 (UTC)",
        "Generated: 2026-02-08 12:00Z",
        "",
        "> Not financial advice.",
        "> For research only.",
        "> Human review required.",
        "> Notedness (measured Attention + curated following tier) discounts a company's Opportunity so already-followed names surface lower — a research signal, not a valuation.",
        "> \"GuidanceChange\" in evidence lines is a historical earnings-release signal type — either a deterministic Neutral earnings-filing marker or an AI earnings-trajectory read; it does not by itself mean the company issued or changed guidance.",
        "> \"InsiderActivity\" rows are SEC Form 4 insider filings of any kind; a Neutral row is a routine or planned filing, not a discretionary transaction.",
        "",
        "## Live strategy leaders",
        "",
        "### Operating call",
        "",
        "No operating call is declared (no operating-calls file was found). Narrative prominence remains with the storage-primary strategy by default. A call is a maintainer decision recorded in data/strategy-operating-calls.json and journaled in docs/strategy-lifecycle.md.",
        "",
        "### Calls and evidence status",
        "",
        "Evidence status is computed, descriptive and never a verdict; the call is a recorded decision. The two are stated side by side and never merged.",
        "",
        "| strategy | operating call | evidence status |",
        "| --- | --- | --- |",
        "| default | — | Accruing (evidence unavailable) |",
        "| filings-led | — | Accruing (evidence unavailable) |",
        "",
        "### Research arms",
        "",
        "Live scores are shown immediately and are never gated on a future price. Forward outcomes are required only to evaluate the strategy later; these rankings are not efficacy results.",
        "",
        "Scores and score magnitudes are comparable only within the same strategy. Repeated company names across arms are not a consensus signal.",
        "",
        "Semantic read: '⚠ challenged' means the designated judgment cohort recorded at least one validated challenge finding; '? unassessed (reason)' means no completed validated judgment exists for that row; '· no challenge found in supplied facts' comes only from a completed validated judgment and is a statement about the supplied typed facts, never a clean bill for the company.",
        "",
        "| strategy | rank | company | ticker | Opportunity | as-of UTC | semantic read |",
        "| --- | ---: | --- | --- | ---: | --- | --- |",
        "| default (primary research) | 1 | Acme Dynamics | ACME | 71 | 2026-01-31 00:00Z | ? unassessed (no-judgment) |",
        "| filings-led | — | No evidence-linked live scores in this report window. | — | — | — | — |",
        "",
        "## Highest opportunity",
        "",
        "### 1. Acme Dynamics (ACME)",
        "- Label: Investigate",
        "- Opportunity 71 · Trajectory 50 · Attention 50 · Evidence 50 · Velocity 50 (first snapshot)",
        "- **Notedness:** Attention 50 · Following: Small (under-followed)",
        "- Why: Opportunity 71 (>= 60); worth investigating.",
        "- Score snapshot: a0000000-0000-0000-0000-000000000002",
        "- Evidence:",
        "  - [Untitled](https://example.com/acme) — Acme Newsroom: Contributed to the score.",
        "",
        "## Collection summary",
        "",
        "Radar checked 0 source(s) this run; 0 could not be read.",
        "",
        "## Strategy: default (radar-formula-v8) — primary (the series reported above)",
        "Evidence status: Accruing (evidence unavailable)",
        "Fingerprint: radar-scoring-fp-111111111111 · 1 company scored · 1 with linked evidence",
        "",
        "These are independent scorings of the SAME collection pass. Absolute scores are not comparable across strategies when the formulas differ, and a higher-looking table is not a better strategy. Ranking strategies against subsequent price movement is data/efficacy/strategy-leaderboard.md, not this table.",
        "",
        "| rank | company | ticker | Opportunity | Trajectory | Attention | Evidence | Velocity |",
        "| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |",
        "| 1 | Acme Dynamics | ACME | 71 | 50 | 50 | 50 | 50 |",
        "",
        "## Strategy: filings-led (radar-formula-v9)",
        "Evidence status: Accruing (evidence unavailable)",
        "Fingerprint: radar-scoring-fp-222222222222 · 0 companies scored · 0 with linked evidence",
        "",
        "| rank | company | ticker | Opportunity | Trajectory | Attention | Evidence | Velocity |",
        "| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |",
        "",
        "",
    ]);

    private static PairedGateFact FailingGateFor(string strategy) => new(
        PrimaryStrategyName: strategy,
        PrimaryPredeclared: true,
        BoundaryDeclared: true,
        Qualifies: false,
        GateReasons: "baseline 'baseline-x': " + Ad15GateReasonCodes.IntervalLowerBoundNotPositive,
        GateVerdictId: GateVerdictId);

    [Fact]
    public async Task Undeclared_LabelsOnPrimaryDefaults_AndDiffersFromThePre212PinByTheBannerLineOnly()
    {
        var h = new Harness(strategies: TwoStrategies);
        await SeedCompanyAsync(h, Guid.Parse("a0000000-0000-0000-0000-000000000001"),
            Guid.Parse("a0000000-0000-0000-0000-000000000002"), opportunity: 71, name: "Acme Dynamics",
            ticker: "ACME");

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        // The builder passed the primary's fallback lines EXPLICITLY (never a null the policy defaults).
        var context = Assert.Single(h.Policy.Contexts);
        Assert.Equal(LabelThresholds.Default, context.Thresholds);

        var labels = h.Renderer.LastModel!.Labels;
        Assert.NotNull(labels);
        Assert.Equal("default", labels.StrategyName);
        Assert.Equal(LabelThresholds.Default, labels.Thresholds);
        Assert.False(labels.Explicit);
        Assert.False(labels.LeadDeclared);
        Assert.Equal("weekly-report-action-v5", labels.PolicyVersion);

        // ONLY diff from the pre-212 pin: the banner, inserted directly under the last legend line.
        var expected = Pre212Undeclared.Replace(
            "not a discretionary transaction.\n\n",
            "not a discretionary transaction.\n" + DefaultsBanner + "\n\n",
            StringComparison.Ordinal);
        Assert.NotEqual(Pre212Undeclared, expected);
        Assert.Equal(expected, markdown);
    }

    [Fact]
    public async Task EffectiveLead_PassesTheLeadsLines_NotThePrimarys_AndStatesThemOnce()
    {
        // (e) The Lead is a non-primary arm whose lines (20 / 15) differ from the primary's fallback (60 / 40).
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: false,
            LifecycleCall("filings-led", OperatingCall.Lead),
            LifecycleCall("default", OperatingCall.DoNotLead)));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls);
        var acmeId = Guid.NewGuid();
        var borealisId = Guid.NewGuid();
        await SeedCompanyAsync(h, acmeId, Guid.NewGuid(), opportunity: 80, name: "Acme Dynamics", ticker: "ACME");
        await SeedCompanyOnlyAsync(h, borealisId, "Borealis Systems", "BOR");
        // 16 is Watch BY SCORE on the Lead's line (>= 15) and would be Ignore on the primary's 40.
        await SeedStrategySnapshotAsync(h, "filings-led", borealisId, Guid.NewGuid(), opportunity: 16,
            trajectory: 50);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        var context = Assert.Single(h.Policy.Contexts);
        Assert.Equal(LeadLines, context.Thresholds);

        var item = Assert.Single(result.Items);
        Assert.Equal(borealisId, item.CompanyId);
        Assert.Equal(RadarReportAction.Watch, item.SuggestedAction);
        Assert.Equal("Opportunity 16 (>= 15); watch for further signals.", item.Summary);

        var labels = h.Renderer.LastModel!.Labels;
        Assert.NotNull(labels);
        Assert.Equal("filings-led", labels.StrategyName);
        Assert.Equal(LeadLines, labels.Thresholds);
        Assert.True(labels.Explicit);
        Assert.True(labels.LeadDeclared);

        // Stated exactly once, and before the first section a reader hits.
        Assert.Equal(1, CountOccurrences(markdown, "Labels in this report follow"));
        Assert.Contains(LeadBanner + "\n", markdown, StringComparison.Ordinal);
        Assert.True(
            markdown.IndexOf(LeadBanner, StringComparison.Ordinal)
                < markdown.IndexOf("## Live strategy leaders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeclaredStopAll_WithEveryArmUnlabelled_IsByteIdenticalToThePre212Pin()
    {
        // (f3a) globalCall StopAll with EVERY arm's Labels null: no narrative, no company labels, no
        // threshold banner — and StopAll must not trip the Lead requirement.
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: true,
            LifecycleCall("default", OperatingCall.DoNotLead)));
        var h = new Harness(strategies: TwoStrategiesUnlabelled, operatingCalls: calls);
        await SeedCompanyAsync(h, Guid.Parse("f3a00000-0000-0000-0000-000000000001"),
            Guid.Parse("f3a00000-0000-0000-0000-000000000002"), opportunity: 80);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.Empty(result.Items);
        Assert.Empty(h.Policy.Contexts);
        Assert.Null(h.Renderer.LastModel!.Labels);
        Assert.DoesNotContain("Labels in this report", result.Report.MarkdownContent, StringComparison.Ordinal);
        Assert.Equal(Pre212DeclaredStopAll, result.Report.MarkdownContent);
    }

    [Fact]
    public async Task GateProducedStopAll_WithALabelledDeclaredLead_IsByteIdenticalToThePre212Pin()
    {
        // (f3b) The declared Lead HAS labels (it must, or Validate stops the fixture first) and is demoted
        // by a failing gate verdict with no other arm promoted — the zero-Lead fallback StopAll applies.
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: false,
            LifecycleCall("filings-led", OperatingCall.Lead)));
        var facts = new FixedFactsSource(new EfficacyEvidenceFacts(
            LeaderboardAvailable: false,
            Leaderboard: [],
            PairedAvailable: true,
            Paired: FailingGateFor("filings-led")));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls, evidenceFacts: facts);
        await SeedCompanyAsync(h, Guid.Parse("f3b00000-0000-0000-0000-000000000001"),
            Guid.Parse("f3b00000-0000-0000-0000-000000000002"), opportunity: 80);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.True(h.Renderer.LastModel!.Lifecycle!.Calls.StopAll);
        Assert.Empty(result.Items);
        Assert.Empty(h.Policy.Contexts);
        Assert.Null(h.Renderer.LastModel.Labels);
        Assert.Equal(Pre212GateProducedStopAll, result.Report.MarkdownContent);
    }

    [Fact]
    public async Task DeclaredLeadWithoutLabels_FailsTheBuild_NamingArmAndConfigPath()
    {
        // The builder surfaces the reducer's spec-212 rule unchanged: default (index 0) is declared Lead
        // but carries no lines in TwoStrategies.
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: false,
            LifecycleCall("default", OperatingCall.Lead)));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default));
        Assert.Contains(CallsSource, ex.Message);
        Assert.Contains("declared Lead 'default' has no Labels", ex.Message);
        Assert.Contains("Radar:Strategies:0:Labels", ex.Message);
    }

    [Fact]
    public async Task SingleStrategy_StatesTheDefaultLinesAsDefaults()
    {
        // A single-strategy composition has no call layer at all (spec 184 §4) — its labels still come
        // from somewhere, and the report says where: the primary's defaults.
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.Contains(DefaultsBanner + "\n", result.Report.MarkdownContent, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result.Report.MarkdownContent, "Labels in this report follow"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
