using System.Text.RegularExpressions;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Source guardrail for spec 211: <c>SignalTypeDisplay</c> is the ONLY <c>SignalType</c>-to-label site
/// reachable from the weekly report. The failure mode this exists for is a second, private copy of the
/// mapping — exactly what let the spec-210 floor rationale print the stored <c>GuidanceChange</c> /
/// <c>InsiderBuying</c> tokens on live reports while the renderer relabelled them: the renderer owned a
/// private <c>DisplaySignalType</c>, the policy never saw it. A second copy must now fail at test time.
/// Modelled on <c>EfficacyReadOnlyGuardrailTests</c> (a source scan located from the test assembly).
/// </summary>
public sealed class SignalTypeDisplayGuardrailTests
{
    // The two files that print signal types to the reader. Anything else that starts stringifying a
    // SignalType for the report should be added here.
    private static readonly string[] GuardedFiles =
    [
        Path.Combine("src", "Radar.Application", "Reporting", "MarkdownWeeklyReportRenderer.cs"),
        Path.Combine("src", "Radar.Application", "Reporting", "WeeklyReportActionPolicyV1.cs"),
    ];

    // Every pattern below is scanned against NON-COMMENT source lines only (see StripComments), so a
    // comment that merely EXPLAINS the seam ("routes through SignalTypeDisplay", "the stored InsiderBuying
    // token") can never trip it. Each carries the reason it exists.
    private static readonly (string Name, Regex Pattern, string Why)[] ForbiddenShapes =
    [
        (
            "switch arm over a SignalType member",
            new Regex(@"\bSignalType\.\w+\s*=>", RegexOptions.CultureInvariant),
            "a `SignalType.X => ...` arm is how a private mapping is written (the deleted DisplaySignalType "
                + "was one); the only such switch lives in SignalTypeDisplay"),
        (
            "the EarningsTrajectory label as a string literal",
            // The lookbehind/lookahead keep an ESCAPED quote (\"EarningsTrajectory\" inside the legend
            // prose) from matching: only an exact string literal equal to the label is a mapping.
            new Regex(@"(?<!\\)""EarningsTrajectory""(?!\\)", RegexOptions.CultureInvariant),
            "the label text belongs to SignalTypeDisplay.Label alone; a literal here is a second copy"),
        (
            "the InsiderActivity label as a string literal",
            new Regex(@"(?<!\\)""InsiderActivity""(?!\\)", RegexOptions.CultureInvariant),
            "the label text belongs to SignalTypeDisplay.Label alone; a literal here is a second copy"),
        (
            "the stored InsiderBuying token in code",
            new Regex(@"InsiderBuying", RegexOptions.CultureInvariant),
            "a Regex/Replace over the stored token (the deleted StoredInsiderTypeToken was one) or a "
                + "SignalType.InsiderBuying reference is a second provenance-rewrite site; the renderer's "
                + "legend deliberately never names the stored token either"),
        (
            "interpolation of a grouping key or a .Type member",
            new Regex(@"\{\s*\w+\.(Key|Type)\s*[\}:]", RegexOptions.CultureInvariant),
            "`$\"{g.Key} ...\"` was the exact v3 defect: interpolating the stored enum prints its stored name"),
        (
            "ToString()/Append on a .Type or .Key member",
            new Regex(@"\.(Type|Key)\.ToString\(|Append\(\s*\w+\.(Type|Key)\s*\)", RegexOptions.CultureInvariant),
            "an explicit stringification of the stored enum bypasses the seam just as interpolation does"),
        (
            "Enum.GetName over a SignalType",
            new Regex(@"Enum\.GetName\s*(<\s*SignalType\s*>)?\s*\(\s*(typeof\s*\(\s*SignalType\s*\)|\w+\.(Type|Key))",
                RegexOptions.CultureInvariant),
            "the reflection route to the stored name is still the stored name"),
    ];

    [Fact]
    public void RendererAndPolicy_ContainNoSignalTypeToStringSite_OtherThanSignalTypeDisplay()
    {
        var root = LocateRepositoryRoot();

        foreach (var relative in GuardedFiles)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"Expected guarded source file at {path}.");

            var code = StripComments(File.ReadAllLines(path));

