using System.Diagnostics.CodeAnalysis;

namespace Radar.Application.Storage;

/// <summary>
/// The single rule for "this configured name is used <b>verbatim</b> as one storage directory segment".
/// <para>
/// Radar has more than one such name — a scoring strategy's <c>Name</c> (spec 137, which segments the
/// non-primary snapshot storage) and a replay run's <c>Label</c> (spec 139, which segments the replay output
/// root) — and both are operator-supplied. A separator or a relative segment in either would escape its root
/// and let a run write outside the directory it was told to write in, so the check is identical for both by
/// construction rather than by convention. It lives here, shared, precisely so a future fix to the rule can
/// only ever be made once (CLAUDE.md reuse-over-copy).
/// </para>
/// <para>
/// Callers keep their OWN error message: the value is the same, but naming the offending config key
/// (<c>Radar:Strategies[i]:Name</c> vs <c>Radar:Replay:Label</c>) is what makes a startup failure actionable.
/// <see cref="Rule"/> is the shared sentence describing the constraint so those messages stay consistent.
/// </para>
/// </summary>
public static class StorageSegmentName
{
    /// <summary>
    /// Characters that are never valid in a storage segment name: the path separators, the characters Windows
    /// reserves in file names, and NUL. Kept explicit (rather than <c>Path.GetInvalidFileNameChars</c>) so the
    /// rule — and therefore which configurations are accepted — is identical on every platform.
    /// <para>
    /// NUL is forbidden for two reasons: it is illegal in a path on every platform (so it would throw deep
    /// inside a file operation rather than at startup), and callers compose validated segments into
    /// NUL-joined composite cache keys — e.g. <c>ReplayScopedScoreSnapshotFileStoreFactory</c>'s
    /// <c>label\0strategy</c> key — whose collision-freedom rests on this rule rejecting the separator.
    /// </para>
    /// </summary>
    private static readonly char[] ForbiddenChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0'];

    /// <summary>
    /// The human-readable constraint, phrased to slot into a caller's message after "…so " — e.g.
    /// <c>$"'{name}' is used verbatim as a storage directory segment, so {StorageSegmentName.Rule}."</c>
    /// </summary>
    public const string Rule =
        "it must be trimmed and must not contain any of / \\ : * ? \" < > | or NUL (nor be \".\" or \"..\")";

    /// <summary>
    /// True when <paramref name="name"/> is safe to use verbatim as a single storage directory segment:
    /// non-blank, already trimmed (so two names differing only by surrounding whitespace cannot resolve to
    /// the same directory), free of every <see cref="ForbiddenChars"/> character, and neither of the relative
    /// segments <c>"."</c> / <c>".."</c>.
    /// </summary>
    public static bool IsUsable([NotNullWhen(true)] string? name) =>
        !string.IsNullOrWhiteSpace(name)
            && name.IndexOfAny(ForbiddenChars) < 0
            && name == name.Trim()
            && name != "."
            && name != "..";
}
