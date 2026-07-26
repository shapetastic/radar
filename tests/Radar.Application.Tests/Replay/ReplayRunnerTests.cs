using Radar.Application.Replay;
using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.Infrastructure.FileSystem;

namespace Radar.Application.Tests.Replay;

/// <summary>
/// Spec 139 — the replay harness end to end, against the REAL scoring path (real engines, real formula, real
/// on-disk signal + snapshot stores). The invariants under test are the ones the whole slice rests on:
/// replay⊆forward, no hindsight leak, no live store mutated, and idempotent/deterministic output.
/// </summary>
public sealed class ReplayRunnerTests
{
    private static readonly DateTimeOffset D = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static ReplayPlan SinglePointPlan(DateTimeOffset asOf, string label = "run") =>
        new(label, ReplaySeries.Create(asOf, asOf, TimeSpan.FromDays(1)));

    /// <summary>
    /// THE INVARIANT (spec 139): a replay as-of D reproduces the forward score at D on every
    /// scoring-relevant field.
    /// <para>
    /// <b>Excluded from the comparison, deliberately and without weakening it:</b>
    /// <see cref="CompanyScoreSnapshot.Id"/> and each <see cref="ScoreEvidenceLink"/>'s <c>Id</c> /
    /// <c>ScoreSnapshotId</c>. The engine mints those with <c>Guid.NewGuid()</c> on EVERY call — two
    /// consecutive FORWARD runs differ in them just as much — so they identify a scoring EVENT, not a scoring
    /// RESULT. Every field that encodes what the score actually says (all five components, the explanation,
    /// the component JSON, both version stamps, the strategy name, the window bounds, the creation instant)
    /// and the whole ordered provenance chain (signal → evidence → reason → weight) IS compared.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReplayAtD_ReproducesTheForwardScoreAtD()
    {
        using var harness = ReplayTestHarness.Create(SinglePointPlan(D));
        var companyId = await harness.SeedCompanyAsync();

        // A spread of signal types/qualities/ages so the formula exercises more than one term, all known
        // (CreatedAtUtc) before D.
        await harness.SeedSignalAsync(
            companyId, SignalType.CustomerWin, D.AddDays(-20), D.AddDays(-20));
        await harness.SeedSignalAsync(
            companyId, SignalType.ProductLaunch, D.AddDays(-9), D.AddDays(-8), EvidenceQuality.Medium);
        await harness.SeedSignalAsync(
            companyId, SignalType.InsiderBuying, D.AddDays(-2), D.AddDays(-2), strength: 8);

        // FORWARD: the live primary engine, at D, exactly as the pipeline's scoring stage calls it.
        var primary = harness.LiveStrategies.Primary;
        var forward = await primary.Engine.ScoreCompanyAsync(companyId, D, CancellationToken.None);
        await harness.LiveScoreFileStores
            .ForStrategy(primary.Definition)
            .WriteAsync(forward.Snapshot, forward.Links, CancellationToken.None);

        // REPLAY: the same as-of instant, through the replay harness.
        var result = await harness.ReplayRunner.RunAsync(CancellationToken.None);
        Assert.Equal(1, result.SnapshotsWritten);

        var replayed = ReadReplayedSnapshots(harness, "run", primary.Definition.Name, companyId);
        var replayedSnapshot = Assert.Single(replayed);

        AssertScoringEquivalent(forward.Snapshot, replayedSnapshot.Snapshot);
        AssertLinksEquivalent(forward.Links, replayedSnapshot.Links);
    }

