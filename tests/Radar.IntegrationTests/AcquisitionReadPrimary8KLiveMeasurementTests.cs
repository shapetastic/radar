using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Acquisitions;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Filings;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sec;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// SPEC 228 §3 — the READ-ONLY LIVE MEASUREMENT of the item-1.01 body read before and after spec 228 (the
/// inline-XBRL primary document is now resolved; the primary is declared or form-typed, never the first untyped
/// row), and of the scan over each read: <c>acqscan-v2</c> (the pre-228 read) against <c>acqscan-v3</c> (the
/// spec-228 read). The scan RULE is unchanged by spec 228 — only the text it is given — so both columns run the
/// production <see cref="AcquisitionAgreementScan.Scan"/>; the version difference IS the read.
/// <para>
/// <b>Population, resolver and mentions are the production pass's own</b> (reused through the spec-227 harness's
/// read-only composition). The NEW read is the production <see cref="IAcquisitionFilingBodyReader"/> through the
/// shared, globally paced SEC client.
/// </para>
/// <para>
/// <b>The OLD read, stated.</b> For every accession spec 227's harness fetched, the old read is that harness's
/// VERBATIM body (<c>{accession}.txt</c>) or named failure (<c>{accession}.failed</c>) from
/// <c>RADAR_ACQREAD_V3_OLD_BODY_DIR</c> — the exact text <c>acqscan-v2</c> was measured on. It is cross-checked
/// against a CONTROL of the pre-228 selection run on today's index page (the production parser in
/// <see cref="InlineViewerLinks.IgnoreAsBeforeSpec228"/> mode, which yields the pre-228 row set by construction,
/// plus a verbatim copy of the deleted first-untyped-row fallback): the control must reproduce each recorded
/// failure detail and name a document the recorded body's SEC header names. An accession absent from that
/// directory (filed after spec 227 measured) is read through the control itself.
/// </para>
/// <para>
/// <b>The append decision, measured.</b> The shipped read appends the EX-2.1 merger agreement after EX-99.1. For every
/// filing the harness also rebuilds the body WITHOUT it (primary → EX-99.1) from the same responses with the same
/// selectors and normalizer, asserts the shipped body is exactly that rebuild plus the agreement, and scans the
/// rebuild — so what the agreement adds and what it costs are both named, filing by filing.
/// </para>
/// <para>
/// <b>Nothing is written under the data root.</b> The composition's store / cache / evidence writes THROW, and
/// every file under the data root is snapshotted (path, length, last-write time) before and after; the harness
/// FAILS if any changed. The report goes to the test log and a per-process temp path. Optionally
/// <c>RADAR_ACQREAD_V3_DOCUMENT_CACHE_DIR</c> (OUTSIDE the data root) keeps every fetched SEC response so a re-run
/// issues no request; the reader's request COUNT is recorded whether a response came from that cache or the wire.
/// </para>
/// <para>
/// <b>ENV-GATED</b>: <c>RADAR_ACQREAD_V3_LIVE_DATA_ROOT</c> AND <c>RADAR_SEC_UA</c>. Skipped with a named reason otherwise.
/// </para>
/// </summary>
public sealed class AcquisitionReadPrimary8KLiveMeasurementTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_ACQREAD_V3_LIVE_DATA_ROOT";

    internal const string OldBodyDirectoryVariable = "RADAR_ACQREAD_V3_OLD_BODY_DIR";

    internal const string DocumentCacheVariable = "RADAR_ACQREAD_V3_DOCUMENT_CACHE_DIR";

    internal const string SkipReason =
        "Spec 228 §3 live pre-228 vs spec-228 item-1.01 read measurement (issues REAL www.sec.gov requests): set "
            + DataRootVariable + " to a Radar data root AND " + AcquisitionScanV2LiveMeasurementTests.UserAgentVariable
            + " to a compliant SEC User-Agent (optionally " + OldBodyDirectoryVariable + " to spec 227's verbatim bodies and "
            + DocumentCacheVariable + " to fetch each SEC response once).";

    /// <summary>HZO's genuine takeover (spec 217/227) — must stay recognised at 53.00 under v3.</summary>
    internal const string MarineMaxMergerAccession = "0001193125-26-341302";

    /// <summary>SHOO's spec-227 false positive — must stay unrecognised under v3.</summary>
    internal const string SteveMaddenAccession = "0001641172-25-008949";

    private static readonly string ReportPath = Path.Combine(
        Path.GetTempPath(),
        "radar-spec-228-acqread-v3-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".md");

    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    [AcqReadV3LiveFact]
    public async Task LiveDistribution_Pre228ReadVersusSpec228Read_AndAcqScanV2VersusV3()
    {
        var root = Path.GetFullPath(DataRoot()!);
        var ct = CancellationToken.None;
        var oldBodyDirectory = OutsideRoot(root, Environment.GetEnvironmentVariable(OldBodyDirectoryVariable), create: false);
        var cacheDirectory = OutsideRoot(root, Environment.GetEnvironmentVariable(DocumentCacheVariable), create: true);

        output.WriteLine($"Report path: {ReportPath}");
        var before = AcquisitionScanV2LiveMeasurementTests.SnapshotTree(root);

        var state = new SecResponseLog(cacheDirectory);
        var sb = new StringBuilder();
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
            await MeasureAsync(sb, provider, client, state, root, companies, oldBodyDirectory, cacheDirectory, ct);
        }
        finally
        {
            // The report and the no-write confirmation are produced even when a measured assertion fails.
            var after = AcquisitionScanV2LiveMeasurementTests.SnapshotTree(root);
            var changed = AcquisitionScanV2LiveMeasurementTests.DiffTrees(before, after);
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
    }

    private static async Task MeasureAsync(
        StringBuilder sb,
        ServiceProvider provider,
        HttpClient client,
        SecResponseLog state,
        string root,
        IReadOnlyList<Company> companies,
        string? oldBodyDirectory,
        string? cacheDirectory,
        CancellationToken ct)
    {
        var mentions = AcquisitionRecognitionPass.BuildMentionIndex(companies);
        var byId = companies.ToDictionary(c => c.Id);
        var resolver = provider.GetRequiredService<ICompanyResolver>();
        var reader = provider.GetRequiredService<IAcquisitionFilingBodyReader>();
        var normalizer = provider.GetRequiredService<IEvidenceNormalizer>();
        var evidence = await provider.GetRequiredService<IEvidenceRepository>().GetAllAsync(ct);

        var candidates = new List<(EvidenceItem Evidence, FilingEvidenceIdentifiers Identifiers)>();
        var untrustworthy = 0;
        foreach (var item in evidence)
        {
            if (FilingEvidenceFacts.TryResolve(
                    item, AcquisitionRecognitionPass.FormCode, AcquisitionRecognitionPass.ItemCode, out var identifiers, out var rejection))
            {
                candidates.Add((item, identifiers!));
            }
            else if (rejection is FilingEvidenceRejection.UnparseableSourceUrl or FilingEvidenceRejection.AccessionMismatch)
            {
                untrustworthy++;
            }
        }

        candidates.Sort(static (a, b) =>
        {
            var byWhen = (a.Evidence.PublishedAtUtc ?? a.Evidence.CollectedAtUtc).CompareTo(b.Evidence.PublishedAtUtc ?? b.Evidence.CollectedAtUtc);
            return byWhen != 0 ? byWhen : a.Evidence.Id.CompareTo(b.Evidence.Id);
        });

        var rows = new List<FilingRow>();
        var unresolved = 0;
        var seen = new Dictionary<string, FilingRow>(StringComparer.Ordinal);
        var duplicateItems = 0;
        foreach (var (item, identifiers) in candidates)
        {
            var resolution = await resolver.ResolveAsync(item.Title, AcquisitionScanV2LiveMeasurementTests.HintsOf(item), ct);
            if (resolution.CompanyId is not { } companyId || !byId.TryGetValue(companyId, out var company))
            {
                unresolved++;
                continue;
            }

            if (seen.ContainsKey(identifiers.Accession))
            {
                // A second evidence item for an accession already measured: the read is per accession.
                duplicateItems++;
                continue;
            }

            var row = await MeasureFilingAsync(
                client, reader, normalizer, state, item, identifiers, company, mentions.GetValueOrDefault(companyId, []), oldBodyDirectory, ct);
            seen[identifiers.Accession] = row;
            rows.Add(row);
        }

        Render(sb, root, rows, candidates.Count, untrustworthy, unresolved, duplicateItems, state, oldBodyDirectory, cacheDirectory);

        Assert.Equal(candidates.Count, rows.Count + unresolved + duplicateItems);
        Assert.All(rows, r => Assert.NotEqual(AcquisitionScanOutcome.VerbatimCheckFailed, r.V3?.Outcome));
        Assert.All(rows, r => Assert.True(r.BodyCompositionMatches is not false, $"The shipped body for {r.Identifiers.Accession} is not primary + EX-99.1 + EX-2.1 as rebuilt."));
        Assert.All(rows, r => Assert.True(r.ControlAgrees is not false, $"The pre-228 control disagrees with spec 227's recorded read for {r.Identifiers.Accession}: {r.ControlNote}"));

        var hzo = Assert.Single(rows, r => r.Identifiers.Accession == MarineMaxMergerAccession);
        Assert.True(hzo.V3?.IsRecognised == true, $"HZO {MarineMaxMergerAccession} must stay recognised under v3; it was {Token(hzo.V3)}.");
        Assert.Equal("53.00", hzo.V3!.ConsiderationPerShare);
        var shoo = Assert.Single(rows, r => r.Identifiers.Accession == SteveMaddenAccession);
        Assert.False(shoo.V3?.IsRecognised == true, $"SHOO {SteveMaddenAccession} must stay unrecognised under v3.");
    }

    private static async Task<FilingRow> MeasureFilingAsync(
        HttpClient client,
        IAcquisitionFilingBodyReader reader,
        IEvidenceNormalizer normalizer,
        SecResponseLog state,
        EvidenceItem item,
        FilingEvidenceIdentifiers identifiers,
        Company company,
        IReadOnlyList<string> companyMentions,
        string? oldBodyDirectory,
        CancellationToken ct)
    {
        // NEW read — the production reader; its request count is the per-filing cost the pass pays.
        var requestsBefore = state.Requests;
        var liveBefore = state.LiveRequests;
        var newBody = await reader.ReadAsync(identifiers.Cik, identifiers.Accession, identifiers.PrimaryDocument, ct);
        var readerRequests = state.Requests - requestsBefore;
        var readerLive = state.LiveRequests - liveBefore;

        var baseUrl = SecEdgarUrls.BuildArchiveBaseUrl(identifiers.Cik.Trim(), identifiers.Accession.Trim());
        var indexHtml = await GetStringOrNullAsync(client, SecEdgarUrls.BuildIndexUrl(identifiers.Cik.Trim(), identifiers.Accession.Trim(), ".html"), ct);
        var rowsNew = indexHtml is null ? [] : SecFilingIndexTable.Parse(indexHtml);
        var selection = SecFilingIndexTable.SelectPrimaryDocument(rowsNew, identifiers.PrimaryDocument);

        // OLD read — the pre-228 control on today's index, and spec 227's verbatim record when it exists.
        var control = await Pre228ControlReadAsync(client, normalizer, baseUrl, indexHtml, identifiers.PrimaryDocument, readBodies: false, ct);
        AcquisitionFilingBody oldBody;
        string oldSource;
        bool? controlAgrees = null;
        string? controlNote = null;
        var txt = oldBodyDirectory is null ? null : Path.Combine(oldBodyDirectory, identifiers.Accession + ".txt");
        var failed = oldBodyDirectory is null ? null : Path.Combine(oldBodyDirectory, identifiers.Accession + ".failed");
        if (txt is not null && File.Exists(txt))
        {
            oldBody = AcquisitionFilingBody.Success(await File.ReadAllTextAsync(txt, ct));
            oldSource = "spec-227 verbatim body";
            var header = string.Join('\n', oldBody.PlainText.Split('\n').Take(6));
            controlAgrees = control.PrimaryFileName is not null && header.Contains(control.PrimaryFileName, StringComparison.OrdinalIgnoreCase);
            controlNote = $"control primary {control.PrimaryFileName ?? control.Detail}; recorded body header: {AcquisitionScanV2LiveMeasurementTests.Trim(header, 120)}";
        }
        else if (failed is not null && File.Exists(failed))
        {
            oldBody = AcquisitionFilingBody.Failed(await File.ReadAllTextAsync(failed, ct));
            oldSource = "spec-227 verbatim failure";
            controlAgrees = control.PrimaryFileName is null && string.Equals(control.Detail, oldBody.Detail, StringComparison.Ordinal);
            controlNote = $"control {control.PrimaryFileName ?? control.Detail}; recorded failure {oldBody.Detail}";
        }
        else
        {
            var read = await Pre228ControlReadAsync(client, normalizer, baseUrl, indexHtml, identifiers.PrimaryDocument, readBodies: true, ct);
            oldBody = read.Body!;
            oldSource = "pre-228 control (not in spec 227's measurement)";
        }

        var v2 = oldBody.IsSuccess ? AcquisitionAgreementScan.Scan(oldBody.PlainText, companyMentions) : null;
        var v3 = newBody.IsSuccess ? AcquisitionAgreementScan.Scan(newBody.PlainText, companyMentions) : null;

        // The append counterfactual. The shipped read appends the EX-2.1 (spec 228 §1 decision); the counterfactual
        // is the same body WITHOUT it — primary → EX-99.1 — rebuilt from the same responses with the same
        // selectors and normalizer. The shipped body must equal that rebuild plus the agreement exactly, which
        // proves the counterfactual differs from production by the agreement and nothing else.
        AcquisitionScanResult? v3WithoutAgreement = null;
        bool? compositionMatches = null;
        var agreement = SecFilingIndexTable.SelectMergerAgreementExhibit(rowsNew);
        if (newBody.IsSuccess && selection.Row is { } primaryRow)
        {
            var parts = new List<string?> { await NormalizedOrNullAsync(client, normalizer, $"{baseUrl}/{primaryRow.FileName}", ct) };
            if (SecFilingIndexTable.SelectEarningsExhibit(rowsNew) is { } ex99)
            {
                parts.Add(await NormalizedOrNullAsync(client, normalizer, $"{baseUrl}/{ex99.FileName}", ct));
            }

            var agreementText = agreement is null ? null : await NormalizedOrNullAsync(client, normalizer, $"{baseUrl}/{agreement.FileName}", ct);
            if (parts.All(p => p is not null) && (agreement is null || agreementText is not null))
            {
                var withoutAgreement = string.Join('\n', parts);
                compositionMatches = string.Equals(
                    newBody.PlainText,
                    agreement is null ? withoutAgreement : withoutAgreement + "\n" + agreementText,
                    StringComparison.Ordinal);
                if (agreement is not null)
                {
                    v3WithoutAgreement = AcquisitionAgreementScan.Scan(withoutAgreement, companyMentions);
                }
            }
        }

        var oldPrimaryType = control.PrimaryFileName is null
            ? null
            : rowsNew.FirstOrDefault(r => string.Equals(r.FileName, control.PrimaryFileName, StringComparison.OrdinalIgnoreCase))?.DocumentType;

        return new FilingRow(
            item, identifiers, company, rowsNew, selection, newBody, readerRequests, readerLive, oldBody, oldSource,
            control.PrimaryFileName, oldPrimaryType, controlAgrees, controlNote, v2, v3, agreement?.FileName, v3WithoutAgreement, compositionMatches);
    }

    /// <summary>
    /// The PRE-228 read, as a control: the pre-228 row set (<see cref="InlineViewerLinks.IgnoreAsBeforeSpec228"/>),
    /// the pre-228 selection (the declared primary when present, else the FIRST row without an EX-99 type — the
    /// fallback spec 228 deleted, copied verbatim here), then that document plus the EX-99 exhibit.
    /// </summary>
    private static async Task<(string? PrimaryFileName, string? Detail, AcquisitionFilingBody? Body)> Pre228ControlReadAsync(
        HttpClient client, IEvidenceNormalizer normalizer, string baseUrl, string? indexHtml, string? declared, bool readBodies, CancellationToken ct)
    {
        if (indexHtml is null)
        {
            return (null, "index fetch failed", AcquisitionFilingBody.Failed("index fetch failed"));
        }

        var rows = SecFilingIndexTable.Parse(indexHtml, InlineViewerLinks.IgnoreAsBeforeSpec228);
        if (rows.Count == 0)
        {
            return (null, "no parseable document table", AcquisitionFilingBody.Failed("no parseable document table"));
        }

        SecFilingIndexRow? primary = null;
        if (!string.IsNullOrWhiteSpace(declared))
        {
            primary = rows.FirstOrDefault(r => string.Equals(r.FileName, declared.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        primary ??= rows.Where(r => r.Ex99Type is null).OrderBy(r => r.Order).FirstOrDefault();
        if (primary is null)
        {
            return (null, "no primary document row", AcquisitionFilingBody.Failed("no primary document row"));
        }

        if (!readBodies)
        {
            return (primary.FileName, null, null);
        }

        var primaryHtml = await GetStringOrNullAsync(client, $"{baseUrl}/{primary.FileName}", ct);
        if (primaryHtml is null)
        {
            return (primary.FileName, "primary fetch failed", AcquisitionFilingBody.Failed("primary fetch failed"));
        }

        var text = new StringBuilder(normalizer.Normalize(title: null, rawText: primaryHtml).NormalizedText);
        var exhibit = SecFilingIndexTable.SelectEarningsExhibit(rows);
        if (exhibit is not null)
        {
            var exhibitHtml = await GetStringOrNullAsync(client, $"{baseUrl}/{exhibit.FileName}", ct);
            if (exhibitHtml is null)
            {
                return (primary.FileName, "exhibit fetch failed", AcquisitionFilingBody.Failed("exhibit fetch failed"));
            }

            text.Append('\n').Append(normalizer.Normalize(title: null, rawText: exhibitHtml).NormalizedText);
        }

        return (primary.FileName, null, AcquisitionFilingBody.Success(text.ToString()));
    }

    private static void Render(
        StringBuilder sb,
        string root,
        List<FilingRow> rows,
        int candidateItems,
        int untrustworthy,
        int unresolved,
        int duplicateItems,
        SecResponseLog state,
        string? oldBodyDirectory,
        string? cacheDirectory)
    {
        sb.Append("### Spec 228 §3 — the item-1.01 read before and after spec 228, and `acqscan-v2` vs `acqscan-v3`, over every item-1.01 filing in the store\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Data root `{root}`, read-only. New read: the production `HttpSecAcquisitionFilingReader` through the shared paced SEC client. Old read: {(oldBodyDirectory is null ? "the pre-228 control for every filing" : "spec 227's verbatim bodies/failures where recorded, cross-checked against the pre-228 control; the control itself otherwise")}. SEC Archives requests this run (production reader and harness together): {state.LiveRequests} fetched live over the wire, {state.Requests - state.LiveRequests} served from {(cacheDirectory is null ? "(no cache configured)" : "the document cache outside the data root (a URL already fetched — by this run's reader or an earlier run)")}. Scan rule: the production `AcquisitionAgreementScan` for both columns (the rule did not change; the read did).\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Item-1.01 filing evidence items: **{candidateItems}** (+{untrustworthy} untrustworthy identifiers, never fetched); unresolved company {unresolved}; second evidence items for an already-measured accession {duplicateItems}; **accessions measured {rows.Count}**.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Old read source: {rows.Count(r => r.OldSource == "spec-227 verbatim body")} spec-227 verbatim bodies, {rows.Count(r => r.OldSource == "spec-227 verbatim failure")} spec-227 verbatim failures, {rows.Count(r => r.OldSource.StartsWith("pre-228 control", StringComparison.Ordinal))} through the control. Control vs recorded read: {rows.Count(r => r.ControlAgrees == true)} agree, {rows.Count(r => r.ControlAgrees == false)} disagree.\n\n");

        // Read outcomes.
        sb.Append("#### Read outcomes — old reader vs new\n\n| outcome | old (pre-228) | new (spec 228) |\n| --- | ---: | ---: |\n");
        var outcomeKeys = rows.Select(r => ReadKey(r.OldBody)).Concat(rows.Select(r => ReadKey(r.NewBody))).Distinct(StringComparer.Ordinal).OrderBy(k => k == "success" ? "" : k, StringComparer.Ordinal);
        foreach (var key in outcomeKeys)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {key} | {rows.Count(r => ReadKey(r.OldBody) == key)} | {rows.Count(r => ReadKey(r.NewBody) == key)} |\n");
        }

        var stillFailing = rows.Where(r => !r.NewBody.IsSuccess).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\nStill failing under the new reader: **{stillFailing.Count}**.\n");
        foreach (var r in stillFailing)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- {Ticker(r)} · {r.Identifiers.Accession} · filed {AcquisitionScanV2LiveMeasurementTests.Filed(r.Evidence)} · {r.NewBody.Detail} · declared primary `{r.Identifiers.PrimaryDocument ?? "(none)"}` · index rows {string.Join(", ", r.IndexRows.Select(x => $"{x.FileName}[{x.DocumentType ?? "?"}]"))}\n");
        }

        var successes = rows.Where(r => r.NewBody.IsSuccess).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\nPrimary chosen by: declared `primaryDocument` {successes.Count(r => r.Selection.Authority == PrimaryDocumentAuthority.Declared)}, form-typed row {successes.Count(r => r.Selection.Authority == PrimaryDocumentAuthority.FormTyped)}. Declared primary present in the index but NOT the form-typed row: {rows.Count(r => r.Selection.Authority == PrimaryDocumentAuthority.Declared && r.Selection.Row!.DocumentType is { } t && !SecFilingIndexTable.PrimaryFormTypes.Contains(t, StringComparer.OrdinalIgnoreCase))}.\n");

        // Body starts with.
        sb.Append("\n#### What the scanned body starts with — the index Type of the FIRST document read\n\n(Old: the document the pre-228 selection took as primary, as the control names it — its file name is in the recorded body's SEC header for every one of the verbatim bodies — typed from today's index Type column. New: the primary the spec-228 reader selected.)\n\n| body began with | old (pre-228) | new (spec 228) |\n| --- | ---: | ---: |\n");
        string OldStart(FilingRow r) => r.OldBody.IsSuccess ? r.OldPrimaryDocumentType ?? "(untyped)" : "(read failed)";
        string NewStart(FilingRow r) => r.NewBody.IsSuccess ? r.Selection.Row?.DocumentType ?? "(untyped)" : "(read failed)";
        var startKeys = rows.Select(OldStart).Concat(rows.Select(NewStart)).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
        foreach (var key in startKeys)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {key} | {rows.Count(r => OldStart(r) == key)} | {rows.Count(r => NewStart(r) == key)} |\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"\n8-K cover (\"CURRENT REPORT\" with \"Section 13\") within the first 5,000 characters of the body: old **{rows.Count(r => CoverFirst(r.OldBody))}** of {rows.Count(r => r.OldBody.IsSuccess)} read; new **{successes.Count(r => CoverFirst(r.NewBody))}** of {successes.Count}. Anywhere in the body: old {rows.Count(r => r.OldBody.IsSuccess && HasCover(r.OldBody.PlainText))}, new {successes.Count(r => HasCover(r.NewBody.PlainText))}.\n");
        var noCover = successes.Where(r => !CoverFirst(r.NewBody)).ToList();
        foreach (var r in noCover)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- new body without the cover first: {Ticker(r)} · {r.Identifiers.Accession} · primary `{r.Selection.Row?.FileName}` [{r.Selection.Row?.DocumentType}] · starts: {AcquisitionScanV2LiveMeasurementTests.Trim(r.NewBody.PlainText, 140)}\n");
        }

        // Scan tally.
        sb.Append("\n#### Scan tally — `acqscan-v2` (pre-228 read) vs `acqscan-v3` (spec-228 read)\n\n| outcome | acqscan-v2 | acqscan-v3 |\n| --- | ---: | ---: |\n");
        foreach (var outcome in AcquisitionAgreementScan.AllOutcomes)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {AcquisitionAgreementScan.Token(outcome)} | {rows.Count(r => r.V2?.Outcome == outcome)} | {rows.Count(r => r.V3?.Outcome == outcome)} |\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"| (not scanned — read failed) | {rows.Count(r => r.V2 is null)} | {rows.Count(r => r.V3 is null)} |\n");

        var changed = rows.Where(r => Token(r.V2) != Token(r.V3)).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### Filings whose outcome changed — {changed.Count}\n\n");
        if (changed.Count > 0)
        {
            sb.Append("| ticker | accession | filed | acqscan-v2 (old read) | acqscan-v3 (new read) |\n| --- | --- | --- | --- | --- |\n");
            foreach (var r in changed)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| {Ticker(r)} | {r.Identifiers.Accession} | {AcquisitionScanV2LiveMeasurementTests.Filed(r.Evidence)} | {Token(r.V2)} | {Token(r.V3)} |\n");
            }
        }

        var recognised = rows.Where(r => r.V2?.IsRecognised == true || r.V3?.IsRecognised == true).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### Every recognition under either — {recognised.Count}\n\n");
        foreach (var r in recognised)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- **{r.Company.Name} ({r.Company.Ticker})** · {r.Identifiers.Accession} · filed {AcquisitionScanV2LiveMeasurementTests.Filed(r.Evidence)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  - acqscan-v2: {Describe(r.V2)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  - acqscan-v3: {Describe(r.V3)}\n");
        }

        var shoo = rows.FirstOrDefault(r => r.Identifiers.Accession == SteveMaddenAccession);
        sb.Append(CultureInfo.InvariantCulture, $"\nSHOO {SteveMaddenAccession}: v2 {Token(shoo?.V2)}, v3 **{Token(shoo?.V3)}**.\n");

        // The formerly unreadable.
        var formerlyUnreadable = rows.Where(r => !r.OldBody.IsSuccess).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n#### The {formerlyUnreadable.Count} formerly unreadable filings — v3 outcome, and whether any is RECOGNISED as a takeover of a Radar company under acqscan-v3\n\n(Not recognised is NOT a checked \"no takeover\": the scan fails closed, and no-merger-agreement / company-not-target / company-is-acquirer say only that the rule did not recognise one. Whether a filing actually is a takeover is a human read of its text, recorded outside this report.)\n\n");
        foreach (var r in formerlyUnreadable)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- {Ticker(r)} · {r.Identifiers.Accession} · filed {AcquisitionScanV2LiveMeasurementTests.Filed(r.Evidence)} · old: {r.OldBody.Detail} · new read: {ReadKey(r.NewBody)} · v3: **{Token(r.V3)}** · recognised as a takeover under acqscan-v3? **{(r.V3?.IsRecognised == true ? "YES — " + Describe(r.V3) : r.V3 is null ? "UNKNOWN (still unread)" : "no")}** · title: {AcquisitionScanV2LiveMeasurementTests.Trim(r.Evidence.Title, 90)}\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"\nRecognised as a takeover of a Radar company under acqscan-v3 among them: **{formerlyUnreadable.Count(r => r.V3?.IsRecognised == true)}** (a count of recognitions, not a checked absence of takeovers); among them with a merger-agreement phrase present but not recognised (company-not-target / acquirer-not-named / no-stated-consideration): {formerlyUnreadable.Count(r => r.V3?.Outcome is AcquisitionScanOutcome.AcquirerNotNamed or AcquisitionScanOutcome.NoStatedConsideration or AcquisitionScanOutcome.CompanyNotTarget)}.\n");

        // Request cost.
        sb.Append("\n#### Request cost — `www.sec.gov` requests per filing issued by the new reader\n\n| requests | filings |\n| ---: | ---: |\n");
        foreach (var group in rows.GroupBy(r => r.ReaderRequests).OrderBy(g => g.Key))
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count()} |\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"\nTotal {rows.Sum(r => r.ReaderRequests)} over {rows.Count} filings (mean {(rows.Count == 0 ? 0 : (double)rows.Sum(r => r.ReaderRequests) / rows.Count):0.00}); {rows.Sum(r => r.ReaderLiveRequests)} of them went over the wire this run. (Index → primary → EX-99.1 when shown → EX-2.1 when shown: at most 4; a filing that fails before a document fetch costs 1.) Filings with EX-99.1 and EX-2.1 both: {rows.Count(r => r.AgreementFile is not null && SecFilingIndexTable.SelectEarningsExhibit(r.IndexRows) is not null)}.\n");

        // The append decision.
        var indexTypes = rows.SelectMany(r => r.IndexRows.Select(x => x.DocumentType ?? "(no type)")).ToList();
        var withAgreement = rows.Where(r => r.AgreementFile is not null).ToList();
        sb.Append("\n#### The append decision — what the EX-2.1 adds, and what it costs\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Index rows by Type across the {rows.Count} filings: EX-2.1 in {rows.Count(r => r.IndexRows.Any(x => x.DocumentType == "EX-2.1"))} filing(s) (any EX-2.* in {rows.Count(r => r.IndexRows.Any(x => x.DocumentType?.StartsWith("EX-2.", StringComparison.Ordinal) == true))}); EX-10.1 in {rows.Count(r => r.IndexRows.Any(x => x.DocumentType == "EX-10.1"))} (any EX-10.* in {rows.Count(r => r.IndexRows.Any(x => x.DocumentType?.StartsWith("EX-10.", StringComparison.Ordinal) == true))}); EX-99.1 in {rows.Count(r => r.IndexRows.Any(x => x.DocumentType == "EX-99.1"))}. Distinct .htm row types: {string.Join(", ", indexTypes.GroupBy(t => t, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"))}.\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"The shipped read appends the EX-2.1 (primary → EX-99.1 → EX-2.1). Counterfactual: the same body WITHOUT it (primary → EX-99.1), rebuilt from the same responses; the shipped body equals that rebuild plus the agreement exactly for {rows.Count(r => r.BodyCompositionMatches == true)} of {successes.Count} successful reads ({rows.Count(r => r.BodyCompositionMatches == false)} mismatch, {successes.Count(r => r.BodyCompositionMatches is null)} not rebuilt). For each of the {withAgreement.Count} filing(s) whose index carries an EX-2.1:\n\n");
        sb.Append("| ticker | accession | filed | without EX-2.1 (counterfactual) | with EX-2.1 (shipped v3) |\n| --- | --- | --- | --- | --- |\n");
        foreach (var r in withAgreement)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {Ticker(r)} | {r.Identifiers.Accession} | {AcquisitionScanV2LiveMeasurementTests.Filed(r.Evidence)} | {(r.V3WithoutAgreement is null ? "(not rebuilt)" : Token(r.V3WithoutAgreement))} | {Token(r.V3)}{(r.V3WithoutAgreement is not null && Token(r.V3) != Token(r.V3WithoutAgreement) ? " ⚠ differs" : "")} |\n");
        }

        var differs = withAgreement.Where(r => r.V3WithoutAgreement is not null && Token(r.V3) != Token(r.V3WithoutAgreement)).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\nFilings whose outcome the EX-2.1 append changes: **{differs.Count}** (into a recognition: {differs.Count(r => r.V3?.IsRecognised == true)}; out of one: {differs.Count(r => r.V3WithoutAgreement?.IsRecognised == true)}).\n");
        foreach (var r in differs)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- {Ticker(r)} · {r.Identifiers.Accession}: without EX-2.1 {Describe(r.V3WithoutAgreement)} → with EX-2.1 (shipped) {Describe(r.V3)}\n");
        }
    }

    private static async Task<string?> NormalizedOrNullAsync(
        HttpClient client, IEvidenceNormalizer normalizer, string url, CancellationToken ct) =>
        await GetStringOrNullAsync(client, url, ct) is { } html
            ? normalizer.Normalize(title: null, rawText: html).NormalizedText
            : null;

    private static async Task<string?> GetStringOrNullAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri(url), ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
    }

    private static string? OutsideRoot(string root, string? directory, bool create)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var full = Path.GetFullPath(directory);
        // A path-BOUNDARY check, not a prefix check: root C:\data must not reject C:\data-cache.
        var relative = Path.GetRelativePath(root, full);
        var outside = Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        Assert.True(outside, $"{full} must lie OUTSIDE the data root — the harness writes nothing under it.");
        if (create)
        {
            Directory.CreateDirectory(full);
        }

        return full;
    }

    private static string ReadKey(AcquisitionFilingBody body) => body.IsSuccess ? "success" : "failed: " + body.Detail;

    private static bool CoverFirst(AcquisitionFilingBody body) =>
        body.IsSuccess && HasCover(body.PlainText[..Math.Min(body.PlainText.Length, 5000)]);

    private static bool HasCover(string text)
    {
        var flat = AcquisitionScanV2LiveMeasurementTests.Flatten(text).ToLowerInvariant();
        return flat.Contains("current report", StringComparison.Ordinal) && flat.Contains("section 13", StringComparison.Ordinal);
    }

    private static string Ticker(FilingRow r) => r.Company.Ticker ?? r.Company.Name;

    private static string Token(AcquisitionScanResult? result) =>
        result is null ? "(not scanned)" : AcquisitionAgreementScan.Token(result.Outcome);

    private static string Describe(AcquisitionScanResult? result) =>
        result is null ? "(not scanned — read failed)"
        : result.IsRecognised
            ? $"recognised · acquirer `{result.AcquirerName}` · consideration `{result.ConsiderationCurrency}{result.ConsiderationPerShare}` ({result.ConsiderationKind}) · quote: {AcquisitionScanV2LiveMeasurementTests.Trim(result.ConsiderationQuote!, 180)}"
            : AcquisitionAgreementScan.Token(result.Outcome);

    private sealed record FilingRow(
        EvidenceItem Evidence,
        FilingEvidenceIdentifiers Identifiers,
        Company Company,
        List<SecFilingIndexRow> IndexRows,
        PrimaryDocumentSelection Selection,
        AcquisitionFilingBody NewBody,
        int ReaderRequests,
        int ReaderLiveRequests,
        AcquisitionFilingBody OldBody,
        string OldSource,
        string? ControlPrimaryFileName,
        string? OldPrimaryDocumentType,
        bool? ControlAgrees,
        string? ControlNote,
        AcquisitionScanResult? V2,
        AcquisitionScanResult? V3,
        string? AgreementFile,
        AcquisitionScanResult? V3WithoutAgreement,
        bool? BodyCompositionMatches);

    /// <summary>Counts every SEC request the harness's clients issue, and (optionally) keeps each 200 response outside the data root.</summary>
    private sealed class SecResponseLog(string? cacheDirectory)
    {
        private int _requests;
        private int _live;

        public int Requests => _requests;

        public int LiveRequests => _live;

        public string? PathFor(Uri uri)
        {
            if (cacheDirectory is null || !uri.AbsolutePath.StartsWith("/Archives/", StringComparison.Ordinal))
            {
                return null;
            }

            return Path.Combine(cacheDirectory, uri.AbsolutePath.Trim('/').Replace('/', '_'));
        }

        public void Counted() => Interlocked.Increment(ref _requests);

        public void Live() => Interlocked.Increment(ref _live);
    }

    /// <summary>
    /// The OUTERMOST handler on the reader's client: counts the request, answers from the document cache when it
    /// holds the URL, and otherwise passes it to the production pipeline (the shared SEC pacer) and keeps a 200.
    /// A new instance per handler-chain build; the state is shared.
    /// </summary>
    private sealed class RecordingCacheHandler(SecResponseLog log) : DelegatingHandler
    {
        private const string ContentTypeSuffix = ".content-type";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isArchive = request.RequestUri!.AbsolutePath.StartsWith("/Archives/", StringComparison.Ordinal);
            if (isArchive)
            {
                log.Counted();
            }

            var path = log.PathFor(request.RequestUri);
            if (path is not null && File.Exists(path))
            {
                var cached = new ByteArrayContent(await File.ReadAllBytesAsync(path, cancellationToken));
                var contentTypePath = path + ContentTypeSuffix;
                if (File.Exists(contentTypePath)
                    && MediaTypeHeaderValue.TryParse(await File.ReadAllTextAsync(contentTypePath, cancellationToken), out var contentType))
                {
                    cached.Headers.ContentType = contentType;
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = cached, RequestMessage = request };
            }

            if (isArchive)
            {
                log.Live();
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (path is not null && response.StatusCode == HttpStatusCode.OK)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                var content = new ByteArrayContent(bytes);
                if (response.Content.Headers.ContentType is { } contentType)
                {
                    // Kept beside the body so a cached replay decodes with the charset the wire declared.
                    content.Headers.ContentType = contentType;
                    await File.WriteAllTextAsync(path + ContentTypeSuffix, contentType.ToString(), cancellationToken);
                }

                var replay = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
                response.Dispose();
                return replay;
            }

            return response;
        }
    }
}

/// <summary>Runs the spec-228 §3 measurement only when a data root AND a compliant SEC User-Agent are configured.</summary>
public sealed class AcqReadV3LiveFactAttribute : FactAttribute
{
    public AcqReadV3LiveFactAttribute()
    {
        if (AcquisitionReadPrimary8KLiveMeasurementTests.DataRoot() is null
            || AcquisitionScanV2LiveMeasurementTests.UserAgent() is null)
        {
            Skip = AcquisitionReadPrimary8KLiveMeasurementTests.SkipReason;
        }
    }
}
