using Radar.Application.Lifecycle;
using Radar.Application.Scoring;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// Spec 184 §2 — the strict operating-calls reader: an absent file is <c>null</c> (an undeclared layer, not
/// an error); a present-but-invalid file fails naming the file and the rule; and the COMMITTED repo file
/// parses, validates against the live strategy set and reduces to exactly one Lead (the shipped calls).
/// </summary>
public sealed class FileOperatingCallSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"radar-calls-{Guid.NewGuid():N}");

    private string PathFor(string name) => Path.Combine(_dir, name);

    private FileOperatingCallSource Source(string fileName) =>
        new(new FileOperatingCallSourceOptions(PathFor(fileName)));

    private async Task<StrategyOperatingCallsFile?> ReadAsync(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = PathFor("calls.json");
        await File.WriteAllTextAsync(path, json);
        return await Source("calls.json").ReadAsync(default);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task AbsentFile_ReturnsNull()
    {
        Assert.Null(await Source("does-not-exist.json").ReadAsync(default));
    }

    [Fact]
    public async Task ValidFile_ParsesCallsGlobalCallAndResolution()
    {
        var file = await ReadAsync("""
            {
              "schemaVersion": "strategy-operating-calls-v1",
              "globalCall": "StopAll",
              "calls": [
                {
                  "strategy": "alpha",
                  "call": "DoNotLead",
                  "asOfUtc": "2026-08-23T00:00:00Z",
                  "basis": "declared basis",
                  "actor": "human",
                  "overridesGate": true,
                  "reviewByUtc": "2026-09-05T00:00:00Z",
                  "resolutionRule": "the immutable rule",
                  "resolution": {
                    "outcome": "Wrong",
                    "resolvedAtUtc": "2027-02-02T00:00:00Z",
                    "evidenceRef": "data/efficacy/strategy-paired-comparison.md"
                  }
                }
              ]
            }
            """);

        Assert.NotNull(file);
        Assert.True(file.StopAll);
        var call = Assert.Single(file.Calls);
        Assert.Equal("alpha", call.Strategy);
        Assert.Equal(OperatingCall.DoNotLead, call.Call);
        Assert.Equal(new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero), call.AsOfUtc);
        Assert.Equal(OperatingCallActor.Human, call.Actor);
        Assert.True(call.OverridesGate);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero), call.ReviewByUtc);
        Assert.Equal("the immutable rule", call.ResolutionRule);
        Assert.Equal(OperatingCallOutcome.Wrong, call.Resolution!.Outcome);
        Assert.Equal("data/efficacy/strategy-paired-comparison.md", call.Resolution.EvidenceRef);
    }

    [Theory]
    [InlineData(
        """{"schemaVersion":"strategy-operating-calls-v1","calls":[{"strategy":"a","call":"Leed","asOfUtc":"2026-08-23T00:00:00Z","basis":"b","actor":"human","reviewByUtc":"2026-09-05T00:00:00Z"}]}""",
        "unknown token 'Leed'")]
    [InlineData(
        """{"schemaVersion":"strategy-operating-calls-v1","calls":[{"strategy":"a","call":"Lead","asOfUtc":"2026-08-23T00:00:00Z","basis":"b","actor":"robot","reviewByUtc":"2026-09-05T00:00:00Z"}]}""",
        "unknown token 'robot'")]
    [InlineData(
        """{"schemaVersion":"strategy-operating-calls-v1","globalCall":"StopSome","calls":[]}""",
        "unknown token 'StopSome'")]
    [InlineData(
        """{"schemaVersion":"strategy-operating-calls-v1","calls":[{"strategy":"a","call":"Lead","asOfUtc":"2026-08-23T00:00:00Z","basis":"b","actor":"human","reviewByUtc":"2026-09-05T00:00:00Z","overidesGate":true}]}""",
        "unknown property 'overidesGate'")]
    [InlineData(
        """{"schemaVersion":"strategy-operating-calls-v2","calls":[]}""",
        "not supported")]
    [InlineData(
        """{"schemaVersion":"strategy-operating-calls-v1","calls":[{"strategy":"a","call":"Lead","basis":"b","actor":"human","reviewByUtc":"2026-09-05T00:00:00Z"}]}""",
        "missing a required field")]
    public async Task InvalidFile_Fails_NamingFileAndRule(string json, string expectedRuleFragment)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(json));
        Assert.Contains("calls.json", ex.Message);
        Assert.Contains(expectedRuleFragment, ex.Message);
    }

    [Fact]
    public async Task UnparseableJson_Fails_RatherThanReadingAsNoCalls()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync("{ not json"));
        Assert.Contains("could not be read or parsed", ex.Message);
    }

    // ---------------------------------------------------------------------------------------------------
    // The COMMITTED file: the shipped initial calls (spec 184 §2) must parse, validate against the live
    // baseline strategy set (the scripts/run-profiles/default.json arms) and reduce to exactly one Lead —
    // disclosure-led-v11 — with default DoNotLead and the remaining research arms Trial.
    // ---------------------------------------------------------------------------------------------------

    private static readonly IReadOnlyList<ScoringStrategyDefinition> LiveBaselineStrategies =
    [
        Define("default", isPrimary: true),
        Define("filings-led-v2"),
        Define("filings-led-halfnoted"),
        Define("filings-led-nonoted"),
        Define("narrative-led-v2"),
        Define("baseline-earnings-only", purpose: StrategyPurpose.Comparator),
        Define("baseline-activity-only", purpose: StrategyPurpose.Comparator),
        Define("baseline-media-only", purpose: StrategyPurpose.Comparator),
        Define("disclosure-led-v11"),
        Define("disclosure-led-v10-control", purpose: StrategyPurpose.Comparator),
    ];

    private static ScoringStrategyDefinition Define(
        string name, bool isPrimary = false, StrategyPurpose purpose = StrategyPurpose.Research) =>
        new(name, name, new ScoringWeights(), isPrimary) { Purpose = purpose };

    [Fact]
    public async Task CommittedCallsFile_Parses_Validates_AndReducesToTheDeclaredLead()
    {
        var path = Path.Combine(LocateRepoRoot(), "data", "strategy-operating-calls.json");
        Assert.True(File.Exists(path), $"The committed operating-calls file is missing at {path}.");

        var source = new FileOperatingCallSource(new FileOperatingCallSourceOptions(path));
        var file = await source.ReadAsync(default);
        Assert.NotNull(file);

        var resolved = OperatingCallReducer.Reduce(file, LiveBaselineStrategies, []);

        Assert.False(resolved.StopAll);
        Assert.Equal("disclosure-led-v11", resolved.LeadStrategyName);
        Assert.Equal(OperatingCall.DoNotLead, resolved.For("default")!.Call);
        Assert.Equal(OperatingCall.Trial, resolved.For("filings-led-v2")!.Call);
        Assert.Equal(OperatingCall.Trial, resolved.For("narrative-led-v2")!.Call);

        // Every shipped call carries the falsifiability contract: an immutable rule, the exact UTC review
        // checkpoint, and the maintainer-directed call instant.
        Assert.All(file.Calls, call =>
        {
            Assert.False(string.IsNullOrWhiteSpace(call.ResolutionRule));
            Assert.Equal(new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero), call.AsOfUtc);
            Assert.Equal(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero), call.ReviewByUtc);
            Assert.Equal(OperatingCallActor.Human, call.Actor);
        });

        // The Lead's rule references the GATE EVENT, not a calendar date (spec 184 §2).
        var lead = file.Calls.Single(c => c.Call == OperatingCall.Lead);
        Assert.Equal("disclosure-led-v11", lead.Strategy);
        Assert.Contains("AD-15 composite gate", lead.ResolutionRule);
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
