namespace Radar.Application.SignalExtraction;

/// <summary>
/// Single source of truth for composing the evidence "searchable text" that signal extraction
/// scans and the mapper validates excerpts against. Title first (events lead the headline),
/// then a single '\n', then the body; null/empty fields are treated as the empty string.
/// </summary>
/// <remarks>
/// The extractor builds verbatim excerpt slices from this composition and the mapper checks
/// each excerpt against it (provenance). Both MUST use this one method so the round-trip
/// invariant cannot silently drift. Changing the composition here changes it for both
/// consumers at once.
/// </remarks>
internal static class EvidenceSearchableText
{
    public static string Compose(string? title, string? rawText) =>
        (title ?? string.Empty) + "\n" + (rawText ?? string.Empty);
}
