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
/// RE-RUNNABLE: an accession whose manifest row already carries a full-text hash, whose exhibit files both
/// exist, and whose body is not suspiciously short is SKIPPED (no SEC request). The short-body tripwire is
/// <see cref="ShortBodyTripwireLength"/> = 200 trimmed characters — the same "a real earnings release is
/// never a few bytes" threshold the production <c>DirectionalFilingSignalSource.MinPlausibleBodyLength</c>
/// applies (spec 114; that const is private, so the VALUE is restated here and documented rather than
/// referenced). A tripwired or failed row is refetched on the next run.
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
    /// True when the accession needs (re)fetching: no successful manifest row, a missing exhibit file, or a
    /// suspiciously short stored body (the tripwire).
    /// </summary>
    public static bool NeedsFetch(
        ExhibitManifestRow? existing, string fullPath, string modelInputPath, out string reason)
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

        if (existing.FullTextLength < ShortBodyTripwireLength)
        {
            reason = $"stored body suspiciously short ({existing.FullTextLength} < {ShortBodyTripwireLength} chars tripwire)";
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

        var trimmedLength = fullText.AsSpan().Trim().Length;
        if (trimmedLength < ShortBodyTripwireLength)
        {
            _logger.LogWarning(
                "Exhibit body for {Accession} is suspiciously short ({Length} trimmed chars < {Tripwire}); "
                    + "recorded, but it will be refetched on the next run.",
                accession, trimmedLength, ShortBodyTripwireLength);
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
