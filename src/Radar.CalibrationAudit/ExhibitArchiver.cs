using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Sec;

namespace Radar.CalibrationAudit;

/// <summary>One exhibit-manifest row (the re-runnability key is <see cref="Accession"/>).</summary>
public sealed record ExhibitManifestRow(
    string Accession,
    string Ticker,
    string Cik,
    string DocumentFileName,
    string DocumentType,
    string ExhibitUrl,
    string FullTextSha256,
    int FullTextLength,
    string ModelInputSha256,
    int ModelInputLength,
    bool Truncated,
    int MaxInputLength,
    string Outcome,
    string FetchedAtUtc)
{
    public bool IsSuccess => string.Equals(Outcome, "success", StringComparison.Ordinal);
}

/// <summary>
/// Archives per-filing earnings-release exhibit text for the calibration study (spec 162 Phase A), through
/// the PRODUCTION <c>HttpSecEarningsReleaseReader</c> (production index-table parse, EX-99.1-preferred /
/// largest-EX-99.* selection, shared normalizer) resolved via DI — never copied parsing. All traffic rides
/// the reader's typed HttpClient, whose pipeline routes through the shared process-wide
/// <c>SecRequestPacer</c>; fetches are strictly SEQUENTIAL on top of that. Per filing it writes TWO texts:
/// <c>exhibits-full/{ticker}-{accession}.txt</c> (the full normalized text — comparability analysis and
/// adjudication only) and <c>exhibits-model-input/{ticker}-{accession}.txt</c> (the EXACT model input —
/// the leading MaxInputLength substring exactly as <c>ChatFilingAnalyzer</c> truncates, via
/// <see cref="ModelInputTruncation"/>), recording both SHA-256 hashes, lengths, the truncated flag and the
/// MaxInputLength in force in <c>exhibit-manifest.csv</c>.
/// <para>
/// RE-RUNNABLE, VERIFIED (spec 163): an accession is SKIPPED (no SEC request) only when its manifest row
/// is successful, both exhibit files exist, the STORED full-text file's trimmed body is not suspiciously
/// short, the stored files' SHA-256 hashes match the manifest's recorded hashes, the stored model-input
/// length matches, and the manifest row's recorded <c>MaxInputLength</c> equals the cap in force this run
/// — existence alone is not trusted, so a corrupted stored file or a rerun under a different input cap
/// refetches instead of silently preserving a wrong study input. The short-body tripwire is
/// <see cref="ShortBodyTripwireLength"/> = 200 trimmed characters — the same "a real earnings release is
/// never a few bytes" threshold the production <c>DirectionalFilingSignalSource.MinPlausibleBodyLength</c>
/// applies (spec 114; that const is private, so the VALUE is restated here and documented rather than
/// referenced). A tripwired or failed row is refetched on the next run; a below-tripwire FETCH is itself a
/// typed failure (<c>failed:short-body</c>) with empty hashes, never a warned success.
/// </para>
/// </summary>
internal sealed class ExhibitArchiver
{
    /// <summary>
    /// Minimum plausible trimmed exhibit length in characters. Below this the stored body is treated as a
    /// degenerate fetch (block page / empty shell) and refetched on the next run. Mirrors the production
    /// spec-114 <c>MinPlausibleBodyLength</c> (200) — kept equal so "authoritative" means the same thing in
    /// the study as in the pipeline.
    /// </summary>
    public const int ShortBodyTripwireLength = 200;

    private static readonly string[] ManifestHeader =
    [
        "accession", "ticker", "cik", "documentFileName", "documentType", "exhibitUrl",
        "fullTextSha256", "fullTextLength", "modelInputSha256", "modelInputLength",
        "truncated", "maxInputLength", "outcome", "fetchedAtUtc",
    ];

    private readonly ISecEarningsReleaseReader _reader;
    private readonly int _maxInputLength;
    private readonly ILogger _logger;