    /// <summary>
    /// The spec-136 predicate, exercised THROUGH the replay path: a signal that entered the store AFTER the
    /// as-of instant is invisible to a replay at that instant — in the current window and in the
    /// previous/velocity window alike. Without this, replay would manufacture hindsight and every downstream
    /// comparison would be worthless.
    /// </summary>
    [Fact]
    public async Task PostAsOfSignals_AreInvisibleToAReplayAsOfD_InBothWindows()
    {
        var window = TimeSpan.FromDays(30);

        // The control: only the signals Radar knew by D exist at all.
        using var known = ReplayTestHarness.Create(SinglePointPlan(D), scoringWindow: window);
        // The subject: the SAME store plus two signals learned a day AFTER D — one landing in the current
        // window, one in the previous (velocity) window.
        using var withFuture = ReplayTestHarness.Create(SinglePointPlan(D), scoringWindow: window);

        var knownCompany = await SeedKnownHistoryAsync(known, window);
        var futureCompany = await SeedKnownHistoryAsync(withFuture, window);

        await withFuture.SeedSignalAsync(
            futureCompany, SignalType.CustomerWin, D.AddDays(-1), D.AddDays(1), strength: 10);
        await withFuture.SeedSignalAsync(
            futureCompany, SignalType.CustomerWin, D - window - TimeSpan.FromDays(1), D.AddDays(1),
            strength: 10);

        await known.ReplayRunner.RunAsync(CancellationToken.None);
        await withFuture.ReplayRunner.RunAsync(CancellationToken.None);

        var expected = Assert.Single(ReadReplayedSnapshots(
            known, "run", known.LiveStrategies.Primary.Definition.Name, knownCompany));
        var actual = Assert.Single(ReadReplayedSnapshots(
            withFuture, "run", withFuture.LiveStrategies.Primary.Definition.Name, futureCompany));

        // Identical scores AND identical provenance: neither future signal contributed, and neither moved
        // velocity by inflating the previous window.
        AssertScoringEquivalent(expected.Snapshot, actual.Snapshot, exceptCompanyId: true);
        Assert.Equal(expected.Links.Count, actual.Links.Count);
    }

    /// <summary>
    /// Spec 136's legacy rule, honoured through replay: a persisted signal whose CreatedAtUtc was never
    /// recorded is treated as KNOWN (included) rather than silently dropped or back-dated. The fact was never
    /// captured, so replay must not invent it in either direction — and must match what the forward read does.
    /// </summary>
    [Fact]
    public async Task LegacySignalWithNoRecordedCreatedAt_IsIncludedAsKnown_AndReplayMatchesForward()
    {
        var window = TimeSpan.FromDays(30);

        using var withLegacy = ReplayTestHarness.Create(SinglePointPlan(D), scoringWindow: window);
        using var control = ReplayTestHarness.Create(SinglePointPlan(D), scoringWindow: window);

        var legacyCompany = await withLegacy.SeedCompanyAsync();
        var controlCompany = await control.SeedCompanyAsync();

        // Written straight to disk WITHOUT a createdAt property — the pre-136 on-disk shape. It falls in the
        // PREVIOUS window, which is the window sourced from disk (and therefore the one the null-CreatedAt
        // rule actually governs).
        WriteLegacySignalFile(
            withLegacy, legacyCompany, SignalType.CustomerWin, D - window - TimeSpan.FromDays(5));

        // One in-window signal on both sides so the snapshots are not entirely empty.
        await withLegacy.SeedSignalAsync(legacyCompany, SignalType.ProductLaunch, D.AddDays(-3), D.AddDays(-3));
        await control.SeedSignalAsync(controlCompany, SignalType.ProductLaunch, D.AddDays(-3), D.AddDays(-3));

        var primary = withLegacy.LiveStrategies.Primary;
        var forward = await primary.Engine.ScoreCompanyAsync(legacyCompany, D, CancellationToken.None);
        var controlForward = await control.LiveStrategies.Primary.Engine
            .ScoreCompanyAsync(controlCompany, D, CancellationToken.None);

        await withLegacy.ReplayRunner.RunAsync(CancellationToken.None);
        var replayed = Assert.Single(
            ReadReplayedSnapshots(withLegacy, "run", primary.Definition.Name, legacyCompany));

        // Not vacuous: the legacy previous-window signal really was counted as known activity, so velocity
        // moved relative to the otherwise-identical control. Had the null CreatedAt been read as "unknown ⇒
        // exclude", the two would be identical and this assertion would fail.
        Assert.NotEqual(controlForward.Snapshot.SignalVelocityScore, forward.Snapshot.SignalVelocityScore);

        // …and replay reproduces exactly that behaviour rather than reinterpreting the missing field.
        AssertScoringEquivalent(forward.Snapshot, replayed.Snapshot);
    }

