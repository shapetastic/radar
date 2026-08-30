using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Application.Filings;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Filings;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// Opt-in enrichment that turns an earnings-8-K <see cref="EvidenceItem"/> into at most one
/// <c>GuidanceChange</c> <see cref="ExtractedSignal"/>: a confidence-gated DIRECTIONAL read
/// (Improving→Positive / Deteriorating→Negative at the configured Strength/Novelty), or — since spec 204 —
/// the NON-DIRECTIONAL read persisted as its own signal (a confident Mixed as Direction <c>Mixed</c>;
/// Unknown or a below-gate read as <c>Neutral</c>; all at the keyword-fallback magnitudes with the model's
/// direction/confidence/rationale in the <see cref="FilingReadSignalMetadata"/> envelope, so a two-sided
/// quarter is no longer indistinguishable from a filing Radar never read). It composes the merged Infrastructure
/// interfaces — <see cref="ISecEarningsReleaseReader"/> (fetch + strip the EX-99.1 body) and
/// <see cref="IFilingAnalyzer"/> (typed directional read) — behind the Application
/// <see cref="IDirectionalFilingSignalSource"/> seam. It contains <b>no</b> HTTP and <b>no</b> provider SDK:
/// all network/AI specifics stay behind the injected interfaces (AD-5).
/// <para>
/// Every reader/analyzer failure degrades to "no directional signal for that filing" and never aborts the
/// batch; only genuine caller cancellation propagates. Analysis is strictly sequential and capped at
/// <see cref="DirectionalFilingSignalOptions.MaxFilingsPerRun"/> per run.
/// </para>
/// <para>
/// To cut the www.sec.gov footprint (spec 107) it is CACHE-FIRST, structured as TWO passes (spec 126) so the
/// per-run cap (<see cref="DirectionalFilingSignalOptions.MaxFilingsPerRun"/>) bounds only NEW AI analyses, never
/// total scoring contribution:
/// <list type="number">
/// <item>
/// <b>Pass 1 — replay (unbounded, SEC-independent, breaker-independent).</b> Every eligible in-window earnings
/// filing is looked up in the <see cref="IAnalyzedFilingCache"/> by accession. A hit replays a field-identical
/// <see cref="DirectionalFilingSignal"/> (or, since spec 204, reconstructs the non-directional READ signal
/// from a v3 no-signal record's cause fields — a cause-less no-signal record replays nothing, the pre-204
/// behaviour) WITHOUT any www.sec.gov
/// fetch or AI call — so a cached directional read keeps contributing for as long as its evidence is in the
/// scoring window, regardless of how many newer filings exist elsewhere in the universe, and regardless of a
/// tripped 429 breaker. Cache MISSES are collected newest-first for pass 2. Cache hits never touch the cap and
/// never touch the breaker.
/// </item>
/// <item>
/// <b>Pass 2 — analyze (capped + breaker-guarded).</b> The misses are walked newest-first and at most
/// <see cref="DirectionalFilingSignalOptions.MaxFilingsPerRun"/> of them are fetched + analyzed (each a genuine
/// NEW analysis attempt); the remainder is left uncached for a later run (an uncached backlog therefore drains
/// over successive runs instead of starving under the newest-N window). A miss that is analyzed caches ONLY an
/// authoritative successful read (a signal or a confirmed no-signal seen on REAL content) — a failed read is
/// NEVER cached, and neither is a structurally successful read whose fetched body was empty/implausibly short
/// (spec 114: a degenerate fetch is not a real no-signal; it is left uncached so a later healthy run re-attempts
/// it), so a transient block cannot permanently suppress a filing. The per-run 429 circuit breaker
/// (<see cref="DirectionalFilingSignalOptions.MaxConsecutiveRateLimited"/>) lives in THIS pass: it stops
/// attempting the remaining misses once the host appears blocked; a success or any non-429 failure resets the
/// count, so only an unbroken run of 429s trips it (cache hits are already handled in pass 1 and cannot be
/// dropped by a tripped breaker). The cache only changes WHETHER a fetch happens — the scored signal set for a
/// given evidence window is unchanged.
/// </item>
/// </list>
/// </para>
/// <para>
/// Diagnostics (spec 115): when an optional <see cref="IFilingReadDebugSink"/> is registered, every ANALYSIS
/// attempt — signal produced, below-confidence, non-directional (Mixed/Unknown), or empty-body-skipped — emits
/// one <see cref="FilingReadDebugRecord"/> stamped with the pipeline's <c>asOfUtc</c>. Cache hits and fetch
/// failures are NOT analysis attempts and emit nothing. The sink is best-effort: every call is guarded so even
/// a throwing implementation cannot abort the batch or change the produced signal set, and a null sink (the
/// default when the feature is off) is zero behaviour change. Deliberately NOT a fingerprint input.
/// </para>
/// </summary>
internal sealed partial class DirectionalFilingSignalSource : IDirectionalFilingSignalSource
{
    private const string EarningsItemCode = "2.02";

    /// <summary>
    /// Minimum plausible EX-99.1 body length (chars, after trimming) for a read to count as authoritative
    /// (spec 114). A real earnings release is never a few bytes — a shorter body means the fetch was degenerate
    /// (e.g. an error/interstitial page during a www.sec.gov block stripped to almost nothing), so the read is
    /// neither analyzed nor cached and a later run re-attempts it. An operational threshold like
    /// MaxFilingsPerRun — deliberately NOT a scoring-fingerprint input.
    /// </summary>
    private const int MinPlausibleBodyLength = 200;