    public ExhibitArchiver(ISecEarningsReleaseReader reader, int maxInputLength, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);
        if (maxInputLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInputLength), maxInputLength,
                "MaxInputLength must be positive (the production analyzer fails registration otherwise).");
        }

        _reader = reader;
        _maxInputLength = maxInputLength;
        _logger = logger;
    }

    public static string ManifestPath(string outputRoot) => Path.Combine(outputRoot, "exhibit-manifest.csv");

    public static Dictionary<string, ExhibitManifestRow> LoadManifest(string outputRoot)
    {
        var rows = new Dictionary<string, ExhibitManifestRow>(StringComparer.OrdinalIgnoreCase);
        var path = ManifestPath(outputRoot);
        if (!File.Exists(path))
        {
            return rows;
        }

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var f = Csv.ParseLine(line);
            if (f.Count < ManifestHeader.Length)
            {
                continue; // A malformed manifest line just means that accession is refetched.
            }

            var row = new ExhibitManifestRow(
                f[0], f[1], f[2], f[3], f[4], f[5], f[6],
                ParseInt(f[7]), f[8], ParseInt(f[9]),
                bool.TryParse(f[10], out var truncated) && truncated,
                ParseInt(f[11]), f[12], f[13]);
            rows[row.Accession] = row;
        }

        return rows;

        static int ParseInt(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    public static void WriteManifest(string outputRoot, IEnumerable<ExhibitManifestRow> rows)
    {
        var ordered = rows
            .OrderBy(static r => AccessionHash.HexOf(r.Accession), StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", ManifestHeader));
        foreach (var r in ordered)
        {
            sb.AppendLine(Csv.Line(
                r.Accession, r.Ticker, r.Cik, r.DocumentFileName, r.DocumentType, r.ExhibitUrl,
                r.FullTextSha256, r.FullTextLength.ToString(CultureInfo.InvariantCulture),
                r.ModelInputSha256, r.ModelInputLength.ToString(CultureInfo.InvariantCulture),
                r.Truncated ? "true" : "false",
                r.MaxInputLength.ToString(CultureInfo.InvariantCulture),
                r.Outcome, r.FetchedAtUtc));
        }

        File.WriteAllText(ManifestPath(outputRoot), sb.ToString());
    }

    /// <summary>
    /// True when the accession needs (re)fetching: no successful manifest row, a missing exhibit file, a
    /// suspiciously short stored body (the tripwire), a stored file whose SHA-256 no longer matches the
    /// manifest's recorded hash, a stored model input whose character length differs from the recorded
    /// value, or a manifest row recorded under a different <c>MaxInputLength</c> than
    /// <paramref name="currentMaxInputLength"/> (spec 163 — stored artifacts are VERIFIED against the
    /// manifest, never trusted on existence). The stored-file hashes are computed over the files' RAW
    /// BYTES: <see cref="FetchAsync"/> records <c>SHA-256(UTF-8(text))</c> and writes with
    /// <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/> (UTF-8, no BOM), so raw
    /// bytes reproduce the recorded hash exactly and an untouched valid row stays a no-op skip.
    /// </summary>
    public static bool NeedsFetch(
        ExhibitManifestRow? existing,
        string fullPath,
        string modelInputPath,
        int currentMaxInputLength,
        out string reason)
    {
        if (existing is null || !existing.IsSuccess || string.IsNullOrEmpty(existing.FullTextSha256))
        {
            reason = existing is null ? "no manifest row" : $"previous outcome '{existing.Outcome}'";
            return true;
        }

        if (!File.Exists(fullPath) || !File.Exists(modelInputPath))
        {
            reason = "exhibit file missing on disk";
            return true;
        }

        // The production guard is on TRIMMED body length, so the tripwire measures the actual stored file
        // rather than the manifest's untrimmed FullTextLength — a mostly-whitespace body (or a file that no
        // longer matches its manifest row) must refetch, not skip.
        var storedTrimmedLength = File.ReadAllText(fullPath).AsSpan().Trim().Length;
        if (storedTrimmedLength < ShortBodyTripwireLength)
        {
            reason = $"stored body suspiciously short ({storedTrimmedLength} < {ShortBodyTripwireLength} trimmed chars tripwire)";
            return true;
        }

        // Verify the stored artifacts against the manifest (raw-byte hashes — see the XML doc above).
        var storedFullSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath)));
        if (!string.Equals(storedFullSha, existing.FullTextSha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"stored full-text hash {storedFullSha} != manifest fullTextSha256 {existing.FullTextSha256}";
            return true;
        }

        var storedModelInputSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(modelInputPath)));
        if (!string.Equals(storedModelInputSha, existing.ModelInputSha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"stored model-input hash {storedModelInputSha} != manifest modelInputSha256 {existing.ModelInputSha256}";
            return true;
        }

        // The manifest records the model input's CHAR count (string length), not its byte count.
        var storedModelInputLength = File.ReadAllText(modelInputPath).Length;
        if (storedModelInputLength != existing.ModelInputLength)
        {
            reason = $"stored model-input length {storedModelInputLength} != manifest modelInputLength {existing.ModelInputLength}";
            return true;
        }

        if (existing.MaxInputLength != currentMaxInputLength)
        {
            reason = $"manifest maxInputLength {existing.MaxInputLength} != current --max-input-length {currentMaxInputLength} (the stored model input reproduces a different cap)";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Fetches one filing's exhibit through the production reader and writes the dual outputs. Returns the
    /// manifest row (success or a typed failure outcome; failures carry empty hashes so they refetch).
    /// </summary>
    public async Task<ExhibitManifestRow> FetchAsync(
        string outputRoot,
        string accession,
        string ticker,
        string cik,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var fetchedAt = timeProvider.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        var read = await _reader.ReadAsync(cik, accession, ct).ConfigureAwait(false);

        if (!read.IsSuccess)
        {
            _logger.LogWarning(
                "Exhibit fetch for {Accession} (CIK {Cik}) failed: {Outcome} ({Detail}).",
                accession, cik, read.Outcome, read.Detail);
            return new ExhibitManifestRow(
                accession, ticker, cik,
                DocumentFileName: string.Empty, DocumentType: string.Empty, ExhibitUrl: string.Empty,
                FullTextSha256: string.Empty, FullTextLength: 0,
                ModelInputSha256: string.Empty, ModelInputLength: 0,
                Truncated: false, MaxInputLength: _maxInputLength,
                Outcome: $"failed:{read.Outcome}", FetchedAtUtc: fetchedAt);
        }

        var fullText = read.PlainText;
        var (modelInput, truncated) = ModelInputTruncation.Apply(fullText, _maxInputLength);

        // The exhibit URL, from the SAME shared URL builder the production reader used for its fetch.
        var exhibitUrl = read.DocumentFileName is { Length: > 0 } fileName
            ? $"{SecEdgarUrls.BuildArchiveBaseUrl(cik.Trim(), accession.Trim())}/{fileName}"
            : string.Empty;

        var fullPath = FullTextPath(outputRoot, ticker, accession);
        var modelInputPath = ModelInputPath(outputRoot, ticker, accession);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modelInputPath)!);
        await File.WriteAllTextAsync(fullPath, fullText, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(modelInputPath, modelInput, ct).ConfigureAwait(false);

        // Spec 163: a below-tripwire body is a typed FAILURE, not a warned success — Phase B must never
        // consume a degenerate fetch (block page / empty shell). The two files stay on disk as evidence of
        // what came back; the row carries EMPTY hashes so NeedsFetch refetches it next run (the existing
        // failure semantics), and it counts in the console's failed tally / nonzero exit like any failure.
        var trimmedLength = fullText.AsSpan().Trim().Length;
        if (trimmedLength < ShortBodyTripwireLength)
        {
            _logger.LogWarning(
                "Exhibit body for {Accession} is suspiciously short ({Length} trimmed chars < {Tripwire}); "
                    + "recorded as typed failure 'failed:short-body' (files kept as evidence; refetched next run).",
                accession, trimmedLength, ShortBodyTripwireLength);
            return new ExhibitManifestRow(
                accession, ticker, cik,
                read.DocumentFileName ?? string.Empty,
                read.DocumentType ?? string.Empty,
                exhibitUrl,
                FullTextSha256: string.Empty, fullText.Length,
                ModelInputSha256: string.Empty, modelInput.Length,
                truncated, MaxInputLength: _maxInputLength,
                Outcome: "failed:short-body", FetchedAtUtc: fetchedAt);
        }

        return new ExhibitManifestRow(
            accession, ticker, cik,
            read.DocumentFileName ?? string.Empty,
            read.DocumentType ?? string.Empty,
            exhibitUrl,
            Sha256Hex(fullText), fullText.Length,
            Sha256Hex(modelInput), modelInput.Length,
            truncated, _maxInputLength,
            Outcome: "success", FetchedAtUtc: fetchedAt);
    }

    public static string FullTextPath(string outputRoot, string ticker, string accession) =>
        Path.Combine(outputRoot, "exhibits-full", ExhibitFileName(ticker, accession));

    public static string ModelInputPath(string outputRoot, string ticker, string accession) =>
        Path.Combine(outputRoot, "exhibits-model-input", ExhibitFileName(ticker, accession));

    private static string ExhibitFileName(string ticker, string accession)
    {
        // Filename-safe, deterministic: lowercased ticker (blank/unsafe → "unknown") + the accession
        // (already filename-safe: digits and dashes).
        var safeTicker = Radar.Infrastructure.FileSystem.FileTickerKey.Sanitize(ticker) ?? "unknown";
        return $"{safeTicker}-{accession}.txt";
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
