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
/// fetch or AI call. A miss fetches + analyzes, then caches ONLY a successful read (a signal or a confirmed
/// no-signal) — a failed read is NEVER cached, so a transient block cannot permanently suppress a filing. A
/// per-run 429 circuit breaker (<see cref="DirectionalFilingSignalOptions.MaxConsecutiveRateLimited"/>) stops
/// attempting the remaining filings once the host appears blocked; a success, a cache hit, or any non-429 failure
/// resets the count, so only an unbroken run of 429s trips it. The cache only changes WHETHER a fetch happens — the
/// scored signal set is unchanged.
/// </para>
/// </summary>
internal sealed partial class DirectionalFilingSignalSource : IDirectionalFilingSignalSource
{
    private const string EarningsItemCode = "2.02";

    private readonly ISecEarningsReleaseReader _reader;
    private readonly IFilingAnalyzer _analyzer;
    private readonly IAnalyzedFilingCache _cache;
    private readonly DirectionalFilingSignalOptions _options;
    private readonly ILogger<DirectionalFilingSignalSource> _logger;
    private readonly string _scoringDescriptor;

    public DirectionalFilingSignalSource(
        ISecEarningsReleaseReader reader,
        IFilingAnalyzer analyzer,
        IAnalyzedFilingCache cache,
        DirectionalFilingSignalOptions options,
        ILogger<DirectionalFilingSignalSource> logger)
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
                var (outcome, signal) = await AnalyzeFilingAsync(evidence, read.Value.Cik, accession, ct)
                    .ConfigureAwait(false);

                if (outcome == SecEarningsReleaseReadOutcome.Success)
                {
                    consecutiveRateLimited = 0;
                    if (signal is not null)
                    {
                        produced.Add(new DirectionalFilingSignal(signal, evidence));
                        await _cache.PutAsync(
                            new AnalyzedFilingRecord(
                                accession,
                                AnalyzedFilingOutcome.DirectionalSignalProduced,
                                signal,
                                evidence.PublishedAtUtc ?? evidence.CollectedAtUtc),
                            ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Read OK but no directional signal (Mixed/Unknown/below-confidence) — cache it so we
                        // never re-fetch this filing.
                        await _cache.PutAsync(
                            new AnalyzedFilingRecord(accession, AnalyzedFilingOutcome.NoDirectionalSignal, null, null),
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
    /// <see cref="ExtractedSignal"/> or <c>null</c>. The outcome lets the caller distinguish a fetch FAILURE
    /// (non-<see cref="SecEarningsReleaseReadOutcome.Success"/>, never cached) from a SUCCESS with no directional
    /// signal (below the gate, Mixed/Unknown — cached as no-signal). Never calls the analyzer on a non-success
    /// read.
    /// </summary>
    private async Task<(SecEarningsReleaseReadOutcome Outcome, ExtractedSignal? Signal)> AnalyzeFilingAsync(
        EvidenceItem evidence, string cik, string accession, CancellationToken ct)
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
            return (read.Outcome, null);
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
            return (SecEarningsReleaseReadOutcome.Success, null);
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
            return (SecEarningsReleaseReadOutcome.Success, null);
        }

        // The SupportingExcerpt must be a verbatim slice of the evidence (the mapper enforces
        // excerpt-in-evidence). The evidence Title is wholly contained in the composed searchable text, so
        // it is a stable, guaranteed-present excerpt. The advice-scrubbed AI rationale (spec 74) rides the
        // Reason field (not provenance-checked) to surface the AI basis for audit/report.
        return (SecEarningsReleaseReadOutcome.Success, new ExtractedSignal(
            CompanyMention: evidence.SourceName,
            SignalType: "GuidanceChange",
            Direction: direction,
            Strength: _options.Strength,
            Novelty: _options.Novelty,
            Confidence: sentiment.Confidence,
            SupportingExcerpt: evidence.Title,
            Reason: sentiment.Rationale));
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
