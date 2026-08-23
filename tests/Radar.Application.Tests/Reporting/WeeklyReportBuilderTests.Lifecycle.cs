using Radar.Application.Collectors;
using Radar.Application.Efficacy.Claims;
using Radar.Application.Lifecycle;
using Radar.Application.Reporting;
using Radar.Domain.Scoring;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 184 — the operating-call layer and per-strategy evidence statuses, exercised through the SAME
/// harness (and therefore the real renderer) the other builder tests use. Covers: single-strategy
/// structural inertness (byte-identical, sources never consulted), Lead ≠ storage-primary prominence,
/// StopAll (declared and the zero-Lead gate-failed fallback), resolution rendering, status rendering, and
/// the invariance of the news-risk nomination input (the spec-179 section instances) across call fixtures.
/// </summary>
public sealed partial class WeeklyReportBuilderTests
{
    private const string CallsSource = "data/strategy-operating-calls.json";

    private static readonly DateTimeOffset CallAsOf = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CallReviewBy = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The artifact's spec-186 semantic gate-verdict identity (never a timestamp).</summary>
    private const string GateVerdictId = "3f6a1c9e5b70";

    private sealed class FixedOperatingCallSource(StrategyOperatingCallsFile? file) : IOperatingCallSource
    {
        public Task<StrategyOperatingCallsFile?> ReadAsync(CancellationToken ct) => Task.FromResult(file);
    }

    private sealed class FixedFactsSource(EfficacyEvidenceFacts facts) : IStrategyEvidenceFactsSource
    {
        public Task<EfficacyEvidenceFacts> ReadAsync(CancellationToken ct) => Task.FromResult(facts);
    }

    // Prove structural inertness: with a single configured strategy the builder must never even consult
    // the call/facts sources, so a source that THROWS is the strongest possible assertion.
    private sealed class ThrowingOperatingCallSource : IOperatingCallSource
    {
        public Task<StrategyOperatingCallsFile?> ReadAsync(CancellationToken ct) =>
            throw new InvalidOperationException(
                "The operating-call source must never be consulted in a single-strategy composition.");
    }

    private sealed class ThrowingFactsSource : IStrategyEvidenceFactsSource
    {
        public Task<EfficacyEvidenceFacts> ReadAsync(CancellationToken ct) =>
            throw new InvalidOperationException(
                "The evidence-facts source must never be consulted in a single-strategy composition.");
    }

    private static StrategyOperatingCall LifecycleCall(
        string strategy,
        OperatingCall call,
        string basis = "declared for the fixture",
        string? resolutionRule = "resolved by the fixture rule.",
        OperatingCallResolution? resolution = null,
        bool overridesGate = false,
        string? overridesVerdictId = null) =>
        new(
            strategy, call, CallAsOf, basis, OperatingCallActor.Human,
            overridesGate, CallReviewBy, resolutionRule, resolution, overridesVerdictId);

    private static StrategyOperatingCallsFile CallsFile(
        bool stopAll = false, params StrategyOperatingCall[] calls) =>
        new(CallsSource, "strategy-operating-calls-v2", stopAll, calls);

