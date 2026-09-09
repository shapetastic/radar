using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Efficacy.Statistics;
using Radar.Application.EntityResolution;
using Radar.Application.Filings;
using Radar.Application.News;
using Radar.Application.NewsTyping;
using Radar.Application.Prices;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;

namespace Radar.Application.Efficacy.FilingReads;

/// <summary>
/// Builds the spec-218 measurement of what the AI directional filing read actually produces: its live
/// distribution (<c>directional-filing-read-distribution-v1</c>), the groundedness of its <c>Reason</c>
/// (<c>filing-reason-groundedness-v1</c>), and its disagreement with the rest of Radar's own record in the
/// same window (<c>filing-read-disagreement-v1</c>).
/// <para>
/// <b>READ-ONLY, and it moves nothing.</b> It changes no score, prompt, schema, weight, formula or strategy;
/// it touches no <c>SignalSourceDescriptor</c> field and no <c>ScoringConfigVersion</c> segment; it declares
/// no gate and no threshold. Price is read strictly downstream of scoring and is DESCRIPTIVE only (AD-14).
/// </para>
/// <para>
/// <b>The <c>cacheVersion</c> judgement, stated so the next reader does not "fix" it back.</b> Spec 218 §1
/// lists "records with <c>cacheVersion</c> below current" among the exclusions. Applying that literally would
/// be wrong here, for two reasons. First, the production cache's own rule is OUTCOME-SCOPED (see
/// <c>AnalyzedFilingRecord.CurrentCacheVersion</c>): a v2 <c>DirectionalSignalProduced</c> record stays a HIT
/// and keeps being replayed into scoring, so excluding it would report a distribution that does not describe
/// the reads Radar is actually using. Second, the live corpus that motivated the spec is overwhelmingly v2:
/// 446 v2 / 49 v3 of the 495 records IN THE CURRENT MODEL SEGMENT, plus 5 files at the cache root outside the
/// segment that this enumeration deliberately excludes and counts as
/// <c>OutsideCurrentModelSegmentFiles</c> (measured 2026-09-09). So the exclusion would report over ~49
/// records while the spec's own headline quotes 242 of 500. The PRIMARY distribution covers ALL parseable records, the cacheVersion
/// split is named and counted as its own dimension, and a current-version-only distribution is reported
/// BESIDE it with its own denominator stated. Nothing is dropped; a reader can compute either view.
/// </para>
/// <para>
/// <b>Failure posture.</b> Every unavailable arm is a NAMED, COUNTED state on the row and in the summary. No
/// read is skipped silently, no cap truncates without reporting its remainder, and a <c>null</c> means "not
/// recorded" — never <c>0</c> and never <c>false</c>.
/// </para>
/// </summary>
public sealed class DirectionalFilingReadReporter
{
    /// <summary>
    /// The forward window, in CALENDAR days, shared verbatim with <see cref="ForwardReturn.TryCompute"/> and
    /// matching the news-risk evaluator's constants. Twenty-one calendar days with a four-day exit tolerance
    /// is NOT "21 sessions" — every rendering says calendar days, because that is what the primitive computes.
    /// </summary>
    public const int HorizonDays = 21;

    /// <summary>The spec-152 measured exit tolerance (4 calendar days), stated at the call site as ForwardReturn requires.</summary>
    public const int ExitToleranceDays = 4;

    /// <summary>The ±N-day window, in calendar days, joining typed news to a filing date (spec 218 §3).</summary>
    public const int NewsWindowDays = 3;

    /// <summary>The per-read cap on VERBATIM matched statements. The remainder is always counted, never dropped.</summary>
    public const int MaxMatchedStatementsPerRead = 5;

    /// <summary>The cap on worked-example news lines rendered. The remainder is counted beside them.</summary>
    public const int MaxWorkedExampleNewsLines = 25;

    /// <summary>The spec-218 §4 worked example's ticker. Resolved from live data, never hard-coded prose.</summary>
    public const string WorkedExampleTicker = "POWL";

    /// <summary>The worked example's filing date (the 8-K carrying items 2.02 and 8.01).</summary>
    public static readonly DateOnly WorkedExampleFilingDate = new(2026, 8, 3);

    /// <summary>The date Radar labelled the company from that read.</summary>
    public static readonly DateOnly WorkedExampleLabelDate = new(2026, 8, 4);

    /// <summary>
    /// Exactly which evidence fields the §2 figure search reads, named because "appears anywhere in the
    /// evidence record's stored text" must be auditable rather than implied.
    /// </summary>
    public const string EvidenceFieldsSearched = "rawText + title + summary";

    /// <summary>
    /// What counts as the METADATA-DERIVED header a coincidental figure match comes from. Named because the
    /// coincidental/genuine split is the whole meaning of the §2 figure count, and a reader must be able to
    /// check the rule rather than trust the classification.
    /// </summary>
    public const string StructuralHeaderDescription =
        "every value of the evidence record's metadata envelope (accessionNumber, form, filingDate, items, "
            + "secFeedUrl, primaryDocument, quality, …) — the region a filing evidence record's ~113-character "
            + "rawText is composed FROM";

    /// <summary>How many excerpt-mismatch accessions the artifact names; the remainder is counted beside them.</summary>
    public const int MaxNamedMismatchAccessions = 25;

    /// <summary>
    /// What a build says when nothing hydrated and the four join stores were therefore never opened. It
    /// names the stores it did not read and — precisely — WHICH counts are consequently NOT COMPUTED, so a
    /// reader is not told that the per-read exclusion counts (which ARE true zeros over zero reads) are
    /// unmeasured too.
    /// </summary>
    public const string JoinStoresNotLoadedDetail =
        "No read record hydrated, so there was nothing to join: the evidence, company, news-typing and "
            + "news-observation stores were NOT LOADED for this run. The counts DERIVED from them — filing "
            + "evidence carrying no accessionNumber, accessions carrying more than one evidence record, the "
            + "groundedness section, and the disagreement section's typing accounting — are NOT COMPUTED, "
            + "not measured zeros. The per-read exclusion counts below ARE measured: they are zero because "
            + "there are zero reads.";

    /// <summary>
    /// Why the capped-confidence count is NOT RECORDED. <c>AnalyzedFilingRecord</c> stores only the EFFECTIVE
    /// (already comparability-capped) confidence, with no separate <c>cappedConfidence</c> field, so a capped
    /// and an uncapped value are indistinguishable on the record. Reporting 0 here would be a fabricated
    /// measurement. The closest thing the corpus DOES record — the spec-160 scan's marker groups — is
    /// reported instead, as its own named distribution.
    /// </summary>
    public const string CappedConfidenceNote =
        "NOT RECORDED: AnalyzedFilingRecord carries no cappedConfidence field — it stores only the EFFECTIVE "
            + "(already comparability-capped) confidence, so a capped value and an uncapped one are "
            + "indistinguishable on the record. The comparability-scan state distribution below is the "
            + "closest measured proxy the corpus does carry (CapTriggeringMarkersMatched = the cap could have "
            + "applied); it is NOT the capped count and must not be read as one.";

    private readonly IAnalyzedFilingReadCorpus? _corpus;
    private readonly IEvidenceRepository _evidence;
    private readonly ICompanyRepository _companies;
    private readonly ICompanyResolver _resolver;
    private readonly INewsTypingStore? _typings;
    private readonly INewsObservationArchive? _observations;
    private readonly IPriceHistoryStore _prices;
    private readonly ILogger<DirectionalFilingReadReporter> _logger;

    public DirectionalFilingReadReporter(
        IEvidenceRepository evidence,
        ICompanyRepository companies,
        ICompanyResolver resolver,
        IPriceHistoryStore prices,
        ILogger<DirectionalFilingReadReporter> logger,
        // Optional: the corpus seam exists only when the AI earnings read is registered. A null seam is
        // reported as SeamNotRegistered — never as an empty corpus, which would read as "zero reads".
        IAnalyzedFilingReadCorpus? corpus = null,
        // Optional: the typing step and the observation archive ride their own gates (a score-mode pass
        // registers neither). Both null is reported as NewsStoresNotRegistered on every read's news arm —
        // never as "no adverse news found".
        INewsTypingStore? typings = null,
        INewsObservationArchive? observations = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(companies);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(logger);

        _evidence = evidence;
        _companies = companies;
        _resolver = resolver;
        _typings = typings;
        _observations = observations;
        _prices = prices;
        _logger = logger;
        _corpus = corpus;
    }

