using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Filings;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;

namespace Radar.Application.Acquisitions;

/// <summary>
/// The counted outcome of ONE recognition pass (spec 217 §1). Every item-1.01 filing the pass considered
/// lands in exactly one bucket — nothing is discarded without being counted (CLAUDE.md).
/// </summary>
/// <param name="Item101Filings">Item-1.01 filing evidence items identified in the store this pass.</param>
/// <param name="AlreadyRecognised">Skipped because a record for (company, accession) is already durable.</param>
/// <param name="AlreadyScanned">Skipped because <c>acqscan-v1</c> already answered for this accession (cache hit).</param>
/// <param name="UnresolvedCompany">Skipped because the filing's company could not be resolved from its hints.</param>
/// <param name="UnparseableIdentifiers">Item-1.01 filings whose CIK/accession could not be trusted (never guessed).</param>
/// <param name="FetchFailed">The bounded body fetch failed; a later run re-attempts the filing.</param>
/// <param name="ByOutcome">The <c>acqscan-v1</c> outcome tally, over fresh scans AND replayed cache hits.</param>
/// <param name="Recognised">The records recognised and durably persisted THIS pass.</param>
/// <param name="NotPersisted">Recognitions whose durable write FAILED — a loss, counted, never reported as stored.</param>
/// <param name="CacheNotWritten">Authoritative answers whose cache write failed — a re-fetch next run, counted.</param>
/// <param name="FetchBudgetRemaining">
/// Item-1.01 filings left unscanned because the per-run fetch budget was exhausted. They drain over later
/// runs — a bounded backlog, never an invisible one.
/// </param>
public sealed record AcquisitionRecognitionResult(
    int Item101Filings,
    int AlreadyRecognised,
    int AlreadyScanned,
    int UnresolvedCompany,
    int UnparseableIdentifiers,
    int FetchFailed,
    IReadOnlyDictionary<AcquisitionScanOutcome, int> ByOutcome,
    IReadOnlyList<PendingAcquisitionRecord> Recognised,
    int NotPersisted,
    int CacheNotWritten,
    int FetchBudgetRemaining)
{
    /// <summary>The pass that ran nothing (not composed) — an honest all-zero record, never a null.</summary>
    public static readonly AcquisitionRecognitionResult NotRun =
        new(0, 0, 0, 0, 0, 0, new Dictionary<AcquisitionScanOutcome, int>(), [], 0, 0, 0);
}

/// <summary>The Application seam the pipeline runner calls between collection and scoring (spec 217 §1).</summary>
public interface IAcquisitionRecognitionPass
{
    Task<AcquisitionRecognitionResult> RunAsync(IReadOnlyList<Company> companies, CancellationToken ct);
}

/// <summary>
/// SPEC 217 §1 — the deterministic acquisition-recognition pass. It walks the accrued evidence store for
/// item-1.01 8-Ks, resolves each to a company, fetches that filing's own text ONCE (bounded, paced and
/// cached), runs the pure <see cref="AcquisitionAgreementScan"/>, and durably persists every recognition.
/// <para>
/// <b>Every skip is counted and surfaced</b> in <see cref="AcquisitionRecognitionResult"/> and in ONE
/// aggregated log line per pass (never one line per filing — CLAUDE.md). The buckets are deliberately
/// distinct facts: already recognised, already scanned, unresolved company, untrustworthy identifiers,
/// fetch failed, budget exhausted, and the per-outcome <c>acqscan-v1</c> tally naming each failed leg.
/// </para>
/// <para>
/// <b>It never writes evidence, a signal or a score.</b> Its only durable outputs are the append-only
/// acquisitions store and the heal-forward scan cache; a failed write of either is COUNTED and never
/// reported as stored (spec 206's rule).
/// </para>
/// <para>
/// <b>Fetch cost is bounded per run</b> by <see cref="AcquisitionRecognitionOptions.MaxFetchesPerRun"/>.
/// Filings are walked NEWEST-FIRST (a takeover is news, and the newest unscanned filing is the one most
/// likely to close a live thesis), and the unscanned remainder is reported rather than hidden, so a backlog
/// drains over successive runs instead of starving.
/// </para>
/// </summary>
public sealed class AcquisitionRecognitionPass : IAcquisitionRecognitionPass
{
    /// <summary>
    /// The SEC form and item code this pass reads. Item 1.01 = "Entry into a Material Definitive
    /// Agreement". PUBLIC because the spec 217 §1 LIVE MEASUREMENT must select exactly the same population
    /// the pass selects — a harness with its own copy of these two literals would report a distribution over
    /// a different set of filings and nobody could tell (reuse over copy, CLAUDE.md).
    /// </summary>
    public const string FormCode = "8-K";