    [Fact]
    public async Task AStaleGateOverride_ReArmsTheGateDefault_AndIsRenderedNotSilentlyDropped()
    {
        // Spec 186 §3. The maintainer overrode a GATE-FAILED verdict; new admitted evidence then minted a
        // new gateVerdictId. The override no longer binds, so the gate default re-arms (Stop ⇒ zero Leads
        // ⇒ the predeclared StopAll fallback) — and the report SAYS SO, naming the arm, the id the call
        // bound to and the id the artifact now carries.
        const string boundId = "0000111122223333444455556666777788889999aaaabbbbccccddddeeeeffff";

        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: false,
            LifecycleCall(
                "filings-led",
                OperatingCall.Lead,
                overridesGate: true,
                overridesVerdictId: boundId)));
        var facts = new FixedFactsSource(new EfficacyEvidenceFacts(
            LeaderboardAvailable: false,
            Leaderboard: [],
            PairedAvailable: true,
            Paired: new PairedGateFact(
                PrimaryStrategyName: "filings-led",
                PrimaryPredeclared: true,
                BoundaryDeclared: true,
                Qualifies: false,
                GateReasons: "baseline 'baseline-x': " + Ad15GateReasonCodes.IntervalLowerBoundNotPositive,
                GateVerdictId: GateVerdictId)));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls, evidenceFacts: facts);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 80);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        var lifecycle = h.Renderer.LastModel!.Lifecycle!;
        var stale = Assert.Single(lifecycle.Calls.StaleOverrides);
        Assert.Equal("filings-led", stale.StrategyName);
        Assert.Equal(boundId, stale.BoundVerdictId);
        Assert.Equal(GateVerdictId, stale.CurrentVerdictId);

        Assert.Equal(OperatingCall.Stop, lifecycle.Calls.For("filings-led")!.Call);
        Assert.True(lifecycle.Calls.StopAll);

        Assert.Contains("### Stale gate override", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "filings-led: overridesVerdictId " + boundId
                + " no longer matches the current gate verdict id " + GateVerdictId,
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABoundGateOverride_Holds_AndRendersNoStaleLine()
    {
        // The same fixture with the override bound to the CURRENT id: the declared Lead stands, and the
        // stale-override section is absent entirely (a report with nothing stale is unchanged).
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: false,
            LifecycleCall(
                "filings-led",
                OperatingCall.Lead,
                overridesGate: true,
                overridesVerdictId: GateVerdictId)));
        var facts = new FixedFactsSource(new EfficacyEvidenceFacts(
            LeaderboardAvailable: false,
            Leaderboard: [],
            PairedAvailable: true,
            Paired: new PairedGateFact(
                PrimaryStrategyName: "filings-led",
                PrimaryPredeclared: true,
                BoundaryDeclared: true,
                Qualifies: false,
                GateReasons: "baseline 'baseline-x': " + Ad15GateReasonCodes.IntervalLowerBoundNotPositive,
                GateVerdictId: GateVerdictId)));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls, evidenceFacts: facts);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 80);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        var lifecycle = h.Renderer.LastModel!.Lifecycle!;
        Assert.Empty(lifecycle.Calls.StaleOverrides);
        Assert.Equal("filings-led", lifecycle.Calls.LeadStrategyName);
        Assert.DoesNotContain("Stale gate override", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleStrategy_CallLayerIsStructurallyInert_AndReportIsByteIdentical()
    {
        var companyId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        var snapshotId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

        async Task<string> RunAsync(IOperatingCallSource calls, IStrategyEvidenceFactsSource facts)
        {
            var h = new Harness(operatingCalls: calls, evidenceFacts: facts);
            await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70);
            var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
            Assert.Null(h.Renderer.LastModel!.Lifecycle);
            return result.Report.MarkdownContent;
        }

        // Throwing sources prove "never consulted"; byte-equality against the inert defaults proves
        // "nothing new renders" (spec 184 §4).
        var withThrowingSources = await RunAsync(new ThrowingOperatingCallSource(), new ThrowingFactsSource());
        var withInertDefaults = await RunAsync(
            NullOperatingCallSource.Instance, UnavailableStrategyEvidenceFactsSource.Instance);

        Assert.Equal(withInertDefaults, withThrowingSources);
        Assert.DoesNotContain("Operating call", withThrowingSources, StringComparison.Ordinal);
        Assert.DoesNotContain("Evidence status", withThrowingSources, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiStrategy_NoCallsFile_StatesUndeclared_AndPrimaryKeepsNarrativeByDefault()
    {
        var h = new Harness(strategies: TwoStrategies);
        var acmeId = Guid.NewGuid();
        await SeedCompanyAsync(h, acmeId, Guid.NewGuid(), opportunity: 71, name: "Acme Dynamics",
            ticker: "ACME");

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        var lifecycle = h.Renderer.LastModel!.Lifecycle;
        Assert.NotNull(lifecycle);
        Assert.False(lifecycle.Calls.HasDeclaredCalls);
        Assert.Contains("No operating call is declared", markdown, StringComparison.Ordinal);
        // Undeclared ⇒ pre-184 prominence vocabulary stands, stated as the default rather than silent.
        Assert.Contains("(primary research)", markdown, StringComparison.Ordinal);
        // The narrative still comes from the storage primary's series.
        var entry = Assert.Single(result.Items);
        Assert.Equal(acmeId, entry.CompanyId);
        // Unreadable/absent artifacts degrade the status display; the arms are never hidden (spec 184 §1).
        Assert.Contains("Accruing (evidence unavailable)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeadThatIsNotStoragePrimary_GovernsNarrativeAndLabels_StorageIdentityUntouched()
    {
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: false,
            LifecycleCall("filings-led", OperatingCall.Lead,
                basis: "the prospectively declared arm under test"),
            LifecycleCall("default", OperatingCall.DoNotLead, basis: "oos rho spans zero at call time")));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls);

        // Two companies: the PRIMARY series scored Acme; the LEAD series scored Borealis. If narrative
        // prominence follows the lead, the report's labelled entries are Borealis-only.
        var acmeId = Guid.NewGuid();
        var borealisId = Guid.NewGuid();
        await SeedCompanyAsync(h, acmeId, Guid.NewGuid(), opportunity: 80, name: "Acme Dynamics",
            ticker: "ACME");
        await SeedCompanyOnlyAsync(h, borealisId, "Borealis Systems", "BOR");
        await SeedStrategySnapshotAsync(h, "filings-led", borealisId, Guid.NewGuid(), opportunity: 66);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        // Labels are LEAD-only: exactly one labelled entry, from the lead's series, and the policy was
        // consulted exactly once (per surfaced lead entry — never per strategy row).
        var item = Assert.Single(result.Items);
        Assert.Equal(borealisId, item.CompanyId);
        Assert.Single(h.Policy.Contexts);
        Assert.Contains("### 1. Borealis Systems (BOR)", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("### 1. Acme Dynamics", markdown, StringComparison.Ordinal);

        // The banner renders the call, basis, as-of, review-by and resolution rule (spec 184 §2).
        Assert.Contains("**Lead: filings-led**", markdown, StringComparison.Ordinal);
        Assert.Contains("Call: Lead · actor human · as of 2026-08-23 00:00Z · review by 2026-09-05 00:00Z",
            markdown, StringComparison.Ordinal);
        Assert.Contains("Basis: the prospectively declared arm under test", markdown, StringComparison.Ordinal);
        Assert.Contains("Resolution rule: resolved by the fixture rule.", markdown, StringComparison.Ordinal);

        // Prominence vocabulary: the lead is annotated, the storage primary is a series identity only.
        Assert.Contains("filings-led (lead)", markdown, StringComparison.Ordinal);
        Assert.Contains("default (storage primary)", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "## Strategy: filings-led (radar-formula-v9) — lead (the series reported above)",
            markdown, StringComparison.Ordinal);
        Assert.Contains(
            "## Strategy: default (radar-formula-v8) — storage primary (series identity only; the narrative "
                + "above follows the lead)",
            markdown, StringComparison.Ordinal);

        // DoNotLead is stated with its basis.
        Assert.Contains("DoNotLead (human, as of 2026-08-23 00:00Z) — oos rho spans zero at call time",
            markdown, StringComparison.Ordinal);

        // Storage identity untouched: the section ORDER (the spec-179 nomination input) is still
        // primary-first, and the primary section still exists in full.
        var sections = h.Renderer.LastModel!.Strategies!;
        Assert.Equal(new[] { "default", "filings-led" }, sections.Select(s => s.StrategyName).ToArray());
    }

    [Fact]
    public async Task DeclaredStopAll_RendersDiagnosticBanner_NoNarrative_NoLabels()
    {
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: true,
            LifecycleCall("default", OperatingCall.DoNotLead)));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 80);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        Assert.Empty(result.Items);
        Assert.Empty(h.Policy.Contexts); // labels are Lead-only, and there is no Lead
        Assert.Contains("**No lead — StopAll.**", markdown, StringComparison.Ordinal);
        Assert.Contains("No narrative entries: no lead — StopAll.", markdown, StringComparison.Ordinal);
        // The arms remain fully visible in the diagnostic tables — nothing is hidden.
        Assert.Contains("## Strategy: default", markdown, StringComparison.Ordinal);
        Assert.Contains("## Strategy: filings-led", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GateFailedLead_FallsBackToPredeclaredStopAll_AndSaysWhy()
    {
        // The declared Lead arm carries a PERSISTED merit-failure gate verdict; without overridesGate the
        // gate default demotes it to Stop, and zero Leads resolve to the predeclared StopAll fallback.
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: false,
            LifecycleCall("filings-led", OperatingCall.Lead)));
        var facts = new FixedFactsSource(new EfficacyEvidenceFacts(
            LeaderboardAvailable: false,
            Leaderboard: [],
            PairedAvailable: true,
            Paired: new PairedGateFact(
                PrimaryStrategyName: "filings-led",
                PrimaryPredeclared: true,
                BoundaryDeclared: true,
                Qualifies: false,
                GateReasons: "baseline 'baseline-x': " + Ad15GateReasonCodes.IntervalLowerBoundNotPositive,
                GateVerdictId: GateVerdictId)));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls, evidenceFacts: facts);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 80);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        Assert.Empty(result.Items);
        Assert.Contains("**No lead — StopAll.**", markdown, StringComparison.Ordinal);
        Assert.Contains("zero Leads after reduction", markdown, StringComparison.Ordinal);
        Assert.Contains("Stop (gate default: the AD-15 composite gate failed for this arm)",
            markdown, StringComparison.Ordinal);
        Assert.Contains("Gate failed (AD-15 composite gate, evaluated on its merits)",
            markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatePassedLead_RendersTheGateDefaultProvenance_NotTheDeclaredCallFields()
    {
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: false,
            LifecycleCall("filings-led", OperatingCall.Lead)));
        var facts = new FixedFactsSource(new EfficacyEvidenceFacts(
            LeaderboardAvailable: false,
            Leaderboard: [],
            PairedAvailable: true,
            Paired: new PairedGateFact(
                "filings-led", PrimaryPredeclared: true, BoundaryDeclared: true, Qualifies: true,
                GateReasons: string.Empty,
                GateVerdictId: GateVerdictId)));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls, evidenceFacts: facts);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        Assert.Contains("**Lead: filings-led**", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "Call: Lead · actor gate-default (the AD-15 composite gate passed for this arm; gate verdict "
                + "id " + GateVerdictId + ")",
            markdown, StringComparison.Ordinal);
        Assert.Contains("Gate passed (AD-15 composite gate)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppedArm_MovesToDiagnosticAppendix_NeverHidden()
    {
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: false,
            LifecycleCall("default", OperatingCall.Lead),
            LifecycleCall("filings-led", OperatingCall.Stop, basis: "stopped by the fixture")));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls);
        var acmeId = Guid.NewGuid();
        await SeedCompanyAsync(h, acmeId, Guid.NewGuid(), opportunity: 70, name: "Acme Dynamics",
            ticker: "ACME");
        await SeedStrategySnapshotAsync(h, "filings-led", acmeId, Guid.NewGuid(), opportunity: 55);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        var appendixIndex = markdown.IndexOf(
            "### Stopped arms — diagnostic appendix", StringComparison.Ordinal);
        Assert.True(appendixIndex >= 0, "The stopped arm must render in the diagnostic appendix.");

        // The stopped arm's live-leader rows render inside the appendix, not the research table.
        var researchIndex = markdown.IndexOf("### Research arms", StringComparison.Ordinal);
        var researchBlock = markdown[researchIndex..appendixIndex];
        Assert.DoesNotContain("| filings-led", researchBlock, StringComparison.Ordinal);
        Assert.Contains("| filings-led", markdown[appendixIndex..], StringComparison.Ordinal);

        // …and its complete spec-150 table still renders below (nothing hidden).
        Assert.Contains("## Strategy: filings-led", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvedWrongCall_RendersOutcomeAndEvidenceReference()
    {
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: false,
            LifecycleCall("filings-led", OperatingCall.Lead),
            LifecycleCall(
                "default",
                OperatingCall.DoNotLead,
                resolution: new OperatingCallResolution(
                    OperatingCallOutcome.Wrong,
                    new DateTimeOffset(2027, 2, 2, 0, 0, 0, TimeSpan.Zero),
                    "data/efficacy/strategy-paired-comparison.md"))));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.Contains(
            "resolved Wrong at 2027-02-02 00:00Z — evidence: data/efficacy/strategy-paired-comparison.md",
            result.Report.MarkdownContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RankedStatus_RendersItsNumbers_AndTheNoDiscriminationSentenceWhenCiSpansZero()
    {
        var facts = new FixedFactsSource(new EfficacyEvidenceFacts(
            LeaderboardAvailable: true,
            Leaderboard:
            [
                new LeaderboardStrategyFact(
                    "default", Ranked: true, new RankedEvidence(1, -0.05, -0.30, 0.20, 72), null),
                new LeaderboardStrategyFact(
                    "filings-led", Ranked: false, null, "insufficient-out-of-sample-observations"),
            ],
            PairedAvailable: false,
            Paired: null));
        var h = new Harness(strategies: TwoStrategies, evidenceFacts: facts);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        Assert.Contains(
            "Ranked #1 — out-of-sample rho -0.0500 (95% CI -0.3000 to 0.2000) over 72 observation(s) — no "
                + "evidence of discrimination yet",
            markdown, StringComparison.Ordinal);
        Assert.Contains("Accruing — insufficient-out-of-sample-observations", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrategySections_TheNewsRiskNominationInput_AreByteIdenticalAcrossCallFixtures()
    {
        var acmeId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
        var acmeSnapshotId = Guid.Parse("dddddddd-4444-4444-4444-444444444444");
        var filingsSnapshotId = Guid.Parse("eeeeeeee-5555-5555-5555-555555555555");

        async Task<(IReadOnlyList<StrategyReportSection> Sections,
            IReadOnlyList<CompanyScoreSnapshot> PrimarySnapshots)> RunAsync(
            IOperatingCallSource calls)
        {
            var h = new Harness(strategies: TwoStrategies, operatingCalls: calls);
            await SeedCompanyAsync(h, acmeId, acmeSnapshotId, opportunity: 70, name: "Acme Dynamics",
                ticker: "ACME");
            await SeedStrategySnapshotAsync(h, "filings-led", acmeId, filingsSnapshotId, opportunity: 55);
            var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
            var primarySnapshots = await h.Scores.GetSnapshotsForCompanyAsync(acmeId, default);
            return (result.StrategySections!, primarySnapshots);
        }

        var none = await RunAsync(NullOperatingCallSource.Instance);
        var lead = await RunAsync(new FixedOperatingCallSource(CallsFile(
            stopAll: false, LifecycleCall("filings-led", OperatingCall.Lead))));
        var stopAll = await RunAsync(new FixedOperatingCallSource(CallsFile(
            stopAll: true, LifecycleCall("default", OperatingCall.DoNotLead))));

        static IReadOnlyList<(string, int, Guid, int)> Shape(IReadOnlyList<StrategyReportSection> sections) =>
            sections
                .SelectMany(s => s.Rows.Select(r =>
                    (s.StrategyName, r.Rank, r.ScoreSnapshotId, r.Snapshot.OpportunityScore)))
                .ToList();

        // The section instances the news-risk shadow step nominates from are identical whatever the call
        // layer says — a call changes prominence, never nomination, scores or snapshots (spec 184 §4).
        Assert.Equal(Shape(none.Sections), Shape(lead.Sections));
        Assert.Equal(Shape(none.Sections), Shape(stopAll.Sections));
        Assert.Equal(
            none.Sections.Select(s => s.StrategyName),
            stopAll.Sections.Select(s => s.StrategyName));

        // …and the persisted snapshots themselves are untouched by the call fixture.
        Assert.Equal(
            none.PrimarySnapshots.Select(s => (s.Id, s.OpportunityScore, s.ScoringConfigVersion)),
            stopAll.PrimarySnapshots.Select(s => (s.Id, s.OpportunityScore, s.ScoringConfigVersion)));
    }

    [Fact]
    public async Task InvalidCallsFile_FailsTheBuild_NamingFileAndRule()
    {
        // The builder re-runs the reducer's validation (the Worker already failed startup on this; a
        // non-Worker composition must not render from an invalid file either).
        var calls = new FixedOperatingCallSource(CallsFile(
            stopAll: false,
            LifecycleCall("ghost", OperatingCall.Lead)));
        var h = new Harness(strategies: TwoStrategies, operatingCalls: calls);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default));
        Assert.Contains(CallsSource, ex.Message);
        Assert.Contains("unknown strategy 'ghost'", ex.Message);
    }
}
