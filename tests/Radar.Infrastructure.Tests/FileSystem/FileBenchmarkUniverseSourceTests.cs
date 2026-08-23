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
}