    /// <summary>
    /// Replay mutates nothing but its own output: the signal store, the raw evidence on disk, the LIVE scores
    /// directory and the shared score repository the weekly report renders are all byte-for-byte untouched.
    /// The forward efficacy series is accrued history; a replay is a hypothesis.
    /// </summary>
    [Fact]
    public async Task Replay_WritesOnlyUnderTheReplayRoot_AndMutatesNoLiveStore()
    {
        using var harness = ReplayTestHarness.Create(
            new ReplayPlan("hypothesis", ReplaySeries.Create(D.AddDays(-2), D, TimeSpan.FromDays(1))));
        var companyId = await harness.SeedCompanyAsync();
        await harness.SeedSignalAsync(companyId, SignalType.CustomerWin, D.AddDays(-5), D.AddDays(-5));

        // A real forward snapshot already on disk and in the shared repository — the sacred history.
        var primary = harness.LiveStrategies.Primary;
        var forward = await primary.Engine.ScoreCompanyAsync(companyId, D, CancellationToken.None);
        await harness.LiveScoreFileStores
            .ForStrategy(primary.Definition)
            .WriteAsync(forward.Snapshot, forward.Links, CancellationToken.None);

        var signalsBefore = ReplayTestHarness.SnapshotOf(harness.SignalsDirectory);
        var scoresBefore = ReplayTestHarness.SnapshotOf(harness.ScoresDirectory);
        var evidenceBefore = await harness.Evidence.GetAllAsync(CancellationToken.None);
        var signalsRepoBefore = await harness.Signals.GetByCompanyAsync(companyId, CancellationToken.None);
        var liveSnapshotsBefore = await harness.LiveScoreRepository
            .GetSnapshotsForCompanyAsync(companyId, CancellationToken.None);

        var result = await harness.ReplayRunner.RunAsync(CancellationToken.None);

        Assert.Equal(3, result.AsOfPoints);
        Assert.Equal(3, result.SnapshotsWritten);

        // Nothing live moved — not one byte, not one repository entry.
        Assert.Equal(signalsBefore, ReplayTestHarness.SnapshotOf(harness.SignalsDirectory));
        Assert.Equal(scoresBefore, ReplayTestHarness.SnapshotOf(harness.ScoresDirectory));
        Assert.Equal(
            evidenceBefore.Select(e => e.Id).OrderBy(id => id),
            (await harness.Evidence.GetAllAsync(CancellationToken.None)).Select(e => e.Id).OrderBy(id => id));
        Assert.Equal(
            signalsRepoBefore.Select(s => s.Id).OrderBy(id => id),
            (await harness.Signals.GetByCompanyAsync(companyId, CancellationToken.None))
                .Select(s => s.Id).OrderBy(id => id));
        Assert.Equal(
            liveSnapshotsBefore.Count,
            (await harness.LiveScoreRepository
                .GetSnapshotsForCompanyAsync(companyId, CancellationToken.None)).Count);

        // …and the output exists ONLY under the replay root, labelled.
        var replayFiles = ReplayTestHarness.FilesUnder(harness.ReplaysDirectory);
        Assert.Equal(3, replayFiles.Count);
        Assert.All(
            replayFiles,
            f => Assert.StartsWith(
                $"hypothesis/strategies/{primary.Definition.Name}/{companyId}/", f, StringComparison.Ordinal));
    }

