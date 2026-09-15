using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Acquisitions;
using Radar.Application.Collectors;
using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.EntityResolution;
using Radar.Application.Filings;
using Radar.Application.Prices;
using Radar.Application.Storage;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Acquisitions;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Sec;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// SPEC 227 §3 — the READ-ONLY LIVE MEASUREMENT of <c>acqscan-v1</c> against <c>acqscan-v2</c> over every
/// item-1.01 filing in the accrued store, side by side on the SAME fetched body, plus the efficacy effect of
/// the recognitions each version makes.
/// <para>
/// <b>Population and mentions are the production pass's own</b>: <see cref="AcquisitionRecognitionPass.FormCode"/>,
/// <see cref="AcquisitionRecognitionPass.ItemCode"/>, <see cref="FilingEvidenceFacts.TryResolve"/>, the
/// production <see cref="ICompanyResolver"/> and <see cref="AcquisitionRecognitionPass.BuildMentionIndex"/>.
/// Bodies come from the production <see cref="IAcquisitionFilingBodyReader"/> through the shared, globally
/// paced SEC client. v2 is the production <see cref="AcquisitionAgreementScan"/>; v1 is the frozen
/// <see cref="AcqScanV1HistoricalControl"/> (a measurement control, never edited).
/// </para>
/// <para>
/// (⚠ AMENDED in place by spec 228: the column this harness labels <c>acqscan-v2</c> runs the CURRENT production
/// scan, which spec 228 re-versioned to <c>acqscan-v3</c> without changing the rule, and its bodies come from the
/// production reader, which since spec 228 reads the real 8-K primary. Its 2026-09-14 measurement was taken over
/// the pre-228 read — an exhibit posing as the primary on 153 of 154 bodies. A re-run measures v1 against the current
/// rule over the NEW read unless the body directory still holds the spec-227 bodies. The read-before-and-after
/// measurement is <see cref="AcquisitionReadPrimary8KLiveMeasurementTests"/>. Since spec 229 the current rule is
/// <c>acqscan-v4</c>; its measurement against a frozen v3 is <see cref="AcquisitionScanV4LiveMeasurementTests"/>.)
/// </para>
/// <para>
/// <b>Each body is fetched ONCE.</b> When <c>RADAR_ACQSCAN_V2_BODY_DIR</c> is set (it must lie OUTSIDE the data
/// root), each successful body is kept there verbatim as <c>{accession}.txt</c> and each failed read as
/// <c>{accession}.failed</c> holding the reader's named detail, and a later run of this harness reads those
/// instead of issuing another www.sec.gov request — so the scan rules can be re-measured without re-fetching.
/// Without it every body is fetched live on every run.
/// </para>
/// <para>
/// <b>Nothing is written under the data root.</b> The acquisitions store and scan cache the recognition
/// registration composes are REPLACED by implementations whose writes THROW; the durable acquisitions store is
/// opened only through a read-only wrapper for the "before" arm; evidence writes throw. Every file under the
/// data root is snapshotted (path, length, last-write time) before and after, and the harness FAILS if any
/// changed. The report goes to the test log and to a per-process temp path.
/// </para>
/// <para>
/// <b>ENV-GATED</b>: <c>RADAR_ACQSCAN_V2_LIVE_DATA_ROOT</c> (a Radar data root) AND <c>RADAR_SEC_UA</c> (a compliant
/// SEC User-Agent, never committed). Skipped with a NAMED reason otherwise.
/// </para>
/// </summary>
public sealed class AcquisitionScanV2LiveMeasurementTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_ACQSCAN_V2_LIVE_DATA_ROOT";

    internal const string BodyDirectoryVariable = "RADAR_ACQSCAN_V2_BODY_DIR";

    internal const string UserAgentVariable = "RADAR_SEC_UA";

    internal const string SkipReason =
        "Spec 227 §3 live acqscan-v1 vs acqscan-v2 measurement (may issue REAL www.sec.gov requests): set "
            + DataRootVariable + " to a Radar data root AND " + UserAgentVariable
            + " to a compliant SEC User-Agent to run it (optionally " + BodyDirectoryVariable
            + " to fetch each body once).";

    private static readonly string ReportPath = Path.Combine(
        Path.GetTempPath(),
        "radar-spec-227-acqscan-v2-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".md");

    /// <summary>Phrases that, in ANY evidence item, suggest a Radar company may be the subject of a takeover (the recall check).</summary>
    private static readonly string[] TakeoverPhrases =
    [
        "to be acquired by", "agreement to be acquired", "agreed to be acquired", "to be taken private",
        "take-private", "go-private transaction",
    ];

    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    internal static string? UserAgent()
    {
        var ua = Environment.GetEnvironmentVariable(UserAgentVariable);
        return string.IsNullOrWhiteSpace(ua) ? null : ua.Trim();
    }

    [AcqScanV2LiveFact]
    public async Task LiveDistribution_AcqScanV1VersusV2_AndTheEfficacyEffect()
    {
        var root = Path.GetFullPath(DataRoot()!);
        var ct = CancellationToken.None;
        // SPEC 229: the shared path-BOUNDARY guard (this harness's own text-prefix check rejected C:\data-cache for
        // root C:\data).
        var bodyDirectory = LiveHarnessPaths.OutsideDataRoot(root, Environment.GetEnvironmentVariable(BodyDirectoryVariable), create: true);

        output.WriteLine($"Report path: {ReportPath}");
        var before = SnapshotTree(root);

        var sb = new StringBuilder();
        await using (var provider = BuildComposition(root))
        {
            await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);
            var companies = await provider.GetRequiredService<ICompanyRepository>().GetAllAsync(ct);
            await MeasureAsync(sb, provider, root, companies, bodyDirectory, ct);
        }

        var after = SnapshotTree(root);
        var changed = DiffTrees(before, after);
        sb.Append(CultureInfo.InvariantCulture, $"\n#### No-write confirmation\n\nEvery file under the data root was snapshotted (path, length, last-write UTC) before and after the harness: {before.Count} file(s) before, {after.Count} after, **{changed.Count} changed**.\n");
        foreach (var line in changed.Take(20))
        {
            sb.Append("- ").Append(line).Append('\n');
        }

        var report = sb.ToString();
        await File.WriteAllTextAsync(ReportPath, report, new UTF8Encoding(false), ct);
        output.WriteLine(report);
        output.WriteLine($"Report path: {ReportPath}");

        Assert.Empty(changed);
    }

    private async Task MeasureAsync(
        StringBuilder sb,
        ServiceProvider provider,
        string root,
        IReadOnlyList<Company> companies,
        string? bodyDirectory,
        CancellationToken ct)
    {
        var mentions = AcquisitionRecognitionPass.BuildMentionIndex(companies);
        var byId = companies.ToDictionary(c => c.Id);
        var resolver = provider.GetRequiredService<ICompanyResolver>();
        var reader = provider.GetRequiredService<IAcquisitionFilingBodyReader>();
        var evidence = await provider.GetRequiredService<IEvidenceRepository>().GetAllAsync(ct);

        var candidates = new List<(EvidenceItem Evidence, FilingEvidenceIdentifiers Identifiers)>();
        var untrustworthy = 0;
        foreach (var item in evidence)
        {
            if (FilingEvidenceFacts.TryResolve(
                    item,
                    AcquisitionRecognitionPass.FormCode,
                    AcquisitionRecognitionPass.ItemCode,
                    out var identifiers,
                    out var rejection))
            {
                candidates.Add((item, identifiers!));
            }
            else if (rejection is FilingEvidenceRejection.UnparseableSourceUrl
                or FilingEvidenceRejection.AccessionMismatch)
            {
                untrustworthy++;
            }
        }

        candidates.Sort(static (a, b) =>
        {
            var aWhen = a.Evidence.PublishedAtUtc ?? a.Evidence.CollectedAtUtc;
            var bWhen = b.Evidence.PublishedAtUtc ?? b.Evidence.CollectedAtUtc;
            var byWhen = aWhen.CompareTo(bWhen);
            return byWhen != 0 ? byWhen : a.Evidence.Id.CompareTo(b.Evidence.Id);
        });

        var rows = new List<ScanRow>();
        var unresolved = 0;
        var fetchFailed = new List<string>();
        var liveFetches = 0;
        var bodyCache = new Dictionary<string, AcquisitionFilingBody>(StringComparer.Ordinal);

        foreach (var (item, identifiers) in candidates)
        {
            var resolution = await resolver.ResolveAsync(item.Title, HintsOf(item), ct);
            if (resolution.CompanyId is not { } companyId || !byId.TryGetValue(companyId, out var company))
            {
                unresolved++;
                continue;
            }

            if (!bodyCache.TryGetValue(identifiers.Accession, out var body))
            {
                (body, var fetched) = await ReadBodyOnceAsync(reader, identifiers, bodyDirectory, ct);
                liveFetches += fetched ? 1 : 0;
                bodyCache[identifiers.Accession] = body;
            }

            if (!body.IsSuccess)
            {
                fetchFailed.Add(
                    $"{company.Ticker ?? company.Name} · {identifiers.Accession} · filed {(item.PublishedAtUtc ?? item.CollectedAtUtc):yyyy-MM-dd} · {body.Detail} · title: {Trim(item.Title, 100)}");
                continue;
            }

            var companyMentions = mentions.GetValueOrDefault(companyId, []);
            rows.Add(new ScanRow(
                item,
                identifiers,
                company,
                AcqScanV1HistoricalControl.Scan(body.PlainText, companyMentions),
                AcquisitionAgreementScan.Scan(body.PlainText, companyMentions)));
        }

        sb.Append("### Spec 227 §3 — live `acqscan-v1` vs `acqscan-v2` over every item-1.01 filing in the store\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Data root `{root}`. Bodies: {liveFetches} fetched live this run through the production reader (paced SEC client){(bodyDirectory is null ? "" : "; every other body read from the fetch-once body directory, written by an earlier run of this harness")}.\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Item-1.01 filing evidence items identified: **{candidates.Count}** (+{untrustworthy} with untrustworthy identifiers, never fetched); distinct accessions {candidates.Select(c => c.Identifiers.Accession).Distinct().Count()}.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Company unresolved (skipped before any fetch): {unresolved}.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Body read failed (NOT a \"no acquisition\" answer; the pass re-attempts these): {fetchFailed.Count}.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Scanned by BOTH versions on the identical body: **{rows.Count}**.\n\n");

        sb.Append("| outcome | acqscan-v1 | acqscan-v2 |\n| --- | ---: | ---: |\n");
        foreach (var outcome in AcquisitionAgreementScan.AllOutcomes)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {AcquisitionAgreementScan.Token(outcome)} | {rows.Count(r => r.V1.Outcome == outcome)} | {rows.Count(r => r.V2.Outcome == outcome)} |\n");
        }

        var changedRows = rows.Where(r => r.V1.Outcome != r.V2.Outcome).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### Filings whose outcome changed — {changedRows.Count}\n\n");
        if (changedRows.Count > 0)
        {
            sb.Append("| ticker | accession | filed | acqscan-v1 | acqscan-v2 |\n| --- | --- | --- | --- | --- |\n");
            foreach (var r in changedRows)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| {r.Company.Ticker ?? r.Company.Name} | {r.Identifiers.Accession} | {Filed(r.Evidence)} | {AcquisitionAgreementScan.Token(r.V1.Outcome)} | {AcquisitionAgreementScan.Token(r.V2.Outcome)} |\n");
            }
        }

        var recognisedRows = rows.Where(r => r.V1.IsRecognised || r.V2.IsRecognised).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### Every recognition under either version — {recognisedRows.Count}\n\n");
        foreach (var r in recognisedRows)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- **{r.Company.Name} ({r.Company.Ticker})** · {r.Identifiers.Accession} · filed {Filed(r.Evidence)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  - acqscan-v1: {Describe(r.V1)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  - acqscan-v2: {Describe(r.V2)}\n");
        }

        var nearMisses = rows.Where(r => r.V2.Outcome is AcquisitionScanOutcome.AcquirerNotNamed
            or AcquisitionScanOutcome.NoStatedConsideration
            || r.V1.Outcome is AcquisitionScanOutcome.AcquirerNotNamed or AcquisitionScanOutcome.NoStatedConsideration).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### Near misses (leg (a) held under either version; the acquirer name or leg (b) did not) — {nearMisses.Count}\n\n");
        foreach (var r in nearMisses)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- {r.Company.Ticker ?? r.Company.Name} · {r.Identifiers.Accession} · filed {Filed(r.Evidence)} · v1 {AcquisitionAgreementScan.Token(r.V1.Outcome)} · v2 {AcquisitionAgreementScan.Token(r.V2.Outcome)} · title: {Trim(r.Evidence.Title, 100)}\n");
        }

        // "acquisition of": v2 keeps it only with a direct object + "by {Acquirer}". What would REMOVING it
        // entirely lose? A v2 recognition whose target clause carries no OTHER target phrase leaned on it.
        var reliantOnAcquisitionOf = rows
            .Where(r => r.V2.IsRecognised && r.V2.TargetQuote is { } quote && !HasNonAcquisitionOfTargetPhrase(quote))
            .ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### What removing \"acquisition of\" would lose\n\nv2 recognitions whose target clause holds no target phrase OTHER than \"acquisition of\" (\"acquired by\", \"merge(d) with and into\"): **{reliantOnAcquisitionOf.Count}** of {rows.Count(r => r.V2.IsRecognised)}.");
        foreach (var r in reliantOnAcquisitionOf)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {r.Company.Ticker} {r.Identifiers.Accession};");
        }

        sb.Append('\n');

        if (fetchFailed.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n#### Body read failures — {fetchFailed.Count} (unscanned by either version)\n\n");
            foreach (var line in fetchFailed)
            {
                sb.Append("- ").Append(line).Append('\n');
            }
        }

        await AppendRecallCheckAsync(sb, evidence, resolver, byId, rows, fetchFailed, ct);
        await AppendEfficacyAsync(sb, root, rows, ct);

        Assert.Equal(candidates.Count, rows.Count + fetchFailed.Count + unresolved);
        Assert.All(rows, r => Assert.NotEqual(AcquisitionScanOutcome.VerbatimCheckFailed, r.V2.Outcome));
    }

    private static async Task<(AcquisitionFilingBody Body, bool FetchedLive)> ReadBodyOnceAsync(
        IAcquisitionFilingBodyReader reader,
        FilingEvidenceIdentifiers identifiers,
        string? bodyDirectory,
        CancellationToken ct)
    {
        string? bodyPath = null;
        string? failedPath = null;
        if (bodyDirectory is not null)
        {
            bodyPath = Path.Combine(bodyDirectory, identifiers.Accession + ".txt");
            failedPath = Path.Combine(bodyDirectory, identifiers.Accession + ".failed");
            if (File.Exists(bodyPath))
            {
                return (AcquisitionFilingBody.Success(await File.ReadAllTextAsync(bodyPath, ct)), false);
            }

            if (File.Exists(failedPath))
            {
                return (AcquisitionFilingBody.Failed(await File.ReadAllTextAsync(failedPath, ct)), false);
            }
        }

        var body = await reader.ReadAsync(identifiers.Cik, identifiers.Accession, identifiers.PrimaryDocument, ct);
        if (bodyPath is not null)
        {
            if (body.IsSuccess)
            {
                await File.WriteAllTextAsync(bodyPath, body.PlainText, new UTF8Encoding(false), ct);
            }
            else
            {
                await File.WriteAllTextAsync(failedPath!, body.Detail ?? "unspecified", new UTF8Encoding(false), ct);
            }
        }

        return (body, true);
    }

    /// <summary>
    /// The recall check: every Radar company that ANY evidence item describes with takeover language, and
    /// whether either version recognises an item-1.01 filing of that company. Listed for a human to judge —
    /// the phrase match is deliberately broad and says nothing on its own.
    /// </summary>
    private static async Task AppendRecallCheckAsync(
        StringBuilder sb,
        IReadOnlyList<EvidenceItem> evidence,
        ICompanyResolver resolver,
        Dictionary<Guid, Company> byId,
        List<ScanRow> rows,
        List<string> fetchFailed,
        CancellationToken ct)
    {
        var hits = new Dictionary<Guid, List<(EvidenceItem Item, string Phrase)>>();
        var unresolvedHits = 0;
        foreach (var item in evidence)
        {
            var text = (item.Title + "\n" + item.Summary + "\n" + item.RawText).ToLowerInvariant();
            var phrase = TakeoverPhrases.FirstOrDefault(p => text.Contains(p, StringComparison.Ordinal));
            if (phrase is null)
            {
                continue;
            }

            var resolution = await resolver.ResolveAsync(item.Title, HintsOf(item), ct);
            if (resolution.CompanyId is not { } companyId || !byId.ContainsKey(companyId))
            {
                unresolvedHits++;
                continue;
            }

            if (!hits.TryGetValue(companyId, out var list))
            {
                hits[companyId] = list = [];
            }

            list.Add((item, phrase));
        }

        sb.Append(CultureInfo.InvariantCulture, $"\n#### Recall check — Radar companies any evidence item describes with takeover language ({string.Join(", ", TakeoverPhrases.Select(p => $"\"{p}\""))})\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"{hits.Count} compan(ies) matched ({hits.Values.Sum(v => v.Count)} evidence item(s); +{unresolvedHits} matching item(s) whose company did not resolve). A match is a PROMPT for a human read, not a takeover.\n\n");
        sb.Append("| ticker | matching items | item-1.01 filings scanned | v1 recognised | v2 recognised | item-1.01 read failures | sample titles (newest first) |\n| --- | ---: | ---: | --- | --- | ---: | --- |\n");
        foreach (var (companyId, list) in hits.OrderByDescending(h => h.Value.Count).ThenBy(h => byId[h.Key].Ticker, StringComparer.Ordinal))
        {
            var company = byId[companyId];
            var companyRows = rows.Where(r => r.Company.Id == companyId).ToList();
            var ticker = company.Ticker ?? company.Name;
            var failures = fetchFailed.Count(f => f.StartsWith(ticker + " ·", StringComparison.Ordinal));
            var samples = list
                .OrderByDescending(h => h.Item.PublishedAtUtc ?? h.Item.CollectedAtUtc)
                .Take(3)
                .Select(h => $"{Filed(h.Item)} [{h.Item.SourceType}] {Trim(h.Item.Title, 90)} («{h.Phrase}»)");
            sb.Append(CultureInfo.InvariantCulture, $"| {ticker} | {list.Count} | {companyRows.Count} | {Recognised(companyRows, r => r.V1)} | {Recognised(companyRows, r => r.V2)} | {failures} | {Cell(string.Join(" / ", samples))} |\n");
        }
    }

    private static string Recognised(List<ScanRow> rows, Func<ScanRow, AcquisitionScanResult> version)
    {
        var recognised = rows.Where(r => version(r).IsRecognised).Select(r => r.Identifiers.Accession).ToList();
        return recognised.Count == 0 ? "no" : "YES " + string.Join(", ", recognised);
    }

    /// <summary>
    /// The efficacy effect, through the production comparison harness over ONE store, ONE set of series, ONE
    /// price side, ONE frozen benchmark: only the <see cref="PendingAcquisitions"/> projection differs.
    /// </summary>
    private static async Task AppendEfficacyAsync(
        StringBuilder sb, string root, List<ScanRow> rows, CancellationToken ct)
    {
        await using var provider = AcquisitionLeaderboardCounterfactualTests.BuildReadOnlyComposition(root);
        await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);
        var builder = provider.GetRequiredService<EfficacyDatasetBuilder>();
        var universe = await provider.GetRequiredService<IBenchmarkUniverseSource>().ReadAsync(ct);
        Assert.NotNull(universe);

        var series = await AcquisitionLeaderboardCounterfactualTests.LoadSeriesAsync(builder, root, ct);
        var bars = await AcquisitionLeaderboardCounterfactualTests.LoadBarsAsync(
            provider.GetRequiredService<IPriceHistoryStore>(), universe!, ct);

        // BEFORE — the live state until this slice merges: the durable acqscan-v1 records, admitted as v1 admitted
        // them. Read through a wrapper whose writes throw.
        var durable = new ReadOnlyAcquisitionStore(new FileAcquisitionStore(
            new FileAcquisitionStoreOptions { RootDirectory = Path.Combine(root, "acquisitions") },
            NullLogger<FileAcquisitionStore>.Instance));
        var durableRead = await durable.GetAllAsync(ct);
        var beforeArm = new PendingAcquisitions(durableRead, recognitionAvailable: true, AcqScanV1HistoricalControl.Version);

        // INTERIM — v2 shipped, rescans not yet persisted: every v1 record retired, nothing admitted.
        var interimArm = new PendingAcquisitions(durableRead);

        // AFTER — the recognitions acqscan-v2 makes on the same bodies, built exactly as the pass builds them.
        var v2Records = rows
            .Where(r => r.V2.IsRecognised)
            .Select(r => new PendingAcquisitionRecord(
                Id: PendingAcquisitionRecord.IdentityFor(AcquisitionAgreementScan.Version, r.Company.Id, r.Identifiers.Accession),
                CompanyId: r.Company.Id,
                Accession: r.Identifiers.Accession,
                EvidenceId: r.Evidence.Id,
                AnnouncedOnUtc: r.Evidence.PublishedAtUtc ?? r.Evidence.CollectedAtUtc,
                AcquirerName: r.V2.AcquirerName!,
                ConsiderationPerShare: r.V2.ConsiderationPerShare!,
                ConsiderationCurrency: r.V2.ConsiderationCurrency!,
                ConsiderationKind: r.V2.ConsiderationKind!.Value,
                ConsiderationQuote: r.V2.ConsiderationQuote!,
                TargetQuote: r.V2.TargetQuote!,
                ScanVersion: AcquisitionAgreementScan.Version,
                Verification: AcquisitionVerification.Verbatim))
            .ToList();
        var afterArm = new PendingAcquisitions(new AcquisitionStoreReadResult(v2Records, 0));

        var options = StrategyComparisonOptions.Default;
        var harness = new StrategyComparisonHarness();
        var arms = new (string Name, PendingAcquisitions Projection)[]
        {
            ("BEFORE (acqscan-v1 records)", beforeArm),
            ("INTERIM (v2 shipped, v1 retired, no rescan yet)", interimArm),
            ("AFTER (acqscan-v2 recognitions)", afterArm),
        };
        var boards = arms
            .Select(a => (a.Name, a.Projection, Board: harness.Compare(
                series, options, new UniverseBenchmark(universe!, bars, a.Projection), a.Projection)))
            .ToList();

        var lead = ReadDeclaredLead(root);
        sb.Append("\n#### Efficacy effect — pooled benchmark-adjusted leaderboard, same store, only the acquisitions projection differs\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Horizon {options.ForwardHorizonDays}d · exit tolerance {options.ExitToleranceDays}d · hold-out {options.HoldOutFraction:P0} · minimum observations {options.MinimumObservations} (`StrategyComparisonOptions.Default`, the leaderboard's windows) · `{UniverseBenchmark.ExcessRuleVersion}` + `{ObservationEligibility.Version}`. Declared Lead arm (latest `Lead` call in `strategy-operating-calls.json`): `{lead ?? "(none declared)"}`.\n\n");
        foreach (var (name, projection, board) in boards)
        {
            var excludedDays = board.Benchmark?.Days.Count(d => d.PendingAcquisitionExcludedMembers > 0);
            sb.Append(CultureInfo.InvariantCulture, $"- {name}: pending companies {projection.Records.Count} ({string.Join(", ", projection.Records.Select(r => $"{r.Accession} from {r.AnnouncedOnUtc:yyyy-MM-dd}"))}); retired by scan version {projection.RetiredByScanVersion}; benchmark as-of dates with a member removed from the peer mean: {(excludedDays is { } d ? d.ToString(CultureInfo.InvariantCulture) : "n/a")} of {board.Benchmark?.Days.Count.ToString(CultureInfo.InvariantCulture) ?? "n/a"}; as-of dates {board.Windows.TotalAsOfDates} (in {board.Windows.InSampleAsOfDates} / oos {board.Windows.OutOfSampleAsOfDates}).\n");
        }

        sb.Append("\n| strategy | arm | rank | in-sample rho [95% CI] | in obs | oos rho [95% CI] | oos obs | CorporateActionInWindow |\n| --- | --- | --- | --- | ---: | --- | ---: | ---: |\n");
        var names = new[] { lead, "default" }.Where(n => n is not null).Distinct(StringComparer.Ordinal);
        foreach (var strategy in names)
        {
            foreach (var (name, _, board) in boards)
            {
                var row = board.Rows.FirstOrDefault(r => r.StrategyName == strategy);
                var dropped = board.DroppedStrategies.FirstOrDefault(d => d.StrategyName == strategy);
                sb.Append(CultureInfo.InvariantCulture, $"| {strategy} | {name} | {AcquisitionLeaderboardCounterfactualTests.Rank(row)}{(row is null && dropped is not null ? $" ({dropped.Reason})" : "")} | {AcquisitionLeaderboardCounterfactualTests.Rho(row?.InSample)} [{AcquisitionLeaderboardCounterfactualTests.Ci(row?.InSample)}] | {AcquisitionLeaderboardCounterfactualTests.Obs(row?.InSample)} | {AcquisitionLeaderboardCounterfactualTests.Rho(row?.OutOfSample)} [{AcquisitionLeaderboardCounterfactualTests.Ci(row?.OutOfSample)}] | {AcquisitionLeaderboardCounterfactualTests.Obs(row?.OutOfSample)} | {row?.ObservationsCorporateActionInWindow.ToString(CultureInfo.InvariantCulture) ?? "—"} |\n");
            }
        }

        sb.Append('\n');
        foreach (var (name, _, board) in boards)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Ordering {name}: {(board.Rows.Count == 0 ? "(nothing ranked)" : string.Join(" > ", board.Rows.Select(r => r.StrategyName)))}\n\n");
        }
    }

    private static string? ReadDeclaredLead(string root)
    {
        var path = Path.Combine(root, "strategy-operating-calls.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("calls").EnumerateArray()
            .Where(c => c.GetProperty("call").GetString() == "Lead")
            .OrderBy(c => c.GetProperty("asOfUtc").GetDateTimeOffset())
            .Select(c => c.GetProperty("strategy").GetString())
            .LastOrDefault();
    }

    private static bool HasNonAcquisitionOfTargetPhrase(string quote)
    {
        var lower = quote.ToLowerInvariant();
        return new[] { "acquired by", "merge with and into", "merged with and into" }
            .Any(p => lower.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// The read-only composition (spec 227; reused by the spec-228 §3 harness): the production population, resolver,
    /// mentions and paced SEC reader, with every write seam replaced by one that THROWS. <paramref name="configure"/>
    /// runs last, before the provider is built.
    /// </summary>
    internal static ServiceProvider BuildComposition(string root, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        services.AddLocalFileCompanySeed(Path.Combine(root, "companies.json"));
        services.AddFileRawEvidenceStore(Path.Combine(root, "evidence", "raw"));
        services.AddFileSignalStore(Path.Combine(root, "signals"));

        // The repository IS the durable file store (spec 142) — without this the in-memory repository answers
        // "no evidence at all" and the measurement would describe an empty population.
        services.AddDurableRadarSignalHistory();

        services.AddRadarAcquisitionRecognition(
            new SecCollectorOptions { UserAgent = UserAgent()! },
            new FileAcquisitionStoreOptions
            {
                RootDirectory = Path.Combine(Path.GetTempPath(), "radar-spec227-unused-acquisitions"),
            },
            new FileAcquisitionScanCacheOptions
            {
                RootDirectory = Path.Combine(Path.GetTempPath(), "radar-spec227-unused-cache"),
            });

        // Every write seam the composition holds THROWS: the harness reads, it never records.
        services.RemoveAll<IAcquisitionStore>();
        services.AddSingleton<IAcquisitionStore, ThrowingAcquisitionStore>();
        services.RemoveAll<IAcquisitionScanCache>();
        services.AddSingleton<IAcquisitionScanCache, ThrowingScanCache>();
        var evidenceFactory = services.Last(d => d.ServiceType == typeof(IEvidenceRepository)).ImplementationFactory!;
        services.RemoveAll<IEvidenceRepository>();
        services.AddSingleton<IEvidenceRepository>(sp =>
            new ReadOnlyEvidenceRepository((IEvidenceRepository)evidenceFactory(sp)));

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    internal static Dictionary<string, (long Length, DateTime LastWriteUtc)> SnapshotTree(string root) =>
        new DirectoryInfo(root)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .ToDictionary(f => f.FullName, f => (f.Length, f.LastWriteTimeUtc), StringComparer.OrdinalIgnoreCase);

    internal static List<string> DiffTrees(
        Dictionary<string, (long Length, DateTime LastWriteUtc)> before,
        Dictionary<string, (long Length, DateTime LastWriteUtc)> after)
    {
        var changed = new List<string>();
        foreach (var (path, meta) in after)
        {
            if (!before.TryGetValue(path, out var old))
            {
                changed.Add("ADDED " + path);
            }
            else if (old != meta)
            {
                changed.Add("MODIFIED " + path);
            }
        }

        changed.AddRange(before.Keys.Where(p => !after.ContainsKey(p)).Select(p => "REMOVED " + p));
        return changed;
    }

    private static string Describe(AcquisitionScanResult result) =>
        result.IsRecognised
            ? $"recognised · acquirer `{result.AcquirerName}` · consideration `{result.ConsiderationCurrency}{result.ConsiderationPerShare}` ({result.ConsiderationKind}) · consideration quote: {Trim(result.ConsiderationQuote!, 200)} · target quote (tail): {Tail(result.TargetQuote!, 200)}"
            : AcquisitionAgreementScan.Token(result.Outcome);

    internal static string Filed(EvidenceItem item) =>
        (item.PublishedAtUtc ?? item.CollectedAtUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static IReadOnlyList<string> HintsOf(EvidenceItem evidence)
    {
        EvidenceMetadata.TryRead(evidence.MetadataJson, out _, out var hints);
        return hints;
    }

    internal static string Flatten(string text) =>
        string.Join(' ', text.Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

    internal static string Trim(string text, int max)
    {
        var flat = Flatten(text);
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    internal static string Tail(string text, int max)
    {
        var flat = Flatten(text);
        return flat.Length <= max ? flat : "…" + flat[^max..];
    }

    internal static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    private sealed record ScanRow(
        EvidenceItem Evidence,
        FilingEvidenceIdentifiers Identifiers,
        Company Company,
        AcquisitionScanResult V1,
        AcquisitionScanResult V2);

    private sealed class ThrowingAcquisitionStore : IAcquisitionStore
    {
        public Task<DurableWriteResult> WriteIfNewAsync(PendingAcquisitionRecord record, CancellationToken ct) =>
            throw new InvalidOperationException("The spec-227 §3 harness is read-only and must never write an acquisition.");

        public Task<AcquisitionStoreReadResult> GetAllAsync(CancellationToken ct) =>
            throw new InvalidOperationException("The spec-227 §3 harness reads the durable store only through its explicit read-only wrapper.");

        public Task<bool> ExistsAsync(Guid companyId, string accession, string scanVersion, CancellationToken ct) =>
            throw new InvalidOperationException("The spec-227 §3 harness never consults the recognition store.");
    }

    private sealed class ThrowingScanCache : IAcquisitionScanCache
    {
        public Task<AcquisitionScanCacheRecord?> TryGetAsync(string accession, CancellationToken ct) =>
            throw new InvalidOperationException("The spec-227 §3 harness scans every body; it never consults the scan cache.");

        public Task<bool> SetAsync(AcquisitionScanCacheRecord record, CancellationToken ct) =>
            throw new InvalidOperationException("The spec-227 §3 harness is read-only and must never write the scan cache.");
    }

    private sealed class ReadOnlyAcquisitionStore(IAcquisitionStore inner) : IAcquisitionStore
    {
        public Task<DurableWriteResult> WriteIfNewAsync(PendingAcquisitionRecord record, CancellationToken ct) =>
            throw new InvalidOperationException("The spec-227 §3 harness is read-only and must never write an acquisition.");

        public Task<AcquisitionStoreReadResult> GetAllAsync(CancellationToken ct) => inner.GetAllAsync(ct);

        public Task<bool> ExistsAsync(Guid companyId, string accession, string scanVersion, CancellationToken ct) =>
            inner.ExistsAsync(companyId, accession, scanVersion, ct);
    }

    private sealed class ReadOnlyEvidenceRepository(IEvidenceRepository inner) : IEvidenceRepository
    {
        public Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct) =>
            throw new InvalidOperationException("The spec-227 §3 harness is read-only and must never write evidence.");

        public Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct) => inner.GetByIdAsync(id, ct);

        public Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct) =>
            inner.GetByContentHashAsync(contentHash, ct);

        public Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct) => inner.GetAllAsync(ct);
    }
}

/// <summary>
/// Runs the spec-227 §3 measurement only when BOTH a data root and a compliant SEC User-Agent are configured;
/// otherwise skips with a named reason.
/// </summary>
public sealed class AcqScanV2LiveFactAttribute : FactAttribute
{
    public AcqScanV2LiveFactAttribute()
    {
        if (AcquisitionScanV2LiveMeasurementTests.DataRoot() is null
            || AcquisitionScanV2LiveMeasurementTests.UserAgent() is null)
        {
            Skip = AcquisitionScanV2LiveMeasurementTests.SkipReason;
        }
    }
}
