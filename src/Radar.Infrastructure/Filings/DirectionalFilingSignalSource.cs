using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

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
/// </summary>
internal sealed partial class DirectionalFilingSignalSource : IDirectionalFilingSignalSource
{
    private const string EarningsItemCode = "2.02";

    private readonly ISecEarningsReleaseReader _reader;
    private readonly IFilingAnalyzer _analyzer;
    private readonly DirectionalFilingSignalOptions _options;
    private readonly ILogger<DirectionalFilingSignalSource> _logger;

    public DirectionalFilingSignalSource(
        ISecEarningsReleaseReader reader,
        IFilingAnalyzer analyzer,
        DirectionalFilingSignalOptions options,
        ILogger<DirectionalFilingSignalSource> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _analyzer = analyzer;
        _options = options;
        _logger = logger;
    }

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
        foreach (var (evidence, read) in eligible)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var signal = await AnalyzeFilingAsync(evidence, read!.Value.Cik, read.Value.Accession, ct)
                    .ConfigureAwait(false);
                if (signal is not null)
                {
                    produced.Add(new DirectionalFilingSignal(signal, evidence));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Graceful degradation: one bad filing must never abort the batch (mirrors the reader /
                // analyzer discipline). No directional signal for this filing; the run continues.
                _logger.LogWarning(
                    ex,
                    "Directional filing read failed for evidence {EvidenceId}; skipping (no directional signal).",
                    evidence.Id);
            }
        }

        return produced;
    }

    /// <summary>
    /// Reads the EX-99.1 body, analyzes it, applies the confidence gate + direction mapping, and returns a
    /// single directional <c>GuidanceChange</c> <see cref="ExtractedSignal"/> or <c>null</c> (below the
    /// gate, Mixed/Unknown, or a non-success read). Never calls the analyzer on a non-success read.
    /// </summary>
    private async Task<ExtractedSignal?> AnalyzeFilingAsync(
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
            return null;
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
            return null;
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
            return null;
        }

        // The SupportingExcerpt must be a verbatim slice of the evidence (the mapper enforces
        // excerpt-in-evidence). The evidence Title is wholly contained in the composed searchable text, so
        // it is a stable, guaranteed-present excerpt. The advice-scrubbed AI rationale (spec 74) rides the
        // Reason field (not provenance-checked) to surface the AI basis for audit/report.
        return new ExtractedSignal(
            CompanyMention: evidence.SourceName,
            SignalType: "GuidanceChange",
            Direction: direction,
            Strength: _options.Strength,
            Novelty: _options.Novelty,
            Confidence: sentiment.Confidence,
            SupportingExcerpt: evidence.Title,
            Reason: sentiment.Rationale);
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

        var metadata = ReadMetadata(evidence.MetadataJson);

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

    /// <summary>
    /// Reads the flat metadata dictionary out of the persisted MetadataJson, whose shape is
    /// <c>{ "metadata": { ... }, "companyHints": [ ... ] }</c> (produced by
    /// <c>CollectedEvidenceMapper</c>). Returns an empty (ordinal) dictionary on any parse failure.
    /// </summary>
    private static Dictionary<string, string> ReadMetadata(string? metadataJson)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in metadata.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        result[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed metadata is treated as "no usable metadata" — the filing is simply skipped by the
            // form/items gate above, never crashing the batch.
        }

        return result;
    }

    [GeneratedRegex(@"\[items:\s*(?<items>[^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ItemsInTitleRegex();

    [GeneratedRegex(
        @"/edgar/data/(?<cik>\d+)/[^/]+/(?<accession>[^/]+?)-index\.html?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex IndexUrlRegex();
}
