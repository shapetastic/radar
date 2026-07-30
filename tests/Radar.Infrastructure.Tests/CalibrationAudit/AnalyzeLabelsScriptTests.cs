using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Radar.CalibrationAudit;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 163: end-to-end tests of <c>scripts/calibration-audit/analyze-labels.ps1</c> — each test copies
/// the real script into a sandbox beside its OWN study-contract.json / labeling-prompt.md / worksheet /
/// manifest / labels (the sanctioned test seam: the contract is loaded from the script's own directory,
/// so tests substitute contract values by owning that directory — no production switch exists). Runs
/// under <c>pwsh</c> when available (ubuntu CI), falling back to Windows PowerShell 5.1; if neither host
/// exists the tests FAIL (never skip — CI always has pwsh).
/// </summary>
public sealed class AnalyzeLabelsScriptTests : IDisposable
{
    // ---------------------------------------------------------------- host + repo discovery

    private static readonly Lazy<string> HostExe = new(() =>
    {
        foreach (var host in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo(host)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("exit 0");
                using var probe = Process.Start(psi);
                if (probe is null)
                {
                    continue;
                }

                probe.StandardOutput.ReadToEnd();
                probe.StandardError.ReadToEnd();
                if (probe.WaitForExit(60_000) && probe.ExitCode == 0)
                {
                    return host;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Host not on PATH — try the next one.
            }
        }

        throw new InvalidOperationException(
            "Neither 'pwsh' nor 'powershell' was found on PATH. The analyze-labels.ps1 tests require a "
                + "PowerShell host; CI provides pwsh, Windows provides powershell — this is a failure, not a skip.");
    });

