using Radar.Application.Filings;

namespace Radar.Application.Efficacy.FilingReads;

/// <summary>How the corpus enumeration ended. A non-<see cref="Available"/> state is stated on the artifact.</summary>
public enum FilingReadCorpusAvailability
{
    /// <summary>The corpus was enumerated; the counts below are a complete accounting of it.</summary>
    Available = 0,

    /// <summary>
    /// No <see cref="IAnalyzedFilingReadCorpus"/> is registered — the AI earnings read is disabled for this
    /// process, so no corpus seam exists. NOT "zero reads": the reads may well be on disk, unreachable here.
    /// </summary>
    SeamNotRegistered,

    /// <summary>The cache directory for the current model segment does not exist — a MEASURED absence.</summary>
    DirectoryMissing,

    /// <summary>The directory listing itself failed; the counts describe only what was reached, not the corpus.</summary>
    EnumerationFailed,
}

/// <summary>The direction one read produced, as a closed class. An unexpected token is never pooled away.</summary>
public enum FilingReadDirectionClass
{
    /// <summary>The read produced no directional signal (the cause is reported separately).</summary>
    NoDirectionalSignal = 0,

    /// <summary>A directional read whose signal direction token is exactly <c>Positive</c>.</summary>
    Positive,

    /// <summary>A directional read whose signal direction token is exactly <c>Negative</c>.</summary>
    Negative,

    /// <summary>
    /// A directional read carrying some other token. Counted as its own class and the token itself is named
    /// in the artifact — collapsing it into Positive or "other" would hide a contract change.
    /// </summary>
    OtherDirectionToken,
}

/// <summary>Which persisted field a row's confidence came from. Stated per column in the artifact.</summary>
public enum FilingReadConfidenceSource
{
    /// <summary>Neither field carried a value — NOT RECORDED (a pre-204 no-signal record).</summary>
    NotRecorded = 0,

    /// <summary>The produced signal's own <c>Confidence</c> (directional reads).</summary>
    ProducedSignalConfidence,

    /// <summary>The record's <c>ReadConfidence</c> — the effective, comparability-capped value (no-signal reads).</summary>
    RecordReadConfidence,
}

/// <summary>What the spec-160 comparability scan recorded on a read. <c>NotScanned</c> is not <c>Clean</c>.</summary>
public enum FilingReadComparabilityScanState
{
    /// <summary>The record carries no markers: written pre-160, so no scan happened. NOT a clean scan.</summary>
    NotScanned = 0,

    /// <summary>Scanned, and neither marker group matched.</summary>
    ScannedClean,

    /// <summary>At least one CAP-TRIGGERING marker matched (takes precedence when both groups matched).</summary>
    CapTriggeringMarkersMatched,

    /// <summary>Only diagnostic-only markers matched (recorded for hit-rate measurement; they never cap).</summary>
    DiagnosticOnlyMarkersMatched,
}

/// <summary>Whether a read joined the evidence record it was taken from.</summary>
public enum FilingReadEvidenceJoin
{
    /// <summary>A filing evidence record carrying this accession was found.</summary>
    Joined = 0,

    /// <summary>No evidence record carries this accession — a named, counted exclusion, never a silent skip.</summary>
    NoMatchingEvidenceRecord,

    /// <summary>
    /// MORE THAN ONE evidence record carries this accession, so the join is AMBIGUOUS: the read cannot be
    /// attributed to one of them. Measured live (2026-09-09): 71 accessions carry two filing evidence records
    /// with DIFFERENT titles — a short and a long variant produced by a collector change — and 12 directional
    /// reads land on one. Every candidate is kept and every question is asked of the candidate SET, because
    /// collapsing to an arbitrary winner turns a tie-break into a finding. Appended LAST so no existing
    /// member's value moves.
    /// </summary>
    JoinedAmbiguous,
}

/// <summary>Whether a read's company resolves in the seeded universe today.</summary>
public enum FilingReadCompanyResolution
{
    /// <summary>Resolved to exactly one seeded company through the shared resolver.</summary>
    Resolved = 0,

    /// <summary>The hints did not resolve (the company may have left the universe) — counted, never guessed.</summary>
    Unresolved,

    /// <summary>There was no evidence record to take hints from, so resolution was never attempted.</summary>
    NoEvidenceRecord,
}

/// <summary>Where a read's filing date came from. <c>NotRecorded</c> is not a date.</summary>
public enum FilingReadFilingDateSource
{
    /// <summary>Neither source carried a date — the read has no anchor and every dated arm is excluded.</summary>
    NotRecorded = 0,

