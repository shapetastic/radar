using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Scoring;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileScoringConfigStoreTests : IDisposable
{
    private const string EngineVersion = "mvp-engine-v1";
    private const string FormulaVersion = "radar-formula-v7";
    private const string AttentionDescriptor = "attn:v1;unknown=0.4";
    private const string SignalSourceDescriptor = "rules=radar-keyword-rules-v6;collectors=sec-edgar;";
    private const string InsiderMaterialityDescriptor = "buy=5000000:8;sell=5000000:8;cluster=1;";
    // Deliberately a LEGACY descriptor value, and left at v1 by spec 194 §1.5 (which bumped the live one to
    // media-collapse-v2). This store is content-addressed: it round-trips whatever descriptor string it is
    // handed and verifies the fingerprint recomputes from the persisted record, so the fixture's job is to be
    // a stable arbitrary value — exactly like the radar-formula-v7 / radar-keyword-rules-v6 constants beside
    // it. Accrued config records on disk genuinely carry v1; tracking the current version here would assert
    // nothing extra and would have to be re-edited on every structure bump.
    private const string MediaCollapseDescriptor = "media-collapse-v1;window=3;";

    // Spec 148: the recent-signal window is a hashed field AND is carried verbatim on the persisted record,
    // so the store's descriptor↔fingerprint self-verification still holds. Deliberately NOT the 30-day
    // default, so a self-verification that ignored the field would fail rather than pass by coincidence.
    private static readonly TimeSpan Window = TimeSpan.FromDays(21);

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
                InsiderMaterialityDescriptor, MediaCollapseDescriptor, Window),
            EngineVersion: EngineVersion,
            FormulaVersion: FormulaVersion,
            Weights: weights,
            AttentionDescriptor: AttentionDescriptor,
            SignalSourceDescriptor: SignalSourceDescriptor,
            InsiderMaterialityDescriptor: InsiderMaterialityDescriptor,
            MediaCollapseDescriptor: MediaCollapseDescriptor,
            Window: Window);

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

        var path = (await store.WriteIfNewAsync(config, CancellationToken.None)).Path;

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
        Assert.Equal(config.Window, stored.Window);
        // Every ScoringWeights value round-trips (record equality compares all init properties).
        Assert.Equal(config.Weights, stored.Weights);
    }

    [Fact]
    public void LegacyConfigFile_WithNoWindowProperty_DeserializesAsUnrecorded_NotZero()
    {
        // Spec 148: EffectiveScoringConfig.Window is nullable ON PURPOSE. A config file written before this
        // slice has no window field, and reading that absence as TimeSpan.Zero would be a FALSE record — it
        // would claim a zero-length window no run ever used, and (worse) it would look recomputable. null
        // means "written pre-148; not recorded", which is honest and visibly un-recomputable.
        var json = """
        {
          "fingerprint": "radar-scoring-fp-legacy00000",
          "engineVersion": "mvp-engine-v1",
          "formulaVersion": "radar-formula-v8",
          "weights": {},
          "attentionDescriptor": "attn:v1;unknown=0.4",
          "signalSourceDescriptor": "rules=radar-keyword-rules-v6;",
          "insiderMaterialityDescriptor": "buy=5000000:8;sell=5000000:8;cluster=1;",
          "mediaCollapseDescriptor": "media-collapse-v1;window=3;"
        }
        """;

        var config = JsonSerializer.Deserialize<EffectiveScoringConfig>(json, RadarFileStoreJson.Options);

        Assert.NotNull(config);
        Assert.Null(config!.Window);
    }

    [Fact]
    public async Task WriteIfNewAsync_IsInsertIfNew_DoesNotOverwriteExistingFile()
    {
        var config = ConfigFor(new ScoringWeights());
        var store = CreateStore();

        var path = (await store.WriteIfNewAsync(config, CancellationToken.None)).Path;

        // Tamper with the file on disk. Insert-if-new must NOT rewrite it — the tampered bytes survive,
        // proving the second call truly skipped (mirrors AD-1 evidence immutability).
        const string tampered = "TAMPERED-NOT-JSON";
        await File.WriteAllTextAsync(path, tampered);

        var secondPath = (await store.WriteIfNewAsync(config, CancellationToken.None)).Path;

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

        var path = (await store.WriteIfNewAsync(config, CancellationToken.None)).Path;

        var stored = ReadStored(path);

        // Spec 148: the window is carried on the record precisely so the recompute below is still possible.
        // Every NEW write populates it (null would mean "written before spec 148"), so a missing value here
        // is a defect, not a legacy file.
        Assert.Equal(Window, stored.Window);

        // Self-verification: the hash is no longer opaque — recomputing it from the DESERIALIZED config
        // equals both the filename (sans .json) and the stored Fingerprint field.
        var recomputed = ScoringConfigFingerprint.Compute(
            stored.EngineVersion, stored.FormulaVersion, stored.Weights, stored.AttentionDescriptor,
            stored.SignalSourceDescriptor, stored.InsiderMaterialityDescriptor, stored.MediaCollapseDescriptor,
            stored.Window!.Value);

        Assert.Equal(Path.GetFileNameWithoutExtension(path), recomputed);
        Assert.Equal(stored.Fingerprint, recomputed);
    }

    [Fact]
    public async Task ComposedFormulaIdentity_RoundTrips_AndTheSelfVerificationStillHolds()
    {
        // SPEC 153. A formula may now declare a CompositionRevision, in which case ScoringEngine stamps the
        // COMPOSED identity "{Version}@{Revision}" (radar-formula-v10@rev1) rather than the bare token — in
        // all three places, including this record's FormulaVersion. That matters HERE specifically: this store
        // recomputes the fingerprint FROM the persisted FormulaVersion, so storing the composed value is
        // exactly what keeps the self-verification invariant true. Storing the bare version while hashing the
        // composed one would break it silently, and the failure would only surface as an unresolvable stamp.
        var composed = $"{ScoreFormulaVersions.V10}{FormulaIdentity.RevisionSeparator}rev1";
        var weights = new ScoringWeights();
        var config = new EffectiveScoringConfig(
            Fingerprint: ScoringConfigFingerprint.Compute(
                EngineVersion, composed, weights, AttentionDescriptor, SignalSourceDescriptor,
                InsiderMaterialityDescriptor, MediaCollapseDescriptor, Window),
            EngineVersion: EngineVersion,
            FormulaVersion: composed,
            Weights: weights,
            AttentionDescriptor: AttentionDescriptor,
            SignalSourceDescriptor: SignalSourceDescriptor,
            InsiderMaterialityDescriptor: InsiderMaterialityDescriptor,
            MediaCollapseDescriptor: MediaCollapseDescriptor,
            Window: Window);

        var store = CreateStore();
        var path = (await store.WriteIfNewAsync(config, CancellationToken.None)).Path;
        var stored = ReadStored(path);

        Assert.Equal(composed, stored.FormulaVersion);

        var recomputed = ScoringConfigFingerprint.Compute(
            stored.EngineVersion, stored.FormulaVersion, stored.Weights, stored.AttentionDescriptor,
            stored.SignalSourceDescriptor, stored.InsiderMaterialityDescriptor, stored.MediaCollapseDescriptor,
            stored.Window!.Value);

        Assert.Equal(Path.GetFileNameWithoutExtension(path), recomputed);
        Assert.Equal(stored.Fingerprint, recomputed);

        // …and a revision bump is a genuinely different identity, which is the whole mechanism: it re-stamps,
        // so StrategyIdentityGuard trips instead of two compositions sharing one series.
        Assert.NotEqual(
            config.Fingerprint,
            ScoringConfigFingerprint.Compute(
                EngineVersion, $"{ScoreFormulaVersions.V10}{FormulaIdentity.RevisionSeparator}rev2", weights,
                AttentionDescriptor, SignalSourceDescriptor, InsiderMaterialityDescriptor,
                MediaCollapseDescriptor, Window));
    }

    [Fact]
    public async Task CustomProfile_ProducesDistinctFile_WithCustomValuesRecoverable()
    {
        var store = CreateStore();

        var defaultConfig = ConfigFor(new ScoringWeights());
        var customConfig = ConfigFor(new ScoringWeights { AttentionHalfSaturation = 12.0 });

        // Distinct content -> distinct fingerprint -> distinct filename (content-addressed).
        Assert.NotEqual(defaultConfig.Fingerprint, customConfig.Fingerprint);

        var defaultPath = (await store.WriteIfNewAsync(defaultConfig, CancellationToken.None)).Path;
        var customPath = (await store.WriteIfNewAsync(customConfig, CancellationToken.None)).Path;

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

        var path = (await store.WriteIfNewAsync(config, CancellationToken.None)).Path;

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

        var path = (await store.WriteIfNewAsync(config, CancellationToken.None)).Path;

        Assert.Equal(Path.Combine(_tempDir, config.Fingerprint + ".json"), path);
        Assert.False(File.Exists(path), "Serialization failure must not leave a file on disk.");
    }

    // ---- Per-strategy-name fingerprint record (spec 141: the fingerprint as a tripwire) ----

    [Fact]
    public async Task ReadStrategyFingerprintAsync_NeverRecorded_ReturnsNull()
    {
        var store = CreateStore();

        Assert.Null(await store.ReadStrategyFingerprintAsync("momentum", CancellationToken.None));
    }

    [Fact]
    public async Task RecordStrategyFingerprintAsync_WritesUnderStrategiesFolder_AndRoundTrips()
    {
        var store = CreateStore();

        var path = (await store.RecordStrategyFingerprintAsync(
            "momentum", "radar-scoring-fp-aaaa1111", CancellationToken.None)).Path;

        // A subdirectory, so the root's content-addressed {fingerprint}.json listing is untouched.
        Assert.Equal(Path.Combine(_tempDir, "strategies", "momentum.json"), path);
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.json"));

        Assert.Equal(
            "radar-scoring-fp-aaaa1111",
            await store.ReadStrategyFingerprintAsync("momentum", CancellationToken.None));

        // The record names the strategy it belongs to, so a hand-inspected file is self-describing.
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("momentum", doc.RootElement.GetProperty("strategyName").GetString());
        Assert.Equal("radar-scoring-fp-aaaa1111", doc.RootElement.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task RecordStrategyFingerprintAsync_IsUpsert_NotInsertIfNew()
    {
        // The DELIBERATE opposite of the content-addressed config files above: this record answers "what does
        // this NAME resolve to NOW", so a legitimate re-record (after an operator consciously retires a
        // series) must overwrite rather than be skipped.
        var store = CreateStore();

        await store.RecordStrategyFingerprintAsync("momentum", "radar-scoring-fp-old", CancellationToken.None);
        await store.RecordStrategyFingerprintAsync("momentum", "radar-scoring-fp-new", CancellationToken.None);

        Assert.Equal(
            "radar-scoring-fp-new",
            await store.ReadStrategyFingerprintAsync("momentum", CancellationToken.None));
        Assert.Single(Directory.GetFiles(Path.Combine(_tempDir, "strategies"), "*.json"));
    }

    [Fact]
    public async Task StrategyFingerprints_AreTrackedPerName()
    {
        var store = CreateStore();

        await store.RecordStrategyFingerprintAsync("momentum", "radar-scoring-fp-aaaa", CancellationToken.None);
        await store.RecordStrategyFingerprintAsync(
            "insider-only", "radar-scoring-fp-bbbb", CancellationToken.None);

        Assert.Equal(
            "radar-scoring-fp-aaaa",
            await store.ReadStrategyFingerprintAsync("momentum", CancellationToken.None));
        Assert.Equal(
            "radar-scoring-fp-bbbb",
            await store.ReadStrategyFingerprintAsync("insider-only", CancellationToken.None));
    }

    [Fact]
    public async Task ReadStrategyFingerprintAsync_MalformedRecord_ReadsAsUnrecorded_WithoutThrowing()
    {
        // Graceful degrade (AD-8): "cannot read" must read as "unrecorded", never as "changed" — otherwise a
        // corrupted byte would fail every run through the startup tripwire.
        var store = CreateStore();
        var path = (await store.RecordStrategyFingerprintAsync(
            "momentum", "radar-scoring-fp-aaaa", CancellationToken.None)).Path;
        await File.WriteAllTextAsync(path, "{ not json");

        Assert.Null(await store.ReadStrategyFingerprintAsync("momentum", CancellationToken.None));

        // A record present but carrying no fingerprint is equally "unrecorded".
        await File.WriteAllTextAsync(path, "{ \"strategyName\": \"momentum\" }");
        Assert.Null(await store.ReadStrategyFingerprintAsync("momentum", CancellationToken.None));
    }

    [Fact]
    public async Task RecordStrategyFingerprintAsync_IoFailure_ReturnsAttemptedPathWithoutThrowing()
    {
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir-2");
        await File.WriteAllTextAsync(rootAsFile, "x");
        var store = CreateStore(rootAsFile);

        var path = (await store.RecordStrategyFingerprintAsync(
            "momentum", "radar-scoring-fp-aaaa", CancellationToken.None)).Path;

        Assert.Equal(Path.Combine(rootAsFile, "strategies", "momentum.json"), path);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData(" leading")]
    public async Task StrategyName_ThatWouldEscapeTheRoot_IsRejected(string name)
    {
        // The name is used verbatim as a file name, so it is held to the SAME shared StorageSegmentName rule
        // ScoringStrategySet enforces at startup — a defence in depth, not a second rule.
        var store = CreateStore();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RecordStrategyFingerprintAsync(name, "radar-scoring-fp-aaaa", CancellationToken.None));
    }
}
