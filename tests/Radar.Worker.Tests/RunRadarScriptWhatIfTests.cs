using System.Diagnostics;

using Radar.TestSupport;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 187 §6: the ONE test that executes the PRODUCTION flattener rather than a C# mirror of it.
/// <para>
/// <b>Why a mirror is not enough.</b> On 2026-08-23 the scheduled baseline crashed at startup because
/// <c>run-radar.ps1</c> skipped only the exact annotation key <c>_comment</c> while the baseline profile had
/// grown a <c>_comment2</c> — and the ENTIRE test suite was green throughout, because the C# mirror had the
/// same bug. A mirror is necessary (it is how config binding is tested cross-platform) and insufficient: it
/// proves the two implementations agree with each other, never that either agrees with the shipped
/// PowerShell. So this test runs <c>powershell -File scripts/run-radar.ps1 -Profile default -WhatIf</c>,
/// reads the resolved <c>--Radar:…</c> arguments the script itself prints, and asserts the properties that
/// actually failed live.
/// </para>
/// <para>
/// <b>Hermetic.</b> <c>-WhatIf</c> returns before the build, before the keep-awake power request and before
/// the Worker is launched: it resolves and prints arguments and nothing else. No data directory is touched,
/// no collector runs, no provider is called and no API key is read (the script only WARNS when the env var
/// it names is unset).
/// </para>
/// <para>
/// <b>Windows-conditional.</b> Windows PowerShell is the interpreter the scheduled baseline task uses, so
/// this test runs there and is SKIPPED WITH A NAMED REASON elsewhere (see
/// <see cref="WindowsPowerShellFactAttribute"/>). The cross-platform mirror tests
/// (<c>RunProfileMirror</c>, <c>RunProfileGuardCompatibilityTests</c>,
/// <c>RunProfileNewsResearchGuardTests</c>) still run everywhere — this one adds the implementation-agreement
/// proof on the platform that can give it.
/// </para>
/// </summary>
public sealed class RunRadarScriptWhatIfTests
{
    private const string ResolvedArgsHeader = "Resolved --Radar args:";
    private const string ArgumentPrefix = "--Radar:";

    /// <summary>The NAMED reason a host without Windows PowerShell reports instead of a silent pass.</summary>
    internal const string SkipReason =
        "Windows PowerShell is unavailable on this host, so the PRODUCTION run-radar.ps1 flattener cannot "
            + "be executed here. The cross-platform mirror tests (RunProfileMirror, "
            + "RunProfileGuardCompatibilityTests, RunProfileNewsResearchGuardTests) still cover the "
            + "_comment*-skipping rule and the full NewsResearch config binding.";

