using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Evidence;
using Radar.Application.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Infrastructure.Tests.DependencyInjection;

/// <summary>
/// Composition side of spec 142's reconciliation choice: the repository IS the file store, exposed under
/// both interfaces from ONE singleton instance (one instance ⇒ one hydration cache).
/// </summary>
public sealed class DurableRadarSignalHistoryRegistrationTests : IDisposable
{
    private readonly string _tempDir;

    public DurableRadarSignalHistoryRegistrationTests()
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

    private ServiceProvider BuildDurableProvider() => new ServiceCollection()
        .AddLogging()
        .AddInMemoryRadarPersistence()
        .AddFileRawEvidenceStore(Path.Combine(_tempDir, "evidence-raw"))
        .AddFileSignalStore(Path.Combine(_tempDir, "signals"))
        .AddDurableRadarSignalHistory()
        .BuildServiceProvider();

    [Fact]
    public void DurableRepositories_AreTheSameInstanceAsTheFileStores()
    {
        using var sp = BuildDurableProvider();

        // ONE instance behind all four interfaces — anything else would mean two hydration caches, and a
        // write through one that a read through the other could not see.
        Assert.Same(sp.GetRequiredService<ISignalFileStore>(), sp.GetRequiredService<ISignalRepository>());
        Assert.Same(sp.GetRequiredService<IRawEvidenceStore>(), sp.GetRequiredService<IEvidenceRepository>());
        Assert.Same(sp.GetRequiredService<FileSignalStore>(), sp.GetRequiredService<ISignalRepository>());
        Assert.Same(sp.GetRequiredService<FileRawEvidenceStore>(), sp.GetRequiredService<IEvidenceRepository>());
    }

    [Fact]
    public void DurableRegistration_LeavesNoDanglingInMemoryRegistration()
    {
        var services = new ServiceCollection()
            .AddInMemoryRadarPersistence()
            .AddFileRawEvidenceStore(Path.Combine(_tempDir, "evidence-raw"))
            .AddFileSignalStore(Path.Combine(_tempDir, "signals"))
            .AddDurableRadarSignalHistory();

        // RemoveAll, not merely "last registration wins": a leftover in-memory descriptor would still be
        // resolved by an IEnumerable<T> injection and would silently instantiate a second, empty store.
        Assert.Single(services, d => d.ServiceType == typeof(ISignalRepository));
        Assert.Single(services, d => d.ServiceType == typeof(IEvidenceRepository));
        Assert.DoesNotContain(
            services, d => d.ImplementationType == typeof(InMemorySignalRepository));
        Assert.DoesNotContain(
            services, d => d.ImplementationType == typeof(InMemoryEvidenceRepository));

        // The other four in-memory repositories are untouched by this slice.
        Assert.Contains(services, d => d.ImplementationType == typeof(InMemoryCompanyRepository));
        Assert.Contains(services, d => d.ImplementationType == typeof(InMemoryScoreRepository));
    }

    [Fact]
    public void WithoutTheDurableCall_TheInMemoryRepositoriesStillWin()
    {
        // Every existing composition (tests, the integration harness) is unaffected: AddFileSignalStore /
        // AddFileRawEvidenceStore keep doing exactly what they did.
        using var sp = new ServiceCollection()
            .AddLogging()
            .AddInMemoryRadarPersistence()
            .AddFileRawEvidenceStore(Path.Combine(_tempDir, "evidence-raw"))
            .AddFileSignalStore(Path.Combine(_tempDir, "signals"))
            .BuildServiceProvider();

        Assert.IsType<InMemorySignalRepository>(sp.GetRequiredService<ISignalRepository>());
        Assert.IsType<InMemoryEvidenceRepository>(sp.GetRequiredService<IEvidenceRepository>());
        Assert.IsType<FileSignalStore>(sp.GetRequiredService<ISignalFileStore>());
        Assert.IsType<FileRawEvidenceStore>(sp.GetRequiredService<IRawEvidenceStore>());
    }
}
