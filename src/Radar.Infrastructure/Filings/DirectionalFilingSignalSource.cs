using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Application.Filings;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Filings;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// Opt-in enrichment that turns an earnings-8-K <see cref="EvidenceItem"/> into at most one confidence-gated
/// directional <c>GuidanceChange</c> <see cref="ExtractedSignal"/>. It composes the merged Infrastructure
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
/// To cut the www.sec.gov footprint (spec 107) it is CACHE-FIRST: each eligible filing's analysis result is
/// looked up in the <see cref="IAnalyzedFilingCache"/> by accession, and a hit replays a field-identical
/// <see cref="DirectionalFilingSignal"/> (or emits nothing for a confirmed no-signal) WITHOUT any www.sec.gov
/// fetch or AI call. A miss fetches + analyzes, then caches ONLY an authoritative successful read (a signal or a
/// confirmed no-signal seen on REAL content) — a failed read is NEVER cached, and neither is a structurally
/// successful read whose fetched body was empty/implausibly short (spec 114: a degenerate fetch is not a real
/// no-signal; it is left uncached so a later healthy run re-attempts it), so a transient block cannot permanently
/// suppress a filing. A
/// per-run 429 circuit breaker (<see cref="DirectionalFilingSignalOptions.MaxConsecutiveRateLimited"/>) stops
/// attempting the remaining filings once the host appears blocked; a success, a cache hit, or any non-429 failure
/// resets the count, so only an unbroken run of 429s trips it. The cache only changes WHETHER a fetch happens — the
/// scored signal set is unchanged.
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
        // emitted signal's Strength/Novelty/confidence-gate are hashed. MaxFilingsPerRun and
        // MaxConsecutiveRateLimited are cost/operational caps (a per-run fetch limit and a 429 circuit breaker,
        // like ScoringWindowDays which spec 105 confirmed is deliberately NOT a fingerprint input) — they are
        // EXCLUDED so tuning them does not falsely re-stamp otherwise-comparable runs. InvariantCulture keeps the
        // string culture-independent; "G29" is the decimal round-trip format ("R" is documented only for the
        // floating-point types, not decimal), so the MinConfidence contribution is injective across [0,1].
        _scoringDescriptor = string.Create(
            CultureInfo.InvariantCulture,
            $"directional-filing:str={_options.Strength};nov={_options.Novelty};minconf={_options.MinConfidence.ToString("G29", CultureInfo.InvariantCulture)}");
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
        // SourceUrl, order deterministically (newest observed first, Id tiebreak), and cap the batch.
        var eligible = candidateEvidence
            .Select(ev => (Evidence: ev, Read: TryResolveFiling(ev)))
            .Where(x => x.Read is not null)
            .OrderByDescending(x => x.Evidence.PublishedAtUtc ?? x.Evidence.CollectedAtUtc)
            .ThenBy(x => x.Evidence.Id)
            .Take(_options.MaxFilingsPerRun)
            .ToList();

        var produced = new List<DirectionalFilingSignal>();

        // Per-run 429 circuit breaker (spec 107): stop after this many CONSECUTIVE rate-limited reads (the host
        // appears blocked). 0 disables it (unbounded — the pre-spec-107 behaviour). A success, a cache hit, or any
        // non-429 failure resets the count, so only an unbroken run of 429s trips the breaker.
        var breaker = Math.Max(0, _options.MaxConsecutiveRateLimited);
        var consecutiveRateLimited = 0;

        for (var i = 0; i < eligible.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var (evidence, read) = eligible[i];
            var accession = read!.Value.Accession;

            try
            {
                // Cache-first (spec 107): a hit replays the previously-analyzed result with NO www.sec.gov fetch
                // or AI call, and does not interact with the breaker (resets the consecutive-429 counter).
                var cached = await _cache.TryGetAsync(accession, ct).ConfigureAwait(false);
                if (cached is not null)
                {
                    consecutiveRateLimited = 0;
                    if (cached.Outcome == AnalyzedFilingOutcome.DirectionalSignalProduced && cached.Signal is not null)
                    {
                        produced.Add(new DirectionalFilingSignal(cached.Signal, evidence));
                    }

                    continue;
                }

                // Cache miss: fetch + analyze.
                var (outcome, signal, cacheable) = await AnalyzeFilingAsync(evidence, read.Value.Cik, accession, asOfUtc, ct)
                    .ConfigureAwait(false);

                if (outcome == SecEarningsReleaseReadOutcome.Success)
                {
                    // Any structurally successful read is a non-429 outcome: it resets the consecutive-429
                    // counter whether or not it was authoritative enough to cache.
                    consecutiveRateLimited = 0;
                    if (!cacheable)
                    {
                        // Non-authoritative read (empty/implausibly-short body, spec 114): NOT cached — leave the
                        // filing for a later healthy run to re-attempt. Caching it would freeze a degenerate
                        // fetch in as a false no-signal forever (the 2026-07-18 block-era poison).
                    }
                    else if (signal is not null)
                    {
                        produced.Add(new DirectionalFilingSignal(signal, evidence));
                        await _cache.PutAsync(
                            new AnalyzedFilingRecord(
                                accession,
                                AnalyzedFilingOutcome.DirectionalSignalProduced,
                                signal,
                                evidence.PublishedAtUtc ?? evidence.CollectedAtUtc,
                                AnalyzedFilingRecord.CurrentCacheVersion),
                            ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Read OK on real content but no directional signal (Mixed/Unknown/below-confidence) —
                        // cache it so we never re-fetch this filing.
                        await _cache.PutAsync(
                            new AnalyzedFilingRecord(
                                accession,
                                AnalyzedFilingOutcome.NoDirectionalSignal,
                                null,
                                null,
                                AnalyzedFilingRecord.CurrentCacheVersion),
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
                            eligible.Count - (i + 1));
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
    /// Reads the EX-99.1 body, analyzes it, applies the confidence gate + direction mapping, and returns the
    /// read <see cref="SecEarningsReleaseReadOutcome"/> paired with a single directional <c>GuidanceChange</c>
    /// <see cref="ExtractedSignal"/> or <c>null</c>, plus a <c>Cacheable</c> flag. The outcome lets the caller
    /// distinguish a fetch FAILURE (non-<see cref="SecEarningsReleaseReadOutcome.Success"/>, never cached) from a
    /// SUCCESS; <c>Cacheable</c> is false for a structurally successful read whose body was empty/implausibly
    /// short (below <see cref="MinPlausibleBodyLength"/>, spec 114) — a non-authoritative read the caller must
    /// NOT cache (and never sees the analyzer), so a later run re-attempts it. Only a SUCCESS on real content
    /// with no directional signal (below the gate, Mixed/Unknown) is cached as no-signal. Never calls the
    /// analyzer on a non-success read. When the spec-115 debug sink is registered, each analysis attempt
    /// (including the empty-body skip) emits one guarded, best-effort debug record stamped with
    /// <paramref name="asOfUtc"/>; a fetch failure emits nothing (no analysis happened).
    /// </summary>
    private async Task<(SecEarningsReleaseReadOutcome Outcome, ExtractedSignal? Signal, bool Cacheable)> AnalyzeFilingAsync(
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
            return (read.Outcome, null, Cacheable: false);
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
                sentiment: null, FilingReadOutcome.EmptyBodySkipped, asOfUtc, ct).ConfigureAwait(false);
            return (SecEarningsReleaseReadOutcome.Success, null, Cacheable: false);
        }

        var sentiment = await _analyzer.AnalyzeAsync(read.PlainText, ct).ConfigureAwait(false);

        // Confidence gate (CLAUDE.md): a low-confidence read produces no directional signal — the
        // deterministic Neutral GuidanceChange (spec 57) stands.
        if (sentiment.Confidence < _options.MinConfidence)
        {
            _logger.LogDebug(
                "Directional read for evidence {EvidenceId} was below MinConfidence ({Confidence} < {Min}); no signal.",
                evidence.Id,
                sentiment.Confidence,
                _options.MinConfidence);
            await TryRecordReadDebugAsync(
                accession, evidence, read.PlainText, trimmedBodyLength,
                sentiment, FilingReadOutcome.BelowConfidence, asOfUtc, ct).ConfigureAwait(false);
            return (SecEarningsReleaseReadOutcome.Success, null, Cacheable: true);
        }

        var direction = sentiment.Direction switch
        {
            FilingDirection.Improving => "Positive",
            FilingDirection.Deteriorating => "Negative",
            _ => null, // Mixed / Unknown -> no directional signal.
        };
        if (direction is null)
        {
            _logger.LogDebug(
                "Directional read for evidence {EvidenceId} was {Direction}; no directional signal.",
                evidence.Id,
                sentiment.Direction);
            await TryRecordReadDebugAsync(
                accession, evidence, read.PlainText, trimmedBodyLength,
                sentiment, FilingReadOutcome.NoDirectionalRead, asOfUtc, ct).ConfigureAwait(false);
            return (SecEarningsReleaseReadOutcome.Success, null, Cacheable: true);
        }

        await TryRecordReadDebugAsync(
            accession, evidence, read.PlainText, trimmedBodyLength,
            sentiment, FilingReadOutcome.DirectionalSignalProduced, asOfUtc, ct).ConfigureAwait(false);

        // The SupportingExcerpt must be a verbatim slice of the evidence (the mapper enforces
        // excerpt-in-evidence). The evidence Title is wholly contained in the composed searchable text, so
        // it is a stable, guaranteed-present excerpt. The advice-scrubbed AI rationale (spec 74) rides the
        // Reason field (not provenance-checked) to surface the AI basis for audit/report.
        return (
            SecEarningsReleaseReadOutcome.Success,
            new ExtractedSignal(
                CompanyMention: evidence.SourceName,
                SignalType: "GuidanceChange",
                Direction: direction,
                Strength: _options.Strength,
                Novelty: _options.Novelty,
                Confidence: sentiment.Confidence,
                SupportingExcerpt: evidence.Title,
                Reason: sentiment.Rationale),
            Cacheable: true);
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
                    asOfUtc),
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
