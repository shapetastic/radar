using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Ai;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Filings;

namespace Radar.CalibrationAudit;

/// <summary>Inputs for one shadow-read pass (spec 164). Everything under <see cref="ExhibitRoot"/> is READ-ONLY.</summary>
public sealed record ShadowReadOptions
{
    /// <summary>The study artifact root: <c>worksheet.csv</c>, <c>exhibit-manifest.csv</c>, <c>legacy-exclusions.csv</c>, <c>exhibits-model-input/</c>. Never written.</summary>
    public required string ExhibitRoot { get; init; }

    /// <summary>The output root. The pass writes ONLY under <c>{OutputRoot}/shadow/</c>.</summary>
    public required string OutputRoot { get; init; }

    /// <summary>Overwrite existing <c>ok</c> records (default: skip them, so a re-run resumes).</summary>
    public bool Fresh { get; init; }

    /// <summary>Cap on NEW model reads this run (re-run to continue). Default unlimited.</summary>
    public int MaxReads { get; init; } = int.MaxValue;

    /// <summary>Expected sealed-cohort sizes, re-asserted before running (spec 162's counts). Drift is REPORTED.</summary>
    public int ExpectedDirectional { get; init; } = 145;

    public int ExpectedNoSignal { get; init; } = 153;

    /// <summary>The precommitted outcome-conflicting legacy accessions, re-asserted from <c>legacy-exclusions.csv</c>.</summary>
    public IReadOnlyList<string> ExpectedOutcomeConflicts { get; init; } =
        ["0001628280-26-048253", "0001654954-26-006655"];
}

/// <summary>The outcome of one shadow-read pass. <see cref="Drift"/> lists every re-assertion that did not hold.</summary>
public sealed record ShadowReadSummary(
    int Candidates,
    int Attempted,
    int Skipped,
    int Ok,
    int CallFailed,
    int ParseFailed,
    int NotReadable,
    IReadOnlyList<string> Drift,
    IReadOnlyList<string> NotReadableDetail,
    string Text);

/// <summary>
/// The shadow-mode forced-choice second pass (spec 164): read-only research, no scoring change, no
/// fingerprint input, no pin move, ZERO SEC requests.
/// <para>
/// It reads each archived model-input exhibit (after VERIFYING its SHA-256 and character length against
/// <c>exhibit-manifest.csv</c> — a tampered study input is never read, and every candidate is verified BEFORE
/// the first model call), assembles the prompt through the SHARED
/// <see cref="FilingAnalyzerPrompt"/> with the committed <c>cal-shadow-v1</c> instruction as the COMPLETE
/// system instruction (a replacement, never an append), and records a typed
/// <see cref="ShadowFilingSentiment"/> parsed by the CONSOLE — never through
/// <c>ChatFilingAnalyzer.Validate</c>, which would degrade a shadow <c>Neutral</c> to <c>Unknown</c>/0.
/// </para>
/// <para>
/// <b>Cache isolation.</b> The cohort comes from the sealed worksheet, so this pass never registers,
/// constructs or opens the production <c>FileAnalyzedFilingCache</c>: <see cref="BuildShadowServices"/>
/// registers logging and the AI client and NOTHING else, so no code path exists by which a shadow read could
/// be written into <c>data/filings-cache/</c> and later served to a live baseline run. It writes exactly one
/// directory, <c>{OutputRoot}/shadow/</c>.
/// </para>
/// </summary>
internal sealed class ShadowReadRunner
{
    private readonly IChatClient _chatClient;
    private readonly ShadowPromptText _prompt;
    private readonly string _modelIdentity;
    private readonly ILogger _logger;

    public ShadowReadRunner(
        IChatClient chatClient, ShadowPromptText prompt, string modelIdentity, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        ArgumentNullException.ThrowIfNull(logger);

        _chatClient = chatClient;
        _prompt = prompt;
        _modelIdentity = modelIdentity;
        _logger = logger;
    }