    /// <summary>
    /// Deterministic and idempotent: replaying the same range twice over an unchanged store produces the SAME
    /// file set with the SAME content — no accumulation, diffable to zero.
    /// <para>
    /// <b>The same deliberate exclusion as the replay⊆forward test:</b> the snapshot/link <c>Guid</c>s are
    /// freshly minted per call (forward runs do this too), so the comparison is over the persisted JSON with
    /// those id fields normalised away. Every scoring-relevant field is compared verbatim.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoIdenticalReplays_ProduceTheSameFileSetAndContent()
    {
        using var harness = ReplayTestHarness.Create(
            new ReplayPlan("twice", ReplaySeries.Create(D.AddDays(-2), D, TimeSpan.FromDays(1))));
        var companyId = await harness.SeedCompanyAsync();
        await harness.SeedSignalAsync(companyId, SignalType.CustomerWin, D.AddDays(-4), D.AddDays(-4));
        await harness.SeedSignalAsync(companyId, SignalType.InsiderBuying, D.AddDays(-1), D.AddDays(-1));

        await harness.ReplayRunner.RunAsync(CancellationToken.None);
        var first = ReplayTestHarness.SnapshotOf(harness.ReplaysDirectory);

        await harness.ReplayRunner.RunAsync(CancellationToken.None);
        var second = ReplayTestHarness.SnapshotOf(harness.ReplaysDirectory);

        // Same files (as-of-keyed names overwrite in place; ids would have accumulated a second copy).
        Assert.Equal(first.Keys.OrderBy(k => k, StringComparer.Ordinal), second.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(3, first.Count);

        foreach (var (path, content) in first)
        {
            Assert.Equal(WithoutMintedIds(content), WithoutMintedIds(second[path]));
        }
    }

    /// <summary>
    /// A 3-point from/to/step run produces exactly 3 as-of snapshots, each stamped with ITS OWN as-of instant
    /// (both the window end and the creation instant) — a replay series is a time series, not three copies of
    /// the same score.
    /// </summary>
    [Fact]
    public async Task ThreePointSeries_ProducesThreeSnapshots_EachStampedWithItsOwnAsOf()
    {
        using var harness = ReplayTestHarness.Create(
            new ReplayPlan("series", ReplaySeries.Create(D.AddDays(-2), D, TimeSpan.FromDays(1))));
        var companyId = await harness.SeedCompanyAsync();
        await harness.SeedSignalAsync(companyId, SignalType.CustomerWin, D.AddDays(-6), D.AddDays(-6));

        var result = await harness.ReplayRunner.RunAsync(CancellationToken.None);

        Assert.Equal(3, result.AsOfPoints);
        Assert.Equal(1, result.Strategies);
        Assert.Equal(3, result.SnapshotsWritten);

        var snapshots = ReadReplayedSnapshots(
            harness, "series", harness.LiveStrategies.Primary.Definition.Name, companyId);

        Assert.Equal(
            [D.AddDays(-2), D.AddDays(-1), D],
            snapshots.Select(s => s.Snapshot.WindowEndUtc).OrderBy(t => t).ToArray());
        Assert.All(snapshots, s => Assert.Equal(s.Snapshot.WindowEndUtc, s.Snapshot.CreatedAtUtc));
    }

    /// <summary>
    /// Multi-strategy replay (spec 137/138 carried through): each strategy gets its OWN
    /// <c>strategies/{name}/</c> subtree under the run label, its own <c>StrategyName</c> stamp, and — because
    /// their consumed signal sets differ — its own <c>ScoringConfigVersion</c>.
    /// </summary>
    [Fact]
    public async Task TwoStrategies_EachGetTheirOwnSubtreeAndStamp()
    {
        var strategies = new ScoringStrategySet(
        [
            new ScoringStrategyDefinition("broad", "default", new ScoringWeights(), IsPrimary: true),
            new ScoringStrategyDefinition("insider-only", "default", new ScoringWeights(), IsPrimary: false)
            {
                SignalTypes = SignalTypeFilter.Create([SignalType.InsiderBuying]),
            },
        ]);

        using var harness = ReplayTestHarness.Create(SinglePointPlan(D, "multi"), strategies);
        var companyId = await harness.SeedCompanyAsync();
        await harness.SeedSignalAsync(companyId, SignalType.CustomerWin, D.AddDays(-4), D.AddDays(-4));
        await harness.SeedSignalAsync(companyId, SignalType.InsiderBuying, D.AddDays(-3), D.AddDays(-3));

        var result = await harness.ReplayRunner.RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Strategies);
        Assert.Equal(2, result.SnapshotsWritten);

        var broad = Assert.Single(ReadReplayedSnapshots(harness, "multi", "broad", companyId));
        var insider = Assert.Single(ReadReplayedSnapshots(harness, "multi", "insider-only", companyId));

        Assert.Equal("broad", broad.Snapshot.StrategyName);
        Assert.Equal("insider-only", insider.Snapshot.StrategyName);

        // Different consumed signal sets ⇒ genuinely different scorings ⇒ different fingerprints.
        Assert.NotEqual(broad.Snapshot.ScoringConfigVersion, insider.Snapshot.ScoringConfigVersion);

        // Provenance is per strategy: the narrow one links only the signal it consumes.
        Assert.Equal(2, broad.Links.Count);
        Assert.Single(insider.Links);

        // …and the two subtrees are separate on disk under the ONE label.
        var files = ReplayTestHarness.FilesUnder(harness.ReplaysDirectory);
        Assert.Contains(files, f => f.StartsWith("multi/strategies/broad/", StringComparison.Ordinal));
        Assert.Contains(files, f => f.StartsWith("multi/strategies/insider-only/", StringComparison.Ordinal));
    }