    public async Task<DirectionalFilingReadReport> BuildAsync(CancellationToken ct)
    {
        if (_corpus is null)
        {
            var unreachable = EmptyReport(
                FilingReadCorpusAvailability.SeamNotRegistered,
                "No IAnalyzedFilingReadCorpus is registered: the AI earnings read is disabled for this "
                    + "process. Accrued read records may exist on disk; this run could not reach them. This "
                    + "is NOT a measured zero.");
            LogSummary(unreachable, FilingReadCorpusAvailability.SeamNotRegistered);
            return unreachable;
        }

        var corpus = await _corpus.ReadAllAsync(ct).ConfigureAwait(false);
        var availability = corpus.EnumerationFailed
            ? FilingReadCorpusAvailability.EnumerationFailed
            : corpus.CorpusDirectoryExists
                ? FilingReadCorpusAvailability.Available
                : FilingReadCorpusAvailability.DirectoryMissing;
        var unavailableDetail = availability switch
        {
            FilingReadCorpusAvailability.EnumerationFailed =>
                "The analyzed-filing cache directory could not be listed. Every count below describes only "
                    + "what was reached before the failure and is NOT a complete accounting of the corpus.",
            FilingReadCorpusAvailability.DirectoryMissing =>
                "The analyzed-filing cache directory for the current model segment does not exist. Zero "
                    + "records is a MEASURED absence here: nothing has been read under this segment yet.",
            _ => null,
        };

        // No record hydrated ⇒ there is NOTHING to join, so none of the four join stores is loaded: reading
        // four whole stores to join zero rows is pure I/O for a report that can only be empty. The corpus's
        // OWN measured counts (files scanned, unreadable, mismatched, outside the segment) are carried
        // through untouched, and JoinStoresLoaded=false marks exactly which counts did NOT get measured, so
        // no renderer and no reader can mistake an unloaded arm for a measured zero.
        if (corpus.Entries.Count == 0)
        {
            var report = EmptyReport(
                availability,
                (unavailableDetail is { } d ? d + " " : string.Empty) + JoinStoresNotLoadedDetail,
                corpus);
            LogSummary(report, availability);
            return report;
        }

        // ---- the joins, each loaded once -----------------------------------------------------------
        var evidenceByAccession = await BuildEvidenceIndexAsync(ct).ConfigureAwait(false);
        var companiesById = (await _companies.GetAllAsync(ct).ConfigureAwait(false))
            .ToDictionary(c => c.Id);
        var newsIndex = await BuildNewsIndexAsync(ct).ConfigureAwait(false);
        var priceCache = new Dictionary<string, PriceHistory?>(StringComparer.OrdinalIgnoreCase);

        var rows = new List<DirectionalFilingReadRow>(corpus.Entries.Count);
        foreach (var entry in corpus.Entries)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(await BuildRowAsync(
                    entry, evidenceByAccession, companiesById, newsIndex, priceCache, ct)
                .ConfigureAwait(false));
        }

