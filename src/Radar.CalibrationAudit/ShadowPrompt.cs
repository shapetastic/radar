using System.Security.Cryptography;
using System.Text;

namespace Radar.CalibrationAudit;

/// <summary>
/// Loads the committed forced-choice shadow instruction (<c>scripts/calibration-audit/shadow-prompt.md</c>,
/// version <see cref="Version"/>) and canonicalizes it exactly as spec 163 canonicalizes the labeling
/// template: decode the file's bytes as UTF-8, replace CRLF with LF, re-encode as UTF-8, SHA-256, lowercase
/// hex. The repo checks text files out CRLF on Windows and LF on CI (core.autocrlf, no .gitattributes), so
/// the raw-byte hash differs by machine while the LF-normalized hash is stable.
/// <para>
/// ⚠ The canonicalized text — not the raw file text — <b>is</b> the system instruction sent to the model, so
/// <see cref="ShadowPromptText.Sha256"/> is a hash of exactly the bytes that ran. That equivalence is the
/// whole provenance claim and is asserted by <c>ShadowReadTests</c>
/// (<c>AssembledInstruction_EqualsTheCommittedPromptBytes_AndItsHashIsWhatRecordsCarry</c>).
/// </para>
/// <para>
/// The instruction REPLACES the production one (it is never appended): the production prompt instructs the
/// model to return <c>Unknown</c> for ambiguous text, and appending "no abstain" to it would measure the
/// contradiction rather than the forced-choice prompt.
/// </para>
/// </summary>
public static class ShadowPrompt
{
    /// <summary>The precommitted prompt version stamped on every shadow record.</summary>
    public const string Version = "cal-shadow-v1";

    /// <summary>The committed file name (copied beside the console executable at build time).</summary>
    public const string FileName = "shadow-prompt.md";

    public static ShadowPromptText Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No shadow prompt at '{path}'. The forced-choice instruction is COMMITTED at "
                    + $"scripts/calibration-audit/{FileName} and its LF-normalized SHA-256 is stamped on every "
                    + "shadow record — the pass cannot run without it (pass --shadow-prompt <path>).",
                path);
        }

        // Byte-level read (never File.ReadAllText's encoding sniffing) so the canonicalization is exact.
        var raw = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(raw).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"The shadow prompt at '{path}' is empty; an empty system instruction is not a forced-choice "
                    + "prompt and would silently measure the model's default behaviour.");
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return new ShadowPromptText(text, sha, Path.GetFullPath(path));
    }
}

/// <summary>
/// The canonicalized shadow instruction: <see cref="Instruction"/> is the exact string sent as the system
/// message and <see cref="Sha256"/> is SHA-256(UTF-8(<see cref="Instruction"/>)) — the same value spec 163's
/// LF-normalized file hash produces.
/// </summary>
public sealed record ShadowPromptText(string Instruction, string Sha256, string Path);
