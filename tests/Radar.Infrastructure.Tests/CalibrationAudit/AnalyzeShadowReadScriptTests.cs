using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Radar.CalibrationAudit;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 164: end-to-end tests of <c>scripts/calibration-audit/analyze-shadow-read.ps1</c>. Each test copies
/// the REAL script into a sandbox beside its own shadow-prompt.md / worksheet / labels / shadow records (the
/// sanctioned seam — the script recomputes the committed prompt hash from its OWN directory, so a test
/// substitutes it by owning that directory). Runs under <c>pwsh</c> when available, falling back to Windows
/// PowerShell 5.1; if neither host exists the tests FAIL (never skip — CI always has pwsh).
/// </summary>
public sealed class AnalyzeShadowReadScriptTests : IDisposable
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
            "Neither 'pwsh' nor 'powershell' was found on PATH. The analyze-shadow-read.ps1 tests require a "
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

        return Path.Combine(dir.FullName, "scripts", "calibration-audit", "analyze-shadow-read.ps1");
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string LfHash(string text) => Sha256Hex(text.Replace("\r\n", "\n", StringComparison.Ordinal));

    private static List<string> HashOrdered(IEnumerable<string> accessions) =>
        accessions.OrderBy(AccessionHash.HexOf, StringComparer.Ordinal).ToList();

    // ---------------------------------------------------------------- sandbox

    private sealed class Sandbox : IDisposable
    {
        private const string WorksheetHeader =
            "accession,accessionSha256,ticker,cik,companyName,outcome,signalType,direction,confidence,"
            + "strength,novelty,supportingExcerpt,reason,observedAtUtc,comparabilityPolicy,"
            + "comparabilityCapTriggering,comparabilityDiagnosticOnly,cacheVersion,modelIdentity,scopeSegment,cacheFile";

        private readonly List<string> _worksheetRows = [];
        private readonly List<string> _labelLines = [];

        public Sandbox()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(ShadowDir);
        }

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "radar-shadow-script-" + Guid.NewGuid().ToString("N"));

        public string ShadowDir => Path.Combine(Dir, "shadow");

        public string PromptContent { get; set; } = "# spec164 test shadow prompt\nyou must return a direction\n";

        public string PromptHash => LfHash(PromptContent);

        public string WorksheetPath => Path.Combine(Dir, "worksheet.csv");

        public string LabelsPath => Path.Combine(Dir, "labels.jsonl");

        public string OutFilePath => Path.Combine(Dir, "report.md");

        public void AddDirectional(string accession, string direction = "Positive", string confidence = "0.85") =>
            _worksheetRows.Add(string.Join(",",
                accession, AccessionHash.HexOf(accession), "tick", "123", "TestCo",
                "DirectionalSignalProduced", "GuidanceChange", direction, confidence, "2", "0.5",
                "", "", "2026-07-01T00:00:00.0000000Z", "cmpscan-v2", "", "", "3",
                "openai:test-model", "test-scope", "cache.json"));

        public void AddNoSignal(string accession) =>
            _worksheetRows.Add(string.Join(",",
                accession, AccessionHash.HexOf(accession), "tick", "123", "TestCo",
                "NoDirectionalSignal", "", "", "", "", "",
                "", "", "2026-07-01T00:00:00.0000000Z", "cmpscan-v2", "", "", "3",
                "openai:test-model", "test-scope", "cache.json"));

        public void AddLabel(string accession, string direction = "Neutral", string? finalDirection = null)
        {
            var adjudication = new Dictionary<string, object?> { ["status"] = "confirmed" };
            if (finalDirection is not null)
            {
                adjudication["finalDirection"] = finalDirection;
            }

            _labelLines.Add(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["accession"] = accession,
                ["ticker"] = "tick",
                ["protocol"] = new Dictionary<string, object?> { ["version"] = "cal-v2", ["attempt"] = 1 },
                ["label"] = new Dictionary<string, object?> { ["direction"] = direction, ["directionConfidence"] = 0.8 },
                ["adjudication"] = adjudication,
            }));
        }

        public void AddRecord(
            string accession,
            string cohort,
            string status = "ok",
            string? direction = null,
            decimal? confidence = null,
            string? promptSha = null)
        {
            var record = new Dictionary<string, object?>
            {
                ["accession"] = accession,
                ["cohort"] = cohort,
                ["status"] = status,
                ["direction"] = direction,
                ["confidence"] = confidence,
                ["rationale"] = status == "ok" ? "reported facts" : null,
                ["rawResponse"] = "{}",
                ["error"] = status == "ok" ? null : "boom",
                ["promptVersion"] = "cal-shadow-v1",
                ["promptSha256"] = promptSha ?? PromptHash,
                ["modelIdentity"] = "openai:test-model",
                ["modelInputSha256"] = Sha256Hex("model-input:" + accession),
                ["readAtUtc"] = "2026-07-31T00:00:00.0000000Z",
            };

            File.WriteAllText(
                Path.Combine(ShadowDir, accession + ".json"),
                JsonSerializer.Serialize(record));
        }

        public void WriteAll()
        {
            File.Copy(RepoScriptPath(), Path.Combine(Dir, "analyze-shadow-read.ps1"), overwrite: true);
            File.WriteAllText(Path.Combine(Dir, "shadow-prompt.md"), PromptContent);
            File.WriteAllText(WorksheetPath, WorksheetHeader + "\n" + string.Join("\n", _worksheetRows) + "\n");
            File.WriteAllText(LabelsPath, string.Join("\n", _labelLines) + (_labelLines.Count > 0 ? "\n" : string.Empty));
        }

        public (int ExitCode, string Report, string StdErr) Run()
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
            psi.ArgumentList.Add(Path.Combine(Dir, "analyze-shadow-read.ps1"));
            psi.ArgumentList.Add("-ShadowRoot");
            psi.ArgumentList.Add(ShadowDir);
            psi.ArgumentList.Add("-WorksheetPath");
            psi.ArgumentList.Add(WorksheetPath);
            psi.ArgumentList.Add("-LabelsPath");
            psi.ArgumentList.Add(LabelsPath);
            psi.ArgumentList.Add("-OutFile");
            psi.ArgumentList.Add(OutFilePath);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start " + HostExe.Value);
            var stdOut = process.StandardOutput.ReadToEndAsync();
            var stdErr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("analyze-shadow-read.ps1 did not finish within 180s.");
            }

            process.WaitForExit();
            _ = stdOut.Result;

            // The report is read from the UTF-8 -OutFile artifact, not from the console stream: the outcome
            // line carries a non-ASCII tau and console encoding is host-dependent.
            var report = File.Exists(OutFilePath) ? File.ReadAllText(OutFilePath) : string.Empty;
            return (process.ExitCode, report, stdErr.Result);
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

    // ---------------------------------------------------------------- fixture builders

    private static List<string> DirectionalAccessions() =>
        Enumerable.Range(1, 145).Select(static i => $"0000000001-26-{i:D6}").ToList();

    private static List<string> NoSignalAccessions() =>
        Enumerable.Range(1, 153).Select(static i => $"0000000000-26-{i:D6}").ToList();

    /// <summary>
    /// A FULL-SIZE fixture at the study's real shape (145 directional + 153 no-signal, 90 labeled of which 33
    /// are provisional misses). Full size is deliberate: the frozen criteria are ABSOLUTE counts over the
    /// denominators 33 / 57 / 145, so only a full-size fixture can render SUPPORTED at all.
    /// </summary>
    private Sandbox FullFixture(
        int strictRecoveries = 20,
        int wrongDirectionRecoveries = 3,
        int falseAlarms = 5,
        int flips = 5,
        int inversions = 0,
        decimal recoveryConfidence = 0.85m,
        decimal inversionConfidence = 0.95m,
        int failedLabeledNoSignal = 0,
        int failedUnlabeled = 0)
    {
        var sandbox = NewSandbox();

        var directional = HashOrdered(DirectionalAccessions());
        foreach (var accession in directional)
        {
            sandbox.AddDirectional(accession);
            sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive");
        }

        var noSignal = HashOrdered(NoSignalAccessions());
        foreach (var accession in noSignal)
        {
            sandbox.AddNoSignal(accession);
        }

        var labeled = noSignal.Take(90).ToList();
        var misses = labeled.Take(33).ToList();
        var nonMisses = labeled.Skip(33).ToList();
        var unlabeled = noSignal.Skip(90).ToList();

        foreach (var accession in misses)
        {
            sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive");
        }

        foreach (var accession in nonMisses)
        {
            sandbox.AddLabel(accession); // Neutral, no finalDirection ⇒ provisional NON-miss.
        }

        // --- shadow records ---
        for (var i = 0; i < misses.Count; i++)
        {
            if (i < failedLabeledNoSignal)
            {
                sandbox.AddRecord(misses[i], ShadowCohort.NoSignal, status: "call-failed");
            }
            else if (i < failedLabeledNoSignal + strictRecoveries)
            {
                sandbox.AddRecord(misses[i], ShadowCohort.NoSignal, direction: "Improving", confidence: recoveryConfidence);
            }
            else if (i < failedLabeledNoSignal + strictRecoveries + wrongDirectionRecoveries)
            {
                // A miss recovered with the WRONG direction: loose recovery YES, strict recovery NO.
                sandbox.AddRecord(misses[i], ShadowCohort.NoSignal, direction: "Deteriorating", confidence: recoveryConfidence);
            }
            else
            {
                sandbox.AddRecord(misses[i], ShadowCohort.NoSignal, direction: "Neutral", confidence: 0.70m);
            }
        }

        for (var i = 0; i < nonMisses.Count; i++)
        {
            sandbox.AddRecord(
                nonMisses[i], ShadowCohort.NoSignal,
                direction: i < falseAlarms ? "Improving" : "Neutral",
                confidence: i < falseAlarms ? recoveryConfidence : 0.60m);
        }

        for (var i = 0; i < directional.Count; i++)
        {
            if (i < inversions)
            {
                // Sealed Positive, forced Deteriorating.
                sandbox.AddRecord(directional[i], ShadowCohort.Directional, direction: "Deteriorating", confidence: inversionConfidence);
            }
            else if (i < inversions + flips)
            {
                sandbox.AddRecord(directional[i], ShadowCohort.Directional, direction: "Mixed", confidence: 0.50m);
            }
            else
            {
                sandbox.AddRecord(directional[i], ShadowCohort.Directional, direction: "Improving", confidence: 0.90m);
            }
        }

        for (var i = 0; i < unlabeled.Count; i++)
        {
            if (i < failedUnlabeled)
            {
                sandbox.AddRecord(unlabeled[i], ShadowCohort.NoSignal, status: "parse-failed");
            }
            else
            {
                // Directional reads at high confidence — if these ever leaked into an accuracy rate, the
                // false-alarm and recovery numbers below would move.
                sandbox.AddRecord(unlabeled[i], ShadowCohort.NoSignal, direction: "Improving", confidence: 0.99m);
            }
        }

        sandbox.WriteAll();
        return sandbox;
    }

    private static string OutcomeLine(string report)
    {
        var line = report.Split('\n').Select(static l => l.TrimEnd('\r')).FirstOrDefault(static l => l.StartsWith("SHADOW: ", StringComparison.Ordinal));
        Assert.NotNull(line);
        return line!;
    }

    // ---------------------------------------------------------------- descriptive tables

    [Fact]
    public void RecoveryTable_CountsStrictLooseAndFalseAlarms_WrongDirectionIsLooseOnly()
    {
        // 20 correct + 3 WRONG-direction recoveries of 33 misses; 5 false alarms of 57 non-misses.
        var sandbox = FullFixture();
        var (_, report, stdErr) = sandbox.Run();

        Assert.True(report.Length > 0, "no report written. stderr: " + stdErr);
        Assert.Contains("STRICT recovery P(forced directional AND direction agrees with the adjudicated finalDirection | provisional miss): 20/33", report, StringComparison.Ordinal);
        Assert.Contains("LOOSE recovery P(forced directional | provisional miss): 23/33", report, StringComparison.Ordinal);
        Assert.Contains("FALSE ALARM P(forced Improving/Deteriorating | provisional NON-miss): 5/57", report, StringComparison.Ordinal);

        // Wilson intervals accompany every headline rate.
        Assert.Contains("Wilson 95%", report, StringComparison.Ordinal);
    }

    [Fact]
    public void VocabularyMap_MixedNeverCountsAsAgreeingWithADirectionalLabel()
    {
        // Every miss is read `Mixed` at high confidence. `Mixed` maps to label `Mixed`, never to `Positive`,
        // so NOTHING is recovered — strictly or loosely.
        var sandbox = FullFixture(strictRecoveries: 0, wrongDirectionRecoveries: 0, falseAlarms: 0);
        var noSignal = HashOrdered(NoSignalAccessions());
        foreach (var accession in noSignal.Take(33))
        {
            sandbox.AddRecord(accession, ShadowCohort.NoSignal, direction: "Mixed", confidence: 0.99m);
        }

        var (_, report, _) = sandbox.Run();

        Assert.Contains("| `Mixed` | `Mixed` |", report, StringComparison.Ordinal);
        Assert.Contains("provisional miss): 0/33", report, StringComparison.Ordinal);
        Assert.Contains("LOOSE recovery P(forced directional | provisional miss): 0/33", report, StringComparison.Ordinal);
    }

    [Fact]
    public void StabilityTable_SeparatesAgreementFlipsAndInversions()
    {
        var sandbox = FullFixture(flips: 5, inversions: 2);
        var (_, report, _) = sandbox.Run();

        Assert.Contains("Agrees with the sealed direction: 138/145", report, StringComparison.Ordinal);
        Assert.Contains("Flipped to `Mixed`/`Neutral` (or unresolved): 5/145", report, StringComparison.Ordinal);
        Assert.Contains("INVERTED (directional, opposite the sealed direction): 2/145", report, StringComparison.Ordinal);
    }

    [Fact]
    public void UnlabeledRows_NeverEnterAnyAccuracyRate_AndAreMarkedDistributionOnly()
    {
        // The 63 unlabeled rows are ALL directional at 0.99 — if they leaked in, the false-alarm rate would
        // not be 5/57 and the recovery denominators would not be 33.
        var sandbox = FullFixture();
        var (_, report, _) = sandbox.Run();

        Assert.Contains("FALSE ALARM P(forced Improving/Deteriorating | provisional NON-miss): 5/57", report, StringComparison.Ordinal);
        Assert.Contains("provisional miss): 20/33", report, StringComparison.Ordinal);
        Assert.Contains("Unlabeled no-signal rows - DISTRIBUTION ONLY (63 rows)", report, StringComparison.Ordinal);
        Assert.Contains("**No reference labels exist for these rows: this is a DISTRIBUTION, not accuracy.**", report, StringComparison.Ordinal);
        Assert.Contains("| Improving | 63 |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOtherNumber_IsLabelledDescriptiveOnly()
    {
        var sandbox = FullFixture();
        var (_, report, _) = sandbox.Run();

        Assert.Contains("**EVERY OTHER NUMBER IN THIS REPORT IS DESCRIPTIVE ONLY AND GROUNDS NO PRODUCTION RECOMMENDATION.**", report, StringComparison.Ordinal);
        Assert.Contains("### Descriptive threshold sweep - DESCRIPTIVE ONLY", report, StringComparison.Ordinal);
        Assert.Contains("It is NOT a menu", report, StringComparison.Ordinal);
    }

    [Fact]
    public void StandingCaveats_AreStatedOnEveryRun()
    {
        var sandbox = FullFixture();
        var (_, report, _) = sandbox.Run();

        Assert.Contains("EXPLORATORY ratified same-family verdicts", report, StringComparison.Ordinal);
        Assert.Contains("Filings cluster within tickers", report, StringComparison.Ordinal);
        Assert.Contains("SINGLE-SHOT", report, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the decision block

    [Fact]
    public void AllCriteriaMet_RendersSupported_WithItsNumbers()
    {
        var sandbox = FullFixture(strictRecoveries: 20, falseAlarms: 5, flips: 5, inversions: 0);
        var (exit, report, _) = sandbox.Run();

        Assert.Equal(0, exit);
        Assert.Equal(
            "SHADOW: SUPPORTED (τ=0.80, strict recovery 20/33, false alarms 5/57, inversions 0/145, flips 5/145)",
            OutcomeLine(report));
    }

    [Fact]
    public void CriteriaMissed_AtBothThresholds_RendersNotSupported_WithNumbersAtBoth()
    {
        // 10 strict recoveries — below the 17 bound at BOTH precommitted thresholds.
        var sandbox = FullFixture(strictRecoveries: 10, wrongDirectionRecoveries: 0, falseAlarms: 5, flips: 5);
        var (exit, report, _) = sandbox.Run();

        Assert.Equal(0, exit);
        var line = OutcomeLine(report);
        Assert.StartsWith("SHADOW: NOT-SUPPORTED (", line, StringComparison.Ordinal);
        Assert.Contains("τ=0.80, strict recovery 10/33", line, StringComparison.Ordinal);
        Assert.Contains("τ=0.90, strict recovery 0/33", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleFailedLabeledRow_RendersInconclusive_NamingIt()
    {
        var sandbox = FullFixture(failedLabeledNoSignal: 1);
        var failed = HashOrdered(NoSignalAccessions())[0];

        var (exit, report, _) = sandbox.Run();

        Assert.NotEqual(0, exit);
        var line = OutcomeLine(report);
        Assert.StartsWith("SHADOW: INCONCLUSIVE (1 rows unresolved:", line, StringComparison.Ordinal);
        Assert.Contains(failed, line, StringComparison.Ordinal);
        Assert.Contains("call-failed", line, StringComparison.Ordinal);
        Assert.Contains("never to decide on a subset", report, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRecordForALabeledRow_AlsoBlocksTheDecision()
    {
        var sandbox = FullFixture();
        var missing = HashOrdered(DirectionalAccessions())[0];
        File.Delete(Path.Combine(sandbox.ShadowDir, missing + ".json"));

        var (exit, report, _) = sandbox.Run();

        Assert.NotEqual(0, exit);
        Assert.Contains("SHADOW: INCONCLUSIVE", OutcomeLine(report), StringComparison.Ordinal);
        Assert.Contains(missing + " (no record)", OutcomeLine(report), StringComparison.Ordinal);
    }

    [Fact]
    public void FailedUnlabeledRow_DoesNotBlockTheDecision()
    {
        // 12 of the 63 UNLABELED rows failed; the labeled 235 are all ok.
        var sandbox = FullFixture(failedUnlabeled: 12);
        var (exit, report, _) = sandbox.Run();

        Assert.Equal(0, exit);
        Assert.StartsWith("SHADOW: SUPPORTED (", OutcomeLine(report), StringComparison.Ordinal);
        Assert.Contains("failures among them do not block", report, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptProvenanceMismatch_RendersInconclusive()
    {
        var sandbox = FullFixture();
        var accession = HashOrdered(NoSignalAccessions())[0];
        sandbox.AddRecord(accession, ShadowCohort.NoSignal, direction: "Improving", confidence: 0.85m,
            promptSha: new string('0', 64));

        var (exit, report, _) = sandbox.Run();

        Assert.NotEqual(0, exit);
        Assert.Contains("SHADOW: INCONCLUSIVE (prompt provenance:", OutcomeLine(report), StringComparison.Ordinal);
        Assert.Contains("PROVENANCE VIOLATION", report, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- threshold semantics

    [Fact]
    public void OppositeDirectionBelowTau_IsAFlipNotAnInversion()
    {
        // Sealed Positive, forced Deteriorating at 0.50 — BELOW tau=0.80, so it is NONDIRECTIONAL at tau:
        // a FLIP, not an inversion. The descriptive (tau=0) stability table still records it as inverted.
        var sandbox = FullFixture(flips: 4, inversions: 1, inversionConfidence: 0.50m);
        var (exit, report, _) = sandbox.Run();

        Assert.Contains("INVERTED (directional, opposite the sealed direction): 1/145", report, StringComparison.Ordinal);

        Assert.Equal(0, exit);
        var line = OutcomeLine(report);
        Assert.StartsWith("SHADOW: SUPPORTED (", line, StringComparison.Ordinal);
        Assert.Contains("inversions 0/145", line, StringComparison.Ordinal);
        Assert.Contains("flips 5/145", line, StringComparison.Ordinal);
    }

    [Fact]
    public void OppositeDirectionAtOrAboveTau_DisqualifiesOutright()
    {
        // A single inversion at 0.95 disqualifies at BOTH precommitted thresholds, even though every other
        // criterion passes comfortably.
        var sandbox = FullFixture(flips: 4, inversions: 1, inversionConfidence: 0.95m);
        var (exit, report, _) = sandbox.Run();

        Assert.Equal(0, exit);
        var line = OutcomeLine(report);
        Assert.StartsWith("SHADOW: NOT-SUPPORTED (", line, StringComparison.Ordinal);
        Assert.Contains("τ=0.80, strict recovery 20/33, false alarms 5/57, inversions 1/145", line, StringComparison.Ordinal);
        Assert.Contains("τ=0.90, strict recovery 0/33, false alarms 0/57, inversions 1/145", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectionalReadBelowTau_IsNondirectionalAtTau()
    {
        // 20 correct recoveries, but at confidence 0.75 — every one of them is NONDIRECTIONAL at tau=0.80,
        // so strict recovery is 0/33 there even though the descriptive (no-threshold) rate is 20/33.
        var sandbox = FullFixture(strictRecoveries: 20, wrongDirectionRecoveries: 0, falseAlarms: 0,
            recoveryConfidence: 0.75m);
        var (exit, report, _) = sandbox.Run();

        Assert.Contains("provisional miss): 20/33", report, StringComparison.Ordinal);

        Assert.Equal(0, exit);
        var line = OutcomeLine(report);
        Assert.StartsWith("SHADOW: NOT-SUPPORTED (", line, StringComparison.Ordinal);
        Assert.Contains("τ=0.80, strict recovery 0/33", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FallbackThreshold_IsEvaluatedExactlyOnce_WhenThePrimaryFails()
    {
        // Every recovery lands at 0.92: below-bound false alarms at 0.80 (12 > 9) but the SAME reads clear the
        // bound at 0.90 because the false alarms sit at 0.85. The one-shot fallback is what rescues it.
        var sandbox = FullFixture(strictRecoveries: 20, wrongDirectionRecoveries: 0, falseAlarms: 12,
            recoveryConfidence: 0.92m);
        var noSignal = HashOrdered(NoSignalAccessions());
        foreach (var accession in noSignal.Skip(33).Take(12))
        {
            sandbox.AddRecord(accession, ShadowCohort.NoSignal, direction: "Improving", confidence: 0.85m);
        }

        var (exit, report, _) = sandbox.Run();

        Assert.Equal(0, exit);
        var line = OutcomeLine(report);
        Assert.StartsWith("SHADOW: SUPPORTED (", line, StringComparison.Ordinal);
        Assert.Contains("τ=0.90, strict recovery 20/33, false alarms 0/57", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DenominatorDrift_RendersInconclusive_RatherThanDecidingOnADifferentStudy()
    {
        // A small fixture: 2 directional + 2 no-signal, all labeled and ok. The frozen criteria are absolute
        // counts over 33 / 57 / 145, so they are meaningless here and the block must refuse to decide.
        var sandbox = NewSandbox();
        foreach (var accession in new[] { "0000000001-26-000001", "0000000001-26-000002" })
        {
            sandbox.AddDirectional(accession);
            sandbox.AddLabel(accession, direction: "Positive", finalDirection: "Positive");
            sandbox.AddRecord(accession, ShadowCohort.Directional, direction: "Improving", confidence: 0.9m);
        }

        foreach (var accession in new[] { "0000000000-26-000001", "0000000000-26-000002" })
        {
            sandbox.AddNoSignal(accession);
            sandbox.AddLabel(accession, finalDirection: "Positive");
            sandbox.AddRecord(accession, ShadowCohort.NoSignal, direction: "Improving", confidence: 0.9m);
        }

        sandbox.WriteAll();
        var (exit, report, _) = sandbox.Run();

        Assert.NotEqual(0, exit);
        Assert.Contains("SHADOW: INCONCLUSIVE (denominator drift", OutcomeLine(report), StringComparison.Ordinal);
        Assert.Contains("33/57/145", OutcomeLine(report), StringComparison.Ordinal);
    }
}