    /// <summary>The joined evidence record's <c>PublishedAtUtc</c> — the filing's own date. Preferred.</summary>
    EvidencePublishedAt,

    /// <summary>The cache record's <c>ObservedAtUtc</c>, captured at first analysis. Fallback.</summary>
    CacheObservedAt,
}

/// <summary>Why a read has (or lacks) a news arm in the section-3 join.</summary>
public enum FilingReadNewsArm
{
    /// <summary>The window was evaluated against typed facts.</summary>
    Evaluated = 0,

    /// <summary>Not a directional read — section 3 asks about directional reads only.</summary>
    NotADirectionalRead,

    /// <summary>The company does not resolve, so no per-company typing set can be selected.</summary>
    CompanyUnresolved,

    /// <summary>No filing date, so no window exists.</summary>
    FilingDateNotRecorded,

    /// <summary>
    /// The filing predates the FIRST typing record in the store: typing began in 2026-08, so an earlier read
    /// has NO news arm at all. Reported per read rather than scored as agreement.
    /// </summary>
    NoTypingCoverageInWindowPreTypingEra,

    /// <summary>Typing existed by then, but no typed fact for this company published inside the window.</summary>
    NoTypedFactsInWindow,

    /// <summary>
    /// Neither news store is registered in this process (the typing step and the observation archive ride
    /// their own gates), so the news arm could not run AT ALL. Appended LAST so no existing member's value
    /// moves. NOT "no adverse news": the record may well hold some, unreachable here.
    /// </summary>
    NewsStoresNotRegistered,
}

/// <summary>
/// Every named outcome of the section-3 forward-return join. The four
/// <c>ForwardReturnUnavailableReason</c> members map through one-for-one; the three states before them are
/// the joins that never reached the shared primitive.
/// </summary>
public enum FilingReadForwardReturnState
{
    /// <summary>A full-horizon forward return was computed.</summary>
    Computed = 0,

    /// <summary>No filing date, so no as-of anchor exists.</summary>
    FilingDateNotRecorded,

    /// <summary>The company resolves but carries no ticker, so no price series can be addressed.</summary>
    NoTicker,

    /// <summary>No persisted price history for the ticker (or it holds no bars).</summary>
    NoPriceHistory,

    /// <summary><c>ForwardReturnUnavailableReason.NoForwardBar</c>.</summary>
    NoForwardBar,

    /// <summary><c>ForwardReturnUnavailableReason.SingleForwardBar</c>.</summary>
    SingleForwardBar,

    /// <summary><c>ForwardReturnUnavailableReason.NonPositiveEntryPrice</c>.</summary>
    NonPositiveEntryPrice,

    /// <summary><c>ForwardReturnUnavailableReason.PartialWindow</c>.</summary>
    PartialWindow,
}

