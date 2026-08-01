using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 165: tests of <c>scripts/calibration-audit/categorize-comparability.ps1</c>'s cohort switch. The
/// default (<c>directional</c>) cohort must keep reproducing the COMMITTED spec-162 artifact — that artifact
/// and its 145-filing numbers are cited by the Phase B findings, so the switch is only safe if the default
/// path did not move. <c>all-labeled</c> adds the no-signal filings, which is what the spec-165 concept
/// reference needs. Runs under <c>pwsh</c> when available, falling back to Windows PowerShell 5.1; if
/// neither host exists the tests FAIL (never skip — CI always has pwsh).
/// </summary>
public sealed class CategorizeComparabilityScriptTests : IDisposable
{
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
            "Neither 'pwsh' nor 'powershell' was found on PATH. The categorize-comparability.ps1 tests require "
                + "a PowerShell host; CI provides pwsh, Windows provides powershell — this is a failure, not a skip.");
    });

    // The SAME repo-root discovery the sibling script tests use (Radar.sln above the test output directory).
    private static string RepoScriptPath() =>
        Path.Combine(MeasureCmpscanCandidatesScriptTests.RepoRoot().FullName, "scripts", "calibration-audit", "categorize-comparability.ps1");

    private static string RepoDocPath(string fileName) =>
        Path.Combine(MeasureCmpscanCandidatesScriptTests.RepoRoot().FullName, "docs", fileName);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "radar-categorize-script-" + Guid.NewGuid().ToString("N"));

    public CategorizeComparabilityScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private (int ExitCode, string StdOut, string StdErr) Run(
        string scriptPath, string labelsPath, string worksheetPath, string outFile, string? cohort)
    {
        var psi = new ProcessStartInfo(HostExe.Value)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _dir,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-LabelsPath");
        psi.ArgumentList.Add(labelsPath);
        psi.ArgumentList.Add("-WorksheetPath");
        psi.ArgumentList.Add(worksheetPath);
        psi.ArgumentList.Add("-OutFile");
        psi.ArgumentList.Add(outFile);
        if (cohort is not null)
        {
            psi.ArgumentList.Add("-Cohort");
            psi.ArgumentList.Add(cohort);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start " + HostExe.Value);
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("categorize-comparability.ps1 did not finish within 180s.");
        }

        process.WaitForExit();
        return (process.ExitCode, stdOut.Result, stdErr.Result);
    }

    /// <summary>
    /// UTF-8 decode, strip a BOM, normalize CRLF→LF. <b>Raw bytes are deliberately NOT compared:</b>
    /// <c>Export-Csv -Encoding UTF8</c> emits a BOM under Windows PowerShell 5.1 and no BOM under pwsh 7,
    /// and the two hosts also differ on line endings, so a byte comparison would pass on the maintainer's
    /// machine and fail in CI purely because of the host. The CONTENT is what the spec-162 artifact pins.
    /// </summary>
    private static string NormalizedContent(string path)
    {
        var text = new UTF8Encoding(false).GetString(File.ReadAllBytes(path));
        return text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultCohort_ReproducesTheCommittedSpec162Mapping()
    {
        var committed = RepoDocPath("162-comparability-item-mapping.csv");
        Assert.True(File.Exists(committed), "the committed spec-162 mapping artifact is missing: " + committed);

        var outFile = Path.Combine(_dir, "regenerated.csv");
        var (exit, stdOut, stdErr) = Run(
            RepoScriptPath(),
            RepoDocPath("162-calibration-labels-full.jsonl"),
            RepoDocPath("162-study-worksheet.csv"),
            outFile,
            cohort: null);

        Assert.True(exit == 0, $"expected success; stdout: {stdOut}\nstderr: {stdErr}");
        Assert.Contains("cohort: directional", stdOut, StringComparison.Ordinal);
        Assert.Equal(NormalizedContent(committed), NormalizedContent(outFile));
    }

    [Fact]
    public void AllLabeledCohort_IncludesNoSignalFilings_ThatTheDirectionalCohortExcludes()
    {
        const string DirectionalAccession = "0000000001-26-000001";
        const string NoSignalAccession = "0000000000-26-000002";

        var labelsPath = Path.Combine(_dir, "labels.jsonl");
        var worksheetPath = Path.Combine(_dir, "worksheet.csv");
        File.WriteAllText(worksheetPath,
            "accession,accessionSha256,ticker,cik,companyName,outcome,signalType,direction,confidence,strength\n"
            + $"{DirectionalAccession},aa,DIR,1,DirCo,DirectionalSignalProduced,EarningsRelease,Positive,0.85,2\n"
            + $"{NoSignalAccession},bb,NOS,2,NoSigCo,NoDirectionalSignal,,,,\n");
        File.WriteAllText(labelsPath,
            Label(DirectionalAccession, "DIR", "Acquisition of a subsidiary inflates the comparison") + "\n"
            + Label(NoSignalAccession, "NOS", "Divestiture of the legacy segment removes revenue") + "\n");

        var directionalOut = Path.Combine(_dir, "directional.csv");
        var directional = Run(RepoScriptPath(), labelsPath, worksheetPath, directionalOut, cohort: "directional");
        Assert.True(directional.ExitCode == 0, directional.StdErr);
        Assert.Contains("cohort: directional (1 filings)", directional.StdOut, StringComparison.Ordinal);
        var directionalCsv = NormalizedContent(directionalOut);
        Assert.Contains(DirectionalAccession, directionalCsv, StringComparison.Ordinal);
        Assert.DoesNotContain(NoSignalAccession, directionalCsv, StringComparison.Ordinal);

        var allOut = Path.Combine(_dir, "all.csv");
        var all = Run(RepoScriptPath(), labelsPath, worksheetPath, allOut, cohort: "all-labeled");
        Assert.True(all.ExitCode == 0, all.StdErr);
        Assert.Contains("cohort: all-labeled (2 filings)", all.StdOut, StringComparison.Ordinal);
        var allCsv = NormalizedContent(allOut);
        Assert.Contains(DirectionalAccession, allCsv, StringComparison.Ordinal);
        Assert.Contains(NoSignalAccession, allCsv, StringComparison.Ordinal);

        // Same taxonomy, same row shape — the cohort selects the population and nothing else.
        Assert.Contains("acquisition-divestiture-perimeter", allCsv, StringComparison.Ordinal);
        foreach (var line in directionalCsv.Split('\n'))
        {
            if (line.Length > 0)
            {
                Assert.Contains(line, allCsv, StringComparison.Ordinal);
            }
        }
    }

    private static string Label(string accession, string ticker, params string[] items) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["accession"] = accession,
            ["ticker"] = ticker,
            ["protocol"] = new Dictionary<string, object?> { ["version"] = "cal-v2", ["attempt"] = 1 },
            ["label"] = new Dictionary<string, object?>
            {
                ["direction"] = "Positive",
                ["comparisonClean"] = false,
                ["comparabilityItems"] = items,
            },
        });
}
