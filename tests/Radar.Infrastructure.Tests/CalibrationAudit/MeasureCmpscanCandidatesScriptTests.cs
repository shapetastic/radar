using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 165: end-to-end tests of <c>scripts/calibration-audit/measure-cmpscan-candidates.ps1</c>. Each test
/// copies the REAL script into a temp sandbox beside its own fixture exhibits / manifest / labels / mapping
/// (the script takes every path as a parameter, so the sandbox owns all of them). Runs under <c>pwsh</c>
/// when available, falling back to Windows PowerShell 5.1; if neither host exists the tests FAIL (never
/// skip — CI always has pwsh).
/// </summary>
public sealed class MeasureCmpscanCandidatesScriptTests : IDisposable
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
            "Neither 'pwsh' nor 'powershell' was found on PATH. The measure-cmpscan-candidates.ps1 tests "
                + "require a PowerShell host; CI provides pwsh, Windows provides powershell — this is a failure, not a skip.");
    });

    internal static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        return dir
            ?? throw new InvalidOperationException("Could not locate the repo root (Radar.sln) above " + AppContext.BaseDirectory);
    }

    private static string RepoScriptPath() =>
        Path.Combine(RepoRoot().FullName, "scripts", "calibration-audit", "measure-cmpscan-candidates.ps1");

    /// <summary>
    /// PowerShell's error formatter HARD-WRAPS a long message at the (redirected) host width, so a thrown
    /// message can be split mid-token. Every assertion against a failure path therefore compares
    /// whitespace-stripped text: wrapping only ever inserts whitespace, so removing all of it rejoins the
    /// token without weakening the assertion.
    /// </summary>
    private static string StripWhitespace(string text) => Regex.Replace(text, @"\s", string.Empty);

    // ---------------------------------------------------------------- fixture corpus
    //
    // Five filings. Bodies are deliberately single-spaced (so the whitespace collapse is the identity) and
    // contain no candidate/v1 phrase other than the ones named here.
    //
    //   F1  labeled, "acquisitions" + "impairment"  concept YES  break   -> TP for acq-02, v1 hit (overlap)
    //   F2  labeled, "acquisitions"                 concept NO   CLEAN   -> FP for acq-02 (context listed),
    //                                                                       novel-vs-v1, lowers any-break
    //   F3  labeled, no phrase at all               concept YES  break   -> FN for acq-02 (item listed)
    //   F4  UNLABELED, "acquisitions"               n/a          n/a     -> hit rate only
    //   F5  labeled, "impairment"                   concept NO   CLEAN   -> v1-only hit, lowers v1 any-break

    private const string F1 = "0000000001-26-000001";
    private const string F2 = "0000000001-26-000002";
    private const string F3 = "0000000001-26-000003";
    private const string F4 = "0000000001-26-000004";
    private const string F5 = "0000000001-26-000005";

    private const string F2Prefix =
        "Fixture Beta Corporation today announced results for the fiscal period and reviewed its plans in "
        + "detail with investors before turning to the outlook, where management noted that ";

    private const string F2Suffix =
        " remain a stated part of the long term capital allocation framework, alongside dividends, buybacks "
        + "and organic investment across the reporting segments of the company.";

    private const string F2Body = F2Prefix + "acquisitions" + F2Suffix;

    /// <summary>The exact ±80-character context the script must print for F2's false positive.</summary>
    private static string ExpectedF2Context() =>
        "..." + F2Prefix[^80..] + "acquisitions" + F2Suffix[..80] + "...";

    private const string F1Body =
        "Fixture Alpha Corporation reported revenue growth for the period, helped by a completed programme of "
        + "bolt-on acquisitions in the industrial segment, and recorded a goodwill impairment charge against "
        + "the legacy reporting unit during the same quarter.";

    private const string F3Body =
        "Fixture Gamma Corporation reported flat revenue and steady margins for the period with no unusual "
        + "items disclosed anywhere in this release body text.";

    private const string F4Body =
        "Fixture Delta Corporation completed several bolt-on acquisitions during the year, which management "
        + "expects to contribute to growth in the coming periods.";

    private const string F5Body =
        "Fixture Epsilon Corporation recorded a non-cash impairment charge on an equity method investment "
        + "during the quarter, with no other unusual items in the period.";

    private sealed class Sandbox : IDisposable
    {
        private const string ManifestHeader =
            "accession,ticker,cik,documentFileName,documentType,exhibitUrl,fullTextSha256,fullTextLength,"
            + "modelInputSha256,modelInputLength,truncated,maxInputLength,outcome,fetchedAtUtc";

        private readonly List<string> _manifestRows = [];
        private readonly List<string> _labelLines = [];
        private readonly List<string> _mappingRows = [];

        public Sandbox()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(ExhibitsDir);
        }

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "radar-cmpscan-script-" + Guid.NewGuid().ToString("N"));

        public string ExhibitsDir => Path.Combine(Dir, "exhibits-full");

        public string ManifestPath => Path.Combine(Dir, "exhibit-manifest.csv");

        public string LabelsPath => Path.Combine(Dir, "labels.jsonl");

        public string MappingPath => Path.Combine(Dir, "mapping.csv");

        public string OutCsvPath => Path.Combine(Dir, "hits.csv");

        public string OutFilePath => Path.Combine(Dir, "summary.md");

        public string ExhibitPath(string ticker, string accession) =>
            Path.Combine(ExhibitsDir, $"{ticker}-{accession}.txt");

        /// <summary>
        /// Writes the exhibit as BOM-less UTF-8 and pins the manifest hash to the bytes actually written —
        /// the fixture manifest is built FROM the fixture text, never hand-copied.
        /// </summary>
        public void AddExhibit(string accession, string ticker, string body, string outcome = "success", string? forcedHash = null)
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(body);
            File.WriteAllBytes(ExhibitPath(ticker, accession), bytes);
            var hash = forcedHash ?? Convert.ToHexStringLower(SHA256.HashData(bytes));
            _manifestRows.Add(string.Join(",",
                accession, ticker, "123", "ex991.htm", "EX-99.1", "https://example.test/ex991.htm",
                hash, bytes.Length.ToString(CultureInfo.InvariantCulture), hash,
                bytes.Length.ToString(CultureInfo.InvariantCulture), "false", "12000", outcome,
                "2026-07-01T00:00:00.0000000Z"));
        }

        public void AddLabel(string accession, string ticker, bool comparisonClean, params string[] items)
        {
            var root = new Dictionary<string, object?>
            {
                ["accession"] = accession,
                ["ticker"] = ticker,
                ["cik"] = "123",
                ["protocol"] = new Dictionary<string, object?> { ["version"] = "cal-v2", ["attempt"] = 1 },
                ["label"] = new Dictionary<string, object?>
                {
                    ["direction"] = "Positive",
                    ["directionConfidence"] = 0.8,
                    ["comparisonClean"] = comparisonClean,
                    ["comparabilityItems"] = items,
                    ["material"] = "moderate",
                },
            };
            _labelLines.Add(JsonSerializer.Serialize(root));
        }

        public void AddMappingRow(string accession, string ticker, string item, string categories) =>
            _mappingRows.Add(string.Join(",",
                Quote(accession), Quote(ticker), Quote(item), Quote(categories)));

        private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        public void WriteAll()
        {
            File.Copy(RepoScriptPath(), Path.Combine(Dir, "measure-cmpscan-candidates.ps1"), overwrite: true);
            File.WriteAllText(ManifestPath, ManifestHeader + "\n" + string.Join("\n", _manifestRows) + "\n");
            File.WriteAllText(LabelsPath, string.Join("\n", _labelLines) + (_labelLines.Count > 0 ? "\n" : string.Empty));
            File.WriteAllText(MappingPath,
                "\"accession\",\"ticker\",\"item\",\"categories\"\n"
                + (_mappingRows.Count > 0 ? string.Join("\n", _mappingRows) + "\n" : string.Empty));
        }

        public (int ExitCode, string StdOut, string StdErr) Run(string? outCsv = null, bool withOutFile = false)
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
            psi.ArgumentList.Add(Path.Combine(Dir, "measure-cmpscan-candidates.ps1"));
            psi.ArgumentList.Add("-ExhibitsDir");
            psi.ArgumentList.Add(ExhibitsDir);
            psi.ArgumentList.Add("-ManifestPath");
            psi.ArgumentList.Add(ManifestPath);
            psi.ArgumentList.Add("-LabelsPath");
            psi.ArgumentList.Add(LabelsPath);
            psi.ArgumentList.Add("-MappingPath");
            psi.ArgumentList.Add(MappingPath);
            if (outCsv is not null)
            {
                psi.ArgumentList.Add("-OutCsv");
                psi.ArgumentList.Add(outCsv);
            }

            if (withOutFile)
            {
                psi.ArgumentList.Add("-OutFile");
                psi.ArgumentList.Add(OutFilePath);
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start " + HostExe.Value);
            var stdOut = process.StandardOutput.ReadToEndAsync();
            var stdErr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("measure-cmpscan-candidates.ps1 did not finish within 180s.");
            }

            process.WaitForExit();
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

    private Sandbox CreateFixture()
    {
        var sandbox = NewSandbox();
        sandbox.AddExhibit(F1, "aaa", F1Body);
        sandbox.AddExhibit(F2, "bbb", F2Body);
        sandbox.AddExhibit(F3, "ccc", F3Body);
        sandbox.AddExhibit(F4, "ddd", F4Body);
        sandbox.AddExhibit(F5, "eee", F5Body);

        sandbox.AddLabel(F1, "aaa", comparisonClean: false, "Bolt-on purchases inflate the segment comparison");
        sandbox.AddLabel(F2, "bbb", comparisonClean: true, "A fixture item the taxonomy does not categorise");
        sandbox.AddLabel(F3, "ccc", comparisonClean: false, "Perimeter change: fixture subsidiary purchase inflates growth");
        // F4 is deliberately UNLABELED.
        sandbox.AddLabel(F5, "eee", comparisonClean: true);

        sandbox.AddMappingRow(F1, "aaa", "Bolt-on purchases inflate the segment comparison", "acquisition-divestiture-perimeter");
        sandbox.AddMappingRow(F2, "bbb", "A fixture item the taxonomy does not categorise", "uncategorized");
        sandbox.AddMappingRow(F3, "ccc", "Perimeter change: fixture subsidiary purchase inflates growth", "acquisition-divestiture-perimeter");
        return sandbox;
    }

    // ---------------------------------------------------------------- helpers over the rendered report

    private static string RowFor(string report, string candidateId)
    {
        var line = report
            .Split('\n')
            .Select(static l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.StartsWith("| " + candidateId + " |", StringComparison.Ordinal));
        Assert.NotNull(line);
        return line!;
    }

    private static string SectionOf(string report, string startHeading, string endHeading)
    {
        var start = report.IndexOf(startHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing section '{startHeading}' in:\n{report}");
        var end = report.IndexOf(endHeading, start + startHeading.Length, StringComparison.Ordinal);
        return end < 0 ? report[start..] : report[start..end];
    }

    // ---------------------------------------------------------------- hit counting + FP context

    [Fact]
    public void MatchingCandidate_IsCounted_AndItsFalsePositiveListingCarriesTheMatchedContext()
    {
        var sandbox = CreateFixture();
        sandbox.WriteAll();

        var (exit, stdOut, stdErr) = sandbox.Run();
        Assert.True(exit == 0, $"expected success; stdout: {stdOut}\nstderr: {stdErr}");

        // `acquisitions` fires on F1, F2 and F4 (three of the five scanned filings).
        Assert.Contains("| 3/5 | 0.600 |", RowFor(stdOut, "acq-02"), StringComparison.Ordinal);

        // The FP listing names F2 and carries the ±80-character context around the match.
        var section = SectionOf(stdOut, "### acq-02 - `acquisitions`", "### acq-03");
        Assert.Contains("False positives: 1.", section, StringComparison.Ordinal);
        Assert.Contains(F2 + ": " + ExpectedF2Context(), section, StringComparison.Ordinal);
    }

    [Fact]
    public void PrecisionRecallF1_AreComputedAgainstTheFixtureReference_AndAConceptFilingWithNoHitIsListedAsAFalseNegative()
    {
        var sandbox = CreateFixture();
        sandbox.WriteAll();

        var (exit, stdOut, stdErr) = sandbox.Run();
        Assert.True(exit == 0, $"expected success; stdout: {stdOut}\nstderr: {stdErr}");

        // TP = F1, FP = F2, FN = F3 ⇒ precision 1/2, recall 1/2, F1 0.500.
        var row = RowFor(stdOut, "acq-02");
        Assert.Contains("| 1 | 1 | 1 |", row, StringComparison.Ordinal);
        Assert.Contains("1/2 = 0.500 (Wilson 95%: 0.095-0.905)", row, StringComparison.Ordinal);
        Assert.Contains("| 0.500 |", row, StringComparison.Ordinal);

        // F3 has the concept but no phrase hit ⇒ an FN, listed with the label item that mapped to it.
        var section = SectionOf(stdOut, "### acq-02 - `acquisitions`", "### acq-03");
        Assert.Contains("False negatives: 1.", section, StringComparison.Ordinal);
        Assert.Contains(F3 + ": Perimeter change: fixture subsidiary purchase inflates growth", section, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- provenance

    [Fact]
    public void TamperedExhibit_FailsNamingTheFile()
    {
        var sandbox = CreateFixture();
        sandbox.WriteAll();

        // Edit the archived body AFTER the manifest pinned its hash.
        File.WriteAllText(sandbox.ExhibitPath("bbb", F2), F2Body + " tampered.");

        var (exit, stdOut, stdErr) = sandbox.Run();

        Assert.NotEqual(0, exit);
        var combined = StripWhitespace(stdOut + "\n" + stdErr);
        Assert.Contains(StripWhitespace("Exhibit hash mismatch"), combined, StringComparison.Ordinal);
        Assert.Contains(StripWhitespace($"bbb-{F2}.txt"), combined, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingNotCoveringTheLabeledPopulation_FailsNamingTheRemedy()
    {
        // The spec-162 failure mode: a mapping generated over a NARROWER cohort. F3 records a comparability
        // item but has no mapping row, which is only possible if the mapping came from another cohort.
        var sandbox = NewSandbox();
        sandbox.AddExhibit(F1, "aaa", F1Body);
        sandbox.AddExhibit(F3, "ccc", F3Body);
        sandbox.AddLabel(F1, "aaa", comparisonClean: false, "Bolt-on purchases inflate the segment comparison");
        sandbox.AddLabel(F3, "ccc", comparisonClean: false, "Perimeter change: fixture subsidiary purchase inflates growth");
        sandbox.AddMappingRow(F1, "aaa", "Bolt-on purchases inflate the segment comparison", "acquisition-divestiture-perimeter");
        sandbox.WriteAll();

        var (exit, stdOut, stdErr) = sandbox.Run();

        Assert.NotEqual(0, exit);
        var combined = StripWhitespace(stdOut + "\n" + stdErr);
        Assert.Contains(StripWhitespace("does not cover the labeled population"), combined, StringComparison.Ordinal);
        Assert.Contains(StripWhitespace("-Cohort all-labeled"), combined, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- cohort discipline

    [Fact]
    public void UnlabeledFilings_ContributeToHitRate_ButNeverToPrecisionOrRecall()
    {
        var sandbox = CreateFixture();
        sandbox.WriteAll();

        var (exit, stdOut, stdErr) = sandbox.Run();
        Assert.True(exit == 0, $"expected success; stdout: {stdOut}\nstderr: {stdErr}");

        Assert.Contains(
            "Labeled filings (concept + any-break reference): 4 of the 5 scanned. Unlabeled (hit rates ONLY): 1.",
            stdOut,
            StringComparison.Ordinal);

        // F4 is one of the three hits, but the precision denominator is 2 (labeled hits only) and the recall
        // denominator is 2 (labeled concept positives only) — the Ns, not merely the absence of a crash.
        var row = RowFor(stdOut, "acq-02");
        Assert.Contains("| 3/5 |", row, StringComparison.Ordinal);
        Assert.Contains("1/2 = 0.500", row, StringComparison.Ordinal);
        Assert.DoesNotContain("1/3 =", row, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- v1 baseline

    [Fact]
    public void V1BaselineRow_RendersHitRateAnyBreakPrecisionAndOverlap_ButNoConceptPrecisionOrRecall()
    {
        var sandbox = CreateFixture();
        sandbox.WriteAll();

        var (exit, stdOut, stdErr) = sandbox.Run();
        Assert.True(exit == 0, $"expected success; stdout: {stdOut}\nstderr: {stdErr}");

        var baseline = SectionOf(stdOut, "## cmpscan-v1 baseline", "## False positives");

        // The baseline table has EXACTLY these five columns — no concept precision/recall/F1 column exists.
        Assert.Contains("| rule | filings hit | hit rate | labeled hits | any-break precision |", baseline, StringComparison.Ordinal);
        var dataRow = baseline
            .Split('\n')
            .Select(static l => l.TrimEnd('\r'))
            .Single(static l => l.StartsWith("| cmpscan-v1 (", StringComparison.Ordinal));
        Assert.Equal(5, dataRow.Trim('|').Split('|').Length);

        // v1 fires on F1 and F5 (impairment): 2/5. Of its two LABELED hits, F1 is a break and F5 is clean.
        Assert.Contains("| 2/5 | 0.400 | 2 | 1/2 = 0.500 (Wilson 95%: 0.095-0.905) |", dataRow, StringComparison.Ordinal);

        // Overlap is the per-candidate column: `acquisitions` shares F1 with v1 and adds F2 as novel.
        var row = RowFor(stdOut, "acq-02");
        Assert.EndsWith("| 1 | 1 |", row, StringComparison.Ordinal);

        // The baseline never appears as a candidate row in the primary table.
        var primaryTable = SectionOf(stdOut, "## Primary candidates", "## cmpscan-v1 baseline");
        Assert.DoesNotContain("cmpscan-v1 (", primaryTable, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- any-break reference

    [Fact]
    public void AnyBreakPrecision_IsComputedAgainstComparisonCleanFalse_AndACleanLabeledHitLowersIt()
    {
        var sandbox = CreateFixture();
        sandbox.WriteAll();
        var withCleanHit = sandbox.Run();
        Assert.True(withCleanHit.ExitCode == 0, withCleanHit.StdErr);

        Assert.Contains(
            "ANY-BREAK reference (`label.comparisonClean = false`): 2 break / 2 clean / 0 not recorded.",
            withCleanHit.StdOut,
            StringComparison.Ordinal);

        // F1 (break) + F2 (clean) are the labeled hits ⇒ 1/2.
        Assert.Contains("1/2 = 0.500", RowFor(withCleanHit.StdOut, "acq-02"), StringComparison.Ordinal);

        // Flip F2's label to a BREAK and the same hits now score 2/2: the rate is driven by comparisonClean,
        // nothing else about the filing changed.
        var flipped = NewSandbox();
        flipped.AddExhibit(F1, "aaa", F1Body);
        flipped.AddExhibit(F2, "bbb", F2Body);
        flipped.AddExhibit(F3, "ccc", F3Body);
        flipped.AddExhibit(F4, "ddd", F4Body);
        flipped.AddExhibit(F5, "eee", F5Body);
        flipped.AddLabel(F1, "aaa", comparisonClean: false, "Bolt-on purchases inflate the segment comparison");
        flipped.AddLabel(F2, "bbb", comparisonClean: false, "A fixture item the taxonomy does not categorise");
        flipped.AddLabel(F3, "ccc", comparisonClean: false, "Perimeter change: fixture subsidiary purchase inflates growth");
        flipped.AddLabel(F5, "eee", comparisonClean: true);
        flipped.AddMappingRow(F1, "aaa", "Bolt-on purchases inflate the segment comparison", "acquisition-divestiture-perimeter");
        flipped.AddMappingRow(F2, "bbb", "A fixture item the taxonomy does not categorise", "uncategorized");
        flipped.AddMappingRow(F3, "ccc", "Perimeter change: fixture subsidiary purchase inflates growth", "acquisition-divestiture-perimeter");
        flipped.WriteAll();

        var allBreaks = flipped.Run();
        Assert.True(allBreaks.ExitCode == 0, allBreaks.StdErr);
        Assert.Contains("2/2 = 1.000", RowFor(allBreaks.StdOut, "acq-02"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- hit-matrix CSV

    [Fact]
    public void HitMatrixCsv_IsEmittedWithTheExpectedRows()
    {
        var sandbox = CreateFixture();
        sandbox.WriteAll();

        var (exit, stdOut, stdErr) = sandbox.Run(outCsv: sandbox.OutCsvPath);
        Assert.True(exit == 0, $"expected success; stdout: {stdOut}\nstderr: {stdErr}");
        Assert.True(File.Exists(sandbox.OutCsvPath));

        var lines = File.ReadAllLines(sandbox.OutCsvPath);
        Assert.Equal(
            "\"candidateId\",\"literal\",\"concept\",\"rowKind\",\"accession\",\"ticker\",\"labeled\",\"hasConcept\",\"anyBreak\",\"v1Hit\",\"matched\"",
            lines[0].TrimStart('﻿'));

        var acq02 = lines.Where(static l => l.StartsWith("\"acq-02\",", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, acq02.Count);
        Assert.Equal(
            $"\"acq-02\",\"acquisitions\",\"acquisition-divestiture-perimeter\",\"primary\",\"{F1}\",\"aaa\",\"True\",\"True\",\"True\",\"True\",\"acquisitions\"",
            acq02[0]);
        Assert.Equal(
            $"\"acq-02\",\"acquisitions\",\"acquisition-divestiture-perimeter\",\"primary\",\"{F2}\",\"bbb\",\"True\",\"False\",\"False\",\"False\",\"acquisitions\"",
            acq02[1]);
        // The unlabeled filing carries empty reference cells — never a fabricated False.
        Assert.Equal(
            $"\"acq-02\",\"acquisitions\",\"acquisition-divestiture-perimeter\",\"primary\",\"{F4}\",\"ddd\",\"False\",\"\",\"\",\"False\",\"acquisitions\"",
            acq02[2]);

        var baseline = lines.Where(static l => l.StartsWith("\"cmpscan-v1\",", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, baseline.Count);
        Assert.Contains(F1, baseline[0], StringComparison.Ordinal);
        Assert.Contains("\"baseline\"", baseline[0], StringComparison.Ordinal);
        Assert.Contains("\"impairment\"", baseline[0], StringComparison.Ordinal);
        Assert.Contains(F5, baseline[1], StringComparison.Ordinal);

        Assert.Contains("\"exploratory\"", string.Join('\n', lines), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- determinism

    [Fact]
    public void TwoRunsOverIdenticalInputs_ProduceByteIdenticalCsvAndStdout()
    {
        var sandbox = CreateFixture();
        sandbox.WriteAll();

        var first = sandbox.Run(outCsv: sandbox.OutCsvPath);
        Assert.True(first.ExitCode == 0, first.StdErr);
        var firstCsv = File.ReadAllBytes(sandbox.OutCsvPath);

        var second = sandbox.Run(outCsv: sandbox.OutCsvPath);
        Assert.True(second.ExitCode == 0, second.StdErr);
        var secondCsv = File.ReadAllBytes(sandbox.OutCsvPath);

        Assert.Equal(firstCsv, secondCsv);
        Assert.Equal(first.StdOut, second.StdOut);
    }

    // ---------------------------------------------------------------- promotion rule

    [Fact]
    public void PromotionRule_IsRenderedVerbatim_AndOnlyPrimaryRowsAreEvaluated()
    {
        var sandbox = CreateFixture();
        sandbox.WriteAll();

        var (exit, stdOut, stdErr) = sandbox.Run(withOutFile: true);
        Assert.True(exit == 0, $"expected success; stdout: {stdOut}\nstderr: {stdErr}");

        var decisions = SectionOf(stdOut, "## Decisions", "## Standing caveats");
        Assert.Contains("concept precision >= 0.80 AND concept recall >= 0.30", decisions, StringComparison.Ordinal);
        Assert.Contains(">= 5 labeled filings where cmpscan-v1 did not", decisions, StringComparison.Ordinal);
        Assert.Contains("RESULT: **no candidate passes the precommitted rule.**", decisions, StringComparison.Ordinal);

        // Every exploratory id is absent from the decisions table: exclusion is structural, not documentary.
        foreach (var exploratoryId in new[] { "x-01", "x-02", "x-03", "x-04", "x-05" })
        {
            Assert.DoesNotContain("| " + exploratoryId + " |", decisions, StringComparison.Ordinal);
        }

        // -OutFile carries the same markdown as stdout.
        var written = File.ReadAllText(sandbox.OutFilePath);
        Assert.Contains("## Decisions - the PRECOMMITTED promotion rule, applied verbatim", written, StringComparison.Ordinal);
        Assert.DoesNotContain("hit matrix written:", written, StringComparison.Ordinal);
    }
}
