using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// Spec 169: the cohort store fails LOUD, deliberately. AD-16's exclusion is binding, so an empty result from
/// a missing or malformed declaration would let the primary screen quietly include companies an accepted
/// amendment excludes — while the artifact looked entirely normal.
/// </summary>
public sealed class FileExcludedCohortStoreTests : IDisposable
{
    private readonly string _tempDir;

    public FileExcludedCohortStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private FileExcludedCohortStore CreateStore(string? root = null) =>
        new(
            new FileExcludedCohortStoreOptions { RootDirectory = root ?? _tempDir },
            NullLogger<FileExcludedCohortStore>.Instance);

    private async Task WriteAsync(string fileName, string json) =>
        await File.WriteAllTextAsync(Path.Combine(_tempDir, fileName), json);

    [Fact]
    public async Task LoadAsync_ReadsEveryExcludedCohortMemberInDeterministicOrder()
    {
        await WriteAsync("event-enriched-2026-07.json", """
            {
              "cohort": "event-enriched-2026-07",
              "excludeFromPrimaryScreen": true,
              "companies": [
                { "ticker": "THRM", "cik": "0000903129" },
                { "ticker": "BKE", "cik": "0000885245" }
              ]
            }
            """);

        var loaded = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.True(loaded.IsAvailable);
        Assert.Null(loaded.UnavailableDetail);
        Assert.Equal(["BKE", "THRM"], loaded.Members.Select(m => m.Ticker));
        Assert.All(loaded.Members, m => Assert.Equal("event-enriched-2026-07", m.Cohort));
        Assert.Equal("0000885245", loaded.Members[0].Cik);
    }

    [Fact]
    public async Task LoadAsync_ADirectoryDeclaringNoExclusionCohortAtAll_IsUnavailable()
    {
        // THE fail-closed case. A renamed, deleted or flag-stripped declaration looks exactly like this, and
        // returning "available, zero members" would let the primary screen run over the full universe —
        // including the eight event-enriched companies it exists to keep out — and emit a completely
        // normal-looking artifact with a real ScreenStatus. While AD-16's 2026-07-31 amendment stands,
        // membership is append-only and an empty exclusion set is not a legitimate state.
        await WriteAsync("not-an-exclusion.json", """
            { "cohort": "informational", "companies": [ { "ticker": "MRCY", "cik": "1" } ] }
            """);

        var loaded = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.False(loaded.IsAvailable);
        Assert.Contains("excludeFromPrimaryScreen", loaded.UnavailableDetail);
        Assert.Contains(_tempDir, loaded.UnavailableDetail);
        Assert.Empty(loaded.Members);
    }

    [Fact]
    public async Task LoadAsync_AnEmptyCohortsDirectory_IsUnavailable()
    {
        var loaded = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.False(loaded.IsAvailable);
        Assert.Contains("excludeFromPrimaryScreen", loaded.UnavailableDetail);
    }

    [Fact]
    public async Task LoadAsync_ANonExclusionCohortAlongsideARealOne_IsStillAvailable()
    {
        // The other half of the rule: a file that simply is not an exclusion cohort remains a legitimate
        // neighbour. Only the ABSENCE of any declaration is a failure.
        await WriteAsync("informational.json", """
            { "cohort": "informational", "companies": [ { "ticker": "MRCY", "cik": "1" } ] }
            """);
        await WriteAsync("event-enriched-2026-07.json", """
            {
              "cohort": "event-enriched-2026-07",
              "excludeFromPrimaryScreen": true,
              "companies": [ { "ticker": "BKE", "cik": "0000885245" } ]
            }
            """);

        var loaded = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.True(loaded.IsAvailable);
        Assert.Equal("BKE", Assert.Single(loaded.Members).Ticker);
    }

    [Fact]
    public async Task LoadAsync_ADeclaredCohortWithAnEmptyCompanyList_IsAvailableWithNoMembers()
    {
        // A DECLARATION that lists nobody is a deliberate, committed statement — quite different from no
        // declaration at all. The store reports it faithfully rather than refusing.
        await WriteAsync("declared-but-empty.json", """
            { "cohort": "declared-but-empty", "excludeFromPrimaryScreen": true, "companies": [] }
            """);

        var loaded = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.True(loaded.IsAvailable);
        Assert.Empty(loaded.Members);
    }

    [Fact]
    public async Task LoadAsync_MissingDirectory_IsUnavailable_NeverAnEmptyCohort()
    {
        var loaded = await CreateStore(Path.Combine(_tempDir, "nope")).LoadAsync(CancellationToken.None);

        Assert.False(loaded.IsAvailable);
        Assert.Contains("does not exist", loaded.UnavailableDetail);
        Assert.Empty(loaded.Members);
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_IsUnavailable()
    {
        await WriteAsync("broken.json", "{ not json");

        var loaded = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.False(loaded.IsAvailable);
        Assert.Contains("malformed JSON", loaded.UnavailableDetail);
    }

    [Fact]
    public async Task LoadAsync_MemberWithoutATicker_IsUnavailable_RatherThanPartiallyApplied()
    {
        await WriteAsync("partial.json", """
            {
              "cohort": "partial",
              "excludeFromPrimaryScreen": true,
              "companies": [ { "ticker": "BKE", "cik": "1" }, { "cik": "2" } ]
            }
            """);

        var loaded = await CreateStore().LoadAsync(CancellationToken.None);

        // "Exclude this company" with no company named cannot be honoured, and honouring the OTHER half of
        // the declaration would silently apply a different exclusion than the one that was committed.
        Assert.False(loaded.IsAvailable);
        Assert.Contains("no ticker", loaded.UnavailableDetail);
    }

    [Fact]
    public async Task LoadAsync_ExclusionCohortWithoutACompaniesArray_IsUnavailable()
    {
        await WriteAsync("no-companies.json", """
            { "cohort": "x", "excludeFromPrimaryScreen": true }
            """);

        var loaded = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.False(loaded.IsAvailable);
        Assert.Contains("no 'companies' array", loaded.UnavailableDetail);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheCommittedRepositoryCohortFile()
    {
        // The real committed declaration must actually parse through this store — the evaluator reads the
        // file, never git history (AD-16, 2026-07-31), so a shape drift here would silently suppress the
        // primary screen in production.
        var repoCohorts = LocateRepositoryCohortsDirectory();
        var loaded = await CreateStore(repoCohorts).LoadAsync(CancellationToken.None);

        Assert.True(loaded.IsAvailable);
        Assert.Equal(8, loaded.Members.Count);
        Assert.Contains(loaded.Members, m => m.Ticker == "PSTL" && m.Cik == "0001759774");
        Assert.All(loaded.Members, m => Assert.Equal("event-enriched-2026-07", m.Cohort));
    }

    private static string LocateRepositoryCohortsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "cohorts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate docs/cohorts from the test output directory.");
    }
}