    /// <inheritdoc cref="FormCode"/>
    public const string ItemCode = "1.01";

    private readonly IEvidenceRepository _evidence;
    private readonly ICompanyResolver _resolver;
    private readonly IAcquisitionFilingBodyReader _reader;
    private readonly IAcquisitionStore _store;
    private readonly IAcquisitionScanCache _scanCache;
    private readonly AcquisitionRecognitionOptions _options;
    private readonly ILogger<AcquisitionRecognitionPass> _logger;

    public AcquisitionRecognitionPass(
        IEvidenceRepository evidence,
        ICompanyResolver resolver,
        IAcquisitionFilingBodyReader reader,
        IAcquisitionStore store,
        IAcquisitionScanCache scanCache,
        AcquisitionRecognitionOptions options,
        ILogger<AcquisitionRecognitionPass> logger)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scanCache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _evidence = evidence;
        _resolver = resolver;
        _reader = reader;
        _store = store;
        _scanCache = scanCache;
        _options = options;
        _logger = logger;
    }

    public async Task<AcquisitionRecognitionResult> RunAsync(
        IReadOnlyList<Company> companies, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(companies);

        var mentionsByCompany = BuildMentionIndex(companies);

        var all = await _evidence.GetAllAsync(ct).ConfigureAwait(false);

        // Newest first, id-tiebroken (AD-3): the fetch budget must be spent on the filings most likely to
        // close a live thesis, and the order must not depend on store enumeration order.
        var candidates = new List<(EvidenceItem Evidence, FilingEvidenceIdentifiers Identifiers)>();
        var unparseable = 0;
        foreach (var item in all)
        {
            if (FilingEvidenceFacts.TryResolve(item, FormCode, ItemCode, out var identifiers, out var rejection))
            {
                candidates.Add((item, identifiers!));
            }
            else if (rejection is FilingEvidenceRejection.UnparseableSourceUrl
                or FilingEvidenceRejection.AccessionMismatch)
            {
                // An item-1.01 8-K we could NOT identify. Counted so "we saw N" never quietly becomes
                // "we could identify N".
                unparseable++;
            }
        }

        candidates.Sort(static (a, b) =>
        {
            var aWhen = a.Evidence.PublishedAtUtc ?? a.Evidence.CollectedAtUtc;
            var bWhen = b.Evidence.PublishedAtUtc ?? b.Evidence.CollectedAtUtc;
            var byWhen = bWhen.CompareTo(aWhen);
            return byWhen != 0 ? byWhen : a.Evidence.Id.CompareTo(b.Evidence.Id);
        });

        var byOutcome = new Dictionary<AcquisitionScanOutcome, int>();
        var recognised = new List<PendingAcquisitionRecord>();
        var alreadyRecognised = 0;
        var alreadyScanned = 0;
        var unresolvedCompany = 0;
        var fetchFailed = 0;
        var notPersisted = 0;
        var cacheNotWritten = 0;
        var fetches = 0;
        var budgetRemaining = 0;

        foreach (var (evidence, identifiers) in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var resolution = await _resolver
                .ResolveAsync(evidence.Title, CompanyHintsOf(evidence), ct)
                .ConfigureAwait(false);
            if (resolution.CompanyId is not { } companyId)
            {
                unresolvedCompany++;
                continue;
            }

            if (await _store.ExistsAsync(companyId, identifiers.Accession, ct).ConfigureAwait(false))
            {
                alreadyRecognised++;
                continue;
            }

            // The NEGATIVE half of the cache: an accession acqscan-v1 has already answered for under THIS
            // scan version is never re-fetched. Without it the 177 not-recognised item-1.01 filings would
            // be re-fetched on every run, forever. A version bump retires every entry (heal-forward).
            var cached = await _scanCache.TryGetAsync(identifiers.Accession, ct).ConfigureAwait(false);
            if (cached is not null
                && string.Equals(
                    cached.ScanVersion, AcquisitionAgreementScan.Version, StringComparison.Ordinal))
            {
                alreadyScanned++;
                byOutcome[cached.Outcome] = byOutcome.GetValueOrDefault(cached.Outcome) + 1;
                continue;
            }

            if (fetches >= _options.MaxFetchesPerRun)
            {
                budgetRemaining++;
                continue;
            }

            fetches++;
            var body = await _reader
                .ReadAsync(identifiers.Cik, identifiers.Accession, identifiers.PrimaryDocument, ct)
                .ConfigureAwait(false);
            if (!body.IsSuccess)
            {
                fetchFailed++;
                continue;
            }

            var mentions = mentionsByCompany.TryGetValue(companyId, out var m) ? m : [];
            var scan = AcquisitionAgreementScan.Scan(body.PlainText, mentions);
            byOutcome[scan.Outcome] = byOutcome.GetValueOrDefault(scan.Outcome) + 1;

            // Cache ONLY an authoritative answer (the spec-114 rule): an EmptyBody read means the fetch was
            // degenerate, so it is left uncached and a later healthy run re-attempts it.
            if (scan.Outcome != AcquisitionScanOutcome.EmptyBody)
            {
                var cacheWritten = await _scanCache
                    .SetAsync(
                        new AcquisitionScanCacheRecord(
                            identifiers.Accession,
                            companyId,
                            scan.Outcome,
                            AcquisitionAgreementScan.Version,
                            evidence.PublishedAtUtc ?? evidence.CollectedAtUtc),
                        ct)
                    .ConfigureAwait(false);
                if (!cacheWritten)
                {
                    cacheNotWritten++;
                }
            }

            if (!scan.IsRecognised)
            {
                continue;
            }

            var record = new PendingAcquisitionRecord(
                Id: PendingAcquisitionRecord.IdentityFor(
                    AcquisitionAgreementScan.Version, companyId, identifiers.Accession),
                CompanyId: companyId,
                Accession: identifiers.Accession,
                EvidenceId: evidence.Id,
                AnnouncedOnUtc: evidence.PublishedAtUtc ?? evidence.CollectedAtUtc,
                AcquirerName: scan.AcquirerName!,
                ConsiderationPerShare: scan.ConsiderationPerShare!,
                ConsiderationCurrency: scan.ConsiderationCurrency!,
                ConsiderationKind: scan.ConsiderationKind!.Value,
                ConsiderationQuote: scan.ConsiderationQuote!,
                TargetQuote: scan.TargetQuote!,
                ScanVersion: AcquisitionAgreementScan.Version,
                Verification: AcquisitionVerification.Verbatim);

            var write = await _store.WriteIfNewAsync(record, ct).ConfigureAwait(false);
            if (write.Written)
            {
                recognised.Add(record);
            }
            else
            {
                // A recognition that never reached disk is a LOSS, not a success: the next run re-fetches
                // and re-recognises the same filing, and until then the thesis stays open. Counted here and
                // named in the aggregated line below (spec 206's rule).
                notPersisted++;
                _logger.LogWarning(
                    "Recognised a pending acquisition of company {CompanyId} from accession {Accession} but "
                        + "the acquisitions store write did not persist (path {Path}); it will be "
                        + "re-attempted on the next run.",
                    companyId,
                    identifiers.Accession,
                    write.Path);
            }
        }

        var result = new AcquisitionRecognitionResult(
            candidates.Count,
            alreadyRecognised,
            alreadyScanned,
            unresolvedCompany,
            unparseable,
            fetchFailed,
            byOutcome,
            recognised,
            notPersisted,
            cacheNotWritten,
            budgetRemaining);

        LogPass(result);
        return result;
    }

    /// <summary>
    /// ONE aggregated line per pass (never one per filing — CLAUDE.md), naming every bucket including the
    /// zero ones: a measured zero is information, and omitting it would make "nothing failed" and "nothing
    /// was looked at" indistinguishable.
    /// </summary>
    private void LogPass(AcquisitionRecognitionResult result)
    {
        var tally = string.Join(
            ", ",
            AcquisitionAgreementScan.AllOutcomes.Select(o =>
                AcquisitionAgreementScan.Token(o)
                    + "="
                    + AcquisitionAgreementScan.Int(result.ByOutcome.GetValueOrDefault(o))));

        _logger.LogInformation(
            "Acquisition recognition ({ScanVersion}): {Candidates} item-1.01 filing(s) identified "
                + "(+{Unparseable} with untrustworthy identifiers); {AlreadyRecognised} already recognised / "
                + "{AlreadyScanned} already scanned (cache hit) / {UnresolvedCompany} unresolved company / "
                + "{FetchFailed} fetch failed / {BudgetRemaining} left for a later run (budget {Budget}); "
                + "scan tally {Tally}; {Recognised} newly recognised, {NotPersisted} recognised but NOT "
                + "persisted, {CacheNotWritten} authoritative answer(s) not cached.",
            AcquisitionAgreementScan.Version,
            result.Item101Filings,
            result.UnparseableIdentifiers,
            result.AlreadyRecognised,
            result.AlreadyScanned,
            result.UnresolvedCompany,
            result.FetchFailed,
            result.FetchBudgetRemaining,
            _options.MaxFetchesPerRun,
            tally,
            result.Recognised.Count,
            result.NotPersisted,
            result.CacheNotWritten);
    }

    /// <summary>
    /// The company's own mentions for the scan: name and legal name, each also offered with its legal
    /// suffix stripped (a filing writes "MarineMax, Inc." where the seed writes "MarineMax", or the other
    /// way round). Seed ALIASES are deliberately excluded — a short trade-name alias would match inside
    /// unrelated prose, and leg (a) is meant to fail closed. Nothing is invented: a stripped form is always
    /// a strict prefix of a seeded name.
    /// </summary>
    /// <remarks>
    /// PUBLIC for the same reason <see cref="FormCode"/> is: the live measurement must feed the scan the
    /// SAME mentions the pass feeds it, or its distribution describes a different rule.
    /// </remarks>
    public static Dictionary<Guid, IReadOnlyList<string>> BuildMentionIndex(
        IReadOnlyList<Company> companies)
    {
        var index = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var company in companies)
        {
            var mentions = new List<string>();
            AddIfUsable(mentions, company.Name);
            AddIfUsable(mentions, company.LegalName);

            foreach (var name in mentions.ToList())
            {
                AddIfUsable(mentions, StripLegalSuffix(name));
            }

            index[company.Id] = mentions;
        }

        return index;
    }

    private static void AddIfUsable(List<string> into, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 3 && !into.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            into.Add(trimmed);
        }
    }

    private static readonly string[] LegalSuffixes =
    [
        ", Incorporated", ", Inc.", ", Inc", " Incorporated", " Inc.", " Inc",
        ", Corporation", ", Corp.", ", Corp", " Corporation", " Corp.", " Corp",
        ", Limited", ", Ltd.", ", Ltd", ", LLC", " LLC", ", L.P.", ", LP",
        ", plc", " plc", ", Co.", " Company", ", N.V.", ", S.A.",
    ];

    private static string? StripLegalSuffix(string name)
    {
        foreach (var suffix in LegalSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = name[..^suffix.Length].TrimEnd(',', ' ');
                return stripped.Length >= 3 ? stripped : null;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> CompanyHintsOf(EvidenceItem evidence)
    {
        EvidenceMetadata.TryRead(evidence.MetadataJson, out _, out var hints);
        return hints;
    }
}

/// <summary>
/// The recognition pass's operational knobs (spec 217 §1). Deliberately NOT scoring-fingerprint inputs:
/// they bound how MANY filings are read per run, never whether a filing that IS read is recognised — the
/// same argument that keeps <c>MaxFilingsPerRun</c> out of the directional-filing descriptor (spec 105).
/// </summary>
public sealed class AcquisitionRecognitionOptions
{
    /// <summary>
    /// How many NEW item-1.01 body fetches one run may make. A scanned filing is never re-fetched (the
    /// scan cache and the acquisitions store are the cache), so in steady state this is the number of
    /// newly-filed item-1.01 8-Ks per run — small. The default drains the accrued 178-filing backlog over a
    /// handful of runs without adding a burst to the shared SEC footprint.
    /// </summary>
    public int MaxFetchesPerRun { get; init; } = 40;
}
