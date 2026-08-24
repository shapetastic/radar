using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.NewsTyping;

namespace Radar.Infrastructure.Tests.NewsTyping;

/// <summary>
/// Spec 186 §2: the decomposition artifact gains the ADDITIVE per-cohort <c>RetryExhausted</c> count and its
/// schema tag is bumped to <c>news-typing-decomposition-v2</c>. Readers are BY NAME, so a consumer written
/// against v1 reads a v2 document unchanged — asserted here against the production writer rather than
/// asserted in prose.
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

    private static readonly JsonSerializerOptions ReaderOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    private static NewsTypingDecompositionDocument Document(int retryExhausted) => new(
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
                        RetryExhausted: retryExhausted),
                ]),
        ],
        ObservationsWithoutCompany: 0,
        GeneratedAtUtc: new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));

    private async Task<string> WriteAndReadJsonAsync(NewsTypingDecompositionDocument document)
    {
        var store = new FileNewsTypingArtifactStore(
            new FileNewsTypingArtifactStoreOptions { RootDirectory = _root },
            NullLogger<FileNewsTypingArtifactStore>.Instance);
        await store.WriteDecompositionAsync("2026-08-23", "# md", document, CancellationToken.None);
        return await File.ReadAllTextAsync(
            Path.Combine(_root, "live", "attention-decomposition-2026-08-23.json"));
    }

    [Fact]
    public void SchemaTag_IsTheNamedV2Bump()
    {
        Assert.Equal(
            "news-typing-decomposition-v2", NewsTypingDecompositionDocument.CurrentSchemaVersion);
    }

    [Fact]
    public async Task RetryExhausted_IsWrittenByName_AndAV1ReaderIsUnaffected()
    {
        var json = await WriteAndReadJsonAsync(Document(retryExhausted: 1));

        Assert.Contains("\"retryExhausted\": 1", json, StringComparison.Ordinal);

        // The by-NAME v1 reader still binds every field it knows and simply ignores the additive one.
        var v1 = JsonSerializer.Deserialize<V1Document>(json, ReaderOptions);
        Assert.NotNull(v1);
        Assert.Equal("news-typing-decomposition-v2", v1.SchemaVersion);
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
}
