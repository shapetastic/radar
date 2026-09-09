using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Acquisitions;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Filings;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Acquisitions;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Sec;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// SPEC 217 §1 — the READ-ONLY LIVE MEASUREMENT of what <c>acqscan-v1</c> actually produces over the whole
/// accrued store (CLAUDE.md's "no measure ships without its live distribution").
/// <para>
/// It runs the PRODUCTION path — the same <see cref="IAcquisitionFilingBodyReader"/> the recognition pass
/// uses, through the same globally-paced SEC <c>HttpClient</c> — over EVERY item-1.01 filing evidence item
/// in the store (measured 2026-09-08: exactly 178), and reports the distribution: recognised /
/// not-recognised BY LEG / company-is-acquirer / fetch-failed / unresolved company / untrustworthy
/// identifiers. Expected: 1 recognised (MarineMax, accession 0001193125-26-341302, the 2026-08-10 Safe
/// Harbor Marinas merger) and 177 not.
/// </para>
/// <para>
/// <b>ANY SECOND RECOGNITION MUST BE INVESTIGATED BY HAND AND NAMED IN THE PR BODY.</b> A false positive
/// here CLOSES A LIVE THESIS — strictly worse than missing one — so the harness prints every recognised
/// company, its accession and both verbatim quotes, and it prints every filing whose outcome fell in the
/// two "nearly recognised" buckets (<c>AcquirerNotNamed</c>, <c>NoStatedConsideration</c>) so a
/// near-miss is visible rather than pooled into a total.
/// </para>
/// <para>
/// <b>Nothing is persisted.</b> No acquisitions record, no scan-cache entry, no evidence, no signal, no
/// score: the composition registers only the reader, the seed and the durable evidence store, and the scan
/// itself is pure. The store is read READ-ONLY.
/// </para>
/// <para>
/// <b>It issues real www.sec.gov requests, so it is ENV-GATED and skipped with a NAMED reason otherwise</b>
/// (the <c>NewsRecencyWindowLiveMeasurementTests</c> precedent). BOTH variables are required:
/// <c>RADAR_ACQSCAN_LIVE_DATA_ROOT</c> (a Radar data root holding <c>companies.json</c> and
/// <c>evidence/raw/</c>) and <c>RADAR_SEC_UA</c> (a compliant SEC User-Agent, e.g. <c>"Name email"</c> —
/// SEC returns 403 for every request without one, and it is deliberately never committed). Requests are
/// paced by the shared process-wide <see cref="SecRequestPacer"/>, exactly as a live run's are.
/// </para>
/// <para>
/// The output is markdown on the test log, ready to paste into a PR body.
/// </para>
/// </summary>
public sealed class AcquisitionRecognitionLiveMeasurementTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_ACQSCAN_LIVE_DATA_ROOT";

    internal const string UserAgentVariable = "RADAR_SEC_UA";

    internal const string SkipReason =
        "Spec 217 §1 live acqscan measurement (issues REAL www.sec.gov requests): set " + DataRootVariable
            + " to a Radar data root (companies.json, evidence/raw/) AND " + UserAgentVariable
            + " to a compliant SEC User-Agent to run it.";

    /// <summary>The data root under measurement, or null when the variable is unset/not a directory.</summary>
    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    /// <summary>
    /// The SEC User-Agent, or null when unset. It is deliberately NOT defaulted: the placeholder shipped in
    /// appsettings.json 403s on purpose, so a missing UA must SKIP the measurement rather than produce 178
    /// fetch failures and call that a distribution.
    /// </summary>
    internal static string? UserAgent()
    {
        var ua = Environment.GetEnvironmentVariable(UserAgentVariable);
        return string.IsNullOrWhiteSpace(ua) ? null : ua.Trim();
    }

    [AcqScanLiveFact]
    public async Task LiveDistribution_OfAcqScanV1_OverEveryItem101FilingInTheStore()
    {
        var root = DataRoot()!;
        var ct = CancellationToken.None;

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        services.AddLocalFileCompanySeed(Path.Combine(root, "companies.json"));
        services.AddFileRawEvidenceStore(Path.Combine(root, "evidence", "raw"));
        services.AddRadarAcquisitionRecognition(
            new SecCollectorOptions { UserAgent = UserAgent()! },
            // Temp roots that are never written to: the harness calls the READER only, never the store or
            // the cache, so nothing can reach the accrued data root even by accident.
            new FileAcquisitionStoreOptions
            {
                RootDirectory = Path.Combine(Path.GetTempPath(), "radar-acqscan-live-unused"),
            },
            new FileAcquisitionScanCacheOptions
            {
                RootDirectory = Path.Combine(Path.GetTempPath(), "radar-acqscan-live-unused-cache"),
            });

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);

        var companies = await provider.GetRequiredService<ICompanyRepository>().GetAllAsync(ct);
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

        // Deterministic order (AD-3), oldest first so the log reads as a walk through the store.
        candidates.Sort(static (a, b) =>
        {
            var aWhen = a.Evidence.PublishedAtUtc ?? a.Evidence.CollectedAtUtc;
            var bWhen = b.Evidence.PublishedAtUtc ?? b.Evidence.CollectedAtUtc;
            var byWhen = aWhen.CompareTo(bWhen);
            return byWhen != 0 ? byWhen : a.Evidence.Id.CompareTo(b.Evidence.Id);
        });

        var tally = new Dictionary<AcquisitionScanOutcome, int>();
        var recognised = new List<string>();
        var nearMisses = new List<string>();
        var fetchFailed = new List<string>();
        var unresolved = 0;

        foreach (var (item, identifiers) in candidates)
        {
            var resolution = await resolver.ResolveAsync(item.Title, HintsOf(item), ct);
            if (resolution.CompanyId is not { } companyId || !byId.TryGetValue(companyId, out var company))
            {
                unresolved++;
                continue;
            }

            var body = await reader.ReadAsync(
                identifiers.Cik, identifiers.Accession, identifiers.PrimaryDocument, ct);
            if (!body.IsSuccess)
            {
                fetchFailed.Add(
                    $"{company.Ticker ?? company.Name} {identifiers.Accession} — {body.Detail}");
                continue;
            }

            var scan = AcquisitionAgreementScan.Scan(
                body.PlainText, mentions.GetValueOrDefault(companyId, []));
            tally[scan.Outcome] = tally.GetValueOrDefault(scan.Outcome) + 1;

            if (scan.IsRecognised)
            {
                recognised.Add(
                    $"**{company.Name} ({company.Ticker})** · {identifiers.Accession} · filed "
                    + $"{(item.PublishedAtUtc ?? item.CollectedAtUtc):yyyy-MM-dd} · acquirer "
                    + $"`{scan.AcquirerName}` · consideration `{scan.ConsiderationCurrency}"
                    + $"{scan.ConsiderationPerShare}` ({scan.ConsiderationKind})\n"
                    + $"  - target quote: {Trim(scan.TargetQuote!)}\n"
                    + $"  - consideration quote: {Trim(scan.ConsiderationQuote!)}");
            }
            else if (scan.Outcome is AcquisitionScanOutcome.AcquirerNotNamed
                or AcquisitionScanOutcome.NoStatedConsideration)
            {
                nearMisses.Add(
                    $"{company.Ticker ?? company.Name} {identifiers.Accession} — "
                    + AcquisitionAgreementScan.Token(scan.Outcome));
            }
        }

        output.WriteLine(Render(
            candidates.Count, untrustworthy, unresolved, tally, recognised, nearMisses, fetchFailed));

        // The DISTRIBUTION is the deliverable; these are the invariants it must not violate.
        Assert.Equal(candidates.Count, tally.Values.Sum() + fetchFailed.Count + unresolved);
        Assert.All(tally.Keys, o => Assert.Contains(o, AcquisitionAgreementScan.AllOutcomes));
    }

    private static IReadOnlyList<string> HintsOf(EvidenceItem evidence)
    {
        EvidenceMetadata.TryRead(evidence.MetadataJson, out _, out var hints);
        return hints;
    }

    private static string Trim(string quote) =>
        quote.Length <= 240 ? quote : quote[..240] + "…";

    private static string Render(
        int candidates,
        int untrustworthy,
        int unresolved,
        IReadOnlyDictionary<AcquisitionScanOutcome, int> tally,
        IReadOnlyList<string> recognised,
        IReadOnlyList<string> nearMisses,
        IReadOnlyList<string> fetchFailed)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"### Spec 217 §1 — live `{AcquisitionAgreementScan.Version}` distribution over every item-1.01 filing in the store\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Item-1.01 filing evidence identified: **{candidates}** (+{untrustworthy} with untrustworthy identifiers, never fetched).\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Company unresolved (skipped before any fetch): {unresolved}.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Fetch failed (re-attempted on a later run; NOT a \"no acquisition\" answer): {fetchFailed.Count}.\n\n");

        sb.Append("| acqscan-v1 outcome | count |\n| --- | ---: |\n");
        foreach (var outcome in AcquisitionAgreementScan.AllOutcomes)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {AcquisitionAgreementScan.Token(outcome)} | {tally.GetValueOrDefault(outcome)} |\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"\n**Recognised: {recognised.Count}.** Expected 1 (MarineMax / HZO, accession 0001193125-26-341302). ANY second recognition must be investigated by hand and named in the PR body — a false positive closes a live thesis.\n\n");
        foreach (var line in recognised)
        {
            sb.Append("- ").Append(line).Append('\n');
        }

        if (nearMisses.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\nNear misses (leg (a) held, leg (b) or the acquirer name did not) — {nearMisses.Count}:\n");
            foreach (var line in nearMisses)
            {
                sb.Append("- ").Append(line).Append('\n');
            }
        }

        if (fetchFailed.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\nFetch failures — {fetchFailed.Count}:\n");
            foreach (var line in fetchFailed)
            {
                sb.Append("- ").Append(line).Append('\n');
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Runs the spec-217 §1 live measurement only when BOTH a data root and a compliant SEC User-Agent are
/// configured; otherwise skips with a named reason. A missing UA must never produce a run of 403s that
/// could be mistaken for a distribution.
/// </summary>
public sealed class AcqScanLiveFactAttribute : FactAttribute
{
    public AcqScanLiveFactAttribute()
    {
        if (AcquisitionRecognitionLiveMeasurementTests.DataRoot() is null
            || AcquisitionRecognitionLiveMeasurementTests.UserAgent() is null)
        {
            Skip = AcquisitionRecognitionLiveMeasurementTests.SkipReason;
        }
    }
}
