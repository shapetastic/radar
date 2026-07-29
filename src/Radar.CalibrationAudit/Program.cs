using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

using Radar.Application.Filings;
using Radar.CalibrationAudit;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.Sec;

// Spec 162 (Phase A) — AI filing-read calibration audit console. READ-ONLY research tool: no scoring
// change, no fingerprint input, no pin move. It reads the model-scoped analyzed-filing cache (through the
// PRODUCTION FileAnalyzedFilingCache scoping logic), seals the model answers into worksheet.csv, recovers
// CIK/ticker from the persisted raw filing evidence (data/evidence/raw/filing/**, singular), and archives
// each filing's earnings-release exhibit through the PRODUCTION HttpSecEarningsReleaseReader — writing the
// full normalized text AND the exact ChatFilingAnalyzer model input (leading MaxInputLength substring).
// It writes ONLY under its own --output-root; everything else it touches is read-only.
//
// Usage: Radar.CalibrationAudit --data-root <path> [options]
//   --data-root <path>         root holding filings-cache/ and evidence/raw/filing/**. Required.
//   --cache-root <path>        analyzed-filing cache root (default {data-root}/filings-cache).
//   --output-root <path>       audit output root (default {data-root}/calibration-audit). The ONLY
//                              directory this console writes.
//   --model-identity <id>      the provider:model identity of the ACTIVE earnings reader
//                              (default "openai:deepseek-ai/DeepSeek-V4-Flash", the spec-119 baseline).
//   --expected-scope <segment> the expected cache scope segment; the console FAILS LOUDLY if the segment
//                              derived from --model-identity via the production scoping logic differs
//                              (default "openai-deepseek-ai-deepseek-v4-flash-8f94f2dbe65fcb93").
//   --max-input-length <n>     the ChatFilingAnalyzer input cap in force (default: the production
//                              FilingAnalyzerOptions default, 12000 — the live baseline value).
//   --skip-fetch               build the cohort + worksheet only; no SEC traffic (RADAR_SEC_UA not needed).
//   --max-fetches <n>          cap NEW exhibit fetches this run (re-run to continue; default unlimited).
//
// SEC access: exhibit fetches REQUIRE the RADAR_SEC_UA environment variable (a compliant "Name email" SEC
// User-Agent; fails fast when missing). All traffic goes through the production reader's typed HttpClient,
// paced by the shared SecRequestPacer, strictly sequentially. Re-runnable: accessions already archived in
// exhibit-manifest.csv are skipped; a suspiciously short stored body (< 200 trimmed chars, mirroring the
// production spec-114 MinPlausibleBodyLength) forces a refetch.

const string DefaultModelIdentity = "openai:deepseek-ai/DeepSeek-V4-Flash";
const string DefaultExpectedScope = "openai-deepseek-ai-deepseek-v4-flash-8f94f2dbe65fcb93";
const int ExpectedDirectionalCount = 145; // Spec 162: the active-scope cohort at spec time. Drift is
const int ExpectedNoSignalCount = 153;    // REPORTED, never a hard failure (the cache accrues).

string? dataRoot = null;
string? cacheRootArg = null;
string? outputRootArg = null;
var modelIdentity = DefaultModelIdentity;
var expectedScope = DefaultExpectedScope;
var maxInputLength = new FilingAnalyzerOptions().MaxInputLength; // The production default (12000).
var skipFetch = false;
var maxFetches = int.MaxValue;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data-root" when i + 1 < args.Length:
            dataRoot = args[++i];
            break;
        case "--cache-root" when i + 1 < args.Length:
            cacheRootArg = args[++i];
            break;
        case "--output-root" when i + 1 < args.Length:
            outputRootArg = args[++i];
            break;
        case "--model-identity" when i + 1 < args.Length:
            modelIdentity = args[++i];
            break;
        case "--expected-scope" when i + 1 < args.Length:
            expectedScope = args[++i];
            break;
        case "--max-input-length" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxInputLength))
            {
                Console.Error.WriteLine("--max-input-length must be an integer.");
                return 2;
            }

            break;
        case "--skip-fetch":
            skipFetch = true;
            break;
        case "--max-fetches" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxFetches))
            {
                Console.Error.WriteLine("--max-fetches must be an integer.");
                return 2;
            }

            break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
            return 2;
    }
}

