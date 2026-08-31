using System.Text.RegularExpressions;

using Radar.Application.Identity;
using Radar.Application.Storage;
using Radar.Application.Tests.News;

namespace Radar.Application.Tests.Storage;

/// <summary>
/// Spec 202 §§2–3 source-level guards (the spec-201 §3 shape): a durable store write whose outcome is
/// DISCARDED, and a hand-rolled SHA-256 site outside <see cref="CanonicalHash"/>, are both defects that the
/// type graph cannot see — so they are caught by scanning the sources.
/// </summary>
public sealed class DurableWriteSourceGuardTests
{
    private static readonly string[] ProductionProjects =
    [
        "Radar.Domain",
        "Radar.Application",
        "Radar.Infrastructure",
        "Radar.Worker",
    ];

    /// <summary>
    /// A statement that BEGINS with <c>await _xxxStore</c> and then calls a <c>…Write…Async(</c> method —
    /// i.e. the returned outcome is neither assigned, returned nor compared. Multi-line: <c>\s*</c> spans the
    /// newline between <c>await _familyStore</c> and an indented <c>.WriteAsync(</c> continuation. The
    /// callee name is captured so the scan can tell a VALUE-returning write (<c>Task&lt;bool&gt;</c>,
    /// <c>Task&lt;DurableWriteResult&gt;</c>…) from an artifact write that returns a bare <c>Task</c> and
    /// therefore has no outcome to discard.
    /// </summary>
    private static readonly Regex DiscardedStoreWrite = new(
        @"^\s*await\s+_\w+Store\s*\.\s*(?<method>\w*Write\w*Async)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// Every <c>…Write…Async(</c> DECLARATION under src/, split by whether it returns a value. Derived from
    /// the sources rather than hand-listed, so a new value-returning write method is guarded the day it is
    /// declared.
    /// </summary>
    private static readonly Regex WriteDeclaration = new(
        @"\bTask(?<generic><[^;{(]+>)?\s+(?<method>\w*Write\w*Async)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Spec 206 §3 removed the ONE recorded exception this guard used to carry: CollectionPass discarded
    // IRawEvidenceStore.WriteIfNewAsync's bool because that bool conflated "dedupe skip" with "disk
    // failure". The store now returns the typed Written/AlreadyAvailable/Failed outcome and the pass READS
    // it as its admission decision, so the allowlist is empty by construction — every value-returning store
    // write under src/ is checked, with zero exceptions.

    private static readonly Regex Sha256Site = new(
        @"\bSHA256\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string CanonicalHashFile =>
        Path.Combine("Radar.Application", "Identity", "CanonicalHash.cs");

    [Fact]
    public void AlreadyAvailable_IsDurable_ButIsNotWritten()
    {
        var result = DurableWriteResult.AlreadyOnDisk("x/y.json");

        Assert.Equal(DurableWriteOutcome.AlreadyAvailable, result.Outcome);
        Assert.True(result.Written);
        Assert.NotEqual(DurableWriteOutcome.Written, result.Outcome);
        Assert.False(DurableWriteResult.NotPersisted("x/y.json").Written);
    }

    [Fact]
    public void NoStoreWriteOutcome_IsDiscarded_UnderSrc()
    {
        var sources = ProductionSources().ToList();
        var (valueReturning, voidReturning) = WriteMethodNames(sources);

        // Positive controls on the DECLARATION scan: it found the real value-returning writes, and no method
        // NAME is declared both ways (if one ever is, the call-site scan below flags it conservatively).
        Assert.Contains("WriteAsync", valueReturning);
        Assert.Contains("WriteIfNewAsync", valueReturning);
        Assert.Contains("WriteFailedAsync", voidReturning);
        Assert.Empty(valueReturning.Intersect(voidReturning));

        var offenders = new List<string>();
        var filesScanned = 0;
        foreach (var (relative, text) in sources)
        {
            filesScanned++;
            foreach (Match match in DiscardedStoreWrite.Matches(text))
            {
                // A bare-Task artifact write has no outcome to discard. An UNKNOWN name (declared nowhere
                // under src/ — e.g. an extension) is flagged, never silently spared.
                var method = match.Groups["method"].Value;
                if (voidReturning.Contains(method) && !valueReturning.Contains(method))
                {
                    continue;
                }

                var statement = Regex.Replace(match.Value.Trim(), @"\s+", " ");
                var line = text[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{relative}:{line}: {statement}");
            }
        }

        Assert.True(filesScanned > 100, $"Expected to scan the production sources, scanned {filesScanned}.");
        Assert.True(
            offenders.Count == 0,
            "Every durable store write under src/ must READ its outcome (spec 202 §2 — a discarded "
                + "Task<bool>/DurableWriteResult is a write that can fail silently):\n"
                + string.Join('\n', offenders));
    }

    [Fact]
    public void DiscardedWriteScan_PositiveControl_CatchesEveryShape_AndSparesTheCheckedOnes()
    {
        Assert.Matches(DiscardedStoreWrite, "        await _assessmentStore.WriteAsync(record, ct).ConfigureAwait(false);");
        Assert.Matches(DiscardedStoreWrite, "        await _familyStore\n            .WriteAsync(\n                segment,");
        Assert.Matches(DiscardedStoreWrite, "await _rawEvidenceStore.WriteIfNewAsync(evidence, ct)");

        Assert.Equal(
            "WriteAsync",
            DiscardedStoreWrite.Match("        await _assessmentStore.WriteAsync(record, ct);").Groups["method"].Value);

        Assert.DoesNotMatch(DiscardedStoreWrite, "        var persisted = await _assessmentStore.WriteAsync(record, ct);");
        Assert.DoesNotMatch(DiscardedStoreWrite, "        return await _familyStore\n            .WriteAsync(x, y, ct);");
        Assert.DoesNotMatch(DiscardedStoreWrite, "        if (!await _store.WriteAsync(x, ct)) { }");
        // A store READ is not a write and must not trip the scan.
        Assert.DoesNotMatch(DiscardedStoreWrite, "        await _store.EnsureHydratedAsync(ct);");

        // Spec 206 §3: the raw-evidence write — the shape that was the guard's ONE allowlisted exception
        // until this slice — is checked in the code (the pass assigns the typed outcome), so the shape the
        // regex above matches must no longer appear in CollectionPass at all.
        var collectionPass = ProductionSources()
            .Single(s => s.Relative.EndsWith(
                Path.Combine("Radar.Application", "Pipeline", "CollectionPass.cs"), StringComparison.Ordinal));
        Assert.Contains("_rawEvidenceStore.WriteIfNewAsync(", collectionPass.Text, StringComparison.Ordinal);
        Assert.DoesNotMatch(DiscardedStoreWrite, collectionPass.Text);
    }

    [Fact]
    public void CanonicalHash_IsTheOnlySha256Site_InTheProductionProjects()
    {
        var offenders = new List<string>();
        var canonicalHashSeen = false;
        foreach (var (relative, text) in ProductionSources())
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                // Comments may NAME the idiom (CanonicalHash's own doc comment does); only code is scanned.
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || !Sha256Site.IsMatch(lines[i]))
                {
                    continue;
                }

                if (relative.EndsWith(CanonicalHashFile, StringComparison.Ordinal))
                {
                    canonicalHashSeen = true;
                    continue;
                }

                offenders.Add($"{relative}:{i + 1}: {trimmed.TrimEnd()}");
            }
        }

        // Positive control: the scan demonstrably reached the one sanctioned site.
        Assert.True(canonicalHashSeen, "Expected the scan to find SHA256 inside CanonicalHash.cs.");
        Assert.True(
            offenders.Count == 0,
            "CanonicalHash.Sha256Hex is the ONLY SHA-256 call site in Domain/Application/Infrastructure/"
                + "Worker (spec 202 §3 — a copied hashing step drifts silently):\n"
                + string.Join('\n', offenders));
    }

    private static (HashSet<string> ValueReturning, HashSet<string> VoidReturning) WriteMethodNames(
        IEnumerable<(string Relative, string Text)> sources)
    {
        var valueReturning = new HashSet<string>(StringComparer.Ordinal);
        var voidReturning = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in sources)
        {
            foreach (Match match in WriteDeclaration.Matches(text))
            {
                var method = match.Groups["method"].Value;
                (match.Groups["generic"].Success ? valueReturning : voidReturning).Add(method);
            }
        }

        return (valueReturning, voidReturning);
    }

    private static IEnumerable<(string Relative, string Text)> ProductionSources()
    {
        var src = Path.Combine(NewsObservationArchitectureGuardTests.FindRepositoryRoot(), "src");
        foreach (var project in ProductionProjects)
        {
            var root = Path.Combine(src, project);
            Assert.True(Directory.Exists(root), $"Expected the project source folder at {root}.");
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(src, file);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(s => s is "bin" or "obj"))
                {
                    continue;
                }

                yield return (relative, File.ReadAllText(file));
            }
        }
    }
}
