using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Scoring;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileScoringConfigStoreTests : IDisposable
{
    private const string EngineVersion = "mvp-engine-v1";
    private const string FormulaVersion = "radar-formula-v5";
    private const string AttentionDescriptor = "attn:v1;unknown=0.4";
    private const string SignalSourceDescriptor = "rules=radar-keyword-rules-v3;collectors=sec-edgar;";
    private const string InsiderMaterialityDescriptor = "buy=5000000:8;sell=5000000:8;cluster=1;";
    private const string MediaCollapseDescriptor = "media-collapse-v1;window=3;";

    private readonly string _tempDir;

    public FileScoringConfigStoreTests()
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
            // Best-effort cleanup; ignore transient filesystem locks and permission errors.
        }
    }

    private FileScoringConfigStore CreateStore(string? rootDirectory = null) =>
        new(
            new FileScoringConfigStoreOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FileScoringConfigStore>.Instance);

    /// <summary>
    /// Builds an <see cref="EffectiveScoringConfig"/> whose Fingerprint is the ACTUAL spec-89 fingerprint of
    /// its own inputs, so the store is content-addressed correctly (filename == recomputed hash).
    /// </summary>
    private static EffectiveScoringConfig ConfigFor(ScoringWeights weights) =>
        new(
            Fingerprint: ScoringConfigFingerprint.Compute(
                EngineVersion, FormulaVersion, weights, AttentionDescriptor, SignalSourceDescriptor,
                InsiderMaterialityDescriptor, MediaCollapseDescriptor),
            EngineVersion: EngineVersion,
            FormulaVersion: FormulaVersion,
            Weights: weights,
            AttentionDescriptor: AttentionDescriptor,
            SignalSourceDescriptor: SignalSourceDescriptor,
            InsiderMaterialityDescriptor: InsiderMaterialityDescriptor,
            MediaCollapseDescriptor: MediaCollapseDescriptor);

    private static EffectiveScoringConfig ReadStored(string path)
    {
        var text = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<EffectiveScoringConfig>(text, RadarFileStoreJson.Options);
        Assert.NotNull(config);
        return config!;
    }

    [Fact]
    public async Task WriteIfNewAsync_CreatesContentAddressedFile_ThatRoundTrips()
    {
        var config = ConfigFor(new ScoringWeights());
        var store = CreateStore();

        var path = await store.WriteIfNewAsync(config, CancellationToken.None);

        Assert.Equal(Path.Combine(_tempDir, config.Fingerprint + ".json"), path);
        Assert.True(File.Exists(path), $"Expected file at {path}.");

        var stored = ReadStored(path);
        Assert.Equal(config.Fingerprint, stored.Fingerprint);
        Assert.Equal(config.EngineVersion, stored.EngineVersion);
        Assert.Equal(config.FormulaVersion, stored.FormulaVersion);
        Assert.Equal(config.AttentionDescriptor, stored.AttentionDescriptor);
        Assert.Equal(config.SignalSourceDescriptor, stored.SignalSourceDescriptor);
        Assert.Equal(config.InsiderMaterialityDescriptor, stored.InsiderMaterialityDescriptor);
        Assert.Equal(config.MediaCollapseDescriptor, stored.MediaCollapseDescriptor);
        // Every ScoringWeights value round-trips (record equality compares all init properties).
        Assert.Equal(config.Weights, stored.Weights);
    }

    [Fact]
    public async Task WriteIfNewAsync_IsInsertIfNew_DoesNotOverwriteExistingFile()
    {
        var config = ConfigFor(new ScoringWeights());
        var store = CreateStore();

        var path = await store.WriteIfNewAsync(config, CancellationToken.None);

        // Tamper with the file on disk. Insert-if-new must NOT rewrite it — the tampered bytes survive,
        // proving the second call truly skipped (mirrors AD-1 evidence immutability).
        const string tampered = "TAMPERED-NOT-JSON";
        await File.WriteAllTextAsync(path, tampered);

        var secondPath = await store.WriteIfNewAsync(config, CancellationToken.None);

        Assert.Equal(path, secondPath);
        Assert.Equal(tampered, await File.ReadAllTextAsync(path));

        // Exactly one file exists for that fingerprint.
        var files = Directory.GetFiles(_tempDir, config.Fingerprint + ".json");
        Assert.Single(files);
    }

    [Fact]
    public async Task StoredConfig_RecomputedFingerprint_EqualsFilenameAndStoredField()
    {
        var config = ConfigFor(new ScoringWeights());
        var store = CreateStore();

        var path = await store.WriteIfNewAsync(config, CancellationToken.None);

        var stored = ReadStored(path);

        // Self-verification: the hash is no longer opaque — recomputing it from the DESERIALIZED config
        // equals both the filename (sans .json) and the stored Fingerprint field.
        var recomputed = ScoringConfigFingerprint.Compute(
            stored.EngineVersion, stored.FormulaVersion, stored.Weights, stored.AttentionDescriptor,
            stored.SignalSourceDescriptor, stored.InsiderMaterialityDescriptor, stored.MediaCollapseDescriptor);

        Assert.Equal(Path.GetFileNameWithoutExtension(path), recomputed);
        Assert.Equal(stored.Fingerprint, recomputed);
    }

    [Fact]
    public async Task CustomProfile_ProducesDistinctFile_WithCustomValuesRecoverable()
    {
        var store = CreateStore();

        var defaultConfig = ConfigFor(new ScoringWeights());
        var customConfig = ConfigFor(new ScoringWeights { AttentionHalfSaturation = 12.0 });

        // Distinct content -> distinct fingerprint -> distinct filename (content-addressed).
        Assert.NotEqual(defaultConfig.Fingerprint, customConfig.Fingerprint);

        var defaultPath = await store.WriteIfNewAsync(defaultConfig, CancellationToken.None);
        var customPath = await store.WriteIfNewAsync(customConfig, CancellationToken.None);

        Assert.NotEqual(defaultPath, customPath);
        Assert.True(File.Exists(defaultPath));
        Assert.True(File.Exists(customPath));

        // The custom weights are recoverable from disk (the whole point of the store).
        var storedCustom = ReadStored(customPath);
        Assert.Equal(12.0, storedCustom.Weights.AttentionHalfSaturation);

        // Both files coexist under the root.
        var files = Directory.GetFiles(_tempDir, "*.json");
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public async Task WriteIfNewAsync_IoFailure_ReturnsAttemptedPathWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException; the store
        // degrades gracefully (log + continue) and returns the attempted path — the run keeps going and
        // the snapshot still carries its fingerprint.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var config = ConfigFor(new ScoringWeights());
        var store = CreateStore(rootAsFile);

        var path = await store.WriteIfNewAsync(config, CancellationToken.None);

        Assert.Equal(Path.Combine(rootAsFile, config.Fingerprint + ".json"), path);
    }

    [Fact]
    public async Task WriteIfNewAsync_SerializationFailure_ReturnsAttemptedPathWithoutThrowingOrWriting()
    {
        // A non-finite weight (NaN) cannot be serialized under the store's JSON options (named floating-point
        // literals are not enabled), so JsonSerializer.Serialize throws. The store must degrade like a disk
        // failure — log + return the attempted path — so the run keeps going and the snapshot still carries
        // its fingerprint; no file is written.
        var config = ConfigFor(new ScoringWeights { AttentionHalfSaturation = double.NaN });
        var store = CreateStore();

        var path = await store.WriteIfNewAsync(config, CancellationToken.None);

        Assert.Equal(Path.Combine(_tempDir, config.Fingerprint + ".json"), path);
        Assert.False(File.Exists(path), "Serialization failure must not leave a file on disk.");
    }
}
