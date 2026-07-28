using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Scoring;
using Radar.ChannelFeasibilityAudit;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.DependencyInjection;

// Spec 158 — INPUT-ONLY channel feasibility characterization, at the PINNED as-of instant (never the
// execution time; §2). STRICTLY READ-ONLY over the data root: the composition below registers the durable
// file stores (whose hydration only reads), an in-memory company repository seeded from companies.json, and
// the spec-151 attribution resolver. Nothing in this process writes under the data root: no collector is
// registered, no score store, no report writer, no pipeline. The report goes to STDOUT; logs go to STDERR.
//
// Usage: Radar.ChannelFeasibilityAudit --data-root <path> [--recorded-only]
//   --data-root      the durable store root (holds signals/, evidence/raw/, companies.json). Required.
//   --recorded-only  disable the spec-151 legacy attribution inference (default: ENABLED, because 94.7% of
//                    accrued evidence predates recorded attribution and the audit must report the
//                    recorded/inferred/unattributed split rather than score everything unattributed).

string? dataRoot = null;
var inferLegacyAttribution = true;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data-root" when i + 1 < args.Length:
            dataRoot = args[++i];
            break;
        case "--recorded-only":
            inferLegacyAttribution = false;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
            return 2;
    }
}

if (string.IsNullOrWhiteSpace(dataRoot))
{
    Console.Error.WriteLine(
        "Usage: Radar.ChannelFeasibilityAudit --data-root <path> [--recorded-only]");
    return 2;
}

dataRoot = Path.GetFullPath(dataRoot);
var signalsRoot = Path.Combine(dataRoot, "signals");
var evidenceRawRoot = Path.Combine(dataRoot, "evidence", "raw");
var companiesFile = Path.Combine(dataRoot, "companies.json");

foreach (var (path, what) in new[]
         {
             (signalsRoot, "signals directory"),
             (evidenceRawRoot, "raw evidence directory"),
         })
{
    if (!Directory.Exists(path))
    {
        Console.Error.WriteLine($"No {what} at '{path}'.");
        return 2;
    }
}

if (!File.Exists(companiesFile))
{
    Console.Error.WriteLine($"No company seed file at '{companiesFile}'.");
    return 2;
}

var services = new ServiceCollection();

// Logs to STDERR only, so STDOUT carries exactly the report (the PowerShell launcher captures stdout).
services.AddLogging(builder =>
{
    builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.SetMinimumLevel(LogLevel.Warning);
});

// The same registration chain the Worker composes for the durable read path (spec 142): in-memory
// persistence first, then the file stores, then the repoint of ISignalRepository/IEvidenceRepository onto
// those same singletons. The in-memory company repository stays and is seeded from companies.json below.
services.AddInMemoryRadarPersistence();
services.AddFileRawEvidenceStore(evidenceRawRoot);
services.AddFileSignalStore(signalsRoot);
services.AddDurableRadarSignalHistory();
services.AddLocalFileCompanySeed(companiesFile);

// The spec-151 collector-attribution pair, resolved through the SAME composition-root reader the Worker
// uses (AddRadarCollectorAttribution), so the audit's inference semantics cannot drift from production's.
var attributionConfig = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Radar:Scoring:InferLegacyCollectorAttribution"] =
            inferLegacyAttribution ? "true" : "false",
    })
    .Build();
services.AddRadarCollectorAttribution(attributionConfig);

// The scoring magnitudes and shared components, at the CODE DEFAULTS the live baseline runs with
// (scripts/run-profiles/default.json deliberately omits Radar:Scoring, so code defaults ARE the baseline).
services.AddSingleton(new ScoringWeights());
services.AddSingleton(new MediaCollapseOptions());
services.AddSingleton<MediaAttentionCollapse>();
services.AddSingleton(AttentionSourceTierOptions.Default);
services.AddSingleton<IAttentionSourceWeights, ConfiguredAttentionSourceWeights>();

services.AddSingleton<Radar.ChannelFeasibilityAudit.ChannelFeasibilityAudit>();

await using var provider = services.BuildServiceProvider();

// Seed the 43-company watch universe (curated FollowingTier included) into the in-memory repository.
var seeded = await provider.GetRequiredService<ICompanyUniverseSeeder>()
    .SeedAsync(CancellationToken.None).ConfigureAwait(false);
Console.Error.WriteLine($"Seeded {seeded} companies from '{companiesFile}'.");

var audit = provider.GetRequiredService<Radar.ChannelFeasibilityAudit.ChannelFeasibilityAudit>();

// §6 recommendation candidates, characterized through the SAME in-memory pass as the predeclared budget so
// the findings can quote their integer-score distributions (spec 158 §6: a proposed budget must be run
// through the same distribution before it can be adopted by a later amendment). Built through the production
// validator; all exclude newssearch (AD-16: third-party news is the outcome, not an input) and breadth
// (structurally zero under §3 unless the §5 measurement proves otherwise — reported either way).
var candidateFilingsOnly = ScoringChannelSet.Create(
    [
        ScoringChannel.Collector("filings", [RadarCollectorNames.SecEdgar], 1.00, 3),
    ],
    "candidate-filings-only-v11");

var candidateFilingsPress = ScoringChannelSet.Create(
    [
        ScoringChannel.Collector("filings", [RadarCollectorNames.SecEdgar], 0.60, 3),
        ScoringChannel.Collector("press", [RadarCollectorNames.Rss], 0.40, 3),
    ],
    "candidate-filings-press-v11");

var candidatePressInsider = ScoringChannelSet.Create(
    [
        ScoringChannel.Collector("press", [RadarCollectorNames.Rss], 0.60, 3),
        ScoringChannel.Collector("insider", [RadarCollectorNames.SecForm4], 0.40, 2),
    ],
    "candidate-press-insider-v11");

var report = await audit.RunAsync(
    Radar.ChannelFeasibilityAudit.ChannelFeasibilityAudit.PinnedAsOfUtc,
    Radar.ChannelFeasibilityAudit.ChannelFeasibilityAudit.PinnedWindow,
    CancellationToken.None,
    candidateFilingsOnly,
    candidateFilingsPress,
    candidatePressInsider).ConfigureAwait(false);

Console.Out.Write(ChannelFeasibilityReportRenderer.Render(
    report,
    [
        "candidate A: filings sec-edgar 1.00 (S 3) — single channel, no breadth",
        "candidate B: filings sec-edgar .60 (S 3) / press RssPressReleaseCollector .40 (S 3) — no breadth",
        "candidate C: press RssPressReleaseCollector .60 (S 3) / insider sec-form4 .40 (S 2) — no breadth",
    ]));

return 0;
