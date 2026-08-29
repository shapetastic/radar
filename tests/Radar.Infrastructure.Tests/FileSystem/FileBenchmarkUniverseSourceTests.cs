using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Efficacy.Comparison;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// Spec 183 §1: the frozen benchmark-universe artifact reader — self-contained resolution (nothing but the
/// artifact is an input), content-hash integrity (a drifted pond refuses to load rather than silently
/// redefining "excess"), and the committed repo artifact itself parsing and verifying.
/// </summary>
public sealed class FileBenchmarkUniverseSourceTests : IDisposable
{
    private readonly string _tempDir;

    public FileBenchmarkUniverseSourceTests()
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

    private FileBenchmarkUniverseSource CreateSource(string fileName = FileBenchmarkUniverseSource.FileName) =>
        new(
            new FileBenchmarkUniverseSourceOptions { FilePath = Path.Combine(_tempDir, fileName) },
            NullLogger<FileBenchmarkUniverseSource>.Instance);

    private static readonly Guid MemberA = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid MemberB = new("11111111-0000-0000-0000-000000000002");

    private async Task WriteArtifactAsync(
        string? contentHash = null, string schemaVersion = FileBenchmarkUniverseSource.SupportedSchemaVersion)
    {
        var members = new List<BenchmarkUniverseMember>
        {
            new(MemberA, "AAA", "NASDAQ", "AAA"),
            new(MemberB, "BBB", "NYSE", "BBB"),
        };
        var hash = contentHash
            ?? BenchmarkUniverseContentHash.Compute("benchmark-universe-v1", members);
        var json = $$"""
            {
              "schemaVersion": "{{schemaVersion}}",
              "universeVersion": "benchmark-universe-v1",
              "frozenAtUtc": "2026-08-23T12:00:00Z",
              "sourceSeedHash": "abc123",
              "contentHash": "{{hash}}",
              "members": [
                { "companyId": "{{MemberA:D}}", "ticker": "AAA", "exchange": "NASDAQ", "priceSeriesKey": "AAA" },
                { "companyId": "{{MemberB:D}}", "ticker": "BBB", "exchange": "NYSE", "priceSeriesKey": "BBB" }
              ]
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, FileBenchmarkUniverseSource.FileName), json);
    }

    [Fact]
    public async Task ReadAsync_ParsesAValidArtifact_AndVerifiesItsContentHash()
    {
        await WriteArtifactAsync();

        var universe = await CreateSource().ReadAsync(CancellationToken.None);

        Assert.NotNull(universe);
        Assert.Equal("benchmark-universe-v1", universe!.UniverseVersion);
        Assert.Equal(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero), universe.FrozenAtUtc);
        Assert.Equal("abc123", universe.SourceSeedHash);
        Assert.Equal(2, universe.Members.Count);
        Assert.Equal(MemberA, universe.Members[0].CompanyId);
        Assert.Equal("AAA", universe.Members[0].PriceSeriesKey);
        Assert.Equal(
            BenchmarkUniverseContentHash.Compute(universe.UniverseVersion, universe.Members),
            universe.ContentHash);
    }

    [Fact]
    public async Task ReadAsync_MissingArtifact_ReturnsNullRatherThanThrowing()
    {
        Assert.Null(await CreateSource().ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_TamperedMembers_FailTheIntegrityCheck()
    {
        // The committed hash belongs to a DIFFERENT member set: the pond has drifted from its freeze, and
        // benchmarking against it would silently redefine every published excess number.
        await WriteArtifactAsync(contentHash: BenchmarkUniverseContentHash.Compute(
            "benchmark-universe-v1", [new BenchmarkUniverseMember(MemberA, "AAA", "NASDAQ", "AAA")]));

        Assert.Null(await CreateSource().ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_UnknownSchemaVersion_ReturnsNull()
    {
        await WriteArtifactAsync(schemaVersion: "benchmark-universe-schema-v9");

        Assert.Null(await CreateSource().ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_ReturnsNull()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, FileBenchmarkUniverseSource.FileName), "{ not json");

        Assert.Null(await CreateSource().ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_ReadsOnlyItsOwnArtifact_ASeedFileEditChangesNothing()
    {
        // Spec 183 §1: ticker/price-series resolution reads ONLY the frozen artifact. A companies.json
        // beside it — with a conflicting ticker for the same id — is not an input, so editing it between
        // reads changes not one field.
        await WriteArtifactAsync();
        var seedPath = Path.Combine(_tempDir, "companies.json");
        await File.WriteAllTextAsync(seedPath, """{ "companies": [ { "id": "x", "ticker": "AAA" } ] }""");

        var before = await CreateSource().ReadAsync(CancellationToken.None);

        await File.WriteAllTextAsync(
            seedPath, """{ "companies": [ { "id": "x", "ticker": "RENAMED" }, { "id": "y", "ticker": "NEW" } ] }""");

        var after = await CreateSource().ReadAsync(CancellationToken.None);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before!.ContentHash, after!.ContentHash);
        Assert.Equal(
            before.Members.Select(m => (m.CompanyId, m.Ticker, m.Exchange, m.PriceSeriesKey)),
            after.Members.Select(m => (m.CompanyId, m.Ticker, m.Exchange, m.PriceSeriesKey)));
    }

    [Fact]
    public async Task TheCommittedRepoArtifact_ParsesVerifiesAndCarriesTheFrozenSeventyFourMembers()
    {
        // The artifact this spec froze is a COMMITTED file; its integrity is part of the deliverable. Walk
        // up to the repo root (the folder holding Radar.sln) exactly as the source-scan guardrails do.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var artifactPath = Path.Combine(
            dir!.FullName, "data", "efficacy", FileBenchmarkUniverseSource.FileName);
        Assert.True(File.Exists(artifactPath), $"Expected the committed artifact at {artifactPath}.");

        var source = new FileBenchmarkUniverseSource(
            new FileBenchmarkUniverseSourceOptions { FilePath = artifactPath },
            NullLogger<FileBenchmarkUniverseSource>.Instance);

        var universe = await source.ReadAsync(CancellationToken.None);

        Assert.NotNull(universe);
        Assert.Equal("benchmark-universe-v1", universe!.UniverseVersion);
        Assert.Equal(74, universe.Members.Count);
        Assert.Equal(universe.Members.Count, universe.Members.Select(m => m.CompanyId).Distinct().Count());
        Assert.All(universe.Members, m => Assert.False(string.IsNullOrWhiteSpace(m.PriceSeriesKey)));

        // Frozen before the AD-15/AD-16 claim boundary (2026-09-29) while eligible paired support is zero.
        Assert.True(universe.FrozenAtUtc < new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// Spec 183's rule, exercised by spec 199's seed expansion (74 -> 94): expansion of the watch universe is
    /// a PROSPECTIVE <c>benchmark-universe-v2</c>, never an edit to v1. So none of the 20 companies spec 199
    /// added may appear in the frozen artifact — a new company must resolve as <c>NotInBenchmarkUniverse</c>
    /// on the pooled path rather than being silently admitted, which would retroactively insert a
    /// later-selected member into every historical excess number (mutable-universe leakage). This asserts the
    /// artifact stayed at its 74 frozen members and that no v2 was created by that slice.
    /// </summary>
    [Fact]
    public async Task TheCommittedRepoArtifact_ExcludesTheSpec199SeedAdditions()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var efficacyDir = Path.Combine(dir!.FullName, "data", "efficacy");
        var artifactPath = Path.Combine(efficacyDir, FileBenchmarkUniverseSource.FileName);
        Assert.True(File.Exists(artifactPath), $"Expected the committed artifact at {artifactPath}.");

        // No prospective v2 was declared by spec 199; the pooled path must keep benchmarking against v1.
        Assert.False(
            File.Exists(Path.Combine(efficacyDir, "benchmark-universe-v2.json")),
            "Spec 199 must not create benchmark-universe-v2; expansion is a prospective, separate decision.");

        var source = new FileBenchmarkUniverseSource(
            new FileBenchmarkUniverseSourceOptions { FilePath = artifactPath },
            NullLogger<FileBenchmarkUniverseSource>.Instance);

        var universe = await source.ReadAsync(CancellationToken.None);

        Assert.NotNull(universe);
        Assert.Equal(74, universe!.Members.Count);

        var members = universe.Members.ToDictionary(m => m.CompanyId);
        foreach (var (ticker, id) in Spec199SeedAdditionIds)
        {
            Assert.False(
                members.ContainsKey(id),
                $"{ticker} ({id:D}) was added to the seed by spec 199 and must NOT be a v1 benchmark member.");
        }

        // The tickers must be absent too — a member re-pointed at a new company would keep an old id.
        var frozenTickers = new HashSet<string>(
            universe.Members.Select(m => m.Ticker), StringComparer.OrdinalIgnoreCase);
        foreach (var ticker in Spec199SeedAdditionIds.Keys)
        {
            Assert.DoesNotContain(ticker, frozenTickers);
        }
    }

    /// <summary>
    /// The 20 company ids spec 199 appended to <c>data/companies.json</c>, pinned by ticker. Kept here rather
    /// than read from the seed on purpose: the point of the assertion above is that the frozen artifact is
    /// independent of the mutable seed, so reading the seed to build the expectation would weaken it into a
    /// tautology.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Guid> Spec199SeedAdditionIds =
        new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["GHM"] = new("03d83435-a25a-46d9-8c0b-ccd6ae2f6370"),
            ["CLMB"] = new("3ec8fa0f-0be7-425b-9d18-d3f2cf9c7cce"),
            ["UTMD"] = new("28243c9e-eb18-4a85-acec-8f93aeb8cdef"),
            ["MLAB"] = new("6546421b-76ce-40cf-ad5a-67850e8a6a14"),
            ["JOUT"] = new("345739cf-7b3b-4a5e-9282-9a08d00dbf95"),
            ["FLXS"] = new("977ab7ef-84c4-4415-914e-225445f5bf77"),
            ["ITIC"] = new("2ae6e6da-b714-416f-9d90-b6432f6eac2b"),
            ["ESQ"] = new("971ea074-e524-4d6d-baf2-ead26449a0dc"),
            ["SGA"] = new("b9114ba4-4a9e-444a-b295-e5ee4d6e75c3"),
            ["OOMA"] = new("6abb319e-7404-4771-a7e4-1392afcdd106"),
            ["JBSS"] = new("9033c658-5451-4eb6-a75d-6ee934e0d0ae"),
            ["SENEA"] = new("28a12288-2bef-4e98-9c20-2243eb8c7a3a"),
            ["NWPX"] = new("5a2b18f8-a612-4d50-9987-4aa182205a5f"),
            ["KOP"] = new("0ca2bef7-aebf-4e2b-acdc-d5383c5e9acf"),
            ["GEOS"] = new("77b0bd01-7b66-4b56-b192-954476183ec6"),
            ["EPM"] = new("a86391fc-476a-41ef-8fae-b259b052eec9"),
            ["CTO"] = new("2f6db469-4729-4c7b-af2c-89ec30b7285b"),
            ["OLP"] = new("de02b7db-8c25-4252-96a1-64093e3a5e3a"),
            ["UTL"] = new("db2f28fc-75f0-42c7-9480-04dd4b5e4326"),
            ["RGCO"] = new("1f2c8b48-1daa-41fb-bd08-1cbdc30dcb3b"),
        };
}