    /// <summary>
    /// The shadow pass's ENTIRE composition: logging plus the production AI client seam. Deliberately no
    /// analyzed-filing cache, no filing analyzer, no SEC reader, no collector, no signal/evidence/score/report
    /// store — asserted by <c>ShadowReadTests</c>
    /// (<c>ShadowComposition_RegistersTheAiSeamAndNothingElse_NoAnalyzedFilingCache</c>). Registration performs no network I/O and the
    /// key VALUE inside <paramref name="ai"/> is never logged.
    /// </summary>
    public static ServiceCollection BuildShadowServices(AiClientOptions ai)
    {
        ArgumentNullException.ThrowIfNull(ai);

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            b.SetMinimumLevel(LogLevel.Warning);
        });
        services.AddRadarAi(ai);
        return services;
    }

    public async Task<ShadowReadSummary> RunAsync(
        ShadowReadOptions options, TimeProvider timeProvider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var exhibitRoot = Path.GetFullPath(options.ExhibitRoot);
        var outputRoot = Path.GetFullPath(options.OutputRoot);

        if (!Directory.Exists(exhibitRoot))
        {
            throw new DirectoryNotFoundException(
                $"--exhibit-root '{exhibitRoot}' does not exist. It must point at the study artifact root "
                    + "holding worksheet.csv, exhibit-manifest.csv and exhibits-model-input/.");
        }

        // The exhibit root is READ-ONLY: refuse to write anywhere inside it.
        if (outputRoot.Equals(exhibitRoot, StringComparison.OrdinalIgnoreCase)
            || outputRoot.StartsWith(exhibitRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"--output-root '{outputRoot}' is inside the read-only --exhibit-root '{exhibitRoot}'. The "
                    + "shadow pass never writes into the study archive; choose an output root outside it.");
        }

        var drift = new List<string>();

        // --- 1. Cohort from the SEALED worksheet (never the live cache) ------------------------------------
        var worksheet = StudyWorksheetReader.Read(Path.Combine(exhibitRoot, StudyWorksheetReader.FileName));
        var directionalCount = worksheet.Count(static r => r.Cohort == ShadowCohort.Directional);
        var noSignalCount = worksheet.Count(static r => r.Cohort == ShadowCohort.NoSignal);
        if (directionalCount != options.ExpectedDirectional || noSignalCount != options.ExpectedNoSignal)
        {
            drift.Add(
                $"COHORT COUNT DRIFT: worksheet holds {directionalCount} directional + {noSignalCount} no-signal, "
                    + $"expected {options.ExpectedDirectional} + {options.ExpectedNoSignal}.");
        }

        // --- 2. Re-assert the outcome-conflicting legacy accessions ---------------------------------------
        var conflictsPath = Path.Combine(exhibitRoot, "legacy-exclusions.csv");
        var conflicts = StudyWorksheetReader.ReadOutcomeConflicts(conflictsPath);
        var expectedConflicts = options.ExpectedOutcomeConflicts.OrderBy(static a => a, StringComparer.Ordinal).ToList();
        if (!File.Exists(conflictsPath))
        {
            drift.Add($"OUTCOME-CONFLICT DRIFT: no legacy-exclusions.csv at '{conflictsPath}' to re-assert against.");
        }
        else if (!conflicts.SequenceEqual(expectedConflicts, StringComparer.Ordinal))
        {
            drift.Add(
                "OUTCOME-CONFLICT DRIFT: legacy-exclusions.csv records conflicts ["
                    + string.Join(", ", conflicts) + "], expected [" + string.Join(", ", expectedConflicts) + "].");
        }

        // --- 3. Manifest + read plan ----------------------------------------------------------------------
        var manifest = ExhibitArchiver.LoadManifest(exhibitRoot);
        if (manifest.Count != worksheet.Count)
        {
            drift.Add(
                $"MANIFEST DRIFT: exhibit-manifest.csv holds {manifest.Count} rows for a {worksheet.Count}-row worksheet.");
        }

        var candidates = new List<(StudyWorksheetRow Row, ExhibitManifestRow Manifest, string Path)>();
        var notReadable = new List<string>();
        foreach (var row in worksheet.OrderBy(static r => AccessionHash.HexOf(r.Accession), StringComparer.Ordinal))
        {
            if (!manifest.TryGetValue(row.Accession, out var manifestRow))
            {
                notReadable.Add($"{row.Accession}: no exhibit-manifest row");
                continue;
            }

            if (!manifestRow.IsSuccess)
            {
                notReadable.Add($"{row.Accession}: manifest outcome '{manifestRow.Outcome}'");
                continue;
            }

            candidates.Add((row, manifestRow,
                ExhibitArchiver.ModelInputPath(exhibitRoot, manifestRow.Ticker, row.Accession)));
        }

        // --- 4. VERIFY EVERY candidate's archived input BEFORE any model call ------------------------------
        // A tampered/short/rehashed study input must fail the whole pass naming the file — never be read.
        foreach (var (row, manifestRow, path) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            VerifyModelInput(row.Accession, manifestRow, path);
        }

        // --- 5. Reads (sequential; DeepInfra only; zero SEC requests) --------------------------------------
        var attempted = 0;
        var skipped = 0;
        var ok = 0;
        var callFailed = 0;
        var parseFailed = 0;

        foreach (var (row, manifestRow, path) in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!options.Fresh)
            {
                var existing = ShadowRecordStore.TryRead(outputRoot, row.Accession);
                if (existing is not null && existing.IsOk)
                {
                    skipped++;
                    continue;
                }
            }

            if (attempted >= options.MaxReads)
            {
                break;
            }

            var text = File.ReadAllText(path);
            var record = await ReadOneAsync(row, manifestRow, text, timeProvider, ct).ConfigureAwait(false);
            ShadowRecordStore.Write(outputRoot, record);
            attempted++;

            switch (record.Status)
            {
                case ShadowStatus.Ok:
                    ok++;
                    break;
                case ShadowStatus.ParseFailed:
                    parseFailed++;
                    break;
                default:
                    callFailed++;
                    break;
            }
        }

        // The summary covers every record on disk (this run's AND previously-skipped ok records).
        var allRecords = ShadowRecordStore.ReadAll(outputRoot);
        ShadowRecordStore.WriteSummary(outputRoot, allRecords);

        var text2 = BuildSummaryText(
            options, exhibitRoot, outputRoot, worksheet.Count, directionalCount, noSignalCount,
            manifest.Count, candidates.Count, attempted, skipped, ok, callFailed, parseFailed,
            notReadable, drift, allRecords);

        return new ShadowReadSummary(
            candidates.Count, attempted, skipped, ok, callFailed, parseFailed,
            notReadable.Count, drift, notReadable, text2);
    }

    /// <summary>
    /// Verifies one archived model input against its manifest row: the file must exist, its RAW BYTES must
    /// hash to the recorded <c>modelInputSha256</c>, and its CHARACTER length must equal the recorded
    /// <c>modelInputLength</c>. Any mismatch throws naming the file — the study input is never read tampered.
    /// </summary>
    internal static void VerifyModelInput(string accession, ExhibitManifestRow manifestRow, string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Archived model input for {accession} is MISSING at '{path}' although its manifest row is "
                    + "'success'. The shadow pass never reads an unverifiable study input.");
        }

        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(sha, manifestRow.ModelInputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Archived model input for {accession} FAILED verification: '{path}' hashes to {sha}, but "
                    + $"exhibit-manifest.csv records modelInputSha256 {manifestRow.ModelInputSha256}. The shadow "
                    + "pass never reads a tampered study input — restore the archive or re-run the Phase A fetch.");
        }

        var length = Encoding.UTF8.GetString(bytes).Length;
        if (length != manifestRow.ModelInputLength)
        {
            throw new InvalidOperationException(
                $"Archived model input for {accession} FAILED verification: '{path}' is {length} characters, "
                    + $"but exhibit-manifest.csv records modelInputLength {manifestRow.ModelInputLength}.");
        }
    }

    private async Task<ShadowRecord> ReadOneAsync(
        StudyWorksheetRow row,
        ExhibitManifestRow manifestRow,
        string modelInputText,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var readAt = timeProvider.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

        ShadowRecord Failed(string status, string error, string? raw) => new()
        {
            Accession = row.Accession,
            Cohort = row.Cohort,
            Status = status,
            Direction = null,
            Confidence = null,
            Rationale = null,
            RawResponse = raw,
            Error = error,
            PromptVersion = ShadowPrompt.Version,
            PromptSha256 = _prompt.Sha256,
            ModelIdentity = _modelIdentity,
            ModelInputSha256 = manifestRow.ModelInputSha256,
            ReadAtUtc = readAt,
        };

        // The archived text IS the production model input (already truncated by the Phase A archiver at the
        // recorded cap). The cap handed to the shared assembly can therefore never truncate it further — a
        // second truncation would silently change the study input.
        var cap = Math.Max(manifestRow.MaxInputLength, Math.Max(modelInputText.Length, 1));
        var messages = FilingAnalyzerPrompt.Build(modelInputText, cap, _prompt.Instruction);

        ChatResponse<ShadowFilingSentimentResponse> response;
        try
        {
            response = await _chatClient
                .GetResponseAsync<ShadowFilingSentimentResponse>(messages, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Shadow read for {Accession} could not be deserialized: {Error}.", row.Accession, ex.Message);
            return Failed(ShadowStatus.ParseFailed, ex.Message, raw: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Shadow read for {Accession} FAILED: {Error}.", row.Accession, ex.Message);
            return Failed(ShadowStatus.CallFailed, ex.Message, raw: null);
        }

        var raw = response.Text ?? string.Empty;
        var candidate = response.TryGetResult(out var parsed) ? parsed : null;

        if (!ShadowFilingSentimentParser.TryParse(candidate, raw, out var sentiment, out var reason))
        {
            _logger.LogWarning("Shadow read for {Accession} did not parse: {Reason}.", row.Accession, reason);
            return Failed(ShadowStatus.ParseFailed, reason, raw);
        }

        return new ShadowRecord
        {
            Accession = row.Accession,
            Cohort = row.Cohort,
            Status = ShadowStatus.Ok,
            Direction = sentiment.Direction.ToString(),
            Confidence = sentiment.Confidence,
            Rationale = sentiment.Rationale,
            RawResponse = sentiment.RawResponse,
            Error = null,
            PromptVersion = ShadowPrompt.Version,
            PromptSha256 = _prompt.Sha256,
            ModelIdentity = _modelIdentity,
            ModelInputSha256 = manifestRow.ModelInputSha256,
            ReadAtUtc = readAt,
        };
    }

    private string BuildSummaryText(
        ShadowReadOptions options,
        string exhibitRoot,
        string outputRoot,
        int worksheetRows,
        int directionalCount,
        int noSignalCount,
        int manifestRows,
        int candidates,
        int attempted,
        int skipped,
        int ok,
        int callFailed,
        int parseFailed,
        IReadOnlyList<string> notReadable,
        IReadOnlyList<string> drift,
        IReadOnlyList<ShadowRecord> allRecords)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Radar calibration audit — spec 164 SHADOW READ (forced-choice second pass)");
        sb.AppendLine("READ-ONLY research: no scoring change, no fingerprint input, no pin move, zero SEC requests.");
        sb.AppendLine($"generatedAtUtc:      {DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"exhibitRoot (RO):    {exhibitRoot}");
        sb.AppendLine($"shadow output:       {ShadowRecordStore.RootFor(outputRoot)}");
        sb.AppendLine($"promptVersion:       {ShadowPrompt.Version}");
        sb.AppendLine($"promptSha256:        {_prompt.Sha256}  (LF-normalized, over the exact instruction bytes sent)");
        sb.AppendLine($"promptPath:          {_prompt.Path}");
        sb.AppendLine($"modelIdentity:       {_modelIdentity}");
        sb.AppendLine($"fresh:               {(options.Fresh ? "yes (existing records overwritten)" : "no (ok records skipped; failures retried)")}");
        sb.AppendLine();
        sb.AppendLine($"worksheet: {worksheetRows} rows — {directionalCount} directional, {noSignalCount} no-signal");
        sb.AppendLine($"manifest:  {manifestRows} rows; readable candidates: {candidates}");
        sb.AppendLine(drift.Count == 0
            ? $"re-assertions HOLD: cohort {options.ExpectedDirectional}/{options.ExpectedNoSignal} and outcome-conflicting legacy accessions ["
                + string.Join(", ", options.ExpectedOutcomeConflicts) + "]."
            : "RE-ASSERTION DRIFT (reported, not fatal — record it in the findings):");
        foreach (var d in drift)
        {
            sb.AppendLine("  - " + d);
        }

        sb.AppendLine();
        sb.AppendLine($"reads this run: {attempted} attempted ({ok} ok, {parseFailed} parse-failed, {callFailed} call-failed), {skipped} skipped (already ok)");
        sb.AppendLine($"not readable (reported, never read): {notReadable.Count}");
        foreach (var n in notReadable)
        {
            sb.AppendLine("  - " + n);
        }

        var okOnDisk = allRecords.Count(static r => r.IsOk);
        sb.AppendLine();
        sb.AppendLine($"records on disk: {allRecords.Count} ({okOnDisk} ok, {allRecords.Count - okOnDisk} failed — RERUN to retry failures)");
        sb.AppendLine($"summary csv: {ShadowRecordStore.SummaryPath(outputRoot)}");
        return sb.ToString();
    }
}