    /// <summary>
    /// Upper bound (chars) on the input head carried by a spec-115 debug record. A diagnostic bound only — the
    /// analyzer's own MaxInputLength governs what the model actually sees; this merely caps what the opt-in
    /// debug record stores of it. Like MaxFilingsPerRun, deliberately NOT a scoring/fingerprint input.
    /// </summary>
    private const int DebugInputHeadMaxLength = 2000;

    private readonly ISecEarningsReleaseReader _reader;
    private readonly IFilingAnalyzer _analyzer;
    private readonly IAnalyzedFilingCache _cache;
    private readonly DirectionalFilingSignalOptions _options;
    private readonly ILogger<DirectionalFilingSignalSource> _logger;
    private readonly IFilingReadDebugSink? _debugSink;
    private readonly string _scoringDescriptor;
    private readonly string _comparabilityPolicy;

    public DirectionalFilingSignalSource(
        ISecEarningsReleaseReader reader,
        IFilingAnalyzer analyzer,
        IAnalyzedFilingCache cache,
        DirectionalFilingSignalOptions options,
        ILogger<DirectionalFilingSignalSource> logger,
        IFilingReadDebugSink? debugSink = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _analyzer = analyzer;
        _cache = cache;
        _options = options;
        _logger = logger;
        // Optional dependency (same pattern as RadarPipelineRunner's IDirectionalFilingSignalSource?): MS DI
        // passes the default null when no IFilingReadDebugSink is registered, so the spec-115 diagnostics are
        // strictly opt-in and the default graph is byte-for-byte unchanged.
        _debugSink = debugSink;

        // Build the fingerprint contribution ONCE (AD-3 determinism): only the per-signal magnitudes that set an
        // emitted signal's Strength/Novelty/confidence-gate are hashed, plus (spec 119) the READING MODEL
        // identity, because a different model produces a different DIRECTION for the same filing.
        // MaxFilingsPerRun and MaxConsecutiveRateLimited are cost/operational caps (a per-run fetch limit and a
        // 429 circuit breaker) — they are EXCLUDED so tuning them does not falsely re-stamp otherwise-comparable
        // runs. They bound how MANY filings get read, never the Strength/Novelty/direction/confidence-gate of a
        // signal that IS emitted. NOTE: spec 105 excluded ScoringWindowDays on what looked like the same
        // grounds; SPEC 148 REVERSED THAT SPECIFIC CALL — the scoring window IS hashed now, because it changes
        // WHICH signals are scored (it bounds both the current and the previous/velocity window). These caps do
        // not, so they stay excluded on their own merits rather than by that analogy.
        // InvariantCulture keeps the string culture-independent; "G29" is the decimal round-trip format ("R" is
        // documented only for the floating-point types, not decimal), so the MinConfidence contribution is
        // injective across [0,1]. Field order is FIXED (str, nov, minconf, model, then — spec 160, appended
        // AFTER model per spec 119's new-fields-LAST precedent so the existing prefix stays byte-stable —
        // cmpscan (the comparability-scan rule-STRUCTURE identity) and cmpcap (the cap magnitude by value,
        // G29 like minconf: the cap bounds the confidence of emitted signals, so it is a comparability input
        // exactly like MinConfidence and the reading model). The model value is escaped with the shared
        // DescriptorEscaping so a model id containing a reserved delimiter cannot collide with a different
        // descriptor (AD-3).
        _scoringDescriptor = string.Create(
            CultureInfo.InvariantCulture,
            $"directional-filing:str={_options.Strength};nov={_options.Novelty};minconf={_options.MinConfidence.ToString("G29", CultureInfo.InvariantCulture)};model={DescriptorEscaping.Escape(_options.ModelIdentity?.Trim() ?? string.Empty)};cmpscan={EarningsComparabilityScan.Version};cmpcap={_options.ComparabilityConfidenceCap.ToString("G29", CultureInfo.InvariantCulture)}");

        // The comparability POLICY every cache record written by this source is stamped with (spec 160):
        // scanner structure version + cap magnitude, composed once so the stamp and the pass-1 lookup
        // comparison cannot drift.
        _comparabilityPolicy = EarningsComparabilityScan.Policy(_options.ComparabilityConfidenceCap);
    }

    /// <inheritdoc />
    public string ScoringDescriptor() => _scoringDescriptor;

    public async Task<IReadOnlyList<DirectionalFilingSignal>> ProduceAsync(
        IReadOnlyList<EvidenceItem> candidateEvidence,
        DateTimeOffset asOfUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidateEvidence);
        ct.ThrowIfCancellationRequested();

        // Keep only earnings 8-Ks (form 8-K + item 2.02) whose CIK + dashed accession parse from the index
        // SourceUrl and order them deterministically (newest observed first, Id tiebreak). Spec 126: NO .Take
        // here — the whole in-window eligible set is retained; the MaxFilingsPerRun cap now gates pass 2 (new AI
        // analyses) only, so every cached directional read replays and the cap no longer caps scoring coverage.
        var eligible = candidateEvidence
            .Select(ev => (Evidence: ev, Read: TryResolveFiling(ev)))
            .Where(x => x.Read is not null)
            .OrderByDescending(x => x.Evidence.PublishedAtUtc ?? x.Evidence.CollectedAtUtc)
            .ThenBy(x => x.Evidence.Id)
            .ToList();