/// <summary>
/// The section-2 groundedness read for one row. Every field is nullable BECAUSE the question does not apply
/// to every row: <c>null</c> means "not measured here", named by <see cref="NotApplicableReason"/>, and never
/// reads as a measured false.
/// </summary>
/// <param name="Applicable">Whether the groundedness questions were asked of this row at all.</param>
/// <param name="NotApplicableReason">The named reason they were not (null when they were).</param>
/// <param name="SupportingExcerptEqualsSomeEvidenceTitle">
/// Whether <c>SupportingExcerpt</c> is byte-equal (ordinal) to the <c>Title</c> of AT LEAST ONE of the
/// evidence records carrying this read's accession. Asked of the candidate SET, never of one chosen record:
/// an accession is not unique in the store (a collector change left many filings with a short and a long
/// title variant sharing one publication instant), and the read does not record which record it was taken
/// from — so a single-record comparison would report which record a GUID tie-break happened to pick rather
/// than whether the guard holds. The count is the point: it puts the mapper guard's tautology in an artifact
/// instead of only in a code comment.
/// </param>
/// <param name="EvidenceCandidateCount">
/// How many evidence records carry this accession (0 when none). Greater than 1 is the AMBIGUOUS join, also
/// carried as <see cref="FilingReadEvidenceJoin.JoinedAmbiguous"/> on the row.
/// </param>
/// <param name="ReasonContainsNumericToken">Whether the model's <c>Reason</c> contains at least one digit.</param>
/// <param name="ReasonNumericTokens">The distinct numeric tokens found in the Reason, in order of appearance.</param>
/// <param name="ReasonNumericTokensFoundInEvidenceText">
/// How many of those tokens appear anywhere in the STORED text of ANY candidate (RawText + Title + Summary,
/// the three fields searched — named because "anywhere" must be auditable). This raw count is NOT a grounding
/// rate; the split below is what carries the meaning.
/// </param>
/// <param name="AnyReasonNumericTokenFoundInEvidenceText">Whether at least one did.</param>
/// <param name="ReasonNumericTokensMatchedCoincidentally">
/// How many of the matched tokens ALSO occur in the metadata-derived header (the evidence envelope's values:
/// accession, form, filing date, item codes, feed url, primary document …). A filing evidence record's
/// <c>rawText</c> IS that header, so such a token is a year, a form-type digit or an accession fragment — not
/// a figure grounded in the release.
/// </param>
/// <param name="ReasonNumericTokensMatchedGenuinely">
/// How many matched tokens occur in the stored text but NOWHERE in that header. This is the only count that
/// COULD indicate grounding — it does not establish it: the stored text also carries the SEC-supplied
/// document title, so a quarter label such as <c>Q4'2024</c> matches here too. Read it with
/// <see cref="MatchedNumericTokens"/>, never alone.
/// </param>
/// <param name="MatchedNumericTokens">Every matched token with its class, so the split can be audited, not trusted.</param>
public sealed record FilingReadGroundedness(
    bool Applicable,
    string? NotApplicableReason,
    bool? SupportingExcerptEqualsSomeEvidenceTitle,
    int EvidenceCandidateCount,
    bool? ReasonContainsNumericToken,
    IReadOnlyList<string> ReasonNumericTokens,
    int? ReasonNumericTokensFoundInEvidenceText,
    bool? AnyReasonNumericTokenFoundInEvidenceText,
    int? ReasonNumericTokensMatchedCoincidentally,
    int? ReasonNumericTokensMatchedGenuinely,
    IReadOnlyList<FilingReadMatchedToken> MatchedNumericTokens)
{
    /// <summary>The not-applicable instance, carrying its named reason and no fabricated booleans.</summary>
    public static FilingReadGroundedness NotApplicable(string reason, int candidateCount = 0) =>
        new(false, reason, null, candidateCount, null, [], null, null, null, null, []);
}

/// <summary>
/// One numeric token from a <c>Reason</c> that was found in the evidence record's stored text, plus whether
/// the match is COINCIDENTAL — the token also occurs in the METADATA-DERIVED header, so it is a year, a
/// form-type digit or an accession fragment rather than a figure grounded in the release.
/// </summary>
public sealed record FilingReadMatchedToken(string Token, bool CoincidentalHeaderMatch);

/// <summary>
/// The section-3 disagreement read for one row: what Radar's own record held in the same window.
/// </summary>
/// <param name="NewsArm">Whether the news window was evaluated, or the named reason it was not.</param>
/// <param name="TypedFactsInWindow">Typed facts for this company inside ±3 days; null when the arm did not run.</param>
/// <param name="NegativeMatchCount">How many of them matched the closed adverse vocabulary; null when not run.</param>
/// <param name="MatchedStatements">Matched statements VERBATIM, capped at five.</param>
/// <param name="MatchedStatementsOmitted">How many matched statements the cap left out — never a silent truncation.</param>
/// <param name="MatchedVocabularyTerms">The distinct vocabulary members that matched (event types and phrases).</param>
/// <param name="DisagreesWithPositiveRead">
/// True only when the read is Positive AND at least one matched fact exists. Null when the arm did not run —
/// an unevaluated window is never scored as agreement.
/// </param>
/// <param name="ForwardReturnState">The named outcome of the forward-return join (DESCRIPTIVE, AD-14).</param>
/// <param name="ForwardReturn21d">The 21-calendar-day forward return; null unless the state is Computed.</param>
public sealed record FilingReadDisagreement(
    FilingReadNewsArm NewsArm,
    int? TypedFactsInWindow,
    int? NegativeMatchCount,
    IReadOnlyList<string> MatchedStatements,
    int MatchedStatementsOmitted,
    IReadOnlyList<string> MatchedVocabularyTerms,
    bool? DisagreesWithPositiveRead,
    FilingReadForwardReturnState ForwardReturnState,
    double? ForwardReturn21d,
    DateOnly? ForwardEntryDate,
    DateOnly? ForwardExitDate);

