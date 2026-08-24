using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.NewsTyping;

namespace Radar.Infrastructure.Tests.NewsTyping;

/// <summary>
/// Spec 181 §4: the durable typing store — insert-only writes, lossless round-trip through a FRESH instance
/// (hydration), completed-only cache reads (a failure is persisted but never reused), a malformed file
/// logged and skipped, and the cohort policy segment as LAYOUT while the CohortKey field stays identity.
/// </summary>
public sealed class FileNewsTypingStoreTests : IDisposable
{
    private static readonly Guid ObservationId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunA = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid RunB = new("dddddddd-0000-0000-0000-000000000002");

    private const string CohortKey =
        "openai:test-model|news-typing-prompt-v1|news-typing-schema-v1|news-event-taxonomy-v1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "radar-newstyping-tests-" + Guid.NewGuid().ToString("N"));

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

    private FileNewsTypingStore NewStore() => new(
        new FileNewsTypingStoreOptions { RootDirectory = _root },
        NullLogger<FileNewsTypingStore>.Instance);

    private static NewsTypingRecord Record(
        Guid? runId = null,
        NewsTypingStatus status = NewsTypingStatus.Typed,
        string payloadHash = "ph-1")
    {
        var run = runId ?? RunA;
        return new NewsTypingRecord(
            SchemaVersion: NewsTypingRecord.CurrentSchemaVersion,
            TypingId: NewsTypingRecord.IdentityFor(CohortKey, ObservationId, payloadHash, run),
            RunId: run,
            ObservationId: ObservationId,
            PayloadHash: payloadHash,
            CompanyId: Guid.NewGuid(),
            Ticker: "TST",
            CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
            ReaderName: "reader",
            Provider: "openai",
            ModelId: "test-model",
            PromptVersion: NewsTypingContract.PromptVersion,
            ResultSchemaVersion: NewsTypingContract.SchemaVersion,
            TaxonomyVersion: NewsTypingContract.TaxonomyVersion,
            TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
            CohortKey: CohortKey,
            Relevance: status == NewsTypingStatus.Typed ? NewsTypingRelevance.CompanySpecific : null,
            DerivedPrimaryType: status == NewsTypingStatus.Typed ? NewsEventType.RegulatoryOrLegal : null,
            Facts: status == NewsTypingStatus.Typed
                ?
                [
                    new NewsTypingValidatedFact(
                        FactId: NewsTypingClaimValidator.FactIdFor(CohortKey, ObservationId, payloadHash, 0),
                        EventTypes: [NewsEventType.RegulatoryOrLegal],
                        Statement: "Company faces legal scrutiny",
                        TemporalScope: null,
                        Attribution: NewsFactAttribution.Publisher,
                        AssertionStatus: NewsFactAssertionStatus.Reported,
                        Confidence: 0.8,
                        Citations: ["faces legal scrutiny"]),
                ]
                : [],
            FactsTotal: status == NewsTypingStatus.Typed ? 1 : 0,
            FactsAccepted: status == NewsTypingStatus.Typed ? 1 : 0,
            FactsDropped: 0,
            FactDropReasons: [],
            Status: status,
            RawResponseHash: "raw-hash",
            FailureDetail: status == NewsTypingStatus.ProviderFailure ? "boom" : null,
            Limits: new NewsTypingLimitsRecord(200, 30, 3, 25),
            ReusedFromTypingId: null,
            CreatedAtUtc: new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task RoundTrip_ThroughAFreshInstance_IsLossless()
    {
        var record = Record();
        Assert.True(await NewStore().WriteAsync(record, CancellationToken.None));

        var hydrated = Assert.Single(await NewStore().GetAllAsync(CancellationToken.None));

        Assert.Equal(record.TypingId, hydrated.TypingId);
        Assert.Equal(record.CohortKey, hydrated.CohortKey);
        Assert.Equal(record.TaxonomyHash, hydrated.TaxonomyHash);
        Assert.Equal(record.Status, hydrated.Status);
        Assert.Equal(record.Relevance, hydrated.Relevance);
        Assert.Equal(record.DerivedPrimaryType, hydrated.DerivedPrimaryType);
        var fact = Assert.Single(hydrated.Facts);
        Assert.Equal(record.Facts[0].FactId, fact.FactId);
        Assert.Equal(record.Facts[0].EventTypes, fact.EventTypes);
        Assert.Equal(record.Facts[0].Attribution, fact.Attribution);
        Assert.Equal(record.Facts[0].AssertionStatus, fact.AssertionStatus);
        Assert.Equal(record.Facts[0].Citations, fact.Citations);
    }

    [Fact]
    public async Task SameIdentity_IsInsertOnly_NeverOverwritten()
    {
        var store = NewStore();
        var original = Record();
        Assert.True(await store.WriteAsync(original, CancellationToken.None));
        Assert.True(await store.WriteAsync(
            original with { FailureDetail = "mutated" }, CancellationToken.None));

        var hydrated = Assert.Single(await NewStore().GetAllAsync(CancellationToken.None));
        Assert.Null(hydrated.FailureDetail);
    }

    [Fact]
    public async Task FindCompleted_ReturnsCompletedTypings_ButNeverFailures()
    {
        var store = NewStore();
        await store.WriteAsync(
            Record(RunA, NewsTypingStatus.ProviderFailure), CancellationToken.None);

        Assert.Null(await store.FindCompletedAsync(
            CohortKey, ObservationId, "ph-1", CancellationToken.None));

        await store.WriteAsync(Record(RunB), CancellationToken.None);

        var found = await store.FindCompletedAsync(
            CohortKey, ObservationId, "ph-1", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(NewsTypingStatus.Typed, found.Status);
    }

    [Fact]
    public async Task ValidationFailed_IsNotCompleted_SoItIsNeverReusedFromTheCache()
    {
        var store = NewStore();
        await store.WriteAsync(
            Record(RunA, NewsTypingStatus.ValidationFailed), CancellationToken.None);

        Assert.Null(await store.FindCompletedAsync(
            CohortKey, ObservationId, "ph-1", CancellationToken.None));
    }

    [Fact]
    public async Task InsufficientContent_IsCompleted_AndReusable()
    {
        var store = NewStore();
        await store.WriteAsync(
            Record(RunA, NewsTypingStatus.InsufficientContent), CancellationToken.None);

        var found = await store.FindCompletedAsync(
            CohortKey, ObservationId, "ph-1", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(NewsTypingStatus.InsufficientContent, found.Status);
    }

    [Fact]
    public async Task ProviderDuration_RoundTrips_AndALegacyFileHydratesAsNoCallRecorded()
    {
        // Spec 187 §7: the duration is observational provenance, persisted trailing + nullable. It has to
        // survive the round trip (otherwise the record would silently misreport a measured call) and a
        // pre-187 file has to hydrate as `null` — "not recorded", never a fabricated 0 ms.
        Assert.True(await NewStore().WriteAsync(
            Record() with { ProviderDurationMs = 1234.5 }, CancellationToken.None));

        var hydrated = Assert.Single(await NewStore().GetAllAsync(CancellationToken.None));
        Assert.Equal(1234.5, hydrated.ProviderDurationMs);

        var file = Assert.Single(Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        Assert.True(document.Remove("providerDurationMs"));
        await File.WriteAllTextAsync(file, document.ToJsonString());

        var legacy = Assert.Single(await NewStore().GetAllAsync(CancellationToken.None));
        Assert.Null(legacy.ProviderDurationMs);
        // Nothing else about a pre-187 file changes meaning, and the schema tag is deliberately unmoved.
        Assert.Equal(NewsTypingRecord.CurrentSchemaVersion, legacy.SchemaVersion);
        Assert.Equal("news-typing-v1", legacy.SchemaVersion);
    }

    [Fact]
    public async Task LegacyRecord_WithoutTheSpec186Limits_HydratesAsNotRecorded_NeverAsAFabricatedLimit()
    {
        // Write a real record, then strip the two spec-186 limit fields back out of the file on disk: that
        // IS a pre-186 file, produced by the same serializer that wrote every accrued record.
        Assert.True(await NewStore().WriteAsync(Record(), CancellationToken.None));
        var file = Assert.Single(Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        var limits = document["limits"]!.AsObject();
        Assert.True(limits.Remove("maxTypingAttempts"));
        Assert.True(limits.Remove("maxRetryTypingsPerRun"));
        var legacy = document.ToJsonString();
        Assert.DoesNotContain("maxTypingAttempts", legacy, StringComparison.Ordinal);
        await File.WriteAllTextAsync(file, legacy);

        var hydrated = Assert.Single(await NewStore().GetAllAsync(CancellationToken.None));

        Assert.Null(hydrated.Limits.MaxTypingAttempts);
        Assert.Null(hydrated.Limits.MaxRetryTypingsPerRun);
        // The pre-186 fields are untouched — the additive pair is trailing and nullable, so nothing else
        // about a legacy file changes meaning.
        Assert.Equal(200, hydrated.Limits.MaxNewTypingsPerRun);
        Assert.Equal(30, hydrated.Limits.LookbackDays);
    }

    [Fact]
    public async Task StandaloneAttempts_MintDistinctIdentities_WhileTheFirstKeepsItsLegacyId()
    {
        // Spec 186 §2 rule (b): attempt 1 keeps the ORIGINAL "standalone" identity (every id already on
        // disk is byte-unchanged), while a later standalone attempt gets its own — so the insert-only
        // store records each real hosted call instead of silently deduplicating it away.
        var first = NewsTypingRecord.IdentityFor(CohortKey, ObservationId, "ph-1", null);
        Assert.Equal(first, NewsTypingRecord.IdentityFor(CohortKey, ObservationId, "ph-1", null, 1));
        var second = NewsTypingRecord.IdentityFor(CohortKey, ObservationId, "ph-1", null, 2);
        Assert.NotEqual(first, second);

        var store = NewStore();
        Assert.True(await store.WriteAsync(
            Record(status: NewsTypingStatus.ProviderFailure) with { TypingId = first, RunId = null },
            CancellationToken.None));
        Assert.True(await store.WriteAsync(
            Record(status: NewsTypingStatus.ProviderFailure) with { TypingId = second, RunId = null },
            CancellationToken.None));

        Assert.Equal(2, (await NewStore().GetAllAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task RunScopedIdentity_IsUnchangedByTheAttemptToken_SoRerunningOneRunStaysIdempotent()
    {
        var legacy = NewsTypingRecord.IdentityFor(CohortKey, ObservationId, "ph-1", RunA);

        // Whatever attempt ordinal the pass derives, a RUN-scoped attempt keeps today's id — re-running one
        // run is idempotent by id, and no accrued run-scoped record is re-minted.
        Assert.Equal(legacy, NewsTypingRecord.IdentityFor(CohortKey, ObservationId, "ph-1", RunA, 1));
        Assert.Equal(legacy, NewsTypingRecord.IdentityFor(CohortKey, ObservationId, "ph-1", RunA, 4));

        var store = NewStore();
        Assert.True(await store.WriteAsync(Record(RunA), CancellationToken.None));
        Assert.True(await store.WriteAsync(Record(RunA), CancellationToken.None));
        Assert.Single(await NewStore().GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MalformedFile_IsSkipped_NeverThrown()
    {
        Assert.True(await NewStore().WriteAsync(Record(), CancellationToken.None));
        var strayDir = Path.Combine(_root, "typings", "openai-test-model", "2026", "08");
        await File.WriteAllTextAsync(Path.Combine(strayDir, "not-json.json"), "{ this is not json");

        var hydrated = await NewStore().GetAllAsync(CancellationToken.None);

        Assert.Single(hydrated);
    }

    [Fact]
    public async Task Files_LandUnderTheCohortPolicySegment()
    {
        await NewStore().WriteAsync(Record(), CancellationToken.None);

        var segmentDir = Path.Combine(_root, "typings", "openai-test-model");
        Assert.True(Directory.Exists(segmentDir));
        Assert.Single(Directory.EnumerateFiles(segmentDir, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void PolicySegment_IsFilesystemSafe_AndLayoutOnly()
    {
        Assert.Equal(
            "openai-deepseek-ai-deepseek-v4-flash",
            NewsTypingCohortPath.PolicySegment("openai", "deepseek-ai/DeepSeek-V4-Flash"));
        Assert.Equal("ollama-llama3.1", NewsTypingCohortPath.PolicySegment("ollama", "llama3.1"));
    }
}