        var produced = new List<DirectionalFilingSignal>();

        // Pass 1 — replay (unbounded, SEC-independent, breaker-independent): consult the cache for every eligible
        // filing. A hit replays its result with NO www.sec.gov fetch or AI call (a DirectionalSignalProduced hit
        // re-emits its signal; a confirmed no-signal hit contributes nothing). Cache MISSES are collected in the
        // same newest-first order for pass 2. Cache hits never touch the cap and never touch the 429 breaker.
        var misses = new List<(EvidenceItem Evidence, string Cik, string Accession)>();
        foreach (var (evidence, read) in eligible)
        {
            ct.ThrowIfCancellationRequested();

            var accession = read!.Value.Accession;
            try
            {
                var cached = await _cache.TryGetAsync(accession, ct).ConfigureAwait(false);
                if (cached is null)
                {
                    misses.Add((evidence, read.Value.Cik, accession));
                    continue;
                }

                // Comparability-policy rule (spec 160). A record with a NULL policy is a HIT (heal forward:
                // it was written pre-160 and the accrued cache is never mass-invalidated — legacy reads age out
                // of the scoring window naturally). A record whose policy is non-null but differs from the
                // current policy string (the operator tuned the cap, or the scanner version bumped) is a MISS:
                // it is re-fetched and re-analyzed under the current policy, bounded like any miss by
                // MaxFilingsPerRun and the 429 breaker in pass 2. This applies to BOTH outcomes — a read
                // suppressed under an old lower cap must be re-analyzed (and may now emit) when the cap rises.
                if (cached.ComparabilityPolicy is not null
                    && !string.Equals(cached.ComparabilityPolicy, _comparabilityPolicy, StringComparison.Ordinal))
                {
                    _logger.LogDebug(
                        "Analyzed-filing cache record for accession {Accession} was produced under comparability "
                            + "policy '{Stored}' (current '{Current}'); treating as a cache miss (re-analyze).",
                        accession,
                        cached.ComparabilityPolicy,
                        _comparabilityPolicy);
                    misses.Add((evidence, read.Value.Cik, accession));
                    continue;
                }

                if (cached.Outcome == AnalyzedFilingOutcome.DirectionalSignalProduced && cached.Signal is not null)
                {
                    produced.Add(new DirectionalFilingSignal(cached.Signal, evidence));
                }
                else if (cached.Outcome == AnalyzedFilingOutcome.NoDirectionalSignal
                    && cached.NoSignalCause is FilingNoSignalCause cause
                    && cause != FilingNoSignalCause.EmptyBody
                    && !string.IsNullOrWhiteSpace(cached.ReadDirection)
                    && cached.ReadConfidence is decimal cachedReadConfidence)
                {
                    // Spec 204: a v3 no-signal hit replays the SAME read signal the fresh analysis emitted,
                    // reconstructed deterministically from the record's cause fields through the ONE builder
                    // (BuildReadSignal) the fresh path also uses — so the two cannot drift. The ExtractedSignal
                    // is deliberately NOT stored on the record (IsConsistent keeps requiring
                    // NoDirectionalSignal ⇒ Signal is null); the record carries the FACTS of the read and the
                    // signal is a pure function of them plus the evidence. A record with a null cause
                    // (defensive — a v2 no-signal record is already a version miss at the file-cache layer,
                    // but an in-memory cache implementation may still hand one back) replays nothing, exactly
                    // as pre-204; EmptyBody is in the cause vocabulary but is never cached, so a record
                    // claiming it is untrustworthy and likewise replays nothing.
                    produced.Add(new DirectionalFilingSignal(
                        BuildReadSignal(
                            evidence, cause, cached.ReadDirection, cachedReadConfidence,
                            cached.Rationale ?? string.Empty),
                        evidence));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A cache-lookup failure degrades to "no replay for this filing" and never aborts the batch
                // (mirrors the analyze discipline). It is NOT queued as a miss — a broken cache read must not
                // trigger a fresh www.sec.gov fetch this run; a later run re-consults the cache.
                _logger.LogWarning(
                    ex,
                    "Directional filing cache lookup failed for evidence {EvidenceId}; skipping (no directional signal).",
                    evidence.Id);
            }
        }

        // Pass 2 — analyze (capped + breaker-guarded): walk the misses newest-first and analyze at most
        // MaxFilingsPerRun of them (each a genuine NEW fetch + AI read); the remainder is left uncached for a
        // later run so an uncached backlog drains over successive runs instead of starving under the newest-N cut.
        var cap = Math.Max(0, _options.MaxFilingsPerRun);

        // Per-run 429 circuit breaker (spec 107): stop after this many CONSECUTIVE rate-limited reads (the host
        // appears blocked). 0 disables it (unbounded — the pre-spec-107 behaviour). A success or any non-429
        // failure resets the count, so only an unbroken run of 429s trips the breaker. It guards pass 2 only:
        // pass-1 cache hits have already contributed, so a tripped breaker can no longer drop cached replays.
        var breaker = Math.Max(0, _options.MaxConsecutiveRateLimited);
        var consecutiveRateLimited = 0;
        var newAnalyses = 0;

        for (var i = 0; i < misses.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (newAnalyses >= cap)
            {
                // Cap reached: leave the remaining misses uncached for a later run (same discipline as a
                // failed/unattempted read — never cached).
                break;
            }

            var (evidence, cik, accession) = misses[i];

            // A genuine new analysis attempt consumes one cap slot regardless of its outcome (fetch failure,
            // 429, non-authoritative body, or a produced/no signal all count) — this bounds cost, not results.
            newAnalyses++;

            try
            {
                var analysis = await AnalyzeFilingAsync(evidence, cik, accession, asOfUtc, ct)
                    .ConfigureAwait(false);
                var outcome = analysis.Outcome;

                if (outcome == SecEarningsReleaseReadOutcome.Success)
                {
                    // Any structurally successful read is a non-429 outcome: it resets the consecutive-429
                    // counter whether or not it was authoritative enough to cache.
                    consecutiveRateLimited = 0;
                    if (!analysis.Cacheable)
                    {
                        // Non-authoritative read (empty/implausibly-short body, spec 114): NOT cached — leave the
                        // filing for a later healthy run to re-attempt. Caching it would freeze a degenerate
                        // fetch in as a false no-signal forever (the 2026-07-18 block-era poison).
                    }
                    else if (analysis.Signal is not null && analysis.NoSignalCause is null)
                    {
                        // Directional read (Improving/Deteriorating at-or-above the gate): unchanged pre-204
                        // path — the whole signal rides the record so a replay is field-identical.
                        produced.Add(new DirectionalFilingSignal(analysis.Signal, evidence));
                        await _cache.PutAsync(
                            new AnalyzedFilingRecord(
                                accession,
                                AnalyzedFilingOutcome.DirectionalSignalProduced,
                                analysis.Signal,
                                evidence.PublishedAtUtc ?? evidence.CollectedAtUtc,
                                AnalyzedFilingRecord.CurrentCacheVersion,
                                _comparabilityPolicy,
                                analysis.Markers),
                            ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Read OK on real content but no DIRECTIONAL signal (Mixed/Unknown/below-confidence).
                        // Spec 204: the read is still a READ — it is emitted as its own GuidanceChange signal
                        // (Mixed or Neutral at the keyword-fallback magnitudes, so the score does not move)
                        // and the cache record NAMES THE CAUSE (direction/confidence/rationale) so pass 1 can
                        // replay the same signal deterministically. The record's Outcome stays
                        // NoDirectionalSignal with a null Signal — the signal is reconstructed on replay,
                        // never stored — and the comparability policy + markers are recorded exactly as
                        // before (spec 160): a no-signal verdict reached under an old policy must become a
                        // miss when the policy changes.
                        if (analysis.Signal is not null)
                        {
                            produced.Add(new DirectionalFilingSignal(analysis.Signal, evidence));
                        }

                        await _cache.PutAsync(
                            new AnalyzedFilingRecord(
                                accession,
                                AnalyzedFilingOutcome.NoDirectionalSignal,
                                null,
                                null,
                                AnalyzedFilingRecord.CurrentCacheVersion,
                                _comparabilityPolicy,
                                analysis.Markers,
                                analysis.NoSignalCause,
                                analysis.ReadDirection,
                                analysis.ReadConfidence,
                                analysis.Rationale),
                            ct).ConfigureAwait(false);
                    }
                }
                else if (outcome == SecEarningsReleaseReadOutcome.RateLimited)
                {
                    // A failed read is NEVER cached (leave it for a later run). Only 429s feed the breaker.
                    consecutiveRateLimited++;
                    if (breaker > 0 && consecutiveRateLimited >= breaker)
                    {
                        _logger.LogWarning(
                            "SEC www.sec.gov returned {N} consecutive HTTP 429s; skipping remaining {M} earnings "
                                + "reads this run (host appears blocked).",
                            consecutiveRateLimited,
                            misses.Count - (i + 1));
                        break;
                    }
                }
                else
                {
                    // A non-429 read failure is not cached and BREAKS the consecutive-429 run (it is a per-filing
                    // problem, not a host block): reset the counter so two 429s separated by a different failure
                    // (e.g. a timeout) are not counted as consecutive and cannot trip the breaker.
                    consecutiveRateLimited = 0;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Graceful degradation: one bad filing must never abort the batch (mirrors the reader /
                // analyzer discipline). No directional signal for this filing; the run continues. A thrown
                // failure (e.g. an HttpClient timeout) is also a non-429 outcome that breaks the consecutive-429
                // run — reset the counter so it cannot make separated 429s trip the breaker.
                consecutiveRateLimited = 0;
                _logger.LogWarning(
                    ex,
                    "Directional filing read failed for evidence {EvidenceId}; skipping (no directional signal).",
                    evidence.Id);
            }
        }

        return produced;
    }

    /// <summary>
    /// The result of one analysis attempt. <c>Signal</c> is the emitted <c>GuidanceChange</c> — since spec 204
    /// EVERY authoritative read emits one: a directional signal (Improving/Deteriorating at-or-above the
    /// gate, <c>NoSignalCause</c> null), or the spec-204 read signal (Mixed/Unknown/below-gate,
    /// <c>NoSignalCause</c> + <c>ReadDirection</c>/<c>ReadConfidence</c>/<c>Rationale</c> populated so the
    /// caller can name the cause on the cache record). <c>Cacheable</c> is false for a fetch failure or an
    /// empty/implausibly-short body (spec 114), which also emits no signal.
    /// </summary>
    private sealed record FilingAnalysis(
        SecEarningsReleaseReadOutcome Outcome,
        ExtractedSignal? Signal,
        bool Cacheable,
        ComparabilityMarkers? Markers,
        FilingNoSignalCause? NoSignalCause = null,
        string? ReadDirection = null,
        decimal? ReadConfidence = null,
        string? Rationale = null);

    /// <summary>
    /// Reads the EX-99.1 body, analyzes it, applies the confidence gate + direction mapping, and returns the
    /// read <see cref="SecEarningsReleaseReadOutcome"/> paired with a single <c>GuidanceChange</c>
    /// <see cref="ExtractedSignal"/> (or <c>null</c>), plus a <c>Cacheable</c> flag. The outcome lets the caller
    /// distinguish a fetch FAILURE (non-<see cref="SecEarningsReleaseReadOutcome.Success"/>, never cached) from a
    /// SUCCESS; <c>Cacheable</c> is false for a structurally successful read whose body was empty/implausibly
    /// short (below <see cref="MinPlausibleBodyLength"/>, spec 114) — a non-authoritative read the caller must
    /// NOT cache (and never sees the analyzer), so a later run re-attempts it. Never calls the analyzer on a
    /// non-success read. When the spec-115 debug sink is registered, each analysis attempt (including the
    /// empty-body skip) emits one guarded, best-effort debug record stamped with <paramref name="asOfUtc"/>;
    /// a fetch failure emits nothing (no analysis happened).
    /// <para>
    /// SPEC 204 — a Mixed read is a READ. A SUCCESS on real content with no directional signal is no longer a
    /// silent token: it emits the read as its own signal (see <see cref="BuildReadSignal"/>) and reports the
    /// CAUSE so the caller can persist it. Classification order, deliberate: <b>Unknown first</b> (any
    /// confidence — "the model could not establish a direction" is a fact about the READ, and letting the
    /// gate see an Unknown's confidence first would record the gate's verdict on a value that never claimed a
    /// direction; consequence, recorded: a low-confidence Unknown's DEBUG outcome is now
    /// <see cref="FilingReadOutcome.NoDirectionalRead"/> rather than BelowConfidence — a diagnostic-only
    /// reclassification that mirrors the persisted cause), <b>then the gate</b> on the CAPPED confidence
    /// (unchanged, spec 160 — this is where a below-gate Improving/Deteriorating/Mixed lands), <b>then
    /// Mixed</b> (at-or-above the gate: the confident two-sided read), then the directional mapping.
    /// </para>
    /// </summary>
    private async Task<FilingAnalysis> AnalyzeFilingAsync(
        EvidenceItem evidence, string cik, string accession, DateTimeOffset asOfUtc, CancellationToken ct)
    {
        var read = await _reader.ReadAsync(cik, accession, ct).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            _logger.LogDebug(
                "EX-99.1 read for evidence {EvidenceId} (CIK {Cik}, accession {Accession}) was {Outcome}; skipping.",
                evidence.Id,
                cik,
                accession,
                read.Outcome);
            return new FilingAnalysis(read.Outcome, null, Cacheable: false, Markers: null);
        }

        // Empty/short-body guard (spec 114): a structurally-successful fetch whose stripped body is implausibly
        // short is a degenerate read (an earnings release is never a few bytes) — do NOT analyze and do NOT let
        // the caller cache it; a later healthy run re-attempts the filing.
        var trimmedBodyLength = read.PlainText.AsSpan().Trim().Length;
        if (trimmedBodyLength < MinPlausibleBodyLength)
        {
            _logger.LogDebug(
                "EX-99.1 read for evidence {EvidenceId} (CIK {Cik}, accession {Accession}) succeeded but the body "
                    + "was implausibly short ({Length} chars < {Min}); treating as non-authoritative (not cached).",
                evidence.Id,
                cik,
                accession,
                trimmedBodyLength,
                MinPlausibleBodyLength);
            await TryRecordReadDebugAsync(
                accession, evidence, read.PlainText, trimmedBodyLength,
                sentiment: null, FilingReadOutcome.EmptyBodySkipped, asOfUtc,
                markers: null, cappedConfidence: null, ct).ConfigureAwait(false);
            // Spec 204: deliberately NO read signal and NO cause here — no model call happened, so there is
            // no read to persist. FilingNoSignalCause.EmptyBody exists in the vocabulary but is never cached.
            return new FilingAnalysis(SecEarningsReleaseReadOutcome.Success, null, Cacheable: false, Markers: null);
        }

        // Comparability scan (spec 160): deterministic, on the FULL stripped body — deliberately BEFORE the
        // analyzer's own MaxInputLength truncation, so a marker past the truncation point still counts.
        var markers = EarningsComparabilityScan.Scan(read.PlainText);

        var sentiment = await _analyzer.AnalyzeAsync(read.PlainText, ct).ConfigureAwait(false);

        // Comparability cap (spec 160): when the release itself declares a comparability break, the persisted
        // confidence is bounded by min(readConfidence, cap) — the model's read is kept; only the weight Radar
        // assigns it is bounded. The cap is a CEILING, not a floor: a read already at or below the cap is
        // untouched, and then (mirroring the spec-149 explanation rule) the Reason is only annotated when the
        // cap actually moved the number — which also makes cap = 1.0 the exact off-switch (min(conf, 1.0) is
        // the identity, so behaviour is byte-identical to pre-160).
        var confidence = sentiment.Confidence;
        decimal? cappedConfidence = null;
        if (markers.CapTriggering.Count > 0 && _options.ComparabilityConfidenceCap < confidence)
        {
            confidence = _options.ComparabilityConfidenceCap;
            cappedConfidence = confidence;
            _logger.LogDebug(
                "Directional read for evidence {EvidenceId} capped {Raw} -> {Capped} (comparability markers: {Markers}).",
                evidence.Id,
                sentiment.Confidence,
                confidence,
                string.Join(", ", markers.CapTriggering));
        }

        // Builds the spec-204 non-directional-read result: the read signal (Mixed or Neutral at the keyword
        // magnitudes) plus the cause fields the caller persists on the no-signal cache record. The read
        // direction is the model's OWN token and the confidence is the EFFECTIVE (capped) value the gate
        // saw — one value across the signal's Reason, its metadata envelope and the cache record.
        FilingAnalysis NonDirectionalRead(FilingNoSignalCause cause)
        {
            var readDirection = sentiment.Direction.ToString();
            return new FilingAnalysis(
                SecEarningsReleaseReadOutcome.Success,
                BuildReadSignal(evidence, cause, readDirection, confidence, sentiment.Rationale),
                Cacheable: true,
                Markers: markers,
                NoSignalCause: cause,
                ReadDirection: readDirection,
                ReadConfidence: confidence,
                Rationale: sentiment.Rationale);
        }

        // Spec 204 classification order — Unknown FIRST, at any confidence: an Unknown verdict never claimed
        // a direction, so the gate has nothing to gate; recording it as "below-confidence" would name the
        // wrong cause. (Diagnostic consequence, deliberate: a low-confidence Unknown's debug outcome is now
        // NoDirectionalRead, where it was BelowConfidence pre-204 — the debug outcome mirrors the cause.)
        if (sentiment.Direction == FilingDirection.Unknown)
        {
            _logger.LogDebug(
                "Directional read for evidence {EvidenceId} was Unknown; persisting the read as a Neutral signal.",
                evidence.Id);
            await TryRecordReadDebugAsync(
                accession, evidence, read.PlainText, trimmedBodyLength,
                sentiment, FilingReadOutcome.NoDirectionalRead, asOfUtc,
                markers, cappedConfidence, ct).ConfigureAwait(false);
            return NonDirectionalRead(FilingNoSignalCause.Unknown);
        }

        // Confidence gate (CLAUDE.md): a low-confidence read produces no directional signal. Applied to the
        // CAPPED value (spec 160: the gate comes AFTER the cap, so a cap configured below MinConfidence
        // suppresses capped signals). Spec 204: the suppressed read (Improving/Deteriorating/Mixed alike) is
        // still persisted — as a Neutral read signal naming the below-confidence cause.
        if (confidence < _options.MinConfidence)
        {
            _logger.LogDebug(
                "Directional read for evidence {EvidenceId} was below MinConfidence ({Confidence} < {Min}); "
                    + "persisting the read as a Neutral signal.",
                evidence.Id,
                confidence,
                _options.MinConfidence);
            await TryRecordReadDebugAsync(
                accession, evidence, read.PlainText, trimmedBodyLength,
                sentiment, FilingReadOutcome.BelowConfidence, asOfUtc,
                markers, cappedConfidence, ct).ConfigureAwait(false);
            return NonDirectionalRead(FilingNoSignalCause.BelowConfidence);
        }

        // Spec 204: a confident Mixed read is emitted as a Mixed GuidanceChange — SignalDirection.Mixed has
        // existed since the domain was written and scores 0 exactly like Neutral in every component
        // (ScoreSignalMath treats the two identically), so this is provenance, never a score move.
        if (sentiment.Direction == FilingDirection.Mixed)
        {
            _logger.LogDebug(
                "Directional read for evidence {EvidenceId} was Mixed; persisting the read as a Mixed signal.",
                evidence.Id);
            await TryRecordReadDebugAsync(
                accession, evidence, read.PlainText, trimmedBodyLength,
                sentiment, FilingReadOutcome.NoDirectionalRead, asOfUtc,
                markers, cappedConfidence, ct).ConfigureAwait(false);
            return NonDirectionalRead(FilingNoSignalCause.Mixed);
        }

        var direction = sentiment.Direction switch
        {
            FilingDirection.Improving => "Positive",
            FilingDirection.Deteriorating => "Negative",
            // Unknown/Mixed were handled above; a future enum member is a contract change that must fail
            // loudly here rather than be silently mapped onto a direction.
            _ => throw new InvalidOperationException(
                $"Unhandled filing direction '{sentiment.Direction}' for evidence {evidence.Id}."),
        };

        await TryRecordReadDebugAsync(
            accession, evidence, read.PlainText, trimmedBodyLength,
            sentiment, FilingReadOutcome.DirectionalSignalProduced, asOfUtc,
            markers, cappedConfidence, ct).ConfigureAwait(false);

        // The SupportingExcerpt must be a verbatim slice of the evidence (the mapper enforces
        // excerpt-in-evidence). The evidence Title is wholly contained in the composed searchable text, so
        // it is a stable, guaranteed-present excerpt. The advice-scrubbed AI rationale (spec 74) rides the
        // Reason field (not provenance-checked) to surface the AI basis for audit/report. A capped signal's
        // Reason additionally names the cap-triggering markers (and ONLY those — diagnostic-only matches are
        // recorded, never surfaced as a cap), so the weekly report's "Why noticed" shows the cap the same way
        // it shows everything else.
        var reason = cappedConfidence is null
            ? sentiment.Rationale
            : sentiment.Rationale
                + " (comparability cap: matched "
                + string.Join(", ", markers.CapTriggering.Select(m => "'" + m + "'"))
                + ")";

        return new FilingAnalysis(
            SecEarningsReleaseReadOutcome.Success,
            new ExtractedSignal(
                CompanyMention: evidence.SourceName,
                SignalType: "GuidanceChange",
                Direction: direction,
                Strength: _options.Strength,
                Novelty: _options.Novelty,
                Confidence: confidence,
                SupportingExcerpt: evidence.Title,
                Reason: reason),
            Cacheable: true,
            Markers: markers);
    }

    /// <summary>
    /// SPEC 204 — the ONE builder of the non-directional read signal, used by BOTH the fresh-analysis path
    /// and the pass-1 cache replay so the two cannot drift: the replayed signal is field-for-field what the
    /// fresh path emitted, reconstructed from the record's cause fields plus the evidence.
    /// <para>
    /// Shape: a <c>GuidanceChange</c> at the KEYWORD FALLBACK's exact magnitudes
    /// (<see cref="FilingReadSignalMetadata.Strength"/>/<see cref="FilingReadSignalMetadata.Novelty"/>/<see cref="FilingReadSignalMetadata.Confidence"/> —
    /// pinned equal to the extractor's "results of operations" rule by test), Direction <c>Mixed</c> for a
    /// confident Mixed read and <c>Neutral</c> otherwise; both score 0 directional mass, so every component
    /// is byte-identical to the keyword copy it stands in for (the spec-204 engine pin asserts it). The
    /// SupportingExcerpt is the evidence Title (the same guaranteed-in-evidence excerpt the directional path
    /// uses — it must pass the mapper's excerpt-in-evidence guard) and CompanyMention is the evidence
    /// SourceName, so resolution behaves exactly like every other filing signal.
    /// </para>
    /// <para>
    /// The Reason prefix is exactly informative and nothing more — outcome-bearing direction token, the
    /// EFFECTIVE (capped) confidence the gate saw, the below-gate note on that row only, then the
    /// advice-scrubbed model rationale: <c>AI earnings read: Mixed 0.85 — …</c> /
    /// <c>AI earnings read: Unknown 0.3 — …</c> /
    /// <c>AI earnings read: Improving 0.45 (below MinConfidence 0.6) — …</c>. Confidences render invariant
    /// <c>G29</c> (the decimal round-trip format the descriptor already uses; deterministic, AD-3). The
    /// spec-160 cap-marker annotation the DIRECTIONAL path appends is deliberately NOT added here: the
    /// prefix already carries the capped value the gate compared, and the markers stay on the cache record
    /// and the debug record — keeping the Reason a pure function of (cause, direction, confidence,
    /// rationale, MinConfidence) is what makes the cache replay reconstructible at all.
    /// </para>
    /// <para>
    /// Two recorded replay caveats, both the same class as the directional record's whole-signal replay:
    /// the below-gate note renders the CURRENT <c>MinConfidence</c> (the value is not on the record; a
    /// retuned gate changes only that display text on replays, exactly as a retuned gate never re-gates a
    /// replayed directional signal), and the metadata's <c>filingReadModel</c> is the CURRENT
    /// <see cref="DirectionalFilingSignalOptions.ModelIdentity"/> — safe because the cache is model-SEGMENTED
    /// (spec 118), so a record can only replay under the model identity that wrote it.
    /// </para>
    /// </summary>
    private ExtractedSignal BuildReadSignal(
        EvidenceItem evidence,
        FilingNoSignalCause cause,
        string readDirection,
        decimal effectiveConfidence,
        string rationale)
    {
        var direction = cause == FilingNoSignalCause.Mixed ? "Mixed" : "Neutral";
        var confidenceText = effectiveConfidence.ToString("G29", CultureInfo.InvariantCulture);
        var belowGateNote = cause == FilingNoSignalCause.BelowConfidence
            ? " (below MinConfidence " + _options.MinConfidence.ToString("G29", CultureInfo.InvariantCulture) + ")"
            : string.Empty;

        return new ExtractedSignal(
            CompanyMention: evidence.SourceName,
            SignalType: "GuidanceChange",
            Direction: direction,
            Strength: FilingReadSignalMetadata.Strength,
            Novelty: FilingReadSignalMetadata.Novelty,
            Confidence: FilingReadSignalMetadata.Confidence,
            SupportingExcerpt: evidence.Title,
            Reason: $"AI earnings read: {readDirection} {confidenceText}{belowGateNote} — {rationale}",
            MetadataJson: FilingReadSignalMetadata.Compose(
                cause, readDirection, effectiveConfidence, _options.ModelIdentity?.Trim() ?? string.Empty));
    }

    /// <summary>
    /// Emits one spec-115 diagnostic record for an analysis attempt, best-effort: any sink failure is logged
    /// and swallowed so even a throwing <see cref="IFilingReadDebugSink"/> cannot abort the batch or change the
    /// produced signal set (only genuine caller cancellation propagates, as everywhere in this class). A null
    /// sink (the default, feature off) is a no-op with no allocation. <paramref name="sentiment"/> is null only
    /// for <see cref="FilingReadOutcome.EmptyBodySkipped"/>, where no model call happened.
    /// </summary>
    private async Task TryRecordReadDebugAsync(
        string accession,
        EvidenceItem evidence,
        string plainText,
        int trimmedBodyLength,
        FilingSentiment? sentiment,
        FilingReadOutcome outcome,
        DateTimeOffset asOfUtc,
        ComparabilityMarkers? markers,
        decimal? cappedConfidence,
        CancellationToken ct)
    {
        if (_debugSink is null)
        {
            return;
        }

        try
        {
            await _debugSink.RecordAsync(
                new FilingReadDebugRecord(
                    accession,
                    evidence.Id,
                    trimmedBodyLength,
                    DebugInputHead(plainText),
                    sentiment?.Direction.ToString(),
                    sentiment?.Confidence,
                    sentiment?.Rationale,
                    outcome,
                    asOfUtc,
                    markers,
                    cappedConfidence),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to record the AI filing-read debug record for accession {Accession}; continuing (diagnostic-only).",
                accession);
        }
    }

    /// <summary>
    /// The bounded leading slice of the trimmed EX-99.1 body carried by a debug record (capped at
    /// <see cref="DebugInputHeadMaxLength"/> — a diagnostic bound, never a scoring input).
    /// </summary>
    private static string DebugInputHead(string plainText)
    {
        var trimmed = plainText.AsSpan().Trim();
        return trimmed.Length > DebugInputHeadMaxLength
            ? new string(trimmed[..DebugInputHeadMaxLength])
            : trimmed.ToString();
    }

    /// <summary>
    /// Confirms the evidence is an earnings 8-K (form 8-K + item 2.02) and returns its CIK + dashed
    /// accession parsed from the index <see cref="EvidenceItem.SourceUrl"/>, or <c>null</c> when it is not
    /// an earnings 8-K or the URL cannot be parsed (never guess a CIK/accession — skip instead).
    /// </summary>
    private (string Cik, string Accession)? TryResolveFiling(EvidenceItem evidence)
    {
        if (evidence.SourceType != EvidenceSourceType.Filing)
        {
            return null;
        }

        EvidenceMetadata.TryRead(evidence.MetadataJson, out var metadata, out _);

        var form = metadata.TryGetValue("form", out var f) ? f : null;
        if (!string.Equals(form, "8-K", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Prefer the discrete items metadata key (written by the collector); fall back to parsing the
        // "[items: ...]" segment from the Title so older evidence without the key still gates correctly.
        var items = metadata.TryGetValue("items", out var i) && !string.IsNullOrWhiteSpace(i)
            ? i
            : ParseItemsFromTitle(evidence.Title);
        if (!ContainsEarningsItem(items))
        {
            return null;
        }

        var parsed = ParseCikAndAccession(evidence.SourceUrl);
        if (parsed is null)
        {
            _logger.LogDebug(
                "Could not parse CIK/accession from evidence {EvidenceId} SourceUrl; skipping.",
                evidence.Id);
            return null;
        }

        // Cross-check the parsed accession against the metadata accessionNumber when present; a mismatch
        // means the identifiers are not trustworthy, so skip rather than guess.
        if (metadata.TryGetValue("accessionNumber", out var metaAccession)
            && !string.IsNullOrWhiteSpace(metaAccession)
            && !string.Equals(metaAccession, parsed.Value.Accession, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "Parsed accession {Parsed} disagrees with metadata accessionNumber {Meta} for evidence {EvidenceId}; skipping.",
                parsed.Value.Accession,
                metaAccession,
                evidence.Id);
            return null;
        }

        return parsed;
    }

    private static bool ContainsEarningsItem(string? items)
    {
        if (string.IsNullOrWhiteSpace(items))
        {
            return false;
        }

        foreach (var code in items.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(code, EarningsItemCode, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ParseItemsFromTitle(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var match = ItemsInTitleRegex().Match(title);
        return match.Success ? match.Groups["items"].Value : null;
    }

    private static (string Cik, string Accession)? ParseCikAndAccession(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var match = IndexUrlRegex().Match(sourceUrl);
        if (!match.Success)
        {
            return null;
        }

        var cik = match.Groups["cik"].Value.TrimStart('0');
        if (cik.Length == 0)
        {
            cik = "0";
        }

        var accession = match.Groups["accession"].Value;
        return string.IsNullOrWhiteSpace(accession) ? null : (cik, accession);
    }

    [GeneratedRegex(@"\[items:\s*(?<items>[^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ItemsInTitleRegex();

    [GeneratedRegex(
        @"/edgar/data/(?<cik>\d+)/[^/]+/(?<accession>[^/]+?)-index\.html?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex IndexUrlRegex();
}