/// <summary>One accrued read, joined to everything the measurement could reach. The CSV's row shape.</summary>
public sealed record DirectionalFilingReadRow(
    string Accession,
    string SourceFileName,
    int CacheVersion,
    bool IsCurrentCacheVersion,
    AnalyzedFilingOutcome Outcome,
    FilingReadDirectionClass DirectionClass,
    string? DirectionToken,
    FilingNoSignalCause? NoSignalCause,
    bool NoSignalCauseRecorded,
    decimal? Confidence,
    FilingReadConfidenceSource ConfidenceSource,
    FilingReadComparabilityScanState ComparabilityScan,
    int? CapTriggeringMarkerCount,
    int? DiagnosticOnlyMarkerCount,
    FilingReadEvidenceJoin EvidenceJoin,
    int EvidenceCandidateCount,
    FilingReadCompanyResolution CompanyResolution,
    Guid? CompanyId,
    string? CompanyName,
    string? Ticker,
    DateOnly? FilingDate,
    FilingReadFilingDateSource FilingDateSource,
    string? EvidenceTitle,
    string? SupportingExcerpt,
    string? Reason,
    FilingReadGroundedness Groundedness,
    FilingReadDisagreement Disagreement);

/// <summary>One counted token/value pair. Used wherever a distribution is reported as named counts.</summary>
public sealed record FilingReadCount(string Name, int Count);

/// <summary>One exact-value confidence histogram bucket (the corpus's confidences are discrete).</summary>
public sealed record FilingReadConfidenceBucket(decimal Value, int Count);

/// <summary>
/// A confidence five-number summary over one named population, with the field it was read from stated.
/// <para>
/// The MEDIAN uses the repo's one median definition (<c>ExactMedianInterval.MedianOf</c>: the mean of the two
/// middle order statistics at even n); MIN/P25/P75/MAX use the nearest-rank <c>ExactQuantile</c>. Both
/// conventions are stated here so a reader is never left inferring which one produced a number.
/// </para>
/// </summary>
public sealed record FilingReadConfidenceSummary(
    string Population,
    string SourceField,
    int Count,
    int NotRecordedCount,
    double? Min,
    double? P25,
    double? Median,
    double? P75,
    double? Max,
    IReadOnlyList<FilingReadConfidenceBucket> Histogram);

/// <summary>
/// The section-1 direction distribution over ONE named denominator. The report carries two of these — the
/// whole parseable corpus and the current-cache-version subset — so neither view can be mistaken for the
/// other.
/// </summary>
public sealed record FilingReadDirectionSummary(
    string Denominator,
    int TotalReads,
    int PositiveCount,
    int NegativeCount,
    int OtherDirectionTokenCount,
    IReadOnlyList<FilingReadCount> OtherDirectionTokens,
    int NoDirectionalCount,
    double? PositiveShareOfDirectional,
    double? PositiveShareOfAllReads,
    IReadOnlyList<FilingReadCount> NoSignalCauses,
    int NoSignalCauseNotRecordedCount);

/// <summary>
/// The section-2 aggregate, with its own denominator named. Two counts carry the section's whole meaning and
/// are deliberately separate: an excerpt is compared against EVERY evidence record carrying the accession
/// (so an ambiguous join can never masquerade as a failed guard), and a matched figure is split into
/// COINCIDENTAL header matches and genuine ones (so a year or a form-type digit can never be rendered as
/// groundedness).
/// </summary>
public sealed record FilingReadGroundednessSummary(
    int DirectionalReads,
    int NotApplicableReads,
    IReadOnlyList<FilingReadCount> NotApplicableReasons,
    int MeasuredReads,
    int ExcerptEqualsSomeEvidenceTitleCount,
    int ExcerptMatchedNoEvidenceTitleCount,
    IReadOnlyList<string> ExcerptMismatchAccessions,
    int ExcerptMismatchAccessionsOmitted,
    int MeasuredReadsWithAmbiguousEvidenceJoin,
    int ReasonContainsNumericTokenCount,
    int ReasonNumericTokenFoundInEvidenceTextCount,
    int ReasonNumericTokenCoincidentalHeaderMatchCount,
    int ReasonNumericTokenGenuineMatchCount,
    IReadOnlyList<FilingReadCount> CoincidentalTokenCounts,
    IReadOnlyList<FilingReadCount> GenuineTokenCounts,
    IReadOnlyList<string> EvidenceMetadataKeysObserved,
    string EvidenceFieldsSearched,
    string StructuralHeaderDescription);