if (string.IsNullOrWhiteSpace(dataRoot))
{
    Console.Error.WriteLine(
        "Usage: Radar.CalibrationAudit --data-root <path> [--cache-root <path>] [--output-root <path>] "
            + "[--model-identity <provider:model>] [--expected-scope <segment>] [--max-input-length <n>] "
            + "[--skip-fetch] [--max-fetches <n>]");
    return 2;
}

if (maxInputLength <= 0)
{
    Console.Error.WriteLine("--max-input-length must be positive (the production analyzer rejects a non-positive cap).");
    return 2;
}

dataRoot = Path.GetFullPath(dataRoot);
var cacheRoot = Path.GetFullPath(cacheRootArg ?? Path.Combine(dataRoot, "filings-cache"));
var outputRoot = Path.GetFullPath(outputRootArg ?? Path.Combine(dataRoot, "calibration-audit"));
var rawFilingRoot = Path.Combine(dataRoot, "evidence", "raw", "filing"); // SINGULAR "filing" (spec 162).

// Read-only guard: the output root is the ONLY directory this console writes, so it must never sit inside
// (or be) a store directory it reads.
foreach (var (forbidden, name) in new[] { (cacheRoot, "the filings cache"), (rawFilingRoot, "the raw evidence store") })
{
    if (outputRoot.Equals(forbidden, StringComparison.OrdinalIgnoreCase)
        || outputRoot.StartsWith(forbidden + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"--output-root '{outputRoot}' is inside {name} '{forbidden}'; the audit never writes inside a store it reads.");
        return 2;
    }
}

// Logs to STDERR only, so STDOUT carries exactly the summary (ChannelFeasibilityAudit precedent).
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.SetMinimumLevel(LogLevel.Warning);
});
var logger = loggerFactory.CreateLogger("CalibrationAudit");

// --- 1. Derive the cache scope segment through the PRODUCTION scoping logic ---------------------------
// AddFileAnalyzedFilingCache is the exact call the Worker makes (RadarWorkerServices, spec 118/119): it
// derives the filename-safe model segment (readable token + 16-hex identity hash) and registers the
// production cache. Resolving the options back out IS the production derivation — nothing is re-derived
// here, so this console can never disagree with the Worker about which directory is the active scope.
var cacheServices = new ServiceCollection();
cacheServices.AddLogging(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));
cacheServices.AddFileAnalyzedFilingCache(cacheRoot, modelIdentity);
await using var cacheProvider = cacheServices.BuildServiceProvider();

var derivedScope = cacheProvider.GetRequiredService<FileAnalyzedFilingCacheOptions>().ModelSegment;
if (!string.Equals(derivedScope, expectedScope, StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        $"SCOPE MISMATCH: the production cache scoping logic derives segment '{derivedScope}' for model "
            + $"identity '{modelIdentity}', but the expected scope is '{expectedScope}'. Either the model "
            + "identity is wrong (pass --model-identity exactly as the Worker composes it: "
            + "'{provider}:{effectiveModel}') or the expected pin must be consciously updated "
            + "(--expected-scope) — the cohort is model-scoped and reading another model's scope would "
            + "calibrate the wrong reader.");
    return 1;
}

var scopedCache = cacheProvider.GetRequiredService<IAnalyzedFilingCache>();
var legacyRootCache = new FileAnalyzedFilingCache(
    new FileAnalyzedFilingCacheOptions { RootDirectory = cacheRoot },
    loggerFactory.CreateLogger<FileAnalyzedFilingCache>());

// --- 2. Build the model-scoped cohort ------------------------------------------------------------------
var cohort = await CalibrationCohortBuilder.BuildAsync(
    cacheRoot, derivedScope, scopedCache, legacyRootCache, CancellationToken.None).ConfigureAwait(false);

var directional = cohort.Rows.Where(static r => r.Record.Outcome == AnalyzedFilingOutcome.DirectionalSignalProduced).ToList();
var noSignal = cohort.Rows.Where(static r => r.Record.Outcome == AnalyzedFilingOutcome.NoDirectionalSignal).ToList();

// --- 3. CIK/ticker recovery from the persisted raw filing evidence -------------------------------------
var evidenceIndex = RawFilingEvidenceIndex.Load(rawFilingRoot, logger, CancellationToken.None);
var unrecoverable = cohort.Rows
    .Where(r => !evidenceIndex.TryResolve(r.Accession, out _))
    .ToList();