        var composed = Compose(
            corpus, availability, unavailableDetail, rows, newsIndex, evidenceByAccession);
        LogSummary(composed, availability);
        return composed;
    }

    /// <summary>
    /// The ONE aggregated log line for a build, emitted on EVERY return path — including the early
    /// unavailable/empty ones, which would otherwise complete silently and leave their named exclusion
    /// counts unsurfaced.
    /// </summary>
    private void LogSummary(DirectionalFilingReadReport report, FilingReadCorpusAvailability availability)
    {
        if (availability == FilingReadCorpusAvailability.SeamNotRegistered)
        {
            // No seam ⇒ no enumeration happened either, so even the CORPUS-level file counts are
            // placeholders on this path. Logging "0 files scanned" beside SeamNotRegistered would invite an
            // operator to read a zero that was never taken.
            _logger.LogInformation(
                "Directional filing-read distribution: NOT MEASURED ({Availability}) — no "
                    + "IAnalyzedFilingReadCorpus is registered, so no file was enumerated and no store was "
                    + "loaded. Accrued read records may exist on disk; this run could not reach them.",
                availability);
            return;
        }

        if (!report.JoinStoresLoaded)
        {
            // The store-derived clauses are OMITTED rather than logged as zeros: with the stores unloaded
            // they were never measured, and "typings 0 scanned" in a log line reads exactly like a finding.
            _logger.LogInformation(
                "Directional filing-read distribution: {Total} accrued read(s) ({Availability}); "
                    + "{FilesScanned} file(s) scanned, {Unreadable} unreadable, {NameMismatch} "
                    + "filename/accession mismatch, {OutcomeMismatch} outcome/signal mismatch, {Outside} "
                    + "outside the current model segment. No record hydrated, so the evidence, company, "
                    + "news-typing and news-observation stores were NOT LOADED: the groundedness and "
                    + "disagreement accountings are NOT COMPUTED, not measured zeros.",
                report.AllRecords.TotalReads,
                availability,
                report.FilesScanned,
                report.UnreadableOrUnparseableFiles,
                report.FileNameAccessionMismatchFiles,
                report.OutcomeSignalMismatchFiles,
                report.OutsideCurrentModelSegmentFiles is { } notLoadedOutside
                    ? notLoadedOutside.ToString(CultureInfo.InvariantCulture)
                    : "(not counted)");
            return;
        }

        _logger.LogInformation(
            "Directional filing-read distribution: {Total} accrued read(s) ({Availability}); {Positive} "
                + "Positive, {Negative} Negative, {Other} other-token, {NoDirectional} no-directional "
                + "(Positive share of directional {Share}); excluded {Unreadable} unreadable, {NameMismatch} "
                + "filename/accession mismatch, {OutcomeMismatch} outcome/signal mismatch, {Outside} outside "
                + "the current model segment; {NoEvidence} with no matching evidence record, {Unresolved} "
                + "with an unresolved company, {Ambiguous} with an AMBIGUOUS evidence join; "
                + "excerpt==some-candidate-title {ExcerptEqualsTitle}/{Measured}, reason-contains-figure "
                + "{WithFigure}, figure-found-in-evidence {FigureFound} (of which {Coincidental} are "
                + "COINCIDENTAL header matches and {Genuine} genuine); typings {TypingsScanned} scanned = "
                + "{TypingsContributing} contributing + {TypingsNoFacts} no-facts + {TypingsNoCompany} "
                + "no-company + {TypingsNoObservation} no-observation + {TypingsNoInstant} "
                + "no-publication-instant; news arm evaluated for {NewsEvaluated} of {Directional} "
                + "directional read(s), {Disagreeing} Positive read(s) disagreeing.",
            report.AllRecords.TotalReads,
            availability,
            report.AllRecords.PositiveCount,
            report.AllRecords.NegativeCount,
            report.AllRecords.OtherDirectionTokenCount,
            report.AllRecords.NoDirectionalCount,
            FormatShare(report.AllRecords.PositiveShareOfDirectional),
            report.UnreadableOrUnparseableFiles,
            report.FileNameAccessionMismatchFiles,
            report.OutcomeSignalMismatchFiles,
            report.OutsideCurrentModelSegmentFiles is { } outside
                ? outside.ToString(CultureInfo.InvariantCulture)
                : "(not counted)",
            report.NoMatchingEvidenceRecordCount,
            report.UnresolvedCompanyCount,
            report.AmbiguousEvidenceJoinCount,
            report.Groundedness.ExcerptEqualsSomeEvidenceTitleCount,
            report.Groundedness.MeasuredReads,
            report.Groundedness.ReasonContainsNumericTokenCount,
            report.Groundedness.ReasonNumericTokenFoundInEvidenceTextCount,
            report.Groundedness.ReasonNumericTokenCoincidentalHeaderMatchCount,
            report.Groundedness.ReasonNumericTokenGenuineMatchCount,
            report.Disagreement.TypingRecordsScanned,
            report.Disagreement.TypingsContributingFacts,
            report.Disagreement.TypingsWithNoFacts,
            report.Disagreement.TypingsWithNoCompanyId,
            report.Disagreement.TypingsWithNoArchivedObservation,
            report.Disagreement.TypingsWithNoPublishedAt,
            report.Disagreement.NewsArmEvaluatedReads,
            report.Disagreement.DirectionalReads,
            report.Disagreement.PositiveReadsDisagreeing);
    }

    // -----------------------------------------------------------------------------------------------
    // Joins
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Indexes FILING evidence by its <c>accessionNumber</c> metadata key, read through the shared
    /// <see cref="EvidenceMetadata.TryRead"/> envelope reader exactly as the directional source does.
    /// <para>
    /// <b>Every candidate is KEPT.</b> An accession is not unique in the store: measured 2026-09-09, 71
    /// accessions carry TWO filing evidence records whose titles differ (a short and a long variant, from a
    /// collector change), and both share the same <c>publishedAtUtc</c>. Collapsing them to one record would
    /// make the winner a GUID tie-break, and every question asked of it — does the excerpt equal the title,
    /// does the Reason's figure appear in the stored text, which company hints apply — would then measure the
    /// tie-break rather than the read. So the index maps an accession to ALL its records, ordered
    /// deterministically (most recently published FIRST, then by id), and the caller asks the candidate SET.
    /// </para>
    /// <para>
    /// A filing evidence record with no <c>accessionNumber</c> key simply never joins; that count and the
    /// number of accessions holding more than one record both ride out on the returned index.
    /// </para>
    /// </summary>
    private async Task<EvidenceAccessionIndex> BuildEvidenceIndexAsync(CancellationToken ct)
    {
        var all = await _evidence.GetAllAsync(ct).ConfigureAwait(false);
        var ordered = all
            .Where(e => e.SourceType == EvidenceSourceType.Filing)
            .OrderByDescending(e => e.PublishedAtUtc ?? e.CollectedAtUtc)
            .ThenBy(e => e.Id)
            .ToList();

        var byAccession = new Dictionary<string, List<EvidenceCandidate>>(StringComparer.Ordinal);
        var metadataKeys = new SortedSet<string>(StringComparer.Ordinal);
        var withoutAccession = 0;

        foreach (var item in ordered)
        {
            ct.ThrowIfCancellationRequested();

            EvidenceMetadata.TryRead(item.MetadataJson, out var metadata, out var companyHints);
            if (!metadata.TryGetValue("accessionNumber", out var accession)
                || string.IsNullOrWhiteSpace(accession))
            {
                withoutAccession++;
                continue;
            }

            // The metadata KEYS actually present, so the artifact can name the structural header region a
            // coincidental figure match comes from instead of describing it in prose alone.
            foreach (var key in metadata.Keys)
            {
                metadataKeys.Add(key);
            }

            if (!byAccession.TryGetValue(accession, out var candidates))
            {
                byAccession[accession] = candidates = [];
            }

            candidates.Add(new EvidenceCandidate(item, metadata, companyHints));
        }

        return new EvidenceAccessionIndex(
            byAccession,
            withoutAccession,
            byAccession.Values.Count(c => c.Count > 1),
            [.. metadataKeys]);
    }

    /// <summary>
    /// Builds the per-company typed-fact index for the section-3 news arm: every typed fact joined to the
    /// PUBLICATION instant of the observation it was typed from (the archive is the only place that instant
    /// lives). EVERY typing that does not contribute a fact is a COUNTED, NAMED exclusion — no company id, no
    /// archived observation, no publication instant, or NO FACTS — so the four exclusion counts plus the
    /// contributing count reconcile exactly to the number scanned. (The no-facts case was an uncounted
    /// <c>continue</c> until the spec-218 review: 607 of 5,500 live typings carry zero facts, and they were
    /// invisible in an accounting that printed "scanned 5,500" beside three exclusions.)
    /// </summary>
    private async Task<NewsWindowIndex> BuildNewsIndexAsync(CancellationToken ct)
    {
        if (_typings is null || _observations is null)
        {
            return NewsWindowIndex.NotRegistered();
        }

        var typings = await _typings.GetAllAsync(ct).ConfigureAwait(false);
        var observations = await _observations.GetAllAsync(ct).ConfigureAwait(false);

        var publishedByObservation = new Dictionary<Guid, DateTimeOffset?>();
        foreach (var observation in observations)
        {
            publishedByObservation[observation.ObservationId] = observation.PublishedAtUtc;
        }

        var byCompany = new Dictionary<Guid, List<TypedFactInWindow>>();
        var noCompanyId = 0;
        var noObservation = 0;
        var noPublishedAt = 0;
        var noFacts = 0;
        var contributing = 0;
        DateOnly? firstTypingDate = null;

        foreach (var typing in typings)
        {
            ct.ThrowIfCancellationRequested();

            var createdDate = DateOnly.FromDateTime(typing.CreatedAtUtc.UtcDateTime);
            if (firstTypingDate is null || createdDate < firstTypingDate)
            {
                firstTypingDate = createdDate;
            }

            if (typing.CompanyId is not { } companyId)
            {
                noCompanyId++;
                continue;
            }

            if (!publishedByObservation.TryGetValue(typing.ObservationId, out var published))
            {
                noObservation++;
                continue;
            }

            if (published is not { } publishedAt)
            {
                noPublishedAt++;
                continue;
            }

            if (typing.Facts.Count == 0)
            {
                // A completed typing that produced no fact (InsufficientContent, or a typing whose facts were
                // all validation-dropped) contributes nothing to the window — but it is a record that was
                // read, so it is COUNTED here rather than dropped by a silent continue.
                noFacts++;
                continue;
            }

            contributing++;

            if (!byCompany.TryGetValue(companyId, out var list))
            {
                list = [];
                byCompany[companyId] = list;
            }

            var publishedDate = DateOnly.FromDateTime(publishedAt.UtcDateTime);
            foreach (var fact in typing.Facts)
            {
                list.Add(new TypedFactInWindow(publishedDate, fact));
            }
        }

        foreach (var list in byCompany.Values)
        {
            // Deterministic ordering (AD-3): date, then the fact's own id.
            list.Sort(static (a, b) => a.PublishedDate != b.PublishedDate
                ? a.PublishedDate.CompareTo(b.PublishedDate)
                : a.Fact.FactId.CompareTo(b.Fact.FactId));
        }

        return new NewsWindowIndex(
            byCompany,
            storesRegistered: true,
            typings.Count,
            contributing,
            noFacts,
            noCompanyId,
            noObservation,
            noPublishedAt,
            firstTypingDate);
    }

    // -----------------------------------------------------------------------------------------------
    // One row
    // -----------------------------------------------------------------------------------------------

    private async Task<DirectionalFilingReadRow> BuildRowAsync(
        AnalyzedFilingCorpusEntry entry,
        EvidenceAccessionIndex evidenceIndex,
        IReadOnlyDictionary<Guid, Company> companiesById,
        NewsWindowIndex newsIndex,
        Dictionary<string, PriceHistory?> priceCache,
        CancellationToken ct)
    {
        var record = entry.Record;
        var signal = record.Signal;

        var directionToken = signal?.Direction;
        var directionClass = record.Outcome != AnalyzedFilingOutcome.DirectionalSignalProduced
            ? FilingReadDirectionClass.NoDirectionalSignal
            : string.Equals(directionToken, "Positive", StringComparison.Ordinal)
                ? FilingReadDirectionClass.Positive
                : string.Equals(directionToken, "Negative", StringComparison.Ordinal)
                    ? FilingReadDirectionClass.Negative
                    : FilingReadDirectionClass.OtherDirectionToken;

        var (confidence, confidenceSource) = signal is not null
            ? ((decimal?)signal.Confidence, FilingReadConfidenceSource.ProducedSignalConfidence)
            : record.ReadConfidence is { } read
                ? (read, FilingReadConfidenceSource.RecordReadConfidence)
                : (null, FilingReadConfidenceSource.NotRecorded);

        var markers = record.ComparabilityMarkers;
        var scanState = markers is null
            ? FilingReadComparabilityScanState.NotScanned
            : markers.CapTriggering.Count > 0
                ? FilingReadComparabilityScanState.CapTriggeringMarkersMatched
                : markers.DiagnosticOnly.Count > 0
                    ? FilingReadComparabilityScanState.DiagnosticOnlyMarkersMatched
                    : FilingReadComparabilityScanState.ScannedClean;

        // EVERY evidence record carrying this accession, never one arbitrarily chosen winner.
        var candidates = evidenceIndex.ByAccession.TryGetValue(record.Accession, out var found)
            ? found
            : [];
        var evidenceJoin = candidates.Count switch
        {
            0 => FilingReadEvidenceJoin.NoMatchingEvidenceRecord,
            1 => FilingReadEvidenceJoin.Joined,
            _ => FilingReadEvidenceJoin.JoinedAmbiguous,
        };

        Guid? companyId = null;
        string? companyName = null;
        string? ticker = null;
        FilingReadCompanyResolution resolution;
        if (candidates.Count == 0)
        {
            resolution = FilingReadCompanyResolution.NoEvidenceRecord;
        }
        else
        {
            // The union of every candidate's hints, distinct and in candidate order: with two records for
            // one accession, taking only one record's hints would make resolution a tie-break too. The
            // resolver is already conservative — it ignores an unknown hint and refuses an AMBIGUOUS set
            // (more than one company matched), so widening the input can never fabricate a company.
            var hints = candidates
                .SelectMany(c => c.CompanyHints)
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var result = await _resolver
                .ResolveAsync(signal?.CompanyMention ?? string.Empty, hints, ct)
                .ConfigureAwait(false);
            if (result.CompanyId is { } id && companiesById.TryGetValue(id, out var company))
            {
                companyId = id;
                companyName = company.Name;
                ticker = string.IsNullOrWhiteSpace(company.Ticker) ? null : company.Ticker;
                resolution = FilingReadCompanyResolution.Resolved;
            }
            else
            {
                resolution = FilingReadCompanyResolution.Unresolved;
            }
        }

        // The first candidate (in the deterministic order above) that carries a publication instant. When an
        // accession holds several records they share it, so this is not a tie-break — but it is stated
        // rather than assumed, and a candidate set with none falls through to the cache's own instant.
        var published = candidates
            .Select(c => c.Item.PublishedAtUtc)
            .FirstOrDefault(p => p is not null);
        var (filingDate, filingDateSource) = published is { } publishedAt
            ? ((DateOnly?)DateOnly.FromDateTime(publishedAt.UtcDateTime),
                FilingReadFilingDateSource.EvidencePublishedAt)
            : record.ObservedAtUtc is { } observed
                ? (DateOnly.FromDateTime(observed.UtcDateTime), FilingReadFilingDateSource.CacheObservedAt)
                : (null, FilingReadFilingDateSource.NotRecorded);

        var groundedness = BuildGroundedness(directionClass, signal, candidates);
        var disagreement = await BuildDisagreementAsync(
                directionClass, companyId, ticker, filingDate, newsIndex, priceCache, ct)
            .ConfigureAwait(false);

        return new DirectionalFilingReadRow(
            Accession: record.Accession,
            SourceFileName: entry.SourceFileName,
            CacheVersion: record.CacheVersion,
            IsCurrentCacheVersion: record.CacheVersion == AnalyzedFilingRecord.CurrentCacheVersion,
            Outcome: record.Outcome,
            DirectionClass: directionClass,
            DirectionToken: directionToken,
            NoSignalCause: record.NoSignalCause,
            NoSignalCauseRecorded: record.NoSignalCause is not null,
            Confidence: confidence,
            ConfidenceSource: confidenceSource,
            ComparabilityScan: scanState,
            CapTriggeringMarkerCount: markers?.CapTriggering.Count,
            DiagnosticOnlyMarkerCount: markers?.DiagnosticOnly.Count,
            EvidenceJoin: evidenceJoin,
            EvidenceCandidateCount: candidates.Count,
            CompanyResolution: resolution,
            CompanyId: companyId,
            CompanyName: companyName,
            Ticker: ticker,
            FilingDate: filingDate,
            FilingDateSource: filingDateSource,
            // The MATCHED candidate's title when one matched the excerpt, else the first candidate's — so an
            // ambiguous join renders the record the read is consistent with, never an arbitrary sibling.
            EvidenceTitle: candidates.Count == 0
                ? null
                : (candidates.FirstOrDefault(c => string.Equals(
                        signal?.SupportingExcerpt, c.Item.Title, StringComparison.Ordinal))
                    ?? candidates[0]).Item.Title,
            SupportingExcerpt: signal?.SupportingExcerpt,
            Reason: signal?.Reason,
            Groundedness: groundedness,
            Disagreement: disagreement);
    }

    private static FilingReadGroundedness BuildGroundedness(
        FilingReadDirectionClass directionClass,
        Radar.Application.SignalExtraction.ExtractedSignal? signal,
        IReadOnlyList<EvidenceCandidate> candidates)
    {
        if (directionClass == FilingReadDirectionClass.NoDirectionalSignal || signal is null)
        {
            return FilingReadGroundedness.NotApplicable("not-a-directional-read", candidates.Count);
        }

        if (candidates.Count == 0)
        {
            // NOT a groundedness failure: there is simply nothing to check the excerpt or the figure against.
            return FilingReadGroundedness.NotApplicable("no-matching-evidence-record");
        }

        // Asked of the candidate SET. An accession may carry several evidence records with DIFFERENT titles
        // (a collector change produced a short and a long variant for 71 accessions, both with the same
        // publication instant), and the read does not record which one it was taken from. Comparing against a
        // single arbitrarily-chosen record therefore measures the tie-break, not the guard.
        var excerptEqualsSomeTitle = candidates.Any(
            c => string.Equals(signal.SupportingExcerpt, c.Item.Title, StringComparison.Ordinal));

        var tokens = ExtractNumericTokens(signal.Reason);

        // Two haystacks, deliberately. STORED is every candidate's rawText + title + summary. HEADER is the
        // metadata-derived region those records are built from (the envelope's values: accession, form,
        // filing date, item codes, feed url, primary document, …). A filing evidence record's rawText IS that
        // header — roughly a hundred characters — so a figure that matches there is a year, a form-type digit
        // or an accession fragment, NOT a figure grounded in the release. Splitting the two is what stops a
        // coincidence being rendered as groundedness.
        var stored = new StringBuilder();
        var header = new StringBuilder();
        foreach (var candidate in candidates)
        {
            stored.Append(candidate.Item.RawText ?? string.Empty).Append('\n')
                .Append(candidate.Item.Title ?? string.Empty).Append('\n')
                .Append(candidate.Item.Summary ?? string.Empty).Append('\n');
            foreach (var pair in candidate.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                header.Append(pair.Value).Append('\n');
            }
        }

        var storedText = stored.ToString();
        var headerText = header.ToString();

        var matched = new List<FilingReadMatchedToken>();
        foreach (var token in tokens)
        {
            if (!ContainsNumericToken(storedText, token))
            {
                // NOT an uncounted drop: the not-found set is exactly ReasonNumericTokens minus
                // ReasonNumericTokensFoundInEvidenceText, and BOTH ride out on the row (and the CSV).
                continue;
            }

            matched.Add(new FilingReadMatchedToken(token, ContainsNumericToken(headerText, token)));
        }

        var coincidental = matched.Count(m => m.CoincidentalHeaderMatch);

        // When the Reason asserted no figure at all, the follow-up questions ("does that figure appear in the
        // evidence text? is the match coincidental?") do not apply — so they are NOT RECORDED (null), never a
        // measured 0/false. "We looked and found nothing" and "there was nothing to look for" differ.
        return new FilingReadGroundedness(
            Applicable: true,
            NotApplicableReason: null,
            SupportingExcerptEqualsSomeEvidenceTitle: excerptEqualsSomeTitle,
            EvidenceCandidateCount: candidates.Count,
            ReasonContainsNumericToken: tokens.Count > 0,
            ReasonNumericTokens: tokens,
            ReasonNumericTokensFoundInEvidenceText: tokens.Count > 0 ? matched.Count : null,
            AnyReasonNumericTokenFoundInEvidenceText: tokens.Count > 0 ? matched.Count > 0 : null,
            ReasonNumericTokensMatchedCoincidentally: tokens.Count > 0 ? coincidental : null,
            ReasonNumericTokensMatchedGenuinely: tokens.Count > 0 ? matched.Count - coincidental : null,
            MatchedNumericTokens: matched);
    }

    private async Task<FilingReadDisagreement> BuildDisagreementAsync(
        FilingReadDirectionClass directionClass,
        Guid? companyId,
        string? ticker,
        DateOnly? filingDate,
        NewsWindowIndex newsIndex,
        Dictionary<string, PriceHistory?> priceCache,
        CancellationToken ct)
    {
        // ---- news arm -----------------------------------------------------------------------------
        FilingReadNewsArm arm;
        int? factsInWindow = null;
        int? negativeMatches = null;
        var statements = new List<string>();
        var omitted = 0;
        var terms = new List<string>();
        bool? disagrees = null;

        if (directionClass == FilingReadDirectionClass.NoDirectionalSignal)
        {
            arm = FilingReadNewsArm.NotADirectionalRead;
        }
        else if (!newsIndex.StoresRegistered)
        {
            arm = FilingReadNewsArm.NewsStoresNotRegistered;
        }
        else if (companyId is not { } id)
        {
            arm = FilingReadNewsArm.CompanyUnresolved;
        }
        else if (filingDate is not { } anchor)
        {
            arm = FilingReadNewsArm.FilingDateNotRecorded;
        }
        else
        {
            var facts = newsIndex.FactsForCompanyInWindow(id, anchor, NewsWindowDays);
            if (facts.Count == 0)
            {
                arm = newsIndex.FirstTypingDateUtc is { } first && anchor < first
                    ? FilingReadNewsArm.NoTypingCoverageInWindowPreTypingEra
                    : FilingReadNewsArm.NoTypedFactsInWindow;
            }
            else
            {
                arm = FilingReadNewsArm.Evaluated;
                factsInWindow = facts.Count;
                var matchCount = 0;
                var termSet = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var candidate in facts)
                {
                    var match = FilingReadDisagreementVocabulary.TryMatch(candidate.Fact);
                    if (match is null)
                    {
                        // NOT an uncounted drop: the non-matching set is exactly TypedFactsInWindow minus
                        // NegativeMatchCount, and both are recorded on the row.
                        continue;
                    }

                    matchCount++;
                    foreach (var term in match.MatchedEventTypes)
                    {
                        termSet.Add(term);
                    }

                    foreach (var term in match.MatchedPhrases)
                    {
                        termSet.Add(term);
                    }

                    if (statements.Count < MaxMatchedStatementsPerRead)
                    {
                        statements.Add(match.Statement);
                    }
                    else
                    {
                        // The cap NEVER hides a match: the remainder rides out as its own count.
                        omitted++;
                    }
                }

                negativeMatches = matchCount;
                terms.AddRange(termSet);
                disagrees = directionClass == FilingReadDirectionClass.Positive && matchCount > 0;
            }
        }

        // ---- price arm (DESCRIPTIVE, AD-14) --------------------------------------------------------
        var state = FilingReadForwardReturnState.FilingDateNotRecorded;
        double? forward = null;
        DateOnly? entryDate = null;
        DateOnly? exitDate = null;

        if (filingDate is { } asOf)
        {
            if (string.IsNullOrWhiteSpace(ticker))
            {
                state = FilingReadForwardReturnState.NoTicker;
            }
            else
            {
                if (!priceCache.TryGetValue(ticker, out var history))
                {
                    history = await _prices.ReadAsync(ticker, ct).ConfigureAwait(false);
                    priceCache[ticker] = history;
                }

                if (history is null || history.Bars.Count == 0)
                {
                    state = FilingReadForwardReturnState.NoPriceHistory;
                }
                else
                {
                    var result = ForwardReturn.TryCompute(
                        history.Bars, asOf, HorizonDays, ExitToleranceDays);
                    if (result.IsDefined)
                    {
                        state = FilingReadForwardReturnState.Computed;
                        forward = result.Value;
                        entryDate = result.EntryDate;
                        exitDate = result.ExitDate;
                    }
                    else
                    {
                        state = MapUnavailableReason(result.Reason);
                    }
                }
            }
        }

        return new FilingReadDisagreement(
            NewsArm: arm,
            TypedFactsInWindow: factsInWindow,
            NegativeMatchCount: negativeMatches,
            MatchedStatements: statements,
            MatchedStatementsOmitted: omitted,
            MatchedVocabularyTerms: terms,
            DisagreesWithPositiveRead: disagrees,
            ForwardReturnState: state,
            ForwardReturn21d: forward,
            ForwardEntryDate: entryDate,
            ForwardExitDate: exitDate);
    }

    /// <summary>
    /// Maps every <see cref="ForwardReturnUnavailableReason"/> member onto a named state. <c>None</c> cannot
    /// occur on an undefined result; it throws rather than being silently folded into another bucket, so a
    /// future member added to the shared enum fails loudly instead of disappearing into a default.
    /// </summary>
    private static FilingReadForwardReturnState MapUnavailableReason(ForwardReturnUnavailableReason reason) =>
        reason switch
        {
            ForwardReturnUnavailableReason.NoForwardBar => FilingReadForwardReturnState.NoForwardBar,
            ForwardReturnUnavailableReason.SingleForwardBar => FilingReadForwardReturnState.SingleForwardBar,
            ForwardReturnUnavailableReason.NonPositiveEntryPrice =>
                FilingReadForwardReturnState.NonPositiveEntryPrice,
            ForwardReturnUnavailableReason.PartialWindow => FilingReadForwardReturnState.PartialWindow,
            _ => throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "An undefined forward return must carry a named unavailable reason; this member has no "
                    + "mapped state and must not be pooled into an existing one."),
        };

    // -----------------------------------------------------------------------------------------------
    // Aggregation
    // -----------------------------------------------------------------------------------------------

    private DirectionalFilingReadReport Compose(
        AnalyzedFilingCorpus corpus,
        FilingReadCorpusAvailability availability,
        string? unavailableDetail,
        IReadOnlyList<DirectionalFilingReadRow> rows,
        NewsWindowIndex newsIndex,
        EvidenceAccessionIndex evidenceIndex)
    {
        var currentVersionRows = rows.Where(r => r.IsCurrentCacheVersion).ToList();
        var dated = rows.Where(r => r.FilingDate is not null).Select(r => r.FilingDate!.Value).ToList();

        return new DirectionalFilingReadReport(
            DistributionVersion: DirectionalFilingReadReport.DistributionVersionToken,
            GroundednessVersion: DirectionalFilingReadReport.GroundednessVersionToken,
            DisagreementVersion: FilingReadDisagreementVocabulary.Version,
            CorpusAvailability: availability,
            CorpusUnavailableDetail: unavailableDetail,
            JoinStoresLoaded: true,
            ModelSegment: corpus.ModelSegment,
            FilesScanned: corpus.FilesScanned,
            RecordsHydrated: corpus.RecordsHydrated,
            UnreadableOrUnparseableFiles: corpus.UnreadableOrUnparseableFiles,
            OutsideCurrentModelSegmentFiles: corpus.OutsideCurrentModelSegmentFiles,
            FileNameAccessionMismatchFiles: corpus.FileNameAccessionMismatchFiles,
            OutcomeSignalMismatchFiles: corpus.OutcomeSignalMismatchFiles,
            NoMatchingEvidenceRecordCount:
                rows.Count(r => r.EvidenceJoin == FilingReadEvidenceJoin.NoMatchingEvidenceRecord),
            FilingEvidenceWithoutAccessionMetadata: evidenceIndex.FilingEvidenceWithoutAccessionMetadata,
            AccessionsWithMultipleEvidenceRecords: evidenceIndex.AccessionsWithMultipleEvidenceRecords,
            AmbiguousEvidenceJoinCount:
                rows.Count(r => r.EvidenceJoin == FilingReadEvidenceJoin.JoinedAmbiguous),
            UnresolvedCompanyCount:
                rows.Count(r => r.CompanyResolution == FilingReadCompanyResolution.Unresolved),
            FilingDateNotRecordedCount:
                rows.Count(r => r.FilingDateSource == FilingReadFilingDateSource.NotRecorded),
            CurrentCacheVersion: AnalyzedFilingRecord.CurrentCacheVersion,
            CacheVersions: CountBy(
                rows, r => "cacheVersion=" + r.CacheVersion.ToString(CultureInfo.InvariantCulture)),
            CappedConfidenceRecordedCount: null,
            CappedConfidenceNote: CappedConfidenceNote,
            ComparabilityScanStates: CountEnum<FilingReadComparabilityScanState, DirectionalFilingReadRow>(
                rows, r => r.ComparabilityScan),
            AllRecords: BuildDirectionSummary("all parseable accrued read records", rows),
            CurrentCacheVersionRecords: BuildDirectionSummary(
                "records at cacheVersion " + AnalyzedFilingRecord.CurrentCacheVersion.ToString(
                    CultureInfo.InvariantCulture) + " only",
                currentVersionRows),
            DirectionalConfidence: BuildConfidenceSummary(
                "directional reads",
                "signal.confidence",
                rows.Where(r => r.DirectionClass != FilingReadDirectionClass.NoDirectionalSignal).ToList()),
            NoSignalConfidence: BuildConfidenceSummary(
                "no-directional reads",
                "record.readConfidence",
                rows.Where(r => r.DirectionClass == FilingReadDirectionClass.NoDirectionalSignal).ToList()),
            PerCompanyReadCounts: BuildPerCompanyCounts(rows),
            WindowStartUtc: dated.Count > 0 ? dated.Min() : null,
            WindowEndUtc: dated.Count > 0 ? dated.Max() : null,
            ForwardHorizonDays: HorizonDays,
            ForwardExitToleranceDays: ExitToleranceDays,
            Groundedness: BuildGroundednessSummary(rows, evidenceIndex.MetadataKeysObserved),
            Disagreement: BuildDisagreementSummary(rows, newsIndex),
            WorkedExample: BuildWorkedExample(rows, newsIndex),
            Rows: rows);
    }

    private static FilingReadDirectionSummary BuildDirectionSummary(
        string denominator, IReadOnlyList<DirectionalFilingReadRow> rows)
    {
        var positive = rows.Count(r => r.DirectionClass == FilingReadDirectionClass.Positive);
        var negative = rows.Count(r => r.DirectionClass == FilingReadDirectionClass.Negative);
        var other = rows.Count(r => r.DirectionClass == FilingReadDirectionClass.OtherDirectionToken);
        var none = rows.Count(r => r.DirectionClass == FilingReadDirectionClass.NoDirectionalSignal);
        var directional = positive + negative + other;

        // EVERY FilingNoSignalCause member is named and counted, including members that no record on disk may
        // legitimately carry (EmptyBody): a structural zero is a fact, and an absent row would look like an
        // oversight.
        var causes = new List<FilingReadCount>();
        foreach (var cause in Enum.GetValues<FilingNoSignalCause>())
        {
            causes.Add(new FilingReadCount(cause.ToString(), rows.Count(r => r.NoSignalCause == cause)));
        }

        return new FilingReadDirectionSummary(
            Denominator: denominator,
            TotalReads: rows.Count,
            PositiveCount: positive,
            NegativeCount: negative,
            OtherDirectionTokenCount: other,
            OtherDirectionTokens: CountBy(
                rows.Where(r => r.DirectionClass == FilingReadDirectionClass.OtherDirectionToken).ToList(),
                r => r.DirectionToken ?? "(null direction token)"),
            NoDirectionalCount: none,
            PositiveShareOfDirectional: directional > 0 ? (double)positive / directional : null,
            PositiveShareOfAllReads: rows.Count > 0 ? (double)positive / rows.Count : null,
            NoSignalCauses: causes,
            NoSignalCauseNotRecordedCount: rows.Count(
                r => r.DirectionClass == FilingReadDirectionClass.NoDirectionalSignal
                    && !r.NoSignalCauseRecorded));
    }

    private static FilingReadConfidenceSummary BuildConfidenceSummary(
        string population, string sourceField, IReadOnlyList<DirectionalFilingReadRow> rows)
    {
        var values = rows.Where(r => r.Confidence is not null)
            .Select(r => (double)r.Confidence!.Value)
            .ToList();
        var histogram = rows.Where(r => r.Confidence is not null)
            .GroupBy(r => r.Confidence!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new FilingReadConfidenceBucket(g.Key, g.Count()))
            .ToList();

        return new FilingReadConfidenceSummary(
            Population: population,
            SourceField: sourceField,
            Count: values.Count,
            NotRecordedCount: rows.Count - values.Count,
            Min: values.Count > 0 ? ExactQuantile.Of(values, 0.0) : null,
            P25: values.Count > 0 ? ExactQuantile.Of(values, 0.25) : null,
            Median: values.Count > 0 ? ExactMedianInterval.MedianOf(values) : null,
            P75: values.Count > 0 ? ExactQuantile.Of(values, 0.75) : null,
            Max: values.Count > 0 ? ExactQuantile.Of(values, 1.0) : null,
            Histogram: histogram);
    }

    private static IReadOnlyList<FilingReadCount> BuildPerCompanyCounts(
        IReadOnlyList<DirectionalFilingReadRow> rows) =>
        CountBy(rows, r => r.CompanyResolution switch
        {
            FilingReadCompanyResolution.Resolved =>
                (r.Ticker ?? "(no ticker)") + " — " + (r.CompanyName ?? "(no name)"),
            FilingReadCompanyResolution.Unresolved => "(company no longer resolves)",
            _ => "(no matching evidence record)",
        });

    private static FilingReadGroundednessSummary BuildGroundednessSummary(
        IReadOnlyList<DirectionalFilingReadRow> rows, IReadOnlyList<string> metadataKeys)
    {
        var directional = rows
            .Where(r => r.DirectionClass != FilingReadDirectionClass.NoDirectionalSignal)
            .ToList();
        var measured = directional.Where(r => r.Groundedness.Applicable).ToList();
        var notApplicable = directional.Where(r => !r.Groundedness.Applicable).ToList();

        // A read whose excerpt matched NO candidate title is the only genuine mismatch this section can
        // report, so it is NAMED by accession rather than only counted — a bare count would leave a reader
        // unable to check it.
        var mismatches = measured
            .Where(r => r.Groundedness.SupportingExcerptEqualsSomeEvidenceTitle == false)
            .Select(r => r.Accession)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        var matchedTokens = measured.SelectMany(r => r.Groundedness.MatchedNumericTokens).ToList();

        return new FilingReadGroundednessSummary(
            DirectionalReads: directional.Count,
            NotApplicableReads: notApplicable.Count,
            NotApplicableReasons: CountBy(
                notApplicable, r => r.Groundedness.NotApplicableReason ?? "(reason not recorded)"),
            MeasuredReads: measured.Count,
            ExcerptEqualsSomeEvidenceTitleCount:
                measured.Count(r => r.Groundedness.SupportingExcerptEqualsSomeEvidenceTitle == true),
            ExcerptMatchedNoEvidenceTitleCount: mismatches.Count,
            ExcerptMismatchAccessions: mismatches.Take(MaxNamedMismatchAccessions).ToList(),
            ExcerptMismatchAccessionsOmitted: Math.Max(0, mismatches.Count - MaxNamedMismatchAccessions),
            MeasuredReadsWithAmbiguousEvidenceJoin:
                measured.Count(r => r.EvidenceJoin == FilingReadEvidenceJoin.JoinedAmbiguous),
            ReasonContainsNumericTokenCount:
                measured.Count(r => r.Groundedness.ReasonContainsNumericToken == true),
            ReasonNumericTokenFoundInEvidenceTextCount:
                measured.Count(r => r.Groundedness.AnyReasonNumericTokenFoundInEvidenceText == true),
            ReasonNumericTokenCoincidentalHeaderMatchCount:
                measured.Count(r => r.Groundedness.ReasonNumericTokensMatchedCoincidentally > 0),
            ReasonNumericTokenGenuineMatchCount:
                measured.Count(r => r.Groundedness.ReasonNumericTokensMatchedGenuinely > 0),
            CoincidentalTokenCounts: CountBy(
                matchedTokens.Where(t => t.CoincidentalHeaderMatch).ToList(), t => t.Token),
            GenuineTokenCounts: CountBy(
                matchedTokens.Where(t => !t.CoincidentalHeaderMatch).ToList(), t => t.Token),
            EvidenceMetadataKeysObserved: metadataKeys,
            EvidenceFieldsSearched: EvidenceFieldsSearched,
            StructuralHeaderDescription: StructuralHeaderDescription);
    }

    private static FilingReadDisagreementSummary BuildDisagreementSummary(
        IReadOnlyList<DirectionalFilingReadRow> rows, NewsWindowIndex newsIndex)
    {
        var directional = rows
            .Where(r => r.DirectionClass != FilingReadDirectionClass.NoDirectionalSignal)
            .ToList();
        var evaluated = directional
            .Where(r => r.Disagreement.NewsArm == FilingReadNewsArm.Evaluated)
            .ToList();
        var positiveEvaluated = evaluated
            .Where(r => r.DirectionClass == FilingReadDirectionClass.Positive)
            .ToList();
        var disagreeing = positiveEvaluated.Count(r => r.Disagreement.DisagreesWithPositiveRead == true);

        var termCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in evaluated)
        {
            foreach (var term in row.Disagreement.MatchedVocabularyTerms)
            {
                termCounts[term] = termCounts.TryGetValue(term, out var n) ? n + 1 : 1;
            }
        }

        return new FilingReadDisagreementSummary(
            VocabularyVersion: FilingReadDisagreementVocabulary.Version,
            DirectionalReads: directional.Count,
            NewsArmEvaluatedReads: evaluated.Count,
            NewsArmExclusions: CountEnum<FilingReadNewsArm, DirectionalFilingReadRow>(
                directional.Where(r => r.Disagreement.NewsArm != FilingReadNewsArm.Evaluated).ToList(),
                r => r.Disagreement.NewsArm),
            PositiveReadsWithNewsArm: positiveEvaluated.Count,
            PositiveReadsDisagreeing: disagreeing,
            DisagreementRateOverEvaluatedPositiveReads: positiveEvaluated.Count > 0
                ? (double)disagreeing / positiveEvaluated.Count
                : null,
            TotalMatchedStatementsOmitted: evaluated.Sum(r => r.Disagreement.MatchedStatementsOmitted),
            MatchedVocabularyTermCounts:
                termCounts.Select(kv => new FilingReadCount(kv.Key, kv.Value)).ToList(),
            ForwardReturnStates: CountEnum<FilingReadForwardReturnState, DirectionalFilingReadRow>(
                directional, r => r.Disagreement.ForwardReturnState),
            ForwardReturnByDirection:
            [
                BuildForwardDistribution("Positive", rows, FilingReadDirectionClass.Positive),
                BuildForwardDistribution("Negative", rows, FilingReadDirectionClass.Negative),
                BuildForwardDistribution(
                    "OtherDirectionToken", rows, FilingReadDirectionClass.OtherDirectionToken),
                BuildForwardDistribution(
                    "NoDirectionalSignal", rows, FilingReadDirectionClass.NoDirectionalSignal),
            ],
            TypingRecordsScanned: newsIndex.TypingRecordsScanned,
            TypingsContributingFacts: newsIndex.TypingsContributingFacts,
            TypingsWithNoFacts: newsIndex.TypingsWithNoFacts,
            TypingsWithNoCompanyId: newsIndex.TypingsWithNoCompanyId,
            TypingsWithNoArchivedObservation: newsIndex.TypingsWithNoArchivedObservation,
            TypingsWithNoPublishedAt: newsIndex.TypingsWithNoPublishedAt,
            FirstTypingRecordDateUtc: newsIndex.FirstTypingDateUtc);
    }

    private static FilingReadForwardReturnDistribution BuildForwardDistribution(
        string direction,
        IReadOnlyList<DirectionalFilingReadRow> rows,
        FilingReadDirectionClass directionClass)
    {
        var inClass = rows.Where(r => r.DirectionClass == directionClass).ToList();
        var values = inClass
            .Where(r => r.Disagreement.ForwardReturn21d is not null)
            .Select(r => r.Disagreement.ForwardReturn21d!.Value)
            .ToList();

        return new FilingReadForwardReturnDistribution(
            Direction: direction,
            ReadsInClass: inClass.Count,
            Count: values.Count,
            Min: values.Count > 0 ? ExactQuantile.Of(values, 0.0) : null,
            P25: values.Count > 0 ? ExactQuantile.Of(values, 0.25) : null,
            Median: values.Count > 0 ? ExactMedianInterval.MedianOf(values) : null,
            P75: values.Count > 0 ? ExactQuantile.Of(values, 0.75) : null,
            Max: values.Count > 0 ? ExactQuantile.Of(values, 1.0) : null,
            Mean: values.Count > 0 ? values.Average() : null);
    }

    /// <summary>
    /// The spec-218 §4 worked example, resolved by LOOKING IT UP in the corpus this run just read — never
    /// hard-coded prose and never a fixture. When it cannot be resolved, the artifact says so with the reason,
    /// rather than rendering a fabricated row.
    /// </summary>
    private static FilingReadWorkedExample BuildWorkedExample(
        IReadOnlyList<DirectionalFilingReadRow> rows, NewsWindowIndex newsIndex)
    {
        var byTicker = rows
            .Where(r => string.Equals(r.Ticker, WorkedExampleTicker, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byTicker.Count == 0)
        {
            return Unresolved(
                "no accrued read in this corpus resolves to ticker " + WorkedExampleTicker
                    + " (the read may predate the current model segment, or its company may not resolve)");
        }

        var match = byTicker
            .Where(r => r.FilingDate == WorkedExampleFilingDate)
            .OrderBy(r => r.Accession, StringComparer.Ordinal)
            .FirstOrDefault();
        if (match is null)
        {
            return Unresolved(
                WorkedExampleTicker + " reads exist in this corpus (" + byTicker.Count.ToString(
                    CultureInfo.InvariantCulture)
                    + ") but none carries filing date "
                    + WorkedExampleFilingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        var lines = new List<FilingReadWorkedExampleNewsLine>();
        var omittedLines = 0;
        if (match.CompanyId is { } companyId && match.FilingDate is { } anchor)
        {
            var facts = newsIndex.FactsForCompanyInWindow(companyId, anchor, NewsWindowDays);
            foreach (var fact in facts)
            {
                if (lines.Count >= MaxWorkedExampleNewsLines)
                {
                    omittedLines++;
                    continue;
                }

                lines.Add(new FilingReadWorkedExampleNewsLine(
                    PublishedDateUtc: fact.PublishedDate,
                    EventTypes: string.Join("|", fact.Fact.EventTypes.Select(t => t.ToString())),
                    Statement: fact.Fact.Statement,
                    MatchedAdverseVocabulary: FilingReadDisagreementVocabulary.TryMatch(fact.Fact) is not null));
            }
        }

        return new FilingReadWorkedExample(
            Ticker: WorkedExampleTicker,
            FilingDate: WorkedExampleFilingDate,
            LabelDate: WorkedExampleLabelDate,
            Resolved: true,
            UnresolvedReason: null,
            Row: match,
            SameWindowNews: lines,
            SameWindowNewsOmitted: omittedLines);

        static FilingReadWorkedExample Unresolved(string reason) => new(
            Ticker: WorkedExampleTicker,
            FilingDate: WorkedExampleFilingDate,
            LabelDate: WorkedExampleLabelDate,
            Resolved: false,
            UnresolvedReason: reason,
            Row: null,
            SameWindowNews: [],
            SameWindowNewsOmitted: 0);
    }

    // -----------------------------------------------------------------------------------------------
    // Small shared helpers
    // -----------------------------------------------------------------------------------------------

    /// <summary>Counts by a projected key, ordered by count descending then key ordinal (deterministic).</summary>
    private static IReadOnlyList<FilingReadCount> CountBy<T>(
        IReadOnlyList<T> source, Func<T, string> key) =>
        source
            .GroupBy(key, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new FilingReadCount(g.Key, g.Count()))
            .ToList();

    /// <summary>
    /// Counts EVERY member of an enum in declaration order — including the members with zero rows, because a
    /// missing row and a measured zero must never look alike.
    /// </summary>
    private static IReadOnlyList<FilingReadCount> CountEnum<TEnum, TRow>(
        IReadOnlyList<TRow> source, Func<TRow, TEnum> selector)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>()
            .Select(member => new FilingReadCount(
                member.ToString() ?? string.Empty,
                source.Count(row => EqualityComparer<TEnum>.Default.Equals(selector(row), member))))
            .ToList();

    /// <summary>
    /// The distinct numeric tokens in <paramref name="reason"/>, in order of first appearance: a maximal run
    /// of digits optionally carrying internal <c>.</c> or <c>,</c> separators, with any trailing separator
    /// trimmed. "Trajectory rose 38→60 (+22)" yields 38, 60, 22.
    /// </summary>
    internal static IReadOnlyList<string> ExtractNumericTokens(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return [];
        }

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        while (index < reason.Length)
        {
            if (!char.IsAsciiDigit(reason[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < reason.Length
                && (char.IsAsciiDigit(reason[index]) || reason[index] is '.' or ','))
            {
                index++;
            }

            var token = reason[start..index].TrimEnd('.', ',');
            if (token.Length > 0 && seen.Add(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    /// <summary>
    /// Whether <paramref name="token"/> appears in <paramref name="haystack"/> as a WHOLE figure: neither
    /// neighbour may be a digit or a numeric separator, so "40" does not match inside "1409" or "40.2". Stated
    /// in the artifact, because a looser rule would manufacture groundedness.
    /// </summary>
    internal static bool ContainsNumericToken(string haystack, string token)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length == 0 || haystack.Length < token.Length)
        {
            return false;
        }

        var index = 0;
        while (index <= haystack.Length - token.Length)
        {
            index = haystack.IndexOf(token, index, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var end = index + token.Length;
            var startOk = index == 0 || !IsFigureChar(haystack[index - 1]);
            var endOk = end == haystack.Length || !IsFigureChar(haystack[end]);
            if (startOk && endOk)
            {
                return true;
            }

            index++;
        }

        return false;

        static bool IsFigureChar(char c) => char.IsAsciiDigit(c) || c is '.' or ',';
    }

    private static string FormatShare(double? share) => share is { } value
        ? value.ToString("0.0%", CultureInfo.InvariantCulture)
        : "(undefined — no directional reads)";

    /// <summary>
    /// The report for a build that produced NO rows — either because no corpus seam exists, or because the
    /// corpus hydrated nothing. When <paramref name="corpus"/> is supplied its own measured file counts are
    /// carried through; when it is <c>null</c> the seam was never reached, and the zeros below are
    /// placeholders whose meaning is carried by <paramref name="detail"/> (which every renderer prints
    /// beside the availability), never measurements.
    /// </summary>
    private DirectionalFilingReadReport EmptyReport(
        FilingReadCorpusAvailability availability,
        string detail,
        AnalyzedFilingCorpus? corpus = null)
    {
        var empty = Array.Empty<DirectionalFilingReadRow>();
        var emptyNews = NewsWindowIndex.NotRegistered();
        return new DirectionalFilingReadReport(
            DistributionVersion: DirectionalFilingReadReport.DistributionVersionToken,
            GroundednessVersion: DirectionalFilingReadReport.GroundednessVersionToken,
            DisagreementVersion: FilingReadDisagreementVocabulary.Version,
            CorpusAvailability: availability,
            CorpusUnavailableDetail: detail,
            // The one authority a renderer reads: no row exists, so no join store was opened.
            JoinStoresLoaded: false,
            ModelSegment: corpus?.ModelSegment,
            FilesScanned: corpus?.FilesScanned ?? 0,
            RecordsHydrated: corpus?.RecordsHydrated ?? 0,
            UnreadableOrUnparseableFiles: corpus?.UnreadableOrUnparseableFiles ?? 0,
            OutsideCurrentModelSegmentFiles: corpus?.OutsideCurrentModelSegmentFiles,
            FileNameAccessionMismatchFiles: corpus?.FileNameAccessionMismatchFiles ?? 0,
            OutcomeSignalMismatchFiles: corpus?.OutcomeSignalMismatchFiles ?? 0,
            NoMatchingEvidenceRecordCount: 0,
            FilingEvidenceWithoutAccessionMetadata: 0,
            AccessionsWithMultipleEvidenceRecords: 0,
            AmbiguousEvidenceJoinCount: 0,
            UnresolvedCompanyCount: 0,
            FilingDateNotRecordedCount: 0,
            CurrentCacheVersion: AnalyzedFilingRecord.CurrentCacheVersion,
            CacheVersions: [],
            CappedConfidenceRecordedCount: null,
            CappedConfidenceNote: CappedConfidenceNote,
            ComparabilityScanStates: CountEnum<FilingReadComparabilityScanState, DirectionalFilingReadRow>(
                empty, r => r.ComparabilityScan),
            AllRecords: BuildDirectionSummary("all parseable accrued read records", empty),
            CurrentCacheVersionRecords: BuildDirectionSummary(
                "records at cacheVersion " + AnalyzedFilingRecord.CurrentCacheVersion.ToString(
                    CultureInfo.InvariantCulture) + " only",
                empty),
            DirectionalConfidence: BuildConfidenceSummary("directional reads", "signal.confidence", empty),
            NoSignalConfidence: BuildConfidenceSummary(
                "no-directional reads", "record.readConfidence", empty),
            PerCompanyReadCounts: [],
            WindowStartUtc: null,
            WindowEndUtc: null,
            ForwardHorizonDays: HorizonDays,
            ForwardExitToleranceDays: ExitToleranceDays,
            Groundedness: BuildGroundednessSummary(empty, []),
            Disagreement: BuildDisagreementSummary(empty, emptyNews),
            WorkedExample: BuildWorkedExample(empty, emptyNews),
            Rows: empty);
    }

    // -----------------------------------------------------------------------------------------------
    // Index types
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// One evidence record carrying an accession, with its already-parsed metadata envelope and company
    /// hints — parsed ONCE at index time so the per-row questions never re-parse the same JSON.
    /// </summary>
    private sealed record EvidenceCandidate(
        EvidenceItem Item,
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<string> CompanyHints);

    private sealed record EvidenceAccessionIndex(
        IReadOnlyDictionary<string, List<EvidenceCandidate>> ByAccession,
        int FilingEvidenceWithoutAccessionMetadata,
        int AccessionsWithMultipleEvidenceRecords,
        IReadOnlyList<string> MetadataKeysObserved);

    private sealed record TypedFactInWindow(DateOnly PublishedDate, NewsTypingValidatedFact Fact);

    private sealed class NewsWindowIndex(
        Dictionary<Guid, List<TypedFactInWindow>> byCompany,
        bool storesRegistered,
        int typingRecordsScanned,
        int typingsContributingFacts,
        int typingsWithNoFacts,
        int typingsWithNoCompanyId,
        int typingsWithNoArchivedObservation,
        int typingsWithNoPublishedAt,
        DateOnly? firstTypingDateUtc)
    {
        /// <summary>The "no news store in this process" index: zero counts that mean NOT REGISTERED, not zero.</summary>
        public static NewsWindowIndex NotRegistered() => new(
            new Dictionary<Guid, List<TypedFactInWindow>>(),
            storesRegistered: false,
            typingRecordsScanned: 0,
            typingsContributingFacts: 0,
            typingsWithNoFacts: 0,
            typingsWithNoCompanyId: 0,
            typingsWithNoArchivedObservation: 0,
            typingsWithNoPublishedAt: 0,
            firstTypingDateUtc: null);

        /// <summary>Whether the typing store AND the observation archive both exist in this process.</summary>
        public bool StoresRegistered { get; } = storesRegistered;

        public int TypingRecordsScanned { get; } = typingRecordsScanned;

        /// <summary>Typings that contributed at least one fact — the fifth term of the reconciliation.</summary>
        public int TypingsContributingFacts { get; } = typingsContributingFacts;

        /// <summary>Typings that produced NO fact (InsufficientContent, or all facts validation-dropped).</summary>
        public int TypingsWithNoFacts { get; } = typingsWithNoFacts;

        public int TypingsWithNoCompanyId { get; } = typingsWithNoCompanyId;

        public int TypingsWithNoArchivedObservation { get; } = typingsWithNoArchivedObservation;

        public int TypingsWithNoPublishedAt { get; } = typingsWithNoPublishedAt;

        public DateOnly? FirstTypingDateUtc { get; } = firstTypingDateUtc;

        /// <summary>The typed facts for one company whose observation published within ±<paramref name="days"/> of <paramref name="anchor"/>, INCLUSIVE at both edges.</summary>
        public IReadOnlyList<TypedFactInWindow> FactsForCompanyInWindow(
            Guid companyId, DateOnly anchor, int days)
        {
            if (!byCompany.TryGetValue(companyId, out var facts))
            {
                return [];
            }

            return facts
                .Where(f => Math.Abs(f.PublishedDate.DayNumber - anchor.DayNumber) <= days)
                .ToList();
        }
    }
}