/// <summary>The section-3 aggregate, with every excluded-arm reason named and counted.</summary>
public sealed record FilingReadDisagreementSummary(
    string VocabularyVersion,
    int DirectionalReads,
    int NewsArmEvaluatedReads,
    IReadOnlyList<FilingReadCount> NewsArmExclusions,
    int PositiveReadsWithNewsArm,
    int PositiveReadsDisagreeing,
    double? DisagreementRateOverEvaluatedPositiveReads,
    int TotalMatchedStatementsOmitted,
    IReadOnlyList<FilingReadCount> MatchedVocabularyTermCounts,
    IReadOnlyList<FilingReadCount> ForwardReturnStates,
    IReadOnlyList<FilingReadForwardReturnDistribution> ForwardReturnByDirection,
    int TypingRecordsScanned,
    int TypingsContributingFacts,
    int TypingsWithNoFacts,
    int TypingsWithNoCompanyId,
    int TypingsWithNoArchivedObservation,
    int TypingsWithNoPublishedAt,
    DateOnly? FirstTypingRecordDateUtc);

/// <summary>
/// The DESCRIPTIVE forward-return distribution for one read direction (AD-14, validation-only): no gate, no
/// threshold, nothing promoted or demoted. <c>Count</c> is the number of reads whose window RESOLVED.
/// </summary>
public sealed record FilingReadForwardReturnDistribution(
    string Direction,
    int ReadsInClass,
    int Count,
    double? Min,
    double? P25,
    double? Median,
    double? P75,
    double? Max,
    double? Mean);

/// <summary>The section-4 worked example, resolved from live data or explicitly reported as unresolvable.</summary>
public sealed record FilingReadWorkedExample(
    string Ticker,
    DateOnly FilingDate,
    DateOnly LabelDate,
    bool Resolved,
    string? UnresolvedReason,
    DirectionalFilingReadRow? Row,
    IReadOnlyList<FilingReadWorkedExampleNewsLine> SameWindowNews,
    int SameWindowNewsOmitted);

/// <summary>One same-window typed news line rendered beside the worked example.</summary>
public sealed record FilingReadWorkedExampleNewsLine(
    DateOnly PublishedDateUtc,
    string EventTypes,
    string Statement,
    bool MatchedAdverseVocabulary);

/// <summary>
/// The complete spec-218 measurement: the three versioned sub-reports over one corpus read, plus the corpus
/// accounting that makes every denominator checkable. This record IS the JSON artifact.
/// </summary>
public sealed record DirectionalFilingReadReport(
    string DistributionVersion,
    string GroundednessVersion,
    string DisagreementVersion,
    FilingReadCorpusAvailability CorpusAvailability,
    string? CorpusUnavailableDetail,
    string? ModelSegment,
    int FilesScanned,
    int RecordsHydrated,
    int UnreadableOrUnparseableFiles,
    int? OutsideCurrentModelSegmentFiles,
    int FileNameAccessionMismatchFiles,
    int OutcomeSignalMismatchFiles,
    int NoMatchingEvidenceRecordCount,
    int FilingEvidenceWithoutAccessionMetadata,
    int AccessionsWithMultipleEvidenceRecords,
    int AmbiguousEvidenceJoinCount,
    int UnresolvedCompanyCount,
    int FilingDateNotRecordedCount,
    int CurrentCacheVersion,
    IReadOnlyList<FilingReadCount> CacheVersions,
    int? CappedConfidenceRecordedCount,
    string CappedConfidenceNote,
    IReadOnlyList<FilingReadCount> ComparabilityScanStates,
    FilingReadDirectionSummary AllRecords,
    FilingReadDirectionSummary CurrentCacheVersionRecords,
    FilingReadConfidenceSummary DirectionalConfidence,
    FilingReadConfidenceSummary NoSignalConfidence,
    IReadOnlyList<FilingReadCount> PerCompanyReadCounts,
    DateOnly? WindowStartUtc,
    DateOnly? WindowEndUtc,
    int ForwardHorizonDays,
    int ForwardExitToleranceDays,
    FilingReadGroundednessSummary Groundedness,
    FilingReadDisagreementSummary Disagreement,
    FilingReadWorkedExample WorkedExample,
    IReadOnlyList<DirectionalFilingReadRow> Rows)
{
    /// <summary>The section-1 artifact version token.</summary>
    public const string DistributionVersionToken = "directional-filing-read-distribution-v1";

    /// <summary>The section-2 artifact version token.</summary>
    public const string GroundednessVersionToken = "filing-reason-groundedness-v1";
}
