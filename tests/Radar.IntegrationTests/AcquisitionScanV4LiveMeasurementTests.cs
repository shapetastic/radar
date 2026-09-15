using System.Globalization;
using System.Net;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Acquisitions;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Infrastructure.Sec;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// SPEC 229 §2 — the READ-ONLY LIVE MEASUREMENT of <c>acqscan-v3</c> against <c>acqscan-v4</c> over every item-1.01
/// filing in the store: the tally, every changed filing with the clause that decided each side, every recognition with
/// and without the EX-2.1 merger agreement, the EX-2.1 append decision and its request cost, and the recall probe
/// (every filing whose body carries an explicit merger phrase that v4 does not recognise).
/// <para>
/// <b>Population and mentions are the production pass's own</b> (<see cref="ItemOneOhOnePopulation"/>). v4 is the
/// production <see cref="AcquisitionAgreementScan"/> over the production <see cref="IAcquisitionFilingBodyReader"/>'s
/// body. v3 is the frozen <see cref="AcqScanV3HistoricalControl"/> over the spec-228 read (primary → EX-99.1 → EX-2.1),
/// rebuilt from the same responses with the production selectors and normalizer. The harness also rebuilds the body
/// the OTHER way round from the EX-2.1 decision and asserts the production body is exactly one of the two, so the
/// with/without columns differ from production by the agreement and nothing else.
/// </para>
/// <para>
/// <b>Documents come from a cache first.</b> <c>RADAR_ACQSCAN_V4_SEED_CACHE_DIR</c> (read-only — spec 228's document
/// cache) is consulted for every Archives URL; only a URL it lacks goes over the wire, through the shared paced SEC
/// client after a 200 check on <c>www.sec.gov</c>, and is kept in <c>RADAR_ACQSCAN_V4_DOCUMENT_CACHE_DIR</c>. Both lie
/// OUTSIDE the data root (<see cref="LiveHarnessPaths"/>). The report says how many requests were cached and how many live.
/// </para>
/// <para>
/// <b>Nothing is written under the data root.</b> The composition's store / cache / evidence writes THROW (spec 227's
/// read-only composition), and every file under the data root is snapshotted (path, length, last-write time) before
/// and after; the harness FAILS if any changed. The report goes to the test log and a per-process temp path, and a
/// second temp file holds each company-is-acquirer filing's Item 1.01 narrative for the hand check.
/// </para>
/// <para>
/// <b>ENV-GATED</b>: <c>RADAR_ACQSCAN_V4_LIVE_DATA_ROOT</c> AND <c>RADAR_SEC_UA</c>. Skipped with a named reason otherwise.
/// </para>
/// </summary>
public sealed class AcquisitionScanV4LiveMeasurementTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_ACQSCAN_V4_LIVE_DATA_ROOT";

    internal const string SeedCacheVariable = "RADAR_ACQSCAN_V4_SEED_CACHE_DIR";

    internal const string DocumentCacheVariable = "RADAR_ACQSCAN_V4_DOCUMENT_CACHE_DIR";

    internal const string SkipReason =
        "Spec 229 §2 live acqscan-v3 vs acqscan-v4 measurement (may issue REAL www.sec.gov requests for documents no cache holds): set "
            + DataRootVariable + " to a Radar data root AND " + AcquisitionScanV2LiveMeasurementTests.UserAgentVariable
            + " to a compliant SEC User-Agent (optionally " + SeedCacheVariable + " to spec 228's document cache and "
            + DocumentCacheVariable + " to keep what this run fetches).";

    private static readonly string Pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    private static readonly string ReportPath = Path.Combine(Path.GetTempPath(), "radar-spec-229-acqscan-v4-" + Pid + ".md");

    private static readonly string NarrativePath = Path.Combine(Path.GetTempPath(), "radar-spec-229-acqscan-v4-narratives-" + Pid + ".md");

    /// <summary>The explicit merger phrases (spec 229 rule 1's list) the recall probe keys on.</summary>
    private static readonly string[] ExplicitMergerPhrases = ["agreement and plan of merger", "merger agreement", "plan of merger"];

    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    [AcqScanV4LiveFact]
    public async Task LiveDistribution_AcqScanV3VersusV4_TruthTableInputs_AppendDecision_AndRecallProbe()
    {
        var root = Path.GetFullPath(DataRoot()!);
        var ct = CancellationToken.None;
        var seedCache = LiveHarnessPaths.OutsideDataRoot(root, Environment.GetEnvironmentVariable(SeedCacheVariable), create: false);
        var cacheDirectory = LiveHarnessPaths.OutsideDataRoot(root, Environment.GetEnvironmentVariable(DocumentCacheVariable), create: true);

        output.WriteLine($"Report path: {ReportPath}");
        var before = AcquisitionScanV2LiveMeasurementTests.SnapshotTree(root);

        var state = new SecResponseLog(cacheDirectory, seedCache is null ? [] : [seedCache]);
        var sb = new StringBuilder();
        var narratives = new StringBuilder();
        try
        {
            await using var provider = AcquisitionScanV2LiveMeasurementTests.BuildComposition(root, services =>
                services.AddHttpClient(nameof(IAcquisitionFilingBodyReader))
                    .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Insert(0, new RecordingCacheHandler(state))));
            var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IAcquisitionFilingBodyReader));
            using (var probe = await client.GetAsync(new Uri("https://www.sec.gov/"), ct))
            {
                Assert.True(
                    probe.StatusCode == HttpStatusCode.OK,
                    $"www.sec.gov answered {(int)probe.StatusCode} to the pre-run check; not measuring against a blocked host.");
            }

            await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);
            var companies = await provider.GetRequiredService<ICompanyRepository>().GetAllAsync(ct);
            await MeasureAsync(sb, narratives, provider, client, state, root, companies, seedCache, cacheDirectory, ct);
        }
        finally
        {
            var after = AcquisitionScanV2LiveMeasurementTests.SnapshotTree(root);
            var changed = AcquisitionScanV2LiveMeasurementTests.DiffTrees(before, after);
            sb.Append(CultureInfo.InvariantCulture, $"\n#### No-write confirmation\n\nEvery file under the data root was snapshotted (path, length, last-write UTC) before and after the harness: {before.Count} file(s) before, {after.Count} after, **{changed.Count} changed**.\n");
            foreach (var line in changed.Take(20))
            {
                sb.Append("- ").Append(line).Append('\n');
            }

            var report = sb.ToString();
            await File.WriteAllTextAsync(ReportPath, report, new UTF8Encoding(false), ct);
            await File.WriteAllTextAsync(NarrativePath, narratives.ToString(), new UTF8Encoding(false), ct);
            output.WriteLine(report);
            output.WriteLine($"Report path: {ReportPath}");
            output.WriteLine($"Narratives path: {NarrativePath}");

            Assert.Empty(changed);
        }
    }

    private static async Task MeasureAsync(
        StringBuilder sb,
        StringBuilder narratives,
        ServiceProvider provider,
        HttpClient client,
        SecResponseLog state,
        string root,
        IReadOnlyList<Radar.Domain.Companies.Company> companies,
        string? seedCache,
        string? cacheDirectory,
        CancellationToken ct)
    {
        var reader = provider.GetRequiredService<IAcquisitionFilingBodyReader>();
        var normalizer = provider.GetRequiredService<IEvidenceNormalizer>();
        var population = await ItemOneOhOnePopulation.LoadAsync(provider, companies, ct);

        var rows = new List<Row>();
        foreach (var filing in population.Filings)
        {
            rows.Add(await MeasureFilingAsync(client, reader, normalizer, state, filing, ct));
        }

        Render(sb, root, population, rows, state, seedCache, cacheDirectory);
        RenderNarratives(narratives, rows);

        Assert.Equal(population.CandidateItems, population.Filings.Count + population.Unresolved + population.DuplicateItems);
        Assert.All(rows, r => Assert.True(r.Body.IsSuccess, $"The production read of {r.Filing.Identifiers.Accession} failed: {r.Body.Detail}"));
        Assert.All(rows, r => Assert.True(r.ProductionBodyIs is not null, $"The production body for {r.Filing.Identifiers.Accession} is neither primary → EX-99.1 nor primary → EX-99.1 → EX-2.1 as rebuilt."));
        Assert.All(rows, r => Assert.NotEqual(AcquisitionScanOutcome.VerbatimCheckFailed, r.V4?.Outcome));

        var hzo = Assert.Single(rows, r => r.Filing.Identifiers.Accession == AcquisitionReadPrimary8KLiveMeasurementTests.MarineMaxMergerAccession);
        Assert.True(hzo.V4?.IsRecognised == true, $"HZO must stay recognised under v4; it was {Token(hzo.V4)}.");
        Assert.Equal("53.00", hzo.V4!.ConsiderationPerShare);
        var shoo = Assert.Single(rows, r => r.Filing.Identifiers.Accession == AcquisitionReadPrimary8KLiveMeasurementTests.SteveMaddenAccession);
        Assert.False(shoo.V4?.IsRecognised == true, "SHOO must stay unrecognised under v4.");
    }

    private static async Task<Row> MeasureFilingAsync(
        HttpClient client,
        IAcquisitionFilingBodyReader reader,
        IEvidenceNormalizer normalizer,
        SecResponseLog state,
        ItemOneOhOneFiling filing,
        CancellationToken ct)
    {
        var identifiers = filing.Identifiers;
        var requestsBefore = state.Requests;
        var liveBefore = state.LiveRequests;
        var body = await reader.ReadAsync(identifiers.Cik, identifiers.Accession, identifiers.PrimaryDocument, ct);
        var readerRequests = state.Requests - requestsBefore;
        var readerLive = state.LiveRequests - liveBefore;

        // The two reads, rebuilt from the same (cached) responses with the production selectors and normalizer.
        var baseUrl = SecEdgarUrls.BuildArchiveBaseUrl(identifiers.Cik.Trim(), identifiers.Accession.Trim());
        var indexHtml = await GetStringOrNullAsync(client, SecEdgarUrls.BuildIndexUrl(identifiers.Cik.Trim(), identifiers.Accession.Trim(), ".html"), ct);
        var indexRows = indexHtml is null ? [] : SecFilingIndexTable.Parse(indexHtml);
        var selection = SecFilingIndexTable.SelectPrimaryDocument(indexRows, identifiers.PrimaryDocument);
        var ex99 = SecFilingIndexTable.SelectEarningsExhibit(indexRows);
        var agreement = SecFilingIndexTable.SelectMergerAgreementExhibit(indexRows);

        string? primaryText = null;
        string? withoutAgreement = null;
        string? withAgreement = null;
        if (selection.Row is { } primaryRow)
        {
            primaryText = await NormalizedOrNullAsync(client, normalizer, $"{baseUrl}/{primaryRow.FileName}", ct);
            var ex99Text = ex99 is null ? null : await NormalizedOrNullAsync(client, normalizer, $"{baseUrl}/{ex99.FileName}", ct);
            var agreementText = agreement is null ? null : await NormalizedOrNullAsync(client, normalizer, $"{baseUrl}/{agreement.FileName}", ct);
            if (primaryText is not null && (ex99 is null || ex99Text is not null) && (agreement is null || agreementText is not null))
            {
                withoutAgreement = ex99Text is null ? primaryText : primaryText + "\n" + ex99Text;
                withAgreement = agreementText is null ? withoutAgreement : withoutAgreement + "\n" + agreementText;
            }
        }

        string? productionBodyIs = null;
        if (body.IsSuccess && withoutAgreement is not null)
        {
            productionBodyIs = string.Equals(body.PlainText, withAgreement, StringComparison.Ordinal) ? "with EX-2.1"
                : string.Equals(body.PlainText, withoutAgreement, StringComparison.Ordinal) ? "without EX-2.1"
                : null;
            if (agreement is null && productionBodyIs is not null)
            {
                productionBodyIs = "no EX-2.1 shown";
            }
        }

        var v3 = withAgreement is null ? null : AcqScanV3HistoricalControl.Scan(withAgreement, filing.Mentions);
        var v4 = body.IsSuccess ? AcquisitionAgreementScan.Scan(body.PlainText, filing.Mentions) : null;
        var v4With = withAgreement is null ? null : AcquisitionAgreementScan.Scan(withAgreement, filing.Mentions);
        var v4Without = withoutAgreement is null ? null : AcquisitionAgreementScan.Scan(withoutAgreement, filing.Mentions);
        var mergerPhraseCount = body.IsSuccess ? CountMergerPhrases(body.PlainText) : 0;

        return new Row(
            filing, body, readerRequests, readerLive, ex99 is not null, agreement?.FileName, productionBodyIs,
            v3, v4, v4With, v4Without, mergerPhraseCount, primaryText);
    }

    private static void Render(
        StringBuilder sb,
        string root,
        ItemOneOhOnePopulation population,
        List<Row> rows,
        SecResponseLog state,
        string? seedCache,
        string? cacheDirectory)
    {
        var shipped = rows.Select(r => r.ProductionBodyIs).FirstOrDefault(s => s is "with EX-2.1" or "without EX-2.1") ?? "(no EX-2.1 filing in the population)";
        sb.Append("### Spec 229 §2 — `acqscan-v3` vs `acqscan-v4` over every item-1.01 filing in the store\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Data root `{root}`, read-only. v4: the production `AcquisitionAgreementScan` ({AcquisitionAgreementScan.Version}) over the production `HttpSecAcquisitionFilingReader` body, which for an EX-2.1 filing is **{shipped}**. v3: the frozen `AcqScanV3HistoricalControl` ({AcqScanV3HistoricalControl.Version}) over the spec-228 read (primary → EX-99.1 → EX-2.1), rebuilt from the same responses. SEC Archives requests this run (production reader and rebuilds together): {state.Requests} issued, **{state.LiveRequests} fetched live over the wire** through the shared paced SEC client, {state.Requests - state.LiveRequests} served from {(seedCache is null ? "" : "spec 228's read-only document cache")}{(seedCache is not null && cacheDirectory is not null ? " or " : "")}{(cacheDirectory is null ? "" : "this harness's own document cache")} outside the data root.\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Item-1.01 filing evidence items: **{population.CandidateItems}** (+{population.Untrustworthy} untrustworthy identifiers, never fetched); unresolved company {population.Unresolved}; second evidence items for an already-measured accession {population.DuplicateItems}; **accessions measured {rows.Count}**.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Production read: success {rows.Count(r => r.Body.IsSuccess)}, failed {rows.Count(r => !r.Body.IsSuccess)}. Production body is the rebuild {string.Join(", ", rows.GroupBy(r => r.ProductionBodyIs ?? "(MISMATCH)").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"))}.\n\n");

        sb.Append("#### Tally — `acqscan-v3` vs `acqscan-v4`\n\n| outcome | acqscan-v3 | acqscan-v4 |\n| --- | ---: | ---: |\n");
        foreach (var outcome in AcquisitionAgreementScan.AllOutcomes)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {AcquisitionAgreementScan.Token(outcome)} | {rows.Count(r => r.V3?.Outcome == outcome)} | {rows.Count(r => r.V4?.Outcome == outcome)} |\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"| (not scanned — read failed) | {rows.Count(r => r.V3 is null)} | {rows.Count(r => r.V4 is null)} |\n");

        var changed = rows.Where(r => Token(r.V3) != Token(r.V4)).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### Filings whose outcome changed — {changed.Count}\n\n");
        if (changed.Count > 0)
        {
            sb.Append("| ticker | accession | filed | acqscan-v3 | v3 deciding clause | acqscan-v4 | v4 deciding clause |\n| --- | --- | --- | --- | --- | --- | --- |\n");
            foreach (var r in changed)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| {Ticker(r)} | {r.Filing.Identifiers.Accession} | {Filed(r)} | {Token(r.V3)} | {Clause(r.V3)} | {Token(r.V4)} | {Clause(r.V4)} |\n");
            }
        }

        var acquirerLabelled = rows.Where(r => r.V3?.Outcome == AcquisitionScanOutcome.CompanyIsAcquirer || r.V4?.Outcome == AcquisitionScanOutcome.CompanyIsAcquirer).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### Truth-table inputs — every filing labelled company-is-acquirer under either version — {acquirerLabelled.Count} (v3 {rows.Count(r => r.V3?.Outcome == AcquisitionScanOutcome.CompanyIsAcquirer)}, v4 {rows.Count(r => r.V4?.Outcome == AcquisitionScanOutcome.CompanyIsAcquirer)})\n\n(The harness cannot judge truth. \"Is the filer genuinely acquiring something\" is a human read of the Item 1.01 narrative, written to the narratives file.)\n\n");
        sb.Append("| ticker | accession | filed | explicit merger phrases | acqscan-v3 | v3 deciding clause | acqscan-v4 | v4 deciding clause |\n| --- | --- | --- | ---: | --- | --- | --- | --- |\n");
        foreach (var r in acquirerLabelled)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {Ticker(r)} | {r.Filing.Identifiers.Accession} | {Filed(r)} | {r.MergerPhraseCount} | {Token(r.V3)} | {Clause(r.V3)} | {Token(r.V4)} | {Clause(r.V4)} |\n");
        }

        var recognised = rows.Where(r => r.V3?.IsRecognised == true || r.V4?.IsRecognised == true || r.V4With?.IsRecognised == true || r.V4Without?.IsRecognised == true).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### Every recognition under v3, v4, v4 with EX-2.1 or v4 without it — {recognised.Count}\n\n");
        foreach (var r in recognised)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- **{r.Filing.Company.Name} ({r.Filing.Company.Ticker})** · {r.Filing.Identifiers.Accession} · filed {Filed(r)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  - acqscan-v3 (with EX-2.1): {Describe(r.V3)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  - acqscan-v4 with EX-2.1: {Describe(r.V4With)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  - acqscan-v4 without EX-2.1: {Describe(r.V4Without)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  - acqscan-v4 as shipped ({r.ProductionBodyIs}): {Describe(r.V4)}\n");
        }

        var shoo = rows.FirstOrDefault(r => r.Filing.Identifiers.Accession == AcquisitionReadPrimary8KLiveMeasurementTests.SteveMaddenAccession);
        sb.Append(CultureInfo.InvariantCulture, $"\nSHOO {AcquisitionReadPrimary8KLiveMeasurementTests.SteveMaddenAccession}: v3 {Token(shoo?.V3)}, v4 **{Token(shoo?.V4)}** (with EX-2.1 {Token(shoo?.V4With)}, without {Token(shoo?.V4Without)}).\n");

        var probe = rows.Where(r => r.MergerPhraseCount > 0 && r.V4?.IsRecognised != true).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### Recall probe — every filing whose v4 body carries an explicit merger phrase (\"agreement and plan of merger\" / \"merger agreement\" / \"plan of merger\") that v4 does NOT recognise — {probe.Count}\n\n(A prompt for a human read, not an absence claim: whether any is a takeover of the Radar company is judged from the text.)\n\n");
        sb.Append("| ticker | accession | filed | phrases | acqscan-v4 | deciding clause | title |\n| --- | --- | --- | ---: | --- | --- | --- |\n");
        foreach (var r in probe)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {Ticker(r)} | {r.Filing.Identifiers.Accession} | {Filed(r)} | {r.MergerPhraseCount} | {Token(r.V4)} | {Clause(r.V4)} | {AcquisitionScanV2LiveMeasurementTests.Cell(AcquisitionScanV2LiveMeasurementTests.Trim(r.Filing.Evidence.Title, 80))} |\n");
        }

        var withAgreement = rows.Where(r => r.AgreementFile is not null).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### The EX-2.1 append decision — v4 with and without the merger agreement, for each of the {withAgreement.Count} filing(s) whose index carries one\n\n");
        sb.Append("| ticker | accession | filed | v4 without EX-2.1 | v4 with EX-2.1 | v3 (with EX-2.1) |\n| --- | --- | --- | --- | --- | --- |\n");
        foreach (var r in withAgreement)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {Ticker(r)} | {r.Filing.Identifiers.Accession} | {Filed(r)} | {Token(r.V4Without)} | {Token(r.V4With)}{(Token(r.V4With) != Token(r.V4Without) ? " ⚠ differs" : "")} | {Token(r.V3)} |\n");
        }

        var differs = withAgreement.Where(r => Token(r.V4With) != Token(r.V4Without)).ToList();
        var recognitionDiffers = rows.Where(r => Describe(r.V4With) != Describe(r.V4Without) && (r.V4With?.IsRecognised == true || r.V4Without?.IsRecognised == true)).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\nFilings whose v4 outcome the EX-2.1 append changes: **{differs.Count}**; filings whose v4 RECOGNITION (outcome, acquirer, consideration or quote) differs with vs without it: **{recognitionDiffers.Count}**.\n");
        foreach (var r in differs)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- {Ticker(r)} · {r.Filing.Identifiers.Accession}: without {Describe(r.V4Without)} → with {Describe(r.V4With)}\n");
        }

        sb.Append("\n#### Request cost — `www.sec.gov` requests per filing issued by the production reader (index → primary → EX-99.1 when shown → EX-2.1 when shown and appended)\n\n| requests | filings |\n| ---: | ---: |\n");
        foreach (var group in rows.GroupBy(r => r.ReaderRequests).OrderBy(g => g.Key))
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count()} |\n");
        }

        var withCost = rows.Sum(r => 2 + (r.HasEx99 ? 1 : 0) + (r.AgreementFile is null ? 0 : 1));
        var withoutCost = rows.Sum(r => 2 + (r.HasEx99 ? 1 : 0));
        sb.Append(CultureInfo.InvariantCulture, $"\nProduction reader: {rows.Sum(r => r.ReaderRequests)} requests over {rows.Count} filings (mean {(rows.Count == 0 ? 0 : (double)rows.Sum(r => r.ReaderRequests) / rows.Count):0.00}); {rows.Sum(r => r.ReaderLiveRequests)} went over the wire this run. Computed from each index: with the EX-2.1 append {withCost} (mean {(rows.Count == 0 ? 0 : (double)withCost / rows.Count):0.00}), without it {withoutCost} (mean {(rows.Count == 0 ? 0 : (double)withoutCost / rows.Count):0.00}) — the append costs {withCost - withoutCost} request(s) over the population, one per EX-2.1 filing.\n");
    }

    private static void RenderNarratives(StringBuilder sb, List<Row> rows)
    {
        sb.Append("# Spec 229 §2 — Item 1.01 narratives for the hand check (every filing labelled company-is-acquirer under either version, then every recall-probe filing)\n\n");
        var subjects = rows
            .Where(r => r.V3?.Outcome == AcquisitionScanOutcome.CompanyIsAcquirer || r.V4?.Outcome == AcquisitionScanOutcome.CompanyIsAcquirer)
            .Concat(rows.Where(r => r.MergerPhraseCount > 0 && r.V4?.IsRecognised != true))
            .Distinct()
            .ToList();
        foreach (var r in subjects)
        {
            sb.Append(CultureInfo.InvariantCulture, $"## {Ticker(r)} · {r.Filing.Identifiers.Accession} · filed {Filed(r)} · v3 {Token(r.V3)} · v4 {Token(r.V4)} · phrases {r.MergerPhraseCount}\n\n");
            sb.Append(CultureInfo.InvariantCulture, $"- mentions: {string.Join(" / ", r.Filing.Mentions)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"- v3 deciding: {AcquisitionScanV2LiveMeasurementTests.Flatten(r.V3?.DecidingQuote ?? "(none)")}\n");
            sb.Append(CultureInfo.InvariantCulture, $"- v4 deciding: {AcquisitionScanV2LiveMeasurementTests.Flatten(r.V4?.DecidingQuote ?? "(none)")}\n\n");
            sb.Append(ItemOneOhOneNarrative(r.PrimaryText)).Append("\n\n");
        }
    }

    /// <summary>The primary 8-K's Item 1.01 section (from its heading to the next item heading), flattened and bounded.</summary>
    private static string ItemOneOhOneNarrative(string? primary)
    {
        if (primary is null)
        {
            return "(primary not read)";
        }

        var flat = AcquisitionScanV2LiveMeasurementTests.Flatten(primary);
        var at = flat.IndexOf("Item 1.01", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return "(no Item 1.01 heading) " + AcquisitionScanV2LiveMeasurementTests.Trim(flat, 2500);
        }

        var next = -1;
        foreach (var heading in new[] { "Item 1.02", "Item 2.01", "Item 2.02", "Item 2.03", "Item 3.02", "Item 5.02", "Item 7.01", "Item 8.01", "Item 9.01" })
        {
            var i = flat.IndexOf(heading, at + 10, StringComparison.OrdinalIgnoreCase);
            if (i > 0 && (next < 0 || i < next))
            {
                next = i;
            }
        }

        var section = next > 0 ? flat[at..next] : flat[at..];
        return section.Length <= 3500 ? section : section[..3500] + "…";
    }

    private static int CountMergerPhrases(string text)
    {
        var lower = text.ToLowerInvariant();
        var count = 0;
        foreach (var phrase in ExplicitMergerPhrases)
        {
            for (var at = lower.IndexOf(phrase, StringComparison.Ordinal); at >= 0; at = lower.IndexOf(phrase, at + 1, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static async Task<string?> NormalizedOrNullAsync(HttpClient client, IEvidenceNormalizer normalizer, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri(url), ct);
        return response.IsSuccessStatusCode
            ? normalizer.Normalize(title: null, rawText: await response.Content.ReadAsStringAsync(ct)).NormalizedText
            : null;
    }

    private static async Task<string?> GetStringOrNullAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri(url), ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
    }

    private static string Ticker(Row r) => r.Filing.Company.Ticker ?? r.Filing.Company.Name;

    private static string Filed(Row r) => AcquisitionScanV2LiveMeasurementTests.Filed(r.Filing.Evidence);

    private static string Token(AcquisitionScanResult? result) =>
        result is null ? "(not scanned)" : AcquisitionAgreementScan.Token(result.Outcome);

    private static string Clause(AcquisitionScanResult? result) =>
        result?.DecidingQuote is { } quote
            ? AcquisitionScanV2LiveMeasurementTests.Cell(AcquisitionScanV2LiveMeasurementTests.Trim(quote, 240))
            : result?.IsRecognised == true
                ? "(target) " + AcquisitionScanV2LiveMeasurementTests.Cell(AcquisitionScanV2LiveMeasurementTests.Trim(result.TargetQuote!, 200))
                : "—";

    private static string Describe(AcquisitionScanResult? result) =>
        result is null ? "(not scanned)"
        : result.IsRecognised
            ? $"recognised · acquirer `{result.AcquirerName}` · consideration `{result.ConsiderationCurrency}{result.ConsiderationPerShare}` ({result.ConsiderationKind}) · target clause: {AcquisitionScanV2LiveMeasurementTests.Trim(result.TargetQuote!, 160)} · consideration clause: {AcquisitionScanV2LiveMeasurementTests.Trim(result.ConsiderationQuote!, 160)}"
            : AcquisitionAgreementScan.Token(result.Outcome);

    private sealed record Row(
        ItemOneOhOneFiling Filing,
        AcquisitionFilingBody Body,
        int ReaderRequests,
        int ReaderLiveRequests,
        bool HasEx99,
        string? AgreementFile,
        string? ProductionBodyIs,
        AcquisitionScanResult? V3,
        AcquisitionScanResult? V4,
        AcquisitionScanResult? V4With,
        AcquisitionScanResult? V4Without,
        int MergerPhraseCount,
        string? PrimaryText);
}

/// <summary>Runs the spec-229 §2 measurement only when a data root AND a compliant SEC User-Agent are configured.</summary>
public sealed class AcqScanV4LiveFactAttribute : FactAttribute
{
    public AcqScanV4LiveFactAttribute()
    {
        if (AcquisitionScanV4LiveMeasurementTests.DataRoot() is null
            || AcquisitionScanV2LiveMeasurementTests.UserAgent() is null)
        {
            Skip = AcquisitionScanV4LiveMeasurementTests.SkipReason;
        }
    }
}
