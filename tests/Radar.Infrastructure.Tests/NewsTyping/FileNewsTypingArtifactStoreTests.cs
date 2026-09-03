using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.NewsTyping;

namespace Radar.Infrastructure.Tests.NewsTyping;

/// <summary>
/// Spec 186 §2: the decomposition artifact gains the ADDITIVE per-cohort <c>RetryExhausted</c> count, and
/// spec 187 adds <c>ReservedWithoutOutcome</c> (§3) plus the <c>CandidatePrioritySelected</c> /
/// <c>GeneralSelected</c> lane split (§2) while §4 corrects what <c>UntypedRemaining</c> MEANS — so the
/// schema tag moves to <c>news-typing-decomposition-v3</c> ONCE, jointly, not per section. Spec 189 §3 then
/// moves it to <c>news-typing-decomposition-v4</c> for the capture-inflow fields, the authoritative pass-wide
/// reader summaries and the three per-cohort diagnostics. Readers are BY NAME, so a consumer written against
/// v1 — or against v3 — reads a v4 document unchanged, asserted here against the production writer rather
/// than asserted in prose.
/// </summary>
public sealed class FileNewsTypingArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "radar-newstyping-artifact-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    /// <summary>The pre-186 (v1) document shape, verbatim: no <c>RetryExhausted</c> anywhere.</summary>
    private sealed record V1Document(
        string SchemaVersion,
        Guid? RunId,
        DateTimeOffset WindowStartUtc,
        DateTimeOffset WindowEndUtc,
        string Caveat,
        IReadOnlyList<string> Readers,
        bool? CaptureProvenThisRun,
        IReadOnlyList<V1Company> Companies,
        int ObservationsWithoutCompany,
        DateTimeOffset GeneratedAtUtc);

    private sealed record V1Company(
        Guid CompanyId,
        string? Ticker,
        int ObservationsInWindow,
        bool Incomplete,
        IReadOnlyList<string> IncompleteReasons,
        IReadOnlyList<V1Cohort> Cohorts);

    private sealed record V1Cohort(
        string ReaderName,
        string Provider,
        string ModelId,
        string CohortKey,
        NewsObservationCaptureMode CaptureMode,
        int ObservationsTyped,
        int ObservationsInsufficientContent,
        int UntypedRemaining,
        int FamilyCount,
        IReadOnlyList<NewsTypingDecompositionTypeRow> Types);

    /// <summary>
    /// The pre-189 (v3) document shape, verbatim: the spec-187 counters, and NOTHING spec 189 added — no
    /// batch id, no capture count, no reader summaries, no per-cohort retry/call/retryable-failure columns.
    /// </summary>
    private sealed record V3Document(
        string SchemaVersion,
        Guid? RunId,
        DateTimeOffset WindowStartUtc,
        DateTimeOffset WindowEndUtc,
        string Caveat,
        IReadOnlyList<string> Readers,
        bool? CaptureProvenThisRun,
        IReadOnlyList<V3Company> Companies,
        int ObservationsWithoutCompany,
        DateTimeOffset GeneratedAtUtc);

    private sealed record V3Company(
        Guid CompanyId,
        string? Ticker,
        int ObservationsInWindow,
        bool Incomplete,
        IReadOnlyList<string> IncompleteReasons,
        IReadOnlyList<V3Cohort> Cohorts);

    private sealed record V3Cohort(
        string ReaderName,
        string Provider,
        string ModelId,
        string CohortKey,
        NewsObservationCaptureMode CaptureMode,
        int ObservationsTyped,
        int ObservationsInsufficientContent,
        int UntypedRemaining,
        int FamilyCount,
        IReadOnlyList<NewsTypingDecompositionTypeRow> Types,
        int RetryExhausted,
        int ReservedWithoutOutcome,
        int CandidatePrioritySelected,
        int GeneralSelected);

    private static readonly JsonSerializerOptions ReaderOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    private static NewsTypingDecompositionDocument Document(
        int retryExhausted,
        int reservedWithoutOutcome = 0,
        int candidatePrioritySelected = 0,
        int generalSelected = 0,
        int retrySelected = 0,
        int providerCallsAttempted = 0,
        int retryableFailuresThisRun = 0,
        Guid? newsObservationBatchId = null,
        int? observationsCapturedThisRun = null,
        IReadOnlyList<NewsTypingDecompositionReaderSummary>? readerSummaries = null) => new(
        SchemaVersion: NewsTypingDecompositionDocument.CurrentSchemaVersion,
        RunId: new Guid("cccccccc-0000-0000-0000-000000000001"),
        WindowStartUtc: new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero),
        WindowEndUtc: new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero),
        Caveat: NewsTypingDecompositionDocument.Caveat181,
        Readers: ["a (openai:model-a)"],
        CaptureProvenThisRun: true,
        Companies:
        [
            new NewsTypingDecompositionCompany(
                CompanyId: new Guid("aaaaaaaa-0000-0000-0000-000000000001"),
                Ticker: "TST",
                ObservationsInWindow: 4,
                Incomplete: true,
                IncompleteReasons:
                [
                    "typing retries exhausted: 1 observation(s) will not be typed for a (ProspectiveRss)",
                ],
                Cohorts:
                [
                    new NewsTypingDecompositionCohort(
                        ReaderName: "a",
                        Provider: "openai",
                        ModelId: "model-a",
                        CohortKey: NewsTypingContract.CohortKey("openai", "model-a"),
                        CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
                        ObservationsTyped: 3,
                        ObservationsInsufficientContent: 0,
                        UntypedRemaining: 1,
                        FamilyCount: 2,
                        Types:
                        [
                            new NewsTypingDecompositionTypeRow(
                                NewsEventType.FinancingOrDilution, 3, 2, 2),
                        ],
                        RetryExhausted: retryExhausted,
                        ReservedWithoutOutcome: reservedWithoutOutcome,
                        CandidatePrioritySelected: candidatePrioritySelected,
                        GeneralSelected: generalSelected,
                        RetrySelected: retrySelected,
                        ProviderCallsAttempted: providerCallsAttempted,
                        RetryableFailuresThisRun: retryableFailuresThisRun),
                ]),
        ],
        ObservationsWithoutCompany: 0,
        GeneratedAtUtc: new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
        NewsObservationBatchId: newsObservationBatchId,
        ObservationsCapturedThisRun: observationsCapturedThisRun,
        ReaderSummaries: readerSummaries);

    private async Task<string> WriteAndReadJsonAsync(NewsTypingDecompositionDocument document)
    {
        var store = new FileNewsTypingArtifactStore(
            new FileNewsTypingArtifactStoreOptions { RootDirectory = _root },
            NullLogger<FileNewsTypingArtifactStore>.Instance);
        await store.WriteDecompositionAsync(Instant0250Z, RunA, "# md", document, CancellationToken.None);
        return await File.ReadAllTextAsync(LivePath(PinnedBaseNameA + ".json"));
    }

    [Fact]
    public void SchemaTag_IsTheNamedV4Bump()
    {
        Assert.Equal(
            "news-typing-decomposition-v4", NewsTypingDecompositionDocument.CurrentSchemaVersion);
    }

    [Fact]
    public async Task RetryExhausted_IsWrittenByName_AndAV1ReaderIsUnaffected()
    {
        var json = await WriteAndReadJsonAsync(Document(retryExhausted: 1));

        Assert.Contains("\"retryExhausted\": 1", json, StringComparison.Ordinal);

        // The by-NAME v1 reader still binds every field it knows and simply ignores the additive one.
        var v1 = JsonSerializer.Deserialize<V1Document>(json, ReaderOptions);
        Assert.NotNull(v1);
        Assert.Equal("news-typing-decomposition-v4", v1.SchemaVersion);
        Assert.Equal(NewsTypingDecompositionDocument.Caveat181, v1.Caveat);
        var cohort = Assert.Single(Assert.Single(v1.Companies).Cohorts);
        Assert.Equal("a", cohort.ReaderName);
        Assert.Equal(3, cohort.ObservationsTyped);
        Assert.Equal(1, cohort.UntypedRemaining);
        Assert.Equal(2, cohort.FamilyCount);
        Assert.Equal(NewsObservationCaptureMode.ProspectiveRss, cohort.CaptureMode);
        Assert.Equal(NewsEventType.FinancingOrDilution, Assert.Single(cohort.Types).EventType);
    }

    [Fact]
    public async Task RoundTrip_ThroughTheProductionWriter_IsLossless()
    {
        var document = Document(retryExhausted: 2);

        var json = await WriteAndReadJsonAsync(document);
        var parsed = JsonSerializer.Deserialize<NewsTypingDecompositionDocument>(json, ReaderOptions);

        Assert.NotNull(parsed);
        Assert.Equal(2, Assert.Single(Assert.Single(parsed.Companies).Cohorts).RetryExhausted);
    }

    /// <summary>
    /// Spec 187 §3: the additive <c>ReservedWithoutOutcome</c> counter is written by name and round-trips,
    /// and the by-NAME v1 reader — which knows nothing about it — still binds every field it does know.
    /// Existing v1/v2 artifacts on disk are untouched; this is the "additive, not a migration" proof.
    /// </summary>
    [Fact]
    public async Task ReservedWithoutOutcome_IsWrittenByName_AndAV1ReaderIsStillUnaffected()
    {
        var json = await WriteAndReadJsonAsync(Document(retryExhausted: 1, reservedWithoutOutcome: 4));

        Assert.Contains("\"reservedWithoutOutcome\": 4", json, StringComparison.Ordinal);

        var v1 = JsonSerializer.Deserialize<V1Document>(json, ReaderOptions);
        Assert.NotNull(v1);
        var v1Cohort = Assert.Single(Assert.Single(v1.Companies).Cohorts);
        Assert.Equal(3, v1Cohort.ObservationsTyped);
        Assert.Equal(1, v1Cohort.UntypedRemaining);

        var parsed = JsonSerializer.Deserialize<NewsTypingDecompositionDocument>(json, ReaderOptions);
        Assert.NotNull(parsed);
        Assert.Equal(4, Assert.Single(Assert.Single(parsed.Companies).Cohorts).ReservedWithoutOutcome);
    }

    /// <summary>
    /// Spec 189 §3: the v4 additions — capture inflow, the AUTHORITATIVE pass-wide reader summary, and the
    /// three per-cohort diagnostics — are all written BY NAME and round-trip through the production writer.
    /// </summary>
    [Fact]
    public async Task TheV4Diagnostics_AreWrittenByName_AndRoundTrip()
    {
        var batchId = new Guid("bbbbbbbb-0000-0000-0000-000000000001");
        var json = await WriteAndReadJsonAsync(Document(
            retryExhausted: 0,
            candidatePrioritySelected: 7,
            generalSelected: 5,
            retrySelected: 1,
            providerCallsAttempted: 12,
            retryableFailuresThisRun: 2,
            newsObservationBatchId: batchId,
            observationsCapturedThisRun: 252,
            readerSummaries: [ReaderSummary()]));

        Assert.Contains("\"retrySelected\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"providerCallsAttempted\": 12", json, StringComparison.Ordinal);
        Assert.Contains("\"retryableFailuresThisRun\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"observationsCapturedThisRun\": 252", json, StringComparison.Ordinal);
        Assert.Contains("\"newsObservationBatchId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"readerSummaries\"", json, StringComparison.Ordinal);

        var parsed = JsonSerializer.Deserialize<NewsTypingDecompositionDocument>(json, ReaderOptions);
        Assert.NotNull(parsed);
        Assert.Equal(batchId, parsed.NewsObservationBatchId);
        Assert.Equal(252, parsed.ObservationsCapturedThisRun);

        var summary = Assert.Single(parsed.ReaderSummaries!);
        Assert.Equal(1, summary.RetrySelected);
        Assert.Equal(150, summary.CandidatePrioritySelected);
        Assert.Equal(199, summary.GeneralSelected);
        Assert.Equal(350, summary.ProviderCallsAttempted);
        Assert.Equal(5, summary.ValidationFailures);
        Assert.Equal(2_017, summary.UntypedRemaining);

        var cohort = Assert.Single(Assert.Single(parsed.Companies).Cohorts);
        Assert.Equal(1, cohort.RetrySelected);
        Assert.Equal(12, cohort.ProviderCallsAttempted);
        Assert.Equal(2, cohort.RetryableFailuresThisRun);
    }

    /// <summary>
    /// Spec 189 §3's compatibility claim, asserted against the production writer rather than in prose: a
    /// consumer written against the PRE-189 v3 shape reads a v4 document unchanged, binding every field it
    /// knows and ignoring every field it does not. Existing v1–v3 artifacts on disk are untouched.
    /// </summary>
    [Fact]
    public async Task AV3ByNameConsumer_ReadsAV4Document_Unchanged()
    {
        var json = await WriteAndReadJsonAsync(Document(
            retryExhausted: 1,
            reservedWithoutOutcome: 4,
            candidatePrioritySelected: 7,
            generalSelected: 5,
            retrySelected: 1,
            providerCallsAttempted: 12,
            retryableFailuresThisRun: 2,
            newsObservationBatchId: new Guid("bbbbbbbb-0000-0000-0000-000000000001"),
            observationsCapturedThisRun: 252,
            readerSummaries: [ReaderSummary()]));

        var v3 = JsonSerializer.Deserialize<V3Document>(json, ReaderOptions);
        Assert.NotNull(v3);
        Assert.Equal("news-typing-decomposition-v4", v3.SchemaVersion);
        Assert.Equal(NewsTypingDecompositionDocument.Caveat181, v3.Caveat);

        var cohort = Assert.Single(Assert.Single(v3.Companies).Cohorts);
        Assert.Equal(3, cohort.ObservationsTyped);
        Assert.Equal(1, cohort.UntypedRemaining);
        Assert.Equal(2, cohort.FamilyCount);
        Assert.Equal(1, cohort.RetryExhausted);
        Assert.Equal(4, cohort.ReservedWithoutOutcome);
        Assert.Equal(7, cohort.CandidatePrioritySelected);
        Assert.Equal(5, cohort.GeneralSelected);
    }

    /// <summary>
    /// A document written with the pre-189 field set (no reader summaries, no capture inflow, no per-cohort
    /// v4 columns) hydrates as null/0 — the trailing-defaulted-additive convention, checked against the real
    /// deserializer instead of assumed. <c>null</c> reader summaries mean NOT RECORDED, never "a pass that
    /// selected nothing".
    /// </summary>
    [Fact]
    public async Task APreSpec189Document_WithoutTheV4Fields_StillHydrates()
    {
        var json = await WriteAndReadJsonAsync(Document(
            retryExhausted: 0,
            retrySelected: 1,
            providerCallsAttempted: 12,
            retryableFailuresThisRun: 2,
            newsObservationBatchId: new Guid("bbbbbbbb-0000-0000-0000-000000000001"),
            observationsCapturedThisRun: 252,
            readerSummaries: [ReaderSummary()]));
        var document = JsonNode.Parse(json)!.AsObject();
        Assert.True(document.Remove("newsObservationBatchId"));
        Assert.True(document.Remove("observationsCapturedThisRun"));
        Assert.True(document.Remove("readerSummaries"));
        foreach (var company in document["companies"]!.AsArray())
        {
            foreach (var cohort in company!["cohorts"]!.AsArray())
            {
                Assert.True(cohort!.AsObject().Remove("retrySelected"));
                Assert.True(cohort.AsObject().Remove("providerCallsAttempted"));
                Assert.True(cohort.AsObject().Remove("retryableFailuresThisRun"));
            }
        }

        var parsed = JsonSerializer.Deserialize<NewsTypingDecompositionDocument>(
            document.ToJsonString(), ReaderOptions);

        Assert.NotNull(parsed);
        Assert.Null(parsed.NewsObservationBatchId);
        Assert.Null(parsed.ObservationsCapturedThisRun);
        Assert.Null(parsed.ReaderSummaries);
        var hydrated = Assert.Single(Assert.Single(parsed.Companies).Cohorts);
        Assert.Equal(0, hydrated.RetrySelected);
        Assert.Equal(0, hydrated.ProviderCallsAttempted);
        Assert.Equal(0, hydrated.RetryableFailuresThisRun);
    }

    /// <summary>The <c>a180298d</c>-shaped pass-wide summary, at the spec-189 350/150/25 posture.</summary>
    private static NewsTypingDecompositionReaderSummary ReaderSummary() => new(
        ReaderName: "a",
        Provider: "openai",
        ModelId: "model-a",
        CohortKey: NewsTypingContract.CohortKey("openai", "model-a"),
        RetrySelected: 1,
        CandidatePrioritySelected: 150,
        GeneralSelected: 199,
        ProviderCallsAttempted: 350,
        CompletedOutcomesPersisted: 345,
        ProviderFailures: 0,
        ParseFailures: 0,
        ValidationFailures: 5,
        ReservationsRefused: 0,
        OutcomeWritesFailed: 0,
        RetryExhausted: 0,
        ReservedWithoutOutcome: 0,
        UntypedRemaining: 2_017);

    /// <summary>
    /// Spec 187 §2: the additive per-company lane split is written by name and round-trips, and the by-NAME
    /// v1 reader — which knows nothing about either counter — still binds every field it does know. The
    /// schema tag does NOT move again: §2 and §4 share the single v3 bump.
    /// </summary>
    [Fact]
    public async Task TheLaneSplit_IsWrittenByName_AndAV1ReaderIsStillUnaffected()
    {
        var json = await WriteAndReadJsonAsync(
            Document(retryExhausted: 0, candidatePrioritySelected: 7, generalSelected: 5));

        Assert.Contains("\"candidatePrioritySelected\": 7", json, StringComparison.Ordinal);
        Assert.Contains("\"generalSelected\": 5", json, StringComparison.Ordinal);

        var v1 = JsonSerializer.Deserialize<V1Document>(json, ReaderOptions);
        Assert.NotNull(v1);
        Assert.Equal("news-typing-decomposition-v4", v1.SchemaVersion);
        var v1Cohort = Assert.Single(Assert.Single(v1.Companies).Cohorts);
        Assert.Equal(3, v1Cohort.ObservationsTyped);
        Assert.Equal(1, v1Cohort.UntypedRemaining);

        var parsed = JsonSerializer.Deserialize<NewsTypingDecompositionDocument>(json, ReaderOptions);
        Assert.NotNull(parsed);
        var cohort = Assert.Single(Assert.Single(parsed!.Companies).Cohorts);
        Assert.Equal(7, cohort.CandidatePrioritySelected);
        Assert.Equal(5, cohort.GeneralSelected);
    }

    /// <summary>
    /// A document written with the pre-187 §2 field set (no lane-split keys at all) hydrates as 0/0 — the
    /// trailing-defaulted-additive convention, checked against the real deserializer instead of assumed.
    /// </summary>
    [Fact]
    public async Task APreSpec187LaneSplitDocument_WithoutTheAdditiveCounters_StillHydrates()
    {
        var json = await WriteAndReadJsonAsync(
            Document(retryExhausted: 0, candidatePrioritySelected: 7, generalSelected: 5));
        var document = JsonNode.Parse(json)!.AsObject();
        foreach (var company in document["companies"]!.AsArray())
        {
            foreach (var cohort in company!["cohorts"]!.AsArray())
            {
                Assert.True(cohort!.AsObject().Remove("candidatePrioritySelected"));
                Assert.True(cohort.AsObject().Remove("generalSelected"));
            }
        }

        var stripped = document.ToJsonString();
        var parsed = JsonSerializer.Deserialize<NewsTypingDecompositionDocument>(stripped, ReaderOptions);

        Assert.NotNull(parsed);
        var hydrated = Assert.Single(Assert.Single(parsed!.Companies).Cohorts);
        Assert.Equal(0, hydrated.CandidatePrioritySelected);
        Assert.Equal(0, hydrated.GeneralSelected);
    }

    /// <summary>
    /// A document written with the pre-187 field set (no <c>reservedWithoutOutcome</c> key at all) hydrates
    /// as 0 rather than failing — the trailing-defaulted-additive convention, checked against the real
    /// deserializer instead of assumed.
    /// </summary>
    [Fact]
    public async Task APreSpec187Document_WithoutTheAdditiveCounter_StillHydrates()
    {
        var json = await WriteAndReadJsonAsync(Document(retryExhausted: 1));
        var document = JsonNode.Parse(json)!.AsObject();
        foreach (var company in document["companies"]!.AsArray())
        {
            foreach (var cohort in company!["cohorts"]!.AsArray())
            {
                Assert.True(cohort!.AsObject().Remove("reservedWithoutOutcome"));
            }
        }

        var stripped = document.ToJsonString();
        Assert.DoesNotContain("reservedWithoutOutcome", stripped, StringComparison.Ordinal);

        var parsed = JsonSerializer.Deserialize<NewsTypingDecompositionDocument>(stripped, ReaderOptions);

        Assert.NotNull(parsed);
        Assert.Equal(0, Assert.Single(Assert.Single(parsed.Companies).Cohorts).ReservedWithoutOutcome);
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 208 — run-scoped artifact identity: as-of INSTANT + run id, never the as-of date alone. The
    // date-keyed name let the 2026-09-01 21:46Z run overwrite the 02:50Z run's artifact (run 3 of spec 200
    // §5); these tests are the mutation proof that a same-day run can no longer do that.
    // ---------------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Instant0250Z = new(2026, 9, 1, 2, 50, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Instant2146Z = new(2026, 9, 1, 21, 46, 0, TimeSpan.Zero);
    private static readonly Guid RunA = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly Guid RunB = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    /// <summary>
    /// The pinned shape for <see cref="Instant0250Z"/> + <see cref="RunA"/>: <c>yyyyMMdd'T'HHmmss'Z'</c>
    /// instant + <c>D</c>-format GUID.
    /// </summary>
    private const string PinnedBaseNameA =
        "attention-decomposition-20260901T025000Z-0f8fad5b-d9cb-469f-a165-70867728950e";

    private FileNewsTypingArtifactStore Store(ILogger<FileNewsTypingArtifactStore>? logger = null) => new(
        new FileNewsTypingArtifactStoreOptions { RootDirectory = _root },
        logger ?? NullLogger<FileNewsTypingArtifactStore>.Instance);

    private string LivePath(string fileName) => Path.Combine(_root, "live", fileName);

    private string[] LiveFileNames()
    {
        var live = Path.Combine(_root, "live");
        return Directory.Exists(live)
            ? [.. Directory.GetFiles(live).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)]
            : [];
    }

    [Fact]
    public void BaseName_MatchesThePinnedInstantPlusRunIdShape_Exactly()
    {
        Assert.Equal(PinnedBaseNameA, NewsTypingArtifactNames.BaseName(Instant0250Z, RunA));
        Assert.Equal(PinnedBaseNameA + "-FAILED", NewsTypingArtifactNames.FailedBaseName(Instant0250Z, RunA));
        Assert.Equal(
            "attention-decomposition-20260901T025000Z",
            NewsTypingArtifactNames.BaseName(Instant0250Z, runId: null));
        // A non-UTC offset is normalised to UTC before formatting: 04:50+02:00 IS 02:50Z.
        Assert.Equal(
            PinnedBaseNameA,
            NewsTypingArtifactNames.BaseName(new DateTimeOffset(2026, 9, 1, 4, 50, 0, TimeSpan.FromHours(2)), RunA));
    }

    [Fact]
    public async Task EmittedFileName_MatchesThePinnedShape_Exactly()
    {
        await Store().WriteDecompositionAsync(
            Instant0250Z, RunA, "# md", Document(retryExhausted: 0), CancellationToken.None);

        Assert.Equal(new[] { PinnedBaseNameA + ".json", PinnedBaseNameA + ".md" }, LiveFileNames());
        Assert.True(File.Exists(LivePath(
            "attention-decomposition-20260901T025000Z-0f8fad5b-d9cb-469f-a165-70867728950e.md")));
    }

    /// <summary>
    /// The mutation proof: revert to the date-keyed name and the second write clobbers the first, so the
    /// FIRST pair's content assertions fail.
    /// </summary>
    [Fact]
    public async Task SameInstant_DifferentRunIds_ProduceTwoSurvivingPairs_WithTheFirstIntact()
    {
        var store = Store();
        var first = Document(retryExhausted: 1) with { RunId = RunA };
        var second = Document(retryExhausted: 2) with { RunId = RunB };

        await store.WriteDecompositionAsync(Instant0250Z, RunA, "# run A", first, CancellationToken.None);
        await store.WriteDecompositionAsync(Instant0250Z, RunB, "# run B", second, CancellationToken.None);

        var baseA = NewsTypingArtifactNames.BaseName(Instant0250Z, RunA);
        var baseB = NewsTypingArtifactNames.BaseName(Instant0250Z, RunB);
        Assert.NotEqual(baseA, baseB);
        Assert.Equal(
            new[] { baseA + ".json", baseA + ".md", baseB + ".json", baseB + ".md" },
            LiveFileNames());

        Assert.Equal("# run A", await File.ReadAllTextAsync(LivePath(baseA + ".md")));
        var parsedA = JsonSerializer.Deserialize<NewsTypingDecompositionDocument>(
            await File.ReadAllTextAsync(LivePath(baseA + ".json")), ReaderOptions);
        Assert.NotNull(parsedA);
        Assert.Equal(RunA, parsedA.RunId);
        Assert.Equal(1, Assert.Single(Assert.Single(parsedA.Companies).Cohorts).RetryExhausted);

        Assert.Equal("# run B", await File.ReadAllTextAsync(LivePath(baseB + ".md")));
        var parsedB = JsonSerializer.Deserialize<NewsTypingDecompositionDocument>(
            await File.ReadAllTextAsync(LivePath(baseB + ".json")), ReaderOptions);
        Assert.NotNull(parsedB);
        Assert.Equal(RunB, parsedB.RunId);
        Assert.Equal(2, Assert.Single(Assert.Single(parsedB.Companies).Cohorts).RetryExhausted);
    }

    /// <summary>The 2026-09-01 shape: run 3 at 02:50Z, run 4 at 21:46Z, one UTC date.</summary>
    [Fact]
    public async Task SameDate_DifferentInstants_Coexist_WithTheEarlierIntact()
    {
        var store = Store();

        await store.WriteDecompositionAsync(
            Instant0250Z,
            RunA,
            "# run 3 (02:50Z)",
            Document(retryExhausted: 1) with { RunId = RunA },
            CancellationToken.None);
        await store.WriteDecompositionAsync(
            Instant2146Z,
            RunB,
            "# run 4 (21:46Z)",
            Document(retryExhausted: 2) with { RunId = RunB },
            CancellationToken.None);

        var base0250 = NewsTypingArtifactNames.BaseName(Instant0250Z, RunA);
        var base2146 = NewsTypingArtifactNames.BaseName(Instant2146Z, RunB);
        Assert.Equal(
            "attention-decomposition-20260901T214600Z-7c9e6679-7425-40de-944b-e07fc1f90ae7", base2146);
        Assert.Equal(
            new[] { base0250 + ".json", base0250 + ".md", base2146 + ".json", base2146 + ".md" },
            LiveFileNames());
        Assert.Equal("# run 3 (02:50Z)", await File.ReadAllTextAsync(LivePath(base0250 + ".md")));
        Assert.Equal("# run 4 (21:46Z)", await File.ReadAllTextAsync(LivePath(base2146 + ".md")));
    }

    [Fact]
    public async Task FailedArtifact_CarriesTheRunScopedName()
    {
        await Store().WriteFailedAsync(Instant0250Z, RunA, "boom", CancellationToken.None);

        Assert.Equal(new[] { PinnedBaseNameA + "-FAILED.md" }, LiveFileNames());
        var content = await File.ReadAllTextAsync(LivePath(PinnedBaseNameA + "-FAILED.md"));
        Assert.StartsWith(
            "# News-typing pass FAILED — 2026-09-01T02:50:00.0000000+00:00 "
                + "(run 0f8fad5b-d9cb-469f-a165-70867728950e)",
            content,
            StringComparison.Ordinal);
        Assert.Contains("Reason: boom", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec 208 §2: accrued date-keyed artifacts heal forward only — a legacy pair (and FAILED file) on disk
    /// is byte-for-byte untouched by a run-scoped write on the same date.
    /// </summary>
    [Fact]
    public async Task LegacyDateKeyedFiles_AreByteForByteUntouched_ByASameDateRunScopedWrite()
    {
        Directory.CreateDirectory(Path.Combine(_root, "live"));
        var legacyMd = LivePath("attention-decomposition-2026-09-01.md");
        var legacyJson = LivePath("attention-decomposition-2026-09-01.json");
        var legacyFailed = LivePath("attention-decomposition-2026-09-01-FAILED.md");
        var mdBytes = Encoding.UTF8.GetBytes("# legacy 2026-09-01 (the surviving 21:46Z run)\n");
        var jsonBytes = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":\"news-typing-decomposition-v4\",\"runId\":\"35b57cfd-0000-0000-0000-000000000000\"}");
        var failedBytes = Encoding.UTF8.GetBytes("# News-typing pass FAILED — 2026-09-01\n");
        await File.WriteAllBytesAsync(legacyMd, mdBytes);
        await File.WriteAllBytesAsync(legacyJson, jsonBytes);
        await File.WriteAllBytesAsync(legacyFailed, failedBytes);

        var store = Store();
        await store.WriteDecompositionAsync(
            Instant2146Z,
            RunB,
            "# new run",
            Document(retryExhausted: 0) with { RunId = RunB },
            CancellationToken.None);
        await store.WriteFailedAsync(Instant2146Z, RunB, "reason", CancellationToken.None);

        Assert.Equal(mdBytes, await File.ReadAllBytesAsync(legacyMd));
        Assert.Equal(jsonBytes, await File.ReadAllBytesAsync(legacyJson));
        Assert.Equal(failedBytes, await File.ReadAllBytesAsync(legacyFailed));
        Assert.Equal(6, LiveFileNames().Length);
    }

    /// <summary>
    /// The absent-run-id fallback (unreachable today — typing runs only in unfiltered full mode, which always
    /// mints a run id): the instant-only pair is still written, ONE Warning names the missing id, nothing
    /// throws and no GUID is fabricated.
    /// </summary>
    [Fact]
    public async Task AbsentRunId_WritesTheInstantOnlyPair_AndWarnsExactlyOnce()
    {
        var logger = new CapturingLogger<FileNewsTypingArtifactStore>();

        await Store(logger).WriteDecompositionAsync(
            Instant0250Z, runId: null, "# md", Document(retryExhausted: 0), CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "attention-decomposition-20260901T025000Z.json",
                "attention-decomposition-20260901T025000Z.md",
            },
            LiveFileNames());
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("NO run id", warning.Message, StringComparison.Ordinal);
        Assert.Contains("attention-decomposition-20260901T025000Z", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Information
                && e.Message.StartsWith("Attention-decomposition artifact written", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbsentRunId_OnTheFailedPath_WritesTheInstantOnlyName_AndWarnsOnceAboutTheId()
    {
        var logger = new CapturingLogger<FileNewsTypingArtifactStore>();

        await Store(logger).WriteFailedAsync(Instant0250Z, runId: null, "boom", CancellationToken.None);

        Assert.Equal(new[] { "attention-decomposition-20260901T025000Z-FAILED.md" }, LiveFileNames());
        Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("NO run id", StringComparison.Ordinal));
        Assert.Contains(
            "(run id ABSENT)",
            await File.ReadAllTextAsync(LivePath("attention-decomposition-20260901T025000Z-FAILED.md")),
            StringComparison.Ordinal);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
