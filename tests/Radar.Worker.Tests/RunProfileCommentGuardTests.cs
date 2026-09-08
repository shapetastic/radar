using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 213 §3: every committed run profile's <c>_comment*</c> strings are STANDING FACTS that cite their
/// owners. The guard enforces exactly three things — no fingerprint pin literal (owner:
/// <c>ScoringConfigFingerprintTests</c>), no "verify the first … run reports" operator imperative, and a
/// length limit (<see cref="DefaultProfileLimit"/> / <see cref="OverlayProfileLimit"/>) so the comment cannot
/// regrow into a second architecture-history file. Per-spec history and every historical pin live verbatim in
/// <c>docs/architecture-history.md</c> ("default.json _comment history"). Pure file/JSON assertions: no Worker
/// is started, nothing is read but the profiles.
/// <para>
/// The predicate is one static method (<see cref="Violation"/>) so the positive controls below prove the guard
/// BITES on each forbidden shape rather than merely passing on the committed files.
/// </para>
/// </summary>
public sealed partial class RunProfileCommentGuardTests
{
    /// <summary>Spec 213 §2: the baseline's comment is at most this long (.NET <c>string.Length</c>).</summary>
    public const int DefaultProfileLimit = 4_000;

    /// <summary>Spec 213 §3: an overlay's comment is at most this long (.NET <c>string.Length</c>).</summary>
    public const int OverlayProfileLimit = 6_000;

    private const string HistoryHome = "docs/architecture-history.md";

    [GeneratedRegex("radar-scoring-fp-[0-9a-f]{12}")]
    private static partial Regex PinLiteral();

    [GeneratedRegex("verify the first .* run reports", RegexOptions.IgnoreCase)]
    private static partial Regex FirstRunImperative();

    [Fact]
    public void EveryProfile_EveryCommentKey_IsStandingFactsOnly()
    {
        var profiles = RunProfilePaths();

        // A broken directory walk-up must not pass vacuously: the repo ships the baseline plus four overlays.
        Assert.True(
            profiles.Count >= 5,
            $"Expected at least 5 run profiles under scripts/run-profiles/, found {profiles.Count} — the "
                + "walk-up from the test binary is broken, and a guard that inspects nothing proves nothing.");

        var inspected = 0;
        var violations = new List<string>();
        foreach (var path in profiles)
        {
            var profile = Path.GetFileName(path);
            var limit = string.Equals(profile, "default.json", StringComparison.OrdinalIgnoreCase)
                ? DefaultProfileLimit
                : OverlayProfileLimit;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var (jsonPath, comment) in CommentStrings(doc.RootElement, "$"))
            {
                inspected++;
                if (Violation(profile, jsonPath, comment, limit) is { } violation)
                {
                    violations.Add(violation);
                }
            }
        }

        Assert.True(inspected > 0, "No _comment* string was found in any profile — the JSON walk is broken.");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Violation_Bites_OnAFingerprintPinLiteral()
    {
        var violation = Violation(
            "fixture.json",
            "$._comment",
            "the AI-ON stamp is radar-scoring-fp-0123456789ab at this window",
            DefaultProfileLimit);

        Assert.NotNull(violation);
        Assert.Contains("radar-scoring-fp-", violation, StringComparison.Ordinal);
        AssertNamesProfilePathSpecAndHistoryHome(violation);
    }

    [Fact]
    public void Violation_Bites_OnAFirstRunOperatorImperative()
    {
        var violation = Violation(
            "fixture.json",
            "$._comment",
            "Verify the first post-999 run reports the new stamp before trusting the series.",
            DefaultProfileLimit);

        Assert.NotNull(violation);
        Assert.Contains("run reports", violation, StringComparison.Ordinal);
        AssertNamesProfilePathSpecAndHistoryHome(violation);
    }

    [Theory]
    [InlineData(DefaultProfileLimit)]
    [InlineData(OverlayProfileLimit)]
    public void Violation_Bites_OneCharacterOverTheLimit(int limit)
    {
        var violation = Violation("fixture.json", "$.Radar._comment2", new string('x', limit + 1), limit);

        Assert.NotNull(violation);
        Assert.Contains((limit + 1).ToString(CultureInfo.InvariantCulture), violation, StringComparison.Ordinal);
        AssertNamesProfilePathSpecAndHistoryHome(violation);
    }