    private static string RepoScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate the repo root (Radar.sln) above " + AppContext.BaseDirectory);
        }

        return Path.Combine(dir.FullName, "scripts", "calibration-audit", "analyze-labels.ps1");
    }

    // ---------------------------------------------------------------- fixture sandbox

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The script's canonical prompt-template hash: CRLF→LF-normalized UTF-8 SHA-256.</summary>
    private static string LfHash(string text) => Sha256Hex(text.Replace("\r\n", "\n"));

    private static string ModelInputSha(string accession) => Sha256Hex("model-input:" + accession);

    private static List<string> HashOrdered(IEnumerable<string> accessions) =>
        accessions.OrderBy(AccessionHash.HexOf, StringComparer.Ordinal).ToList();

    private static List<string> DirectionalAccessions(int count) =>
        Enumerable.Range(1, count).Select(static i => $"0000000001-26-{i:D6}").ToList();

    private static List<string> NoSignalAccessions(int count) =>
        Enumerable.Range(1, count).Select(static i => $"0000000000-26-{i:D6}").ToList();

    private sealed class Sandbox : IDisposable
    {
        private const string WorksheetHeader =
            "accession,accessionSha256,ticker,cik,companyName,outcome,signalType,direction,confidence,"
            + "strength,novelty,supportingExcerpt,reason,observedAtUtc,comparabilityPolicy,"
            + "comparabilityCapTriggering,comparabilityDiagnosticOnly,cacheVersion,modelIdentity,scopeSegment,cacheFile";

        private const string ManifestHeader =
            "accession,ticker,cik,documentFileName,documentType,exhibitUrl,fullTextSha256,fullTextLength,"
            + "modelInputSha256,modelInputLength,truncated,maxInputLength,outcome,fetchedAtUtc";

        private readonly List<string> _worksheetRows = [];
        private readonly List<string> _manifestRows = [];
        private readonly List<string> _labelLines = [];

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "radar-cal-script-" + Guid.NewGuid().ToString("N"));

        /// <summary>Default template uses LF; the CRLF test overrides this with CRLF line endings.</summary>
        public string PromptContent { get; set; } = "# spec163 test prompt\njudge only the filing text\n";

        public string PromptHash => LfHash(PromptContent);

        public string LabelsPath => Path.Combine(Dir, "labels.jsonl");

        public string WorksheetPath => Path.Combine(Dir, "worksheet.csv");

        public string OutFilePath => Path.Combine(Dir, "report.md");

        public Sandbox()
        {
            Directory.CreateDirectory(Dir);
        }

        public void AddDirectional(string accession, string confidence = "0.85", string direction = "Positive")
        {
            _worksheetRows.Add(string.Join(",",
                accession, AccessionHash.HexOf(accession), "tick", "123", "TestCo",
                "DirectionalSignalProduced", "EarningsRelease", direction, confidence, "2", "0.5",
                "", "", "2026-07-01T00:00:00.0000000Z", "cmpscan-v2", "", "", "3",
                "openai:test-model", "test-scope", "cache.json"));
            AddManifestRow(accession);
        }

        public void AddNoSignal(string accession)
        {
            _worksheetRows.Add(string.Join(",",
                accession, AccessionHash.HexOf(accession), "tick", "123", "TestCo",
                "NoDirectionalSignal", "", "", "", "", "",
                "", "", "2026-07-01T00:00:00.0000000Z", "cmpscan-v2", "", "", "3",
                "openai:test-model", "test-scope", "cache.json"));
            AddManifestRow(accession);
        }

        private void AddManifestRow(string accession) =>
            _manifestRows.Add(string.Join(",",
                accession, "tick", "123", "ex991.htm", "EX-99.1", "https://example.test/ex991.htm",
                Sha256Hex("full-text:" + accession), "9000", ModelInputSha(accession), "9000",
                "false", "12000", "success", "2026-07-01T00:00:00.0000000Z"));

        public void AddLabel(
            string accession,
            string direction = "Neutral",
            string? finalDirection = null,
            string? selectionReason = null,
            string? status = null,
            string? promptHash = null,
            bool omitPromptHash = false,
            string? modelInputHash = null,
            bool omitModelInputHash = false,
            string provider = "anthropic",
            string model = "claude-fable-5",
            string version = "cal-v2",
            int attempt = 1)
        {
            var protocol = new Dictionary<string, object?>
            {
                ["version"] = version,
                ["labeler"] = new Dictionary<string, object?> { ["provider"] = provider, ["model"] = model },
                ["labeledAtUtc"] = "2026-07-20T00:00:00Z",
                ["attempt"] = attempt,
            };
            if (!omitPromptHash)
            {
                protocol["promptHash"] = promptHash ?? PromptHash;
            }

            var root = new Dictionary<string, object?>
            {
                ["accession"] = accession,
                ["ticker"] = "tick",
                ["cik"] = "123",
                ["batch"] = "b1",
                ["protocol"] = protocol,
                ["label"] = new Dictionary<string, object?>
                {
                    ["direction"] = direction,
                    ["directionConfidence"] = 0.8,
                    ["comparisonClean"] = true,
                    ["comparabilityItems"] = Array.Empty<string>(),
                    ["material"] = "moderate",
                    ["keyFacts"] = new[] { "a reported fact" },
                },
            };
            if (!omitModelInputHash)
            {
                root["modelInputHash"] = modelInputHash ?? ModelInputSha(accession);
            }

            if (finalDirection is not null || selectionReason is not null || status is not null)
            {
                var adjudication = new Dictionary<string, object?> { ["status"] = status ?? "confirmed" };
                if (selectionReason is not null)
                {
                    adjudication["selectionReason"] = selectionReason;
                }

                if (finalDirection is not null)
                {
                    adjudication["finalDirection"] = finalDirection;
                }

                root["adjudication"] = adjudication;
            }

            _labelLines.Add(JsonSerializer.Serialize(root));
        }

        /// <summary>Writes every sandbox file (script copy, contract, template, worksheet, manifest, labels).</summary>
        public void WriteAll()
        {
            File.Copy(RepoScriptPath(), Path.Combine(Dir, "analyze-labels.ps1"), overwrite: true);
            File.WriteAllText(Path.Combine(Dir, "labeling-prompt.md"), PromptContent);
            File.WriteAllText(Path.Combine(Dir, "study-contract.json"),
                "{\n"
                + "  \"protocolVersion\": \"cal-v2\",\n"
                + "  \"labeler\": { \"provider\": \"anthropic\", \"model\": \"claude-fable-5\" },\n"
                + $"  \"promptTemplateSha256\": \"{PromptHash}\",\n"
                + "  \"hashCanonicalization\": \"utf8-crlf-to-lf\"\n"
                + "}\n");
            File.WriteAllText(WorksheetPath, WorksheetHeader + "\n" + string.Join("\n", _worksheetRows) + "\n");
            File.WriteAllText(Path.Combine(Dir, "exhibit-manifest.csv"),
                ManifestHeader + "\n" + string.Join("\n", _manifestRows) + "\n");
            File.WriteAllText(LabelsPath, string.Join("\n", _labelLines) + (_labelLines.Count > 0 ? "\n" : string.Empty));
        }

        public (int ExitCode, string StdOut, string StdErr) Run(
            bool interim = false, bool emitSample = false, bool withOutFile = false)
        {
            var psi = new ProcessStartInfo(HostExe.Value)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Dir,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(Path.Combine(Dir, "analyze-labels.ps1"));
            psi.ArgumentList.Add("-LabelsPath");
            psi.ArgumentList.Add(LabelsPath);
            psi.ArgumentList.Add("-WorksheetPath");
            psi.ArgumentList.Add(WorksheetPath);
            if (withOutFile)
            {
                psi.ArgumentList.Add("-OutFile");
                psi.ArgumentList.Add(OutFilePath);
            }

            if (interim)
            {
                psi.ArgumentList.Add("-Interim");
            }

            if (emitSample)
            {
                psi.ArgumentList.Add("-EmitSample");
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start " + HostExe.Value);
            var stdOut = process.StandardOutput.ReadToEndAsync();
            var stdErr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("analyze-labels.ps1 did not finish within 180s.");
            }

            process.WaitForExit(); // Drain the redirected streams fully.
            return (process.ExitCode, stdOut.Result, stdErr.Result);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private readonly List<Sandbox> _sandboxes = [];

    private Sandbox NewSandbox()
    {
        var sandbox = new Sandbox();
        _sandboxes.Add(sandbox);
        return sandbox;
    }

    public void Dispose()
    {
        foreach (var sandbox in _sandboxes)
        {
            sandbox.Dispose();
        }
    }

    /// <summary>
    /// A fully-labeled, fully-adjudicated fixture: <paramref name="directionalCount"/> directional rows in
    /// one confidence bin (all derived sample members while ≤ 10), agreeing labels adjudicated
    /// calibration-sample/Positive, plus <paramref name="noSignalCount"/> no-signal rows of which the first
    /// <paramref name="noSignalLabeled"/> (hash order) get clean Neutral labels.
    /// </summary>
    private Sandbox CreateFixture(int directionalCount = 4, int noSignalCount = 60, int noSignalLabeled = 60)
    {
        var sandbox = NewSandbox();
        foreach (var accession in DirectionalAccessions(directionalCount))
        {
            sandbox.AddDirectional(accession);
            sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive", selectionReason: "calibration-sample");
        }

        var noSignal = NoSignalAccessions(noSignalCount);
        foreach (var accession in noSignal)
        {
            sandbox.AddNoSignal(accession);
        }

        foreach (var accession in HashOrdered(noSignal).Take(noSignalLabeled))
        {
            sandbox.AddLabel(accession, direction: "Neutral");
        }

        return sandbox;
    }

    // ---------------------------------------------------------------- Fix 1: sample membership + coverage

    [Fact]
    public void LabelClaimingCalibrationSample_OutsideDerivedSet_FailsNamingTheAccession()
    {
        // 12 directional rows in one bin ⇒ the derived sample is the first 10 by hash; row 11 CLAIMS
        // membership it does not have.
        var sandbox = NewSandbox();
        var directional = DirectionalAccessions(12);
        var ordered = HashOrdered(directional);
        foreach (var accession in directional)
        {
            sandbox.AddDirectional(accession);
        }

        foreach (var accession in ordered.Take(10))
        {
            sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive", selectionReason: "calibration-sample");
        }

        sandbox.AddLabel(ordered[10], direction: "Positive", finalDirection: "Positive", selectionReason: "calibration-sample");
        sandbox.AddLabel(ordered[11], direction: "Positive");

        foreach (var accession in NoSignalAccessions(60))
        {
            sandbox.AddNoSignal(accession);
            sandbox.AddLabel(accession, direction: "Neutral");
        }

        sandbox.WriteAll();
        var (exit, stdOut, _) = sandbox.Run();

        Assert.NotEqual(0, exit);
        Assert.Contains(ordered[10], stdOut, StringComparison.Ordinal);
        Assert.Contains("derived probability sample", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void DerivedMemberWithoutAdjudicatedLabel_InterimIncomplete_FinalFailsWritingNoArtifact()
    {
        // One derived sample member is labeled but never adjudicated (no finalDirection).
        var sandbox = NewSandbox();
        var directional = DirectionalAccessions(4);
        var unadjudicated = HashOrdered(directional)[0];
        foreach (var accession in directional)
        {
            sandbox.AddDirectional(accession);
            if (accession == unadjudicated)
            {
                sandbox.AddLabel(accession, direction: "Positive"); // labeled, no finalDirection
            }
            else
            {
                sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive", selectionReason: "calibration-sample");
            }
        }

        foreach (var accession in NoSignalAccessions(60))
        {
            sandbox.AddNoSignal(accession);
            sandbox.AddLabel(accession, direction: "Neutral");
        }

        sandbox.WriteAll();

        var interim = sandbox.Run(interim: true);
        Assert.Equal(0, interim.ExitCode);
        Assert.Contains("Calibration table (derived probability sample ONLY) - INCOMPLETE", interim.StdOut, StringComparison.Ordinal);
        Assert.Contains(unadjudicated, interim.StdOut, StringComparison.Ordinal);

        var final = sandbox.Run(withOutFile: true);
        Assert.NotEqual(0, final.ExitCode);
        Assert.Contains("INCOMPLETE", final.StdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.OutFilePath), "final mode must write NO report artifact on failure");
    }

    [Fact]
    public void EmitSample_IsIdentical_WithZeroLabelsAndFullLabels()
    {
        var sandbox = CreateFixture(directionalCount: 6);
        sandbox.WriteAll();

        var withLabels = sandbox.Run(emitSample: true);
        Assert.Equal(0, withLabels.ExitCode);

        File.WriteAllText(sandbox.LabelsPath, string.Empty); // zero labels
        var withoutLabels = sandbox.Run(emitSample: true);
        Assert.Equal(0, withoutLabels.ExitCode);

        static string SampleSection(string output)
        {
            var index = output.IndexOf("## Calibration probability sample", StringComparison.Ordinal);
            Assert.True(index >= 0, "sample section missing from -EmitSample output");
            return output[index..];
        }

        Assert.Equal(SampleSection(withLabels.StdOut), SampleSection(withoutLabels.StdOut));
    }

    [Fact]
    public void MissingDirectionalCoverage_InterimIncomplete_FinalFails()
    {
        // 4 directional rows; only 3 labeled (the missing one is still a derived sample member, so both
        // the coverage rule and the sample rule report it).
        var sandbox = NewSandbox();
        var directional = DirectionalAccessions(4);
        var missing = HashOrdered(directional)[3];
        foreach (var accession in directional)
        {
            sandbox.AddDirectional(accession);
            if (accession != missing)
            {
                sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive", selectionReason: "calibration-sample");
            }
        }

        foreach (var accession in NoSignalAccessions(60))
        {
            sandbox.AddNoSignal(accession);
            sandbox.AddLabel(accession, direction: "Neutral");
        }

        sandbox.WriteAll();

        var interim = sandbox.Run(interim: true);
        Assert.Equal(0, interim.ExitCode);
        Assert.Contains("INCOMPLETE", interim.StdOut, StringComparison.Ordinal);
        Assert.Contains(missing, interim.StdOut, StringComparison.Ordinal);

        var final = sandbox.Run(withOutFile: true);
        Assert.NotEqual(0, final.ExitCode);
        Assert.Contains("directional coverage incomplete", final.StdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.OutFilePath));
    }

    // ---------------------------------------------------------------- Fix 2: no-signal state machine

    [Fact]
    public void NoSignal59Labeled_IsInterimIncomplete()
    {
        var sandbox = CreateFixture(noSignalCount: 90, noSignalLabeled: 59);
        sandbox.WriteAll();

        var interim = sandbox.Run(interim: true);
        Assert.Equal(0, interim.ExitCode);
        Assert.Contains("no-signal precommitted sample incomplete: 59/60", interim.StdOut, StringComparison.Ordinal);

        var final = sandbox.Run();
        Assert.NotEqual(0, final.ExitCode);
    }

    [Fact]
    public void NoSignal61Labeled_WithoutTrigger_FailsAsUnplannedExtension()
    {
        var sandbox = CreateFixture(noSignalCount: 90, noSignalLabeled: 61);
        sandbox.WriteAll();

        var (exit, stdOut, _) = sandbox.Run();
        Assert.NotEqual(0, exit);
        Assert.Contains("unplanned extension", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSignalRightCountWrongMember_FailsListingTheDifference()
    {
        // 60 labels, but one of them is the 61st row by hash order instead of one of the first 60.
        var sandbox = NewSandbox();
        foreach (var accession in DirectionalAccessions(4))
        {
            sandbox.AddDirectional(accession);
            sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive", selectionReason: "calibration-sample");
        }

        var noSignal = NoSignalAccessions(90);
        var ordered = HashOrdered(noSignal);
        foreach (var accession in noSignal)
        {
            sandbox.AddNoSignal(accession);
        }

        var skipped = ordered[7]; // A first-60 member deliberately left unlabeled.
        foreach (var accession in ordered.Take(60).Where(a => a != skipped).Append(ordered[60]))
        {
            sandbox.AddLabel(accession, direction: "Neutral");
        }

        sandbox.WriteAll();
        var (exit, stdOut, _) = sandbox.Run();

        Assert.NotEqual(0, exit);
        Assert.Contains("does not match the precommitted hash-order prefix", stdOut, StringComparison.Ordinal);
        Assert.Contains(skipped, stdOut, StringComparison.Ordinal);
        Assert.Contains(ordered[60], stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderFlaggedCandidateWithoutAdjudication_IsPending_NeverAMiss()
    {
        var sandbox = NewSandbox();
        foreach (var accession in DirectionalAccessions(4))
        {
            sandbox.AddDirectional(accession);
            sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive", selectionReason: "calibration-sample");
        }

        var noSignal = NoSignalAccessions(60);
        var ordered = HashOrdered(noSignal);
        var flagged = ordered[3];
        foreach (var accession in noSignal)
        {
            sandbox.AddNoSignal(accession);
        }

        foreach (var accession in ordered)
        {
            // One reader-flagged directional candidate WITHOUT adjudication — pending, never a rate.
            sandbox.AddLabel(accession, direction: accession == flagged ? "Positive" : "Neutral");
        }

        sandbox.WriteAll();
        var interim = sandbox.Run(interim: true);

        Assert.Equal(0, interim.ExitCode);
        Assert.Contains("awaiting adjudication", interim.StdOut, StringComparison.Ordinal);
        Assert.Contains(flagged, interim.StdOut, StringComparison.Ordinal);
        Assert.Contains("EXTENSION: PENDING", interim.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("EXTENSION: TRIGGERED", interim.StdOut, StringComparison.Ordinal);
        Assert.Contains("0 confirmed miss", interim.StdOut, StringComparison.Ordinal);

        // Final mode: an undecidable trigger is incompleteness, not a pass.
        var final = sandbox.Run();
        Assert.NotEqual(0, final.ExitCode);
    }

    [Fact]
    public void ConfirmedMissInFirst60_RendersTriggered_AndFinalAt60Fails_Next30Required()
    {
        var sandbox = NewSandbox();
        foreach (var accession in DirectionalAccessions(4))
        {
            sandbox.AddDirectional(accession);
            sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive", selectionReason: "calibration-sample");
        }

        var noSignal = NoSignalAccessions(90);
        var ordered = HashOrdered(noSignal);
        var miss = ordered[5];
        foreach (var accession in noSignal)
        {
            sandbox.AddNoSignal(accession);
        }

        foreach (var accession in ordered.Take(60))
        {
            if (accession == miss)
            {
                sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive");
            }
            else
            {
                sandbox.AddLabel(accession, direction: "Neutral");
            }
        }

        sandbox.WriteAll();

        var interim = sandbox.Run(interim: true);
        Assert.Equal(0, interim.ExitCode);
        Assert.Contains("EXTENSION: TRIGGERED (1 confirmed miss", interim.StdOut, StringComparison.Ordinal);

        var final = sandbox.Run(withOutFile: true);
        Assert.NotEqual(0, final.ExitCode);
        Assert.Contains("next 30 required", final.StdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.OutFilePath));
    }

    [Fact]
    public void NinetyLabeled_WithMissesOnlyInRows61To90_FailsAsUnplannedExtension()
    {
        // Proves rows 61–90 NEVER enter the trigger: if they did, the miss at position 65 would fire it
        // and the N=90 set would be a legitimate extension.
        var sandbox = NewSandbox();
        foreach (var accession in DirectionalAccessions(4))
        {
            sandbox.AddDirectional(accession);
            sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive", selectionReason: "calibration-sample");
        }

        var noSignal = NoSignalAccessions(90);
        var ordered = HashOrdered(noSignal);
        var lateMiss = ordered[64]; // Position 65 — inside 61–90, outside the trigger window.
        foreach (var accession in noSignal)
        {
            sandbox.AddNoSignal(accession);
        }

        foreach (var accession in ordered)
        {
            if (accession == lateMiss)
            {
                sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive");
            }
            else
            {
                sandbox.AddLabel(accession, direction: "Neutral");
            }
        }

        sandbox.WriteAll();
        var (exit, stdOut, _) = sandbox.Run();

        Assert.NotEqual(0, exit);
        Assert.Contains("unplanned extension", stdOut, StringComparison.Ordinal);
        Assert.Contains("did NOT fire", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void NinetyLabeled_WithTriggerFiredOnFirst60_IsAValidFinalState()
    {
        var sandbox = NewSandbox();
        foreach (var accession in DirectionalAccessions(4))
        {
            sandbox.AddDirectional(accession);
            sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive", selectionReason: "calibration-sample");
        }

        var noSignal = NoSignalAccessions(90);
        var ordered = HashOrdered(noSignal);
        var earlyMiss = ordered[2]; // In rows 1–60: the trigger legitimately fired.
        foreach (var accession in noSignal)
        {
            sandbox.AddNoSignal(accession);
        }

        foreach (var accession in ordered)
        {
            if (accession == earlyMiss)
            {
                sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive");
            }
            else
            {
                sandbox.AddLabel(accession, direction: "Neutral");
            }
        }

        sandbox.WriteAll();
        var (exit, stdOut, _) = sandbox.Run(withOutFile: true);

        Assert.Equal(0, exit);
        Assert.Contains("EXTENSION: TRIGGERED", stdOut, StringComparison.Ordinal);
        Assert.True(File.Exists(sandbox.OutFilePath));
    }

    // ---------------------------------------------------------------- Fix 3: provenance

    [Fact]
    public void WrongPromptHashOnOneLabel_Fails_NamingTheRow()
    {
        var sandbox = CreateFixture();
        var bad = NoSignalAccessions(60)[0];
        // A higher-attempt retry REPLACES the earlier label (the effective-label rule), carrying the
        // wrong hash.
        sandbox.AddLabel(bad, direction: "Neutral", promptHash: Sha256Hex("some other template"), attempt: 2);
        sandbox.WriteAll();

        var (exit, stdOut, _) = sandbox.Run();
        Assert.NotEqual(0, exit);
        Assert.Contains(bad, stdOut, StringComparison.Ordinal);
        Assert.Contains("promptHash", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPromptHash_IsIncompleteness_InterimPasses_FinalFails()
    {
        var sandbox = NewSandbox();
        var directional = DirectionalAccessions(4);
        var bare = HashOrdered(directional)[1];
        foreach (var accession in directional)
        {
            sandbox.AddDirectional(accession);
            sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive",
                selectionReason: "calibration-sample", omitPromptHash: accession == bare);
        }

        foreach (var accession in NoSignalAccessions(60))
        {
            sandbox.AddNoSignal(accession);
            sandbox.AddLabel(accession, direction: "Neutral");
        }

        sandbox.WriteAll();

        var interim = sandbox.Run(interim: true);
        Assert.Equal(0, interim.ExitCode);
        Assert.Contains("missing protocol.promptHash", interim.StdOut, StringComparison.Ordinal);

        var final = sandbox.Run();
        Assert.NotEqual(0, final.ExitCode);
        Assert.Contains(bare, final.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelInputHashMismatchAgainstManifest_Fails()
    {
        var sandbox = CreateFixture();
        var bad = NoSignalAccessions(60)[1];
        sandbox.AddLabel(bad, direction: "Neutral", modelInputHash: Sha256Hex("a different input"), attempt: 2);
        sandbox.WriteAll();

        var (exit, stdOut, _) = sandbox.Run();
        Assert.NotEqual(0, exit);
        Assert.Contains(bad, stdOut, StringComparison.Ordinal);
        Assert.Contains("modelInputSha256", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongLabelerModel_AndWrongProtocolVersion_EachFail()
    {
        var wrongModel = CreateFixture();
        var target = NoSignalAccessions(60)[2];
        wrongModel.AddLabel(target, direction: "Neutral", model: "some-other-model", attempt: 2);
        wrongModel.WriteAll();
        var modelRun = wrongModel.Run();
        Assert.NotEqual(0, modelRun.ExitCode);
        Assert.Contains("labeler", modelRun.StdOut, StringComparison.Ordinal);

        var wrongVersion = CreateFixture();
        wrongVersion.AddLabel(target, direction: "Neutral", version: "cal-v1", attempt: 2);
        wrongVersion.WriteAll();
        var versionRun = wrongVersion.Run();
        Assert.NotEqual(0, versionRun.ExitCode);
        Assert.Contains("protocol.version", versionRun.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void EditedTemplate_FailsAgainstContract_EvenInInterimMode()
    {
        var sandbox = CreateFixture();
        sandbox.WriteAll();
        // Drift AFTER the contract + labels were pinned: the recomputed hash no longer matches.
        File.AppendAllText(Path.Combine(sandbox.Dir, "labeling-prompt.md"), "an edit after pinning\n");

        var interim = sandbox.Run(interim: true);
        Assert.NotEqual(0, interim.ExitCode);
        Assert.Contains("prompt template hash mismatch", interim.StdOut, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- final happy path + canonicalization

    [Fact]
    public void CompleteCorrectFixture_FinalMode_Passes_AndWritesTheReport()
    {
        var sandbox = CreateFixture();
        sandbox.WriteAll();

        var (exit, stdOut, stdErr) = sandbox.Run(withOutFile: true);

        Assert.True(exit == 0, $"expected success; stdout: {stdOut}\nstderr: {stdErr}");
        Assert.Contains("EXTENSION: NOT-TRIGGERED", stdOut, StringComparison.Ordinal);
        Assert.True(File.Exists(sandbox.OutFilePath));
        var report = File.ReadAllText(sandbox.OutFilePath);
        Assert.Contains("## Calibration table", report, StringComparison.Ordinal);
    }

    [Fact]
    public void CrlfTemplateOnDisk_MatchesLfNormalizedContractHash()
    {
        // The template checked out with CRLF line endings while the contract pins the LF-normalized hash
        // of the same content: the canonicalization makes them agree (Windows checkout vs CI checkout).
        var sandbox = CreateFixture();
        sandbox.PromptContent = "# spec163 test prompt\r\njudge only the filing text\r\n";
        sandbox.WriteAll();

        var (exit, stdOut, stdErr) = sandbox.Run(withOutFile: true);

        Assert.True(exit == 0, $"expected success; stdout: {stdOut}\nstderr: {stdErr}");
        Assert.True(File.Exists(sandbox.OutFilePath));
    }
}