    /// <summary>
    /// A sub-second step is reachable through config (<c>Radar:Replay:Step</c> accepts a plain TimeSpan
    /// string), so the series can hold several as-of points inside ONE second. Every one of them must reach
    /// disk: the reported <see cref="ReplayResult.SnapshotsWritten"/> and the actual file count have to agree,
    /// or the run silently truncated its own series while claiming otherwise.
    /// </summary>
    [Fact]
    public async Task SubSecondStep_WritesEveryAsOfPoint_ReportedCountMatchesDisk()
    {
        var start = D.AddMilliseconds(-1500);
        using var harness = ReplayTestHarness.Create(
            new ReplayPlan("sub-second", ReplaySeries.Create(start, D, TimeSpan.FromMilliseconds(500))));
        var companyId = await harness.SeedCompanyAsync();
        await harness.SeedSignalAsync(companyId, SignalType.CustomerWin, D.AddDays(-5), D.AddDays(-5));

        var result = await harness.ReplayRunner.RunAsync(CancellationToken.None);

        Assert.Equal(4, result.AsOfPoints);
        Assert.Equal(4, result.SnapshotsWritten);
        Assert.Equal(result.SnapshotsWritten, ReplayTestHarness.FilesUnder(harness.ReplaysDirectory).Count);

        // …and each file really is its own as-of instant, not four writes over one path.
        var snapshots = ReadReplayedSnapshots(
            harness, "sub-second", harness.LiveStrategies.Primary.Definition.Name, companyId);
        Assert.Equal(
            [start, start.AddMilliseconds(500), start.AddMilliseconds(1000), D],
            snapshots.Select(s => s.Snapshot.WindowEndUtc).OrderBy(t => t).ToArray());
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var harness = ReplayTestHarness.Create(SinglePointPlan(D));
        await harness.SeedCompanyAsync();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.ReplayRunner.RunAsync(cts.Token));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static async Task<Guid> SeedKnownHistoryAsync(ReplayTestHarness harness, TimeSpan window)
    {
        var companyId = await harness.SeedCompanyAsync();
        await harness.SeedSignalAsync(companyId, SignalType.ProductLaunch, D.AddDays(-10), D.AddDays(-10));
        await harness.SeedSignalAsync(
            companyId, SignalType.CustomerWin, D - window - TimeSpan.FromDays(3),
            D - window - TimeSpan.FromDays(3));
        return companyId;
    }

    /// <summary>
    /// Every scoring-relevant field of a snapshot. The freshly-minted <see cref="CompanyScoreSnapshot.Id"/>
    /// is excluded on purpose — see the class-level note on the replay⊆forward test.
    /// </summary>
    private static void AssertScoringEquivalent(
        CompanyScoreSnapshot expected, CompanyScoreSnapshot actual, bool exceptCompanyId = false)
    {
        if (!exceptCompanyId)
        {
            Assert.Equal(expected.CompanyId, actual.CompanyId);
        }

        Assert.Equal(expected.TrajectoryScore, actual.TrajectoryScore);
        Assert.Equal(expected.OpportunityScore, actual.OpportunityScore);
        Assert.Equal(expected.AttentionScore, actual.AttentionScore);
        Assert.Equal(expected.EvidenceConfidenceScore, actual.EvidenceConfidenceScore);
        Assert.Equal(expected.SignalVelocityScore, actual.SignalVelocityScore);
        Assert.Equal(expected.Explanation, actual.Explanation);
        Assert.Equal(expected.ComponentJson, actual.ComponentJson);
        Assert.Equal(expected.ScoringVersion, actual.ScoringVersion);
        Assert.Equal(expected.ScoringConfigVersion, actual.ScoringConfigVersion);
        Assert.Equal(expected.StrategyName, actual.StrategyName);
        Assert.Equal(expected.WindowStartUtc, actual.WindowStartUtc);
        Assert.Equal(expected.WindowEndUtc, actual.WindowEndUtc);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
    }

    /// <summary>
    /// The ordered provenance chain. The link <c>Id</c>/<c>ScoreSnapshotId</c> are excluded for the same
    /// reason the snapshot <c>Id</c> is; everything that says WHAT the evidence contributed is compared.
    /// </summary>
    private static void AssertLinksEquivalent(
        IReadOnlyList<ScoreEvidenceLink> expected, IReadOnlyList<ScoreEvidenceLink> actual)
    {
        Assert.Equal(
            expected.Select(l => (l.SignalId, l.EvidenceId, l.ContributionReason, l.ContributionWeight)),
            actual.Select(l => (l.SignalId, l.EvidenceId, l.ContributionReason, l.ContributionWeight)));
        Assert.NotEmpty(actual);
    }