// --- 4. Seal the worksheet + write the exclusion/unrecoverable lists -----------------------------------
Directory.CreateDirectory(outputRoot);
var (worksheetPath, worksheetSha) = WorksheetWriter.Write(
    outputRoot, cohort.Rows, evidenceIndex, modelIdentity, derivedScope);

var legacyCsv = new StringBuilder();
legacyCsv.AppendLine("accession,reason,legacyOutcome,activeOutcome,outcomeConflict,cacheFile");
foreach (var ex in cohort.LegacyExclusions)
{
    legacyCsv.AppendLine(Csv.Line(
        ex.Accession, "legacy-scope", ex.LegacyOutcome, ex.ActiveOutcome ?? string.Empty,
        ex.OutcomeConflict ? "true" : "false", ex.CacheFile));
}

File.WriteAllText(Path.Combine(outputRoot, "legacy-exclusions.csv"), legacyCsv.ToString());

var unrecoverableCsv = new StringBuilder();
unrecoverableCsv.AppendLine("accession,outcome,reason");
foreach (var row in unrecoverable)
{
    unrecoverableCsv.AppendLine(Csv.Line(
        row.Accession, row.Record.Outcome.ToString(), "no-raw-filing-evidence-match"));
}

File.WriteAllText(Path.Combine(outputRoot, "unrecoverable-accessions.csv"), unrecoverableCsv.ToString());

// --- 5. Exhibit archive (production reader, paced, sequential, re-runnable) ----------------------------
var manifest = ExhibitArchiver.LoadManifest(outputRoot);
var fetched = 0;
var skipped = 0;
var failed = 0;

if (!skipFetch)
{
    var secUserAgent = Environment.GetEnvironmentVariable("RADAR_SEC_UA");
    if (string.IsNullOrWhiteSpace(secUserAgent))
    {
        Console.Error.WriteLine(
            "RADAR_SEC_UA is not set. SEC EDGAR requires a compliant declared User-Agent "
                + "(e.g. \"Radar Research you@example.com\") — every request 403s without one. Set RADAR_SEC_UA "
                + "or pass --skip-fetch to build the cohort + worksheet without SEC traffic.");
        return 2;
    }

    // The PRODUCTION earnings-release reader composition (the same registration the Worker calls): typed
    // HttpClient with the SEC UA + gzip, the shared SecRateLimitingHandler/SecRequestPacer pacing, the
    // shared normalizer, the reader's own 429 retry + self-pacing. Nothing re-implemented.
    var secServices = new ServiceCollection();
    secServices.AddLogging(b =>
    {
        b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        b.SetMinimumLevel(LogLevel.Warning);
    });
    secServices.AddSecEarningsReleaseReader(new SecCollectorOptions { UserAgent = secUserAgent });
    await using var secProvider = secServices.BuildServiceProvider();

    var archiver = new ExhibitArchiver(
        secProvider.GetRequiredService<ISecEarningsReleaseReader>(), maxInputLength, logger);

    // Deterministic fetch order: SHA-256(accession) hex ascending — the worksheet/batching order.
    foreach (var row in cohort.Rows.OrderBy(static r => r.AccessionSha256, StringComparer.Ordinal))
    {
        if (!evidenceIndex.TryResolve(row.Accession, out var attribution))
        {
            continue; // Listed in unrecoverable-accessions.csv; no CIK ⇒ no fetch possible.
        }

        var ticker = attribution.Ticker ?? string.Empty;
        var fullPath = ExhibitArchiver.FullTextPath(outputRoot, ticker, row.Accession);
        var modelInputPath = ExhibitArchiver.ModelInputPath(outputRoot, ticker, row.Accession);

        if (!ExhibitArchiver.NeedsFetch(
                manifest.GetValueOrDefault(row.Accession), fullPath, modelInputPath, out var reason))
        {
            skipped++;
            continue;
        }

        if (fetched >= maxFetches)
        {
            break;
        }

        logger.LogInformation("Fetching exhibit for {Accession} ({Reason}).", row.Accession, reason);
        var manifestRow = await archiver.FetchAsync(
            outputRoot, row.Accession, ticker, attribution.Cik, TimeProvider.System, CancellationToken.None)
            .ConfigureAwait(false);
        manifest[row.Accession] = manifestRow;
        fetched++;
        if (!manifestRow.IsSuccess)
        {
            failed++;
        }

        // Persist the manifest after every fetch so an interrupted run resumes where it stopped.
        ExhibitArchiver.WriteManifest(outputRoot, manifest.Values);
    }
}

