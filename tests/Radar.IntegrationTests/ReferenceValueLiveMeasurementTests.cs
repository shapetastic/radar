using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.NewsRisk;
using Radar.Infrastructure.NewsTyping;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// Spec 215 §3 — the READ-ONLY LIVE distribution behind the reported-metrics ledger and the reference
/// projection (its version token is <see cref="ReferenceValueProjector.Version"/> — spec 216 §1 moved it,
/// and this harness renders whatever the code says rather than a literal) over the accrued store (CLAUDE.md's "no measure ships
/// without its live distribution"). The ledger is heal-forward, so at implementation time it is EMPTY by
/// construction; what this harness reports is the ground the ledger will fill and how fast:
/// <list type="bullet">
/// <item>analyzed-filing cache records, and how many carry a null <c>ReportedMetricsPolicy</c> (all, pre-215)
/// — read through the PRODUCTION <see cref="FileAnalyzedFilingCache"/>, root and model segment alike;</item>
/// <item>earnings-8-K reads per baseline run for the last five runs — the forward accrual rate — counted from
/// the spec-115 <c>ai-debug/filings</c> DEBUG records grouped by their <c>asOfUtc</c> (one group per run).
/// Stated plainly: that store keeps the LAST attempt per accession and has no production read seam, so a
/// filing re-read in a later run is counted under the later run only, and the records are deserialized
/// here with the production record type and the file store's JSON shape;</item>
/// <item>among Judged judgments, the share whose supplied families (resolved to typing statements through the
/// typing store, as the spec-214 harness does) name at least one ledger metric under the projector's table —
/// the projection's would-be hit rate on TODAY's facts — plus the per-metric naming counts;</item>
/// <item>the AGX 2026-09-07 judgment's cited facts, each with its <see cref="NewsFactComparisonBasis"/> and the
/// <see cref="ReportedMetric"/>(s) its statement names — i.e. which would receive a reference block once one
/// Argan release has been read.</item>
/// </list>
/// <para>
/// <b>Nothing is written.</b> Every store is opened for hydration only. ENV-GATED and skipped with a NAMED
/// reason otherwise (the spec-198/214 precedent): set <c>RADAR_REFERENCE_VALUE_LIVE_DATA_ROOT</c> to a Radar
/// data root holding <c>filings-cache/</c>, <c>ai-debug/filings/</c>, <c>news-typing/</c>, <c>news-risk/</c>
/// (and, once accrued, <c>reported-metrics/</c>). The output is markdown on the test log, ready to paste into
/// a PR body. Deterministic: fixed ordering, invariant formatting, no clock.
/// </para>
/// </summary>
public sealed class ReferenceValueLiveMeasurementTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_REFERENCE_VALUE_LIVE_DATA_ROOT";

    internal const string SkipReason =
        "Spec 215 §3 live reference-value distribution (reads a live Radar data root): set "
            + DataRootVariable + " to a Radar data root (filings-cache/, ai-debug/filings/, news-typing/, news-risk/) to run it.";

    /// <summary>The AGX 2026-09-07 judgment specs 214/215 open with (Improving on "backlog hits $2.5B").</summary>
    private static readonly Guid ArganJudgmentId = new("928eb9f8-380b-7838-e80d-ad9283cbf066");

    private const int RunsToReport = 5;

    /// <summary>
    /// The on-disk JSON shape of every Radar file store (camelCase, enums as names) — restated here ONLY for
    /// the debug store, which has no production reader; the production options type is Infrastructure-internal.
    /// </summary>
    private static readonly JsonSerializerOptions DebugRecordJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    [ReferenceValueLiveFact]
    public async Task LiveDistribution_OfTheLedgerGround_AndTheReferenceProjection()
    {
        var root = DataRoot()!;
        var ct = CancellationToken.None;

        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            // ---- 1. the analyzed-filing cache, through the production cache (root + every model segment) -
            var cacheRoot = Path.Combine(root, "filings-cache");
            var cacheRecords = 0;
            var cacheNullPolicy = 0;
            var cacheCurrentPolicy = 0;
            var cacheUnreadable = 0;
            var cacheByOutcome = new SortedDictionary<string, int>(StringComparer.Ordinal);
            if (Directory.Exists(cacheRoot))
            {
                var segments = new List<string> { string.Empty };
                segments.AddRange(Directory.EnumerateDirectories(cacheRoot).Select(Path.GetFileName)!
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Order(StringComparer.Ordinal)!);
                foreach (var segment in segments)
                {
                    var cache = new FileAnalyzedFilingCache(
                        new FileAnalyzedFilingCacheOptions { RootDirectory = cacheRoot, ModelSegment = segment },
                        NullLogger<FileAnalyzedFilingCache>.Instance);
                    var directory = segment.Length == 0 ? cacheRoot : Path.Combine(cacheRoot, segment);
                    foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                        .Order(StringComparer.Ordinal))
                    {
                        var accession = Path.GetFileNameWithoutExtension(file);
                        var record = await cache.TryGetAsync(accession, ct);
                        if (record is null)
                        {
                            cacheUnreadable++; // a stale-version or malformed file — a miss under production rules
                            continue;
                        }

                        cacheRecords++;
                        cacheByOutcome[record.Outcome.ToString()] = cacheByOutcome.GetValueOrDefault(record.Outcome.ToString()) + 1;
                        if (record.ReportedMetricsPolicy is null)
                        {
                            cacheNullPolicy++;
                        }
                        else if (string.Equals(record.ReportedMetricsPolicy, ReportedMetricsPolicy.Version, StringComparison.Ordinal))
                        {
                            cacheCurrentPolicy++;
                        }
                    }
                }
            }

            // ---- 2. earnings reads per run, from the debug store grouped by asOfUtc --------------------
            var debugRoot = Path.Combine(root, "ai-debug", "filings");
            var debugRecords = new List<FilingReadDebugRecord>();
            var debugUnreadable = 0;
            if (Directory.Exists(debugRoot))
            {
                foreach (var file in Directory.EnumerateFiles(debugRoot, "*.json", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.Ordinal))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<FilingReadDebugRecord>(
                            await File.ReadAllTextAsync(file, ct), DebugRecordJson);
                        if (parsed is null)
                        {
                            debugUnreadable++;
                            continue;
                        }

                        debugRecords.Add(parsed);
                    }
                    catch (JsonException)
                    {
                        debugUnreadable++;
                    }
                }
            }

            var runs = debugRecords
                .GroupBy(r => r.AsOfUtc)
                .OrderByDescending(g => g.Key)
                .Take(RunsToReport)
                .Select(g => (AsOf: g.Key, Total: g.Count(), ByOutcome: g.GroupBy(r => r.Outcome)
                    .OrderBy(o => o.Key)
                    .Select(o => $"{o.Key} {o.Count()}")
                    .ToList()))
                .ToList();

            // ---- 3. judgments whose supplied families name a ledger metric ---------------------------
            var typingStore = new FileNewsTypingStore(
                new FileNewsTypingStoreOptions { RootDirectory = Path.Combine(root, "news-typing") },
                NullLogger<FileNewsTypingStore>.Instance);
            var judgmentStore = new FileNewsJudgmentStore(
                new FileNewsJudgmentStoreOptions { RootDirectory = Path.Combine(root, "news-risk") },
                NullLogger<FileNewsJudgmentStore>.Instance);
            var typings = await typingStore.GetAllAsync(ct);
            var judgments = await judgmentStore.GetAllAsync(ct);

            var factsById = new Dictionary<Guid, NewsTypingValidatedFact>();
            foreach (var typing in typings.OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.TypingId))
            {
                foreach (var fact in typing.Facts)
                {
                    factsById.TryAdd(fact.FactId, fact);
                }
            }

            var judged = judgments
                .Where(j => j.Status == NewsJudgmentStatus.Judged)
                .OrderBy(j => j.CreatedAtUtc)
                .ThenBy(j => j.JudgmentId)
                .ToList();
            var judgedNamingAMetric = 0;
            var judgedWithUnresolvedFamily = 0;
            var judgedFamiliesTotal = 0;
            var familiesNamingAMetric = 0;
            var namedByMetric = new SortedDictionary<ReportedMetric, int>();
            var judgmentsByMetric = new SortedDictionary<ReportedMetric, int>();
            foreach (var judgment in judged)
            {
                var metricsForJudgment = new HashSet<ReportedMetric>();
                var unresolved = false;
                foreach (var family in judgment.Families)
                {
                    judgedFamiliesTotal++;
                    if (!factsById.TryGetValue(family.RepresentativeFactId, out var fact))
                    {
                        unresolved = true;
                        continue;
                    }

                    var named = ReferenceValueProjector.MetricsNamedIn(fact.Statement);
                    if (named.Count > 0)
                    {
                        familiesNamingAMetric++;
                    }

                    foreach (var metric in named)
                    {
                        namedByMetric[metric] = namedByMetric.GetValueOrDefault(metric) + 1;
                        metricsForJudgment.Add(metric);
                    }
                }

                if (unresolved)
                {
                    judgedWithUnresolvedFamily++;
                }

                if (metricsForJudgment.Count > 0)
                {
                    judgedNamingAMetric++;
                    foreach (var metric in metricsForJudgment)
                    {
                        judgmentsByMetric[metric] = judgmentsByMetric.GetValueOrDefault(metric) + 1;
                    }
                }
            }

            // ---- 4. the ledger itself (heal-forward: empty until the first post-215 read) -------------
            var ledgerRoot = Path.Combine(root, "reported-metrics");
            var ledgerCompanies = 0;
            var ledgerRecords = 0;
            if (Directory.Exists(ledgerRoot))
            {
                var ledger = new FileReportedMetricStore(
                    new FileReportedMetricStoreOptions { RootDirectory = ledgerRoot },
                    NullLogger<FileReportedMetricStore>.Instance);
                foreach (var directory in Directory.EnumerateDirectories(ledgerRoot).Order(StringComparer.Ordinal))
                {
                    if (!Guid.TryParse(Path.GetFileName(directory), out var companyId))
                    {
                        continue;
                    }

                    ledgerCompanies++;
                    ledgerRecords += (await ledger.GetForCompanyAsync(companyId, ct)).Count;
                }
            }

            // ---- 5. the AGX judgment, fact by fact -----------------------------------------------------
            var argan = judgments.FirstOrDefault(j => j.JudgmentId == ArganJudgmentId);

            // ---- render ------------------------------------------------------------------------------
            var report = new StringBuilder();
            report.AppendLine(
                "## Spec 215 §3 — live ground for the reported-metrics ledger and the reference projection ("
                    + ReferenceValueProjector.Version + ")");
            report.AppendLine();
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Data root: `{root}` · typing records {typings.Count} · distinct typed facts {factsById.Count} · "
                    + $"judgment records {judgments.Count} · Judged {judged.Count} · debug records {debugRecords.Count} "
                    + $"(unreadable {debugUnreadable})"));
            report.AppendLine();
            report.AppendLine("| measure | value |");
            report.AppendLine("| --- | ---: |");
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| analyzed-filing cache records (readable under production rules; stale-version/malformed misses {cacheUnreadable}) | {cacheRecords} |"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| … with null reportedMetricsPolicy (pre-215 = HIT, never re-read) | {cacheNullPolicy} ({Share(cacheNullPolicy, cacheRecords)}) |"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| … with reportedMetricsPolicy = {ReportedMetricsPolicy.Version} | {cacheCurrentPolicy} |"));
            foreach (var (outcome, count) in cacheByOutcome)
            {
                report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| … cache outcome {outcome} | {count} |"));
            }

            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| reported-metrics ledger: companies / records (heal-forward; empty until the first post-215 read) | {ledgerCompanies} / {ledgerRecords} |"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| Judged judgments whose supplied families name ≥ 1 ledger metric (would-be projection hit rate) | {judgedNamingAMetric} of {judged.Count} ({Share(judgedNamingAMetric, judged.Count)}) |"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| … supplied families naming ≥ 1 ledger metric | {familiesNamingAMetric} of {judgedFamiliesTotal} ({Share(familiesNamingAMetric, judgedFamiliesTotal)}) |"));
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| … Judged judgments with a supplied family not found in the typing store | {judgedWithUnresolvedFamily} |"));

            report.AppendLine();
            report.AppendLine("| earnings-8-K reads per baseline run (debug store, grouped by asOfUtc; LAST attempt per accession) | reads | by outcome |");
            report.AppendLine("| --- | ---: | --- |");
            foreach (var run in runs)
            {
                report.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {run.AsOf:yyyy-MM-ddTHH:mm:ssZ} | {run.Total} | {string.Join(", ", run.ByOutcome)} |"));
            }

            if (runs.Count == 0)
            {
                report.AppendLine("| (no debug records) | | |");
            }

            report.AppendLine();
            report.AppendLine("| ledger metric named in a supplied statement | families naming it | Judged judgments naming it |");
            report.AppendLine("| --- | ---: | ---: |");
            foreach (var metric in Enum.GetValues<ReportedMetric>())
            {
                report.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {metric} | {namedByMetric.GetValueOrDefault(metric)} | {judgmentsByMetric.GetValueOrDefault(metric)} |"));
            }

            report.AppendLine();
            report.AppendLine("### AGX judgment `928eb9f8-380b-7838-e80d-ad9283cbf066`, cited facts — which would receive a reference block once one Argan release has been read");
            report.AppendLine();
            if (argan is null)
            {
                report.AppendLine("Not present in this store.");
            }
            else
            {
                report.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Status {argan.Status} · trajectory {argan.BusinessTrajectory} · schema {argan.SchemaVersion} · "
                        + $"persisted trajectoryBasis {(argan.TrajectoryBasis is { } b ? b.ToString() : "null (pre-214)")} · "
                        + $"persisted referenceIds {(argan.ReferenceIds is { } r ? r.Count.ToString(CultureInfo.InvariantCulture) : "null (pre-215)")}"));
                report.AppendLine();
                report.AppendLine("| cited fact id | statement | ComparisonBasis | ledger metric(s) named | would receive a reference block |");
                report.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (var factId in argan.TrajectoryFactIds ?? [])
                {
                    if (!factsById.TryGetValue(factId, out var fact))
                    {
                        report.AppendLine(string.Create(
                            CultureInfo.InvariantCulture, $"| `{factId:D}` | (not in typing store) | | | |"));
                        continue;
                    }

                    var basis = StatementComparisonClassifier.Classify(fact.Statement, fact.EventTypes);
                    var named = ReferenceValueProjector.MetricsNamedIn(fact.Statement);
                    report.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"| `{factId:D}` | {fact.Statement.Replace("|", "\\|", StringComparison.Ordinal)} | {basis} | "
                            + $"{(named.Count == 0 ? "(none)" : string.Join(", ", named))} | "
                            + $"{(named.Count == 0 ? "no" : basis == NewsFactComparisonBasis.LevelOnly ? "yes — and could become ReferenceSupported" : "yes — as context beside a " + basis)} |"));
                }
            }

            output.WriteLine(report.ToString());

            // The measurement is the deliverable; these guard only that it MEASURED something, so an empty
            // or misconfigured root can never be reported as a result.
            Assert.True(cacheRecords > 0, "no analyzed-filing cache records were read — check the data root");
            Assert.True(judgments.Count > 0, "no judgment records were read — check the data root");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static string Share(int count, int total) =>
        total == 0 ? "n/a" : ((double)count / total).ToString("P1", CultureInfo.InvariantCulture);
}

/// <summary>Skips the spec-215 §3 live measurement with a named reason unless its data-root variable points at a directory.</summary>
public sealed class ReferenceValueLiveFactAttribute : FactAttribute
{
    public ReferenceValueLiveFactAttribute()
    {
        if (ReferenceValueLiveMeasurementTests.DataRoot() is null)
        {
            Skip = ReferenceValueLiveMeasurementTests.SkipReason;
        }
    }
}