    [Theory]
    [InlineData(DefaultProfileLimit)]
    [InlineData(OverlayProfileLimit)]
    public void Violation_Passes_ACleanCommentExactlyAtTheLimit(int limit)
    {
        Assert.Null(Violation("fixture.json", "$._comment", new string('x', limit), limit));
    }

    [Fact]
    public void Violation_Passes_AStandingFactThatCitesItsOwner()
    {
        // Naming the owner (and the guard's own vocabulary) is exactly what the comment SHOULD do.
        Assert.Null(Violation(
            "fixture.json",
            "$._comment",
            "Live/unit fingerprint pins: ScoringConfigFingerprintTests — never a value quoted in prose. "
                + "RunProfileCommentGuardTests fails the build if this comment regrows a pin literal.",
            DefaultProfileLimit));
    }

    [Fact]
    public void CommentStrings_YieldsEveryCommentKey_NestedInObjectsAndArrays()
    {
        // Proves the WALK bites, not just the predicate: a guard that only inspected the top-level key would
        // still satisfy `inspected > 0` while missing every nested _comment* the profile carries.
        using var doc = JsonDocument.Parse(
            """{"_comment":"top","a":{"_commentX":"nested-object","n":1},"b":[{"_comment":"nested-array"},"str"],"c":"not-a-comment"}""");

        var found = CommentStrings(doc.RootElement, "$").ToList();

        Assert.Equal(
            [("$._comment", "top"), ("$.a._commentX", "nested-object"), ("$.b[0]._comment", "nested-array")],
            found);
    }

    /// <summary>
    /// The spec 213 §3 predicate: <c>null</c> when <paramref name="comment"/> is a clean standing-facts comment,
    /// otherwise ONE message naming the profile, the JSON path of the key, every reason, the spec and where the
    /// history goes.
    /// </summary>
    public static string? Violation(string profile, string jsonPath, string comment, int limit)
    {
        var reasons = new List<string>();

        var pins = PinLiteral().Matches(comment);
        if (pins.Count > 0)
        {
            reasons.Add(
                $"contains {pins.Count} fingerprint pin literal(s) ('{pins[0].Value}', …) — pins are owned by "
                    + "ScoringConfigFingerprintTests and must be cited, never quoted");
        }

        if (FirstRunImperative().IsMatch(comment))
        {
            reasons.Add(
                "contains a 'verify the first … run reports' operator imperative — a one-off boundary step is "
                    + "not a standing fact");
        }

        if (comment.Length > limit)
        {
            reasons.Add($"is {comment.Length} characters, above the {limit}-character limit");
        }

        if (reasons.Count == 0)
        {
            return null;
        }

        return $"{profile}: {jsonPath} {string.Join("; ", reasons)}. Spec 213 §3: a profile comment is standing "
            + $"facts that cite their owners; per-spec history and every historical pin go to {HistoryHome}.";
    }

    private static void AssertNamesProfilePathSpecAndHistoryHome(string violation)
    {
        Assert.Contains("fixture.json", violation, StringComparison.Ordinal);
        Assert.Contains("$.", violation, StringComparison.Ordinal);
        Assert.Contains("Spec 213", violation, StringComparison.Ordinal);
        Assert.Contains(HistoryHome, violation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every <c>_comment*</c>-prefixed string property anywhere in the tree, with its JSON path — objects and
    /// arrays are walked recursively so a comment nested under a strategy or a channel is covered too.
    /// </summary>
    private static IEnumerable<(string Path, string Value)> CommentStrings(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = path + "." + property.Name;
                    if (property.Name.StartsWith("_comment", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return (childPath, property.Value.GetString()!);
                    }

                    foreach (var nested in CommentStrings(property.Value, childPath))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in CommentStrings(item, $"{path}[{index}]"))
                    {
                        yield return nested;
                    }

                    index++;
                }

                break;
        }
    }

    /// <summary>
    /// Every committed run profile, walking up from the test binary to the repo root (the first ancestor
    /// carrying <c>scripts/run-profiles/</c>) — the same idea as <c>DefaultRunProfileTests</c> — so the test
    /// does not depend on the working directory.
    /// </summary>
    private static IReadOnlyList<string> RunProfilePaths()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "run-profiles");
            if (Directory.Exists(candidate))
            {
                return Directory.GetFiles(candidate, "*.json").Order(StringComparer.Ordinal).ToList();
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate scripts/run-profiles/ from " + AppContext.BaseDirectory);
    }
}