            foreach (var (name, pattern, why) in ForbiddenShapes)
            {
                var offending = code
                    .Where(line => pattern.IsMatch(line.Text))
                    .Select(line => $"L{line.Number}: {line.Text.Trim()}")
                    .ToList();

                Assert.True(
                    offending.Count == 0,
                    $"{Path.GetFileName(path)} contains {name} — {why}. Route it through SignalTypeDisplay "
                        + "(spec 211). Offending line(s): " + string.Join(" | ", offending));
            }
        }
    }

    [Fact]
    public void RendererAndPolicy_BothRouteThroughSignalTypeDisplay_SoTheGuardIsNotVacuous()
    {
        // The positive half: if either file stopped calling the seam (say, by dropping the relabel
        // altogether), the negative scan above would still pass while proving nothing.
        var root = LocateRepositoryRoot();

        foreach (var relative in GuardedFiles)
        {
            var code = StripComments(File.ReadAllLines(Path.Combine(root, relative)));
            Assert.True(
                code.Any(line => line.Text.Contains("SignalTypeDisplay.", StringComparison.Ordinal)),
                $"{Path.GetFileName(relative)} no longer calls SignalTypeDisplay — the report's signal-type "
                    + "labels must come from the ONE shared seam (spec 211).");
        }
    }

    [Fact]
    public void TheScanner_FlagsTheExactShapesThatShippedTheDefect_AndIgnoresComments()
    {
        // Self-test, so a pattern regression cannot silently turn the guard into a no-op: the v3 defect
        // shape, the deleted private-mapping shapes and the deleted regex must all be flagged; the
        // renderer's escaped-quote legend line and explanatory comments must not.
        string[] mustFlag =
        [
            "positiveByType.Select(g => $\"{g.Key} ({DescribeSupport(g)})\"));",
            "SignalType.GuidanceChange => \"EarningsTrajectory\",",
            "SignalType.InsiderBuying => \"InsiderActivity\",",
            "new(@\"\\bInsiderBuying\\b\", RegexOptions.Compiled | RegexOptions.CultureInvariant);",
            ".Append(signal.Type)",
            "var name = signal.Type.ToString();",
            "sb.Append($\"{signal.Type}: \");",
        ];
        foreach (var bad in mustFlag)
        {
            Assert.True(
                ForbiddenShapes.Any(shape => shape.Pattern.IsMatch(bad)),
                $"The guard failed to flag a known-bad shape: {bad}");
        }

        string[] mustNotFlag =
        [
            "sb.Append(\"> \\\"InsiderActivity\\\" rows are SEC Form 4 insider filings of any kind; a Neutral row is \")",
            ".Append(SignalTypeDisplay.Label(signal.Type))",
            "positiveByType.Select(g => $\"{SignalTypeDisplay.Label(g.Key)} ({DescribeSupport(g)})\"));",
            "var reason = SignalTypeDisplay.RewriteStoredProvenance(signal.Reason.Trim());",
            ".GroupBy(s => s.Type)",
            ".OrderBy(g => g.Key)",
        ];
        foreach (var good in mustNotFlag)
        {
            var hit = ForbiddenShapes.FirstOrDefault(shape => shape.Pattern.IsMatch(good));
            Assert.True(hit.Pattern is null, $"The guard wrongly flags a sanctioned line as '{hit.Name}': {good}");
        }

        // Comment handling: a whole-line comment naming the stored token and a trailing comment after code
        // are both dropped; a `//` inside a string literal is NOT treated as a comment.
        var stripped = StripComments(
        [
            "    // the stored InsiderBuying token is explained here",
            "    /// <c>SignalType.InsiderBuying</c> => \"InsiderActivity\" in prose",
            "    var x = 1; // InsiderBuying trailing",
            "    var url = \"https://example/InsiderBuying\";",
        ]);
        Assert.Equal(2, stripped.Count);
        Assert.Equal("    var x = 1; ", stripped[0].Text);
        Assert.Equal("    var url = \"https://example/InsiderBuying\";", stripped[1].Text);
    }

    /// <summary>
    /// Drops whole-line <c>//</c> and <c>///</c> comments and trailing <c>//</c> comments on code lines,
    /// keeping a <c>//</c> that sits inside a string literal (a URL) intact. Block comments are not
    /// used in either guarded file; a scan is over lines, not tokens, by design (same as the efficacy
    /// guardrail) — the goal is to fail on the shapes above, not to parse C#.
    /// </summary>
    private static IReadOnlyList<(int Number, string Text)> StripComments(IReadOnlyList<string> lines)
    {
        var result = new List<(int, string)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var marker = line.IndexOf("//", StringComparison.Ordinal);
            if (marker >= 0 && !line[..marker].Contains('"'))
            {
                line = line[..marker];
            }

            result.Add((i + 1, line));
        }

        return result;
    }

    private static string LocateRepositoryRoot()
    {
        // Walk up from the test assembly's base directory to the repo root (the folder holding Radar.sln).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