// --- 6. Summary (stdout + summary.txt) ------------------------------------------------------------------
var conflicts = cohort.LegacyExclusions.Where(static e => e.OutcomeConflict).ToList();
var summary = new StringBuilder();
summary.AppendLine("Radar calibration audit — spec 162 Phase A (read-only; no scoring change, no fingerprint input)");
summary.AppendLine($"generatedAtUtc:      {DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}");
summary.AppendLine($"modelIdentity:       {modelIdentity}");
summary.AppendLine($"scopeSegment:        {derivedScope}  (PINNED — derived via the production AddFileAnalyzedFilingCache logic)");
summary.AppendLine($"cacheRoot:           {cacheRoot}");
summary.AppendLine($"outputRoot:          {outputRoot}");
summary.AppendLine($"maxInputLength:      {maxInputLength}");
summary.AppendLine();
summary.AppendLine($"cohort (model-scoped): {cohort.Rows.Count} records — {directional.Count} directional, {noSignal.Count} no-signal");
summary.AppendLine(
    directional.Count == ExpectedDirectionalCount && noSignal.Count == ExpectedNoSignalCount
        ? $"cohort counts match spec 162 expectations ({ExpectedDirectionalCount} directional + {ExpectedNoSignalCount} no-signal)."
        : $"COHORT COUNT DRIFT vs spec 162 expectations ({ExpectedDirectionalCount} directional + {ExpectedNoSignalCount} no-signal): "
            + "the cache has accrued since the spec was written — reported, not fatal; record the actual counts in the findings.");
summary.AppendLine($"legacy root files excluded (reason legacy-scope): {cohort.LegacyExclusions.Count}");
foreach (var ex in cohort.LegacyExclusions)
{
    summary.AppendLine($"  - {ex.Accession}: legacy={ex.LegacyOutcome}, active={ex.ActiveOutcome ?? "(not in scope)"}"
        + (ex.OutcomeConflict ? "  ** OUTCOME CONFLICT **" : string.Empty));
}

summary.AppendLine($"legacy/active outcome conflicts: {conflicts.Count}"
    + (conflicts.Count > 0 ? " — " + string.Join(", ", conflicts.Select(static c => c.Accession)) : string.Empty));
summary.AppendLine($"unreadable cache files: {cohort.UnreadableFiles.Count}"
    + (cohort.UnreadableFiles.Count > 0
        ? " — " + string.Join(", ", cohort.UnreadableFiles.Select(static u => $"{u.Accession} ({u.Location})"))
        : string.Empty));
summary.AppendLine($"raw filing evidence index: {evidenceIndex.Count} accessions indexed from {rawFilingRoot}");
summary.AppendLine($"unrecoverable accessions (no CIK; listed, never dropped): {unrecoverable.Count}"
    + (unrecoverable.Count > 0 ? " — " + string.Join(", ", unrecoverable.Select(static u => u.Accession)) : string.Empty));
summary.AppendLine();
summary.AppendLine($"worksheet (SEALED model answers): {worksheetPath}");
summary.AppendLine($"worksheet sha256: {worksheetSha}");
summary.AppendLine();
if (skipFetch)
{
    summary.AppendLine("exhibit fetch: SKIPPED (--skip-fetch); manifest untouched.");
}
else
{
    summary.AppendLine($"exhibit fetch: {fetched} fetched ({failed} failed), {skipped} already archived (manifest skip).");
}

var manifestSuccesses = manifest.Values.Count(static r => r.IsSuccess);
summary.AppendLine($"exhibit manifest: {manifest.Count} rows, {manifestSuccesses} archived successfully ({ExhibitArchiver.ManifestPath(outputRoot)})");

File.WriteAllText(Path.Combine(outputRoot, "summary.txt"), summary.ToString());
Console.Out.Write(summary.ToString());

return failed > 0 ? 1 : 0;
