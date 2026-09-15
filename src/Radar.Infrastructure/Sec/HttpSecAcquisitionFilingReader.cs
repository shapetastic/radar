using System.Text;

using Microsoft.Extensions.Logging;

using Radar.Application.Acquisitions;
using Radar.Application.Evidence;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// SPEC 217 §1 — the SEC-side implementation of <see cref="IAcquisitionFilingBodyReader"/>: fetches an
/// item-1.01 8-K's own text so the pure acquisition scan (<c>AcquisitionAgreementScan</c>) can read it.
/// <para>
/// <b>What it fetches, and why both.</b> The item-1.01 NARRATIVE ("the Company entered into an Agreement
/// and Plan of Merger…") lives in the PRIMARY 8-K document; the per-share consideration is usually stated in
/// the EX-99.1 press release. Leg (a) and leg (b) of the scan therefore live in different documents, so the
/// reader fetches the index, then the primary document, then the EX-99.1 exhibit WHEN THE INDEX SHOWS ONE,
/// and concatenates the stripped plain text. That is at most three <c>www.sec.gov</c> requests per filing (four
/// since spec 228, when the index also carries an EX-2.1 — see below) —
/// and only ONCE per filing per scan version, because the caller's durable scan cache and acquisitions store make a
/// scanned accession a permanent hit (the spec-107 cache-first posture).
/// </para>
/// <para>
/// <b>⚠ AMENDED in place by spec 228 — until then it did not read the 8-K.</b> The paragraph above was the design,
/// not the practice: EDGAR links an iXBRL primary document through the inline viewer (<c>/ix?doc=…</c>), the shared
/// index parser dropped that row, and the primary-document selection fell back to the first row without an EX-99
/// type — an EX-10.1 credit agreement, EX-2.1 merger agreement, EX-1.1 underwriting agreement or EX-4.1 indenture on
/// 153 of 154 live reads — while 31 more filings failed outright ("no parseable document table" / "no primary
/// document row"). Since spec 228 the primary document is chosen from what SEC says, in order: the declared
/// <c>primaryDocument</c> when the index carries that row, else the ONE row whose Type column is <c>8-K</c> /
/// <c>8-K/A</c>; anything else is a named failure (<see cref="SecFilingIndexTable.SelectPrimaryDocument"/>). A body
/// can no longer start with an exhibit posing as the 8-K, and because the read is part of the answer the change
/// bumped <c>AcquisitionAgreementScan.Version</c> to <c>acqscan-v3</c>.
/// </para>
/// <para>
/// <b>The EX-2.1 merger agreement IS appended — and no other contract (spec 228 §1 decision, MEASURED).</b> The body
/// is the primary 8-K, then EX-99.1 when shown, then the EX-2.1 exhibit when the index carries one
/// (<see cref="SecFilingIndexTable.SelectMergerAgreementExhibit"/>) — in that order, so the 8-K cover always comes
/// first. The default was NOT to append: a contract in the body is how SHOO's spec-227 false positive assembled
/// itself (a credit agreement's defined roles beside a release's dividend), and the scan is fail-closed because a
/// false positive closes a live thesis. The live measurement overturned that default for EX-2.1 only. Over the 185
/// item-1.01 accessions in the store on 2026-09-15, 21 carry an EX-2.1. Without it, the one genuine takeover —
/// MarineMax (HZO, 0001193125-26-341302) — reads <c>AcquirerNotNamed</c>: its 8-K names the buyer only inside
/// "by and among the Company, SHM Holdco, LLC, … (“Parent”)", which the rule cannot read, while the merger
/// agreement's own party definition names it. With it, HZO is recognised at $53.00 (the consideration quote is the
/// 8-K's own). The append changed three other outcomes, and none into a recognition: STRL and CLMB went
/// <c>NoMergerAgreement</c> → <c>CompanyNotTarget</c>, and ESQ went <c>CompanyNotTarget</c> → <c>CompanyIsAcquirer</c>.
/// SHOO stays unrecognised (its credit-agreement 8-K carries no EX-2.1). EX-10.* material contracts (in 103 of
/// the 185 filings) are never appended. The cost is one more request per EX-2.1 filing. The full table is in
/// <c>docs/architecture-history.md</c> (spec-228 bullet).
/// </para>
/// <para>
/// <b>Re-decided by spec 229 §1 rule 6 — KEPT, MEASURED.</b> Spec 229 made the acquirer keywords case-insensitive so a
/// title-case headline ("MarineMax Enters into Definitive Agreement to be Acquired by Blackstone Infrastructure
/// Portfolio Company, Safe Harbor, …") could name the buyer, and asked whether the EX-2.1 could then be dropped. It
/// cannot: under <c>acqscan-v4</c>, HZO without the EX-2.1 still reads <c>AcquirerNotNamed</c>, because the headline's
/// name is followed by ", Safe Harbor, in a $1.5 Billion All-Cash Transaction …" with no terminator the acquirer capture
/// accepts within its 120 characters, and the 8-K's party list still opens with "the Company". So the append stays
/// (primary → EX-99.1 → EX-2.1), and the body reader did not change in spec 229. Under v4 the append changes the same
/// four outcomes it changed under v3 (HZO into its recognition; STRL and CLMB no-merger-agreement → company-not-target;
/// ESQ company-not-target → company-is-acquirer) and costs 21 requests over the 185 filings (477 with it, 456
/// without). The table is in <c>docs/architecture-history.md</c> (spec-229 bullet).
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

        var selection = SecFilingIndexTable.SelectPrimaryDocument(rows, primaryDocument);
        if (selection.Row is not { } primary)
        {
            // Spec 228: no authoritative primary (neither the declared document nor exactly one row typed as the
            // form) is a NAMED failure, never a positional guess. Counted by the caller as a failed read.
            _logger.LogInformation(
                "SEC filing index {IndexUrl} named no authoritative primary document ({Detail}); the item-1.01 "
                    + "read is skipped and will be re-attempted.",
                indexUrl,
                selection.FailureDetail);
            return AcquisitionFilingBody.Failed(selection.FailureDetail ?? SecFilingIndexTable.NoPrimaryDocumentRow);
        }

        var (primaryFailure, primaryBody) = await FetchAsync($"{baseUrl}/{primary.FileName}", ct)
            .ConfigureAwait(false);
        if (primaryFailure is not null)
        {
            return primaryFailure;
        }

        var text = new StringBuilder(_normalizer.Normalize(title: null, rawText: primaryBody!).NormalizedText);

        // Then, in this order: the EX-99.1 press release, and (spec 228 §1) the EX-2.1 merger agreement, each only
        // when the index shows one. Their ABSENCE is not a failure: many item-1.01 filings carry neither, and the
        // primary document alone is a complete read of the item.
        foreach (var exhibit in new[]
                 {
                     SecFilingIndexTable.SelectEarningsExhibit(rows),
                     SecFilingIndexTable.SelectMergerAgreementExhibit(rows),
                 })
        {
            if (exhibit is null)
            {
                continue;
            }

            var (exhibitFailure, exhibitBody) = await FetchAsync($"{baseUrl}/{exhibit.FileName}", ct)
                .ConfigureAwait(false);
            if (exhibitFailure is not null)
            {
                // A missing exhibit body degrades the READ, and a partial read must never be scanned as a
                // whole one: leg (b) commonly lives in EX-99.1 and the acquirer's name in EX-2.1, so scanning
                // without it could record a NOT-recognised answer that the cache would then make permanent. Fail
                // the whole read.
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
