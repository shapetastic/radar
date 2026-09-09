using System.Text;

using Microsoft.Extensions.Logging;

using Radar.Application.Acquisitions;
using Radar.Application.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// SPEC 217 §1 — the SEC-side implementation of <see cref="IAcquisitionFilingBodyReader"/>: fetches an
/// item-1.01 8-K's own text so the pure <c>acqscan-v1</c> can read it.
/// <para>
/// <b>What it fetches, and why both.</b> The item-1.01 NARRATIVE ("the Company entered into an Agreement
/// and Plan of Merger…") lives in the PRIMARY 8-K document; the per-share consideration is usually stated in
/// the EX-99.1 press release. Leg (a) and leg (b) of the scan therefore live in different documents, so the
/// reader fetches the index, then the primary document, then the EX-99.1 exhibit WHEN THE INDEX SHOWS ONE,
/// and concatenates the stripped plain text. That is at most three <c>www.sec.gov</c> requests per filing —
/// and only ONCE per filing ever, because the caller's durable scan cache and acquisitions store make a
/// scanned accession a permanent hit (the spec-107 cache-first posture).
/// </para>
/// <para>
/// <b>Pacing and failure.</b> Every request goes through this client's <see cref="SecRateLimitingHandler"/>,
/// so it counts against the SAME process-wide <c>*.sec.gov</c> budget as the collectors and the earnings
/// read — the recognition pass can never issue an unpaced burst. Every failure (403 / 429 / non-success /
/// transport / timeout / unparseable index / no primary document) degrades to a NAMED
/// <see cref="AcquisitionBodyReadOutcome.FetchFailed"/> that the caller counts and RE-ATTEMPTS on a later
/// run; nothing is cached and nothing is recognised, so a transient block can neither open nor close a
/// thesis. Only caller-requested cancellation propagates.
/// </para>
/// <para>
/// It reuses the shared <see cref="SecEdgarUrls"/> (URL/CIK construction), <see cref="SecHttpFetch"/> (the
/// SEC outcome ladder), <see cref="SecFilingIndexTable"/> (the document-table parse) and
/// <see cref="IEvidenceNormalizer"/> (the HTML strip) rather than carrying private copies (AD-5 + the
/// reuse-over-copy rule). All HTTP/HTML/SEC code stays in Infrastructure.
/// </para>
/// </summary>
internal sealed class HttpSecAcquisitionFilingReader : IAcquisitionFilingBodyReader
{
    private readonly HttpClient _httpClient;
    private readonly IEvidenceNormalizer _normalizer;
    private readonly ILogger<HttpSecAcquisitionFilingReader> _logger;

    public HttpSecAcquisitionFilingReader(
        HttpClient httpClient,
        IEvidenceNormalizer normalizer,
        ILogger<HttpSecAcquisitionFilingReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _normalizer = normalizer;
        _logger = logger;
    }

    public async Task<AcquisitionFilingBody> ReadAsync(
        string cik, string accession, string? primaryDocument, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cik);
        ArgumentException.ThrowIfNullOrWhiteSpace(accession);

        var cikTrim = cik.Trim();
        var accTrim = accession.Trim();
        var baseUrl = SecEdgarUrls.BuildArchiveBaseUrl(cikTrim, accTrim);
        var indexUrl = SecEdgarUrls.BuildIndexUrl(cikTrim, accTrim, ".html");

        var (indexFailure, indexBody) = await FetchAsync(indexUrl, ct).ConfigureAwait(false);
        if (indexFailure is not null)
        {
            return indexFailure;
        }

        var rows = SecFilingIndexTable.Parse(indexBody!);
        if (rows.Count == 0)
        {
            _logger.LogInformation(
                "SEC filing index {IndexUrl} had no parseable document table; the item-1.01 read is skipped "
                    + "and will be re-attempted.",
                indexUrl);
            return AcquisitionFilingBody.Failed("no parseable document table");
        }

        var primary = SecFilingIndexTable.SelectPrimaryDocument(rows, primaryDocument);
        if (primary is null)
        {
            _logger.LogInformation(
                "SEC filing index {IndexUrl} carried no non-exhibit primary document row; skipping.",
                indexUrl);
            return AcquisitionFilingBody.Failed("no primary document row");
        }

        var (primaryFailure, primaryBody) = await FetchAsync($"{baseUrl}/{primary.FileName}", ct)
            .ConfigureAwait(false);
        if (primaryFailure is not null)
        {
            return primaryFailure;
        }

        var text = new StringBuilder(_normalizer.Normalize(title: null, rawText: primaryBody!).NormalizedText);

        // The EX-99.1 press release, when the index shows one. Its ABSENCE is not a failure: many item-1.01
        // filings carry no exhibit, and the primary document alone is a complete read of the item.
        var exhibit = SecFilingIndexTable.SelectEarningsExhibit(rows);
        if (exhibit is not null)
        {
            var (exhibitFailure, exhibitBody) = await FetchAsync($"{baseUrl}/{exhibit.FileName}", ct)
                .ConfigureAwait(false);
            if (exhibitFailure is not null)
            {
                // A missing exhibit body degrades the READ, and a partial read must never be scanned as a
                // whole one: leg (b) commonly lives in the exhibit, so scanning without it could record a
                // NOT-recognised answer that the cache would then make permanent. Fail the whole read.
                return exhibitFailure;
            }

            text.Append('\n')
                .Append(_normalizer.Normalize(title: null, rawText: exhibitBody!).NormalizedText);
        }

        return AcquisitionFilingBody.Success(text.ToString());
    }

    /// <summary>
    /// Fetches a URL as a string through the shared SEC outcome ladder. Returns a non-null failure (and a
    /// null body) on any bad response; caller-requested cancellation re-throws. Unlike the earnings reader
    /// this one owns NO 429 backoff-retry: a rate-limited item-1.01 read is simply re-attempted on the next
    /// run (the durable scan cache means nothing is lost by waiting), which keeps the recognition pass's
    /// www.sec.gov footprint strictly smaller than the read it sits beside.
    /// </summary>
    private async Task<(AcquisitionFilingBody? Failure, string? Body)> FetchAsync(
        string url, CancellationToken ct)
    {
        var (failure, body) = await SecHttpFetch
            .GetAsync<AcquisitionFilingBody, string>(
                _httpClient,
                url,
                static (content, token) => content.ReadAsStringAsync(token),
                onForbidden: () => Failed(url, "HTTP 403 (SEC requires a compliant User-Agent)"),
                onHttpError: status => Failed(url, $"HTTP {status}"),
                onUnreachable: ex => Failed(url, $"transport error: {ex.Message}"),
                onTimeout: _ => Failed(url, "request timed out"),
                ct)
            .ConfigureAwait(false);

        return (failure, body);
    }

    private AcquisitionFilingBody Failed(string url, string detail)
    {
        _logger.LogWarning(
            "Item-1.01 filing read of {Url} failed ({Detail}); nothing is recognised or cached and the "
                + "filing will be re-attempted on a later run.",
            url,
            detail);
        return AcquisitionFilingBody.Failed(detail);
    }
}