    /// <summary>The repo root: the first ancestor of the test binary carrying <c>scripts/run-radar.ps1</c>.</summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "run-radar.ps1")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate scripts/run-radar.ps1 from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Windows PowerShell's absolute path (the interpreter <c>setup-baseline-task.ps1</c> registers), or
    /// null when this host has none.
    /// </summary>
    internal static string? WindowsPowerShellPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var candidate = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Runs <c>run-radar.ps1 -Profile default -WhatIf</c> and returns every resolved <c>--Radar:…</c>
    /// argument line it printed, in the script's own order.
    /// </summary>
    private static IReadOnlyList<string> ResolvedRadarArguments()
    {
        // Unreachable in practice: [WindowsPowerShellFact] has already SKIPPED the test (with its reason)
        // on any host without the interpreter. Kept so a future caller cannot silently NullReference.
        var powershell = WindowsPowerShellPath()
            ?? throw new InvalidOperationException(SkipReason);

        var repoRoot = RepositoryRoot();
        var startInfo = new ProcessStartInfo(powershell)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repoRoot, "scripts", "run-radar.ps1"));
        startInfo.ArgumentList.Add("-Profile");
        startInfo.ArgumentList.Add("default");
        startInfo.ArgumentList.Add("-WhatIf");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start " + powershell);

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(
            process.WaitForExit(milliseconds: 180_000),
            "run-radar.ps1 -WhatIf did not exit within 180s.");
        Assert.True(
            process.ExitCode == 0,
            $"run-radar.ps1 -WhatIf exited {process.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        // The script prints the header, then one indented argument per line. Take the argument lines
        // themselves (the header is asserted below so a rename cannot leave this silently matching nothing).
        Assert.Contains(ResolvedArgsHeader, stdout, StringComparison.Ordinal);

        return stdout
            .Split('\n')
            .Select(line => line.Trim('\r', ' ', '\t'))
            .Where(line => line.StartsWith(ArgumentPrefix, StringComparison.Ordinal))
            .ToArray();
    }

    [WindowsPowerShellFact]
    public void RunRadarWhatIf_ResolvesTheBaselineProfile_WithNoCommentKeyAndTheHostedReadStages()
    {
        var arguments = ResolvedRadarArguments();

        Assert.NotEmpty(arguments);

        // (a) THE REGRESSION: not one `_comment*` annotation may become a Worker argument. Asserted
        // case-insensitively over the KEY half, mirroring PowerShell's case-insensitive `-like '_comment*'`.
        var commentArguments = arguments
            .Where(argument => KeyOf(argument)
                .Contains(RunProfileMirror.CommentKeyPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(commentArguments);

        // (b)+(c) the two read stages the baseline schedules are actually switched on in the resolved args…
        Assert.Contains("--Radar:NewsResearch:Typing:Enabled=true", arguments, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            "--Radar:NewsResearch:Judgment:Enabled=true", arguments, StringComparer.OrdinalIgnoreCase);

        // (d) …and each one resolves the hosted DeepInfra DeepSeek reader (spec 187 §8: one provider, one
        // key, one cohort per stage). The key VALUE never appears — only the env-var NAME the profile
        // declares.
        Assert.Contains(
            "--Radar:NewsResearch:Typing:Readers:0:OpenAi:Model=deepseek-ai/DeepSeek-V4-Flash",
            arguments,
            StringComparer.Ordinal);
        Assert.Contains(
            "--Radar:NewsResearch:Judgment:Judges:0:OpenAi:Model=deepseek-ai/DeepSeek-V4-Flash",
            arguments,
            StringComparer.Ordinal);
        Assert.Contains(
            "--Radar:NewsResearch:Typing:Readers:0:OpenAi:ApiKeyEnvVar=DEEPINFRA_API_KEY",
            arguments,
            StringComparer.Ordinal);

        // (e) Spec 189 §1: the declared capacity posture survives the PRODUCTION flattener — all five typing
        // limits reach the Worker as arguments, at 350 / 150 / 25 / 3 / 30.
        foreach (var expected in new[]
                 {
                     "--Radar:NewsResearch:Typing:MaxNewTypingsPerRun=350",
                     "--Radar:NewsResearch:Typing:MaxCandidateTypingsPerRun=150",
                     "--Radar:NewsResearch:Typing:MaxRetryTypingsPerRun=25",
                     "--Radar:NewsResearch:Typing:MaxTypingAttempts=3",
                     "--Radar:NewsResearch:Typing:LookbackDays=30",
                 })
        {
            Assert.Contains(expected, arguments, StringComparer.Ordinal);
        }

        // Spec 187 §8: exactly ONE shadow reader is scheduled, and it is the hosted one.
        Assert.Contains(
            "--Radar:NewsResearch:Shadow:Readers:0:Name=deepinfra-deepseek",
            arguments,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            arguments,
            argument => KeyOf(argument).StartsWith(
                "--Radar:NewsResearch:Shadow:Readers:1:", StringComparison.OrdinalIgnoreCase));
    }

    [WindowsPowerShellFact]
    public void RunRadarWhatIf_AgreesWithTheCSharpMirror_ForEveryProfileKey()
    {
        // The mirror↔implementation agreement itself, asserted rather than assumed: every key the shipped
        // PowerShell resolves out of the profile is a key RunProfileMirror produces, with the same value.
        // (The script ADDS runtime keys the mirror deliberately does not model — output directories, the
        // SEC User-Agent, Radar:RunMode — so this is a subset check in that direction only.)
        var arguments = ResolvedRadarArguments();
        var mirrored = RunProfileMirror.Compose(overlayProfileName: null);

        foreach (var argument in arguments)
        {
            var key = KeyOf(argument)[2..]; // strip the leading "--"
            if (!mirrored.TryGetValue(key, out var mirroredValue))
            {
                continue; // a runtime key the script adds itself
            }

            Assert.Equal(mirroredValue, ValueOf(argument));
        }

        // …and nothing the mirror produces went missing on the way through the script.
        var resolvedKeys = arguments
            .Select(argument => KeyOf(argument)[2..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(mirrored.Keys, key => Assert.Contains(key, resolvedKeys));
    }

    /// <summary>The <c>--Radar:…</c> key half of a resolved argument (everything before the first '=').</summary>
    private static string KeyOf(string argument)
    {
        var separator = argument.IndexOf('=', StringComparison.Ordinal);
        return separator < 0 ? argument : argument[..separator];
    }

    /// <summary>The value half of a resolved argument (everything after the first '=').</summary>
    private static string ValueOf(string argument)
    {
        var separator = argument.IndexOf('=', StringComparison.Ordinal);
        return separator < 0 ? string.Empty : argument[(separator + 1)..];
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that SKIPS — with a named reason, at discovery time — on any host without
/// Windows PowerShell. xUnit v2 (2.9.3, the version this repo pins) has no <c>Assert.Skip</c> and does not
/// honour the dynamic-skip token, and <c>[Fact(Skip = …)]</c> takes a compile-time constant; a derived
/// attribute setting <see cref="FactAttribute.Skip"/> from a runtime check is the v2 idiom for
/// "conditionally skipped, and SAID SO".
/// <para>
/// A skip, deliberately, rather than an early return: a host that cannot execute the production flattener
/// must never report a green implementation-agreement check it did not perform.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsPowerShellFactAttribute : FactAttribute
{
    public WindowsPowerShellFactAttribute()
    {
        if (RunRadarScriptWhatIfTests.WindowsPowerShellPath() is null)
        {
            Skip = RunRadarScriptWhatIfTests.SkipReason;
        }
    }
}