    /// <summary>
    /// Reads back a replayed strategy's persisted snapshots + links, straight off disk via the same store the
    /// runner wrote them with (so the assertions are over what an operator would actually find).
    /// </summary>
    private static IReadOnlyList<PersistedSnapshot> ReadReplayedSnapshots(
        ReplayTestHarness harness, string label, string strategyName, Guid companyId)
    {
        var directory = Path.Combine(
            harness.ReplaysDirectory,
            label,
            StrategyScopedScoreSnapshotFileStoreFactory.StrategiesSegment,
            strategyName,
            companyId.ToString());

        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(directory, "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => PersistedSnapshot.Parse(File.ReadAllText(f)))];
    }

    /// <summary>
    /// Normalises the per-call minted GUIDs out of a persisted snapshot's JSON so two runs' files can be
    /// compared verbatim on everything else. Same deliberate exclusion documented above.
    /// </summary>
    private static string WithoutMintedIds(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var buffer = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("snapshotId"))
                {
                    continue;
                }

                if (property.NameEquals("links"))
                {
                    writer.WriteStartArray("links");
                    foreach (var link in property.Value.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        foreach (var linkProperty in link.EnumerateObject())
                        {
                            if (linkProperty.NameEquals("linkId") || linkProperty.NameEquals("scoreSnapshotId"))
                            {
                                continue;
                            }

                            linkProperty.WriteTo(writer);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Writes a signal file in the PRE-136 on-disk shape: no <c>createdAt</c> property at all. Hand-written
    /// rather than produced by the store, because the store (correctly) always writes the field now — the
    /// point of the test is the history that predates it.
    /// </summary>
    private static void WriteLegacySignalFile(
        ReplayTestHarness harness, Guid companyId, SignalType type, DateTimeOffset observedAtUtc)
    {
        var signalId = Guid.NewGuid();
        var path = Path.Combine(
            harness.SignalsDirectory,
            observedAtUtc.UtcDateTime.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            observedAtUtc.UtcDateTime.ToString("MM", System.Globalization.CultureInfo.InvariantCulture),
            signalId + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var json = $$"""
        {
          "signalId": "{{signalId}}",
          "evidenceId": "{{Guid.NewGuid()}}",
          "companyId": "{{companyId}}",
          "companyMention": "Acme Corp",
          "type": "{{type}}",
          "direction": "Positive",
          "strength": 6,
          "novelty": 6,
          "confidence": 0.8,
          "supportingExcerpt": "signed a multi-year deal",
          "reason": "Legacy signal with no recorded knowledge date.",
          "reviewStatus": "Approved",
          "observedAt": "{{observedAtUtc.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}}",
          "review": null
        }
        """;

        File.WriteAllText(path, json);
    }

    private sealed record PersistedSnapshot(
        CompanyScoreSnapshot Snapshot, IReadOnlyList<ScoreEvidenceLink> Links)
    {
        public static PersistedSnapshot Parse(string json)
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;

            var snapshotId = root.GetProperty("snapshotId").GetGuid();
            var snapshot = new CompanyScoreSnapshot(
                Id: snapshotId,
                CompanyId: root.GetProperty("companyId").GetGuid(),
                ScoringVersion: root.GetProperty("scoringVersion").GetString()!,
                TrajectoryScore: root.GetProperty("trajectoryScore").GetInt32(),
                OpportunityScore: root.GetProperty("opportunityScore").GetInt32(),
                AttentionScore: root.GetProperty("attentionScore").GetInt32(),
                EvidenceConfidenceScore: root.GetProperty("evidenceConfidenceScore").GetInt32(),
                SignalVelocityScore: root.GetProperty("signalVelocityScore").GetInt32(),
                Explanation: root.GetProperty("explanation").GetString()!,
                ComponentJson: root.GetProperty("componentJson").GetString()!,
                WindowStartUtc: root.GetProperty("windowStartUtc").GetDateTimeOffset(),
                WindowEndUtc: root.GetProperty("windowEndUtc").GetDateTimeOffset(),
                CreatedAtUtc: root.GetProperty("createdAtUtc").GetDateTimeOffset(),
                ScoringConfigVersion: root.GetProperty("scoringConfigVersion").GetString(),
                StrategyName: root.GetProperty("strategyName").GetString());

            var links = root.GetProperty("links").EnumerateArray()
                .Select(l => new ScoreEvidenceLink(
                    Id: l.GetProperty("linkId").GetGuid(),
                    ScoreSnapshotId: l.GetProperty("scoreSnapshotId").GetGuid(),
                    SignalId: l.GetProperty("signalId").GetGuid(),
                    EvidenceId: l.GetProperty("evidenceId").GetGuid(),
                    ContributionReason: l.GetProperty("contributionReason").GetString()!,
                    ContributionWeight: l.GetProperty("contributionWeight").GetInt32()))
                .ToList();

            return new PersistedSnapshot(snapshot, links);
        }
    }
}
