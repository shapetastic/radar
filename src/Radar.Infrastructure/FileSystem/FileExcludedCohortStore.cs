using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Efficacy.Attention;

namespace Radar.Infrastructure.FileSystem;

/// <summary>Root directory holding the committed cohort declarations (default <c>docs/cohorts</c>, spec 169).</summary>
public sealed class FileExcludedCohortStoreOptions
{
    public required string RootDirectory { get; init; }
}

/// <summary>
/// Reads the committed cohort declarations under the configured cohorts directory (spec 169) — the
/// machine-readable source AD-16's 2026-07-31 amendment points the evaluator at ("the evaluator reads the
/// file, never git history"). Only cohorts declaring <c>"excludeFromPrimaryScreen": true</c> are returned.
/// <para>
/// <b>It fails LOUD, not soft — the opposite posture to every other file store here.</b> Elsewhere in Radar a
/// missing or malformed file degrades to "no findings", because a diagnostic that cannot read its input
/// should not break a run. Here the exclusion is BINDING: silently returning an empty cohort would let the
/// primary screen quietly include companies an accepted AD-16 amendment excludes, and the artifact would look
/// completely normal. So a missing directory, a missing/unreadable/malformed file, a member with no ticker,
/// or — critically — a directory that declares NO exclusion cohort at all yields
/// <see cref="ExcludedCohortSet.Unavailable"/>, which suppresses the primary status.
/// </para>
/// <para>
/// The last of those is the subtle one: "the directory exists and nothing in it declares
/// <c>excludeFromPrimaryScreen</c>" is exactly what a renamed, deleted or flag-stripped declaration looks
/// like, and treating it as "an empty exclusion set" is a silent fail-OPEN. While AD-16's 2026-07-31
/// amendment stands, an empty exclusion set is not expressible: membership is append-only and a company
/// leaves the cohort only via a further amendment.
/// </para>
/// <para>
/// It never throws for a data condition (only cancellation propagates), and it never writes.
/// </para>
/// </summary>
public sealed class FileExcludedCohortStore : IExcludedCohortStore
{
    private readonly FileExcludedCohortStoreOptions _options;
    private readonly ILogger<FileExcludedCohortStore> _logger;

    public FileExcludedCohortStore(
        FileExcludedCohortStoreOptions options,
        ILogger<FileExcludedCohortStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<ExcludedCohortSet> LoadAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_options.RootDirectory))
        {
            return ExcludedCohortSet.Unavailable(
                $"The cohorts directory '{_options.RootDirectory}' does not exist. AD-16's cohort exclusion is "
                    + "binding, so an absent declaration suppresses the primary screen rather than silently "
                    + "including every company.");
        }

        List<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(_options.RootDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Failed to enumerate cohort files in '{RootDirectory}'.", _options.RootDirectory);
            return ExcludedCohortSet.Unavailable(
                $"The cohorts directory '{_options.RootDirectory}' could not be enumerated: {ex.Message}");
        }

        // Ordinal file order, then ordinal member order below, so two runs over the same directory produce
        // byte-identical output (AD-3).
        files.Sort(static (a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));

        var members = new List<ExcludedCohortMember>();

        // How many files actually DECLARED excludeFromPrimaryScreen. Counted separately from the member list
        // because the two answer different questions, and only this one can tell "the declaration is gone"
        // from "the declaration is present and lists nobody".
        var declaredCohorts = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ExcludedCohortSet.Unavailable(
                    $"Cohort file '{Path.GetFileName(file)}' could not be read: {ex.Message}");
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return ExcludedCohortSet.Unavailable(
                        $"Cohort file '{Path.GetFileName(file)}' is not a JSON object.");
                }

                if (!root.TryGetProperty("excludeFromPrimaryScreen", out var exclude)
                    || exclude.ValueKind != JsonValueKind.True)
                {
                    // Not an exclusion cohort. A file that does not declare the flag is simply not one of
                    // these; that is a legitimate state alongside a real declaration, and it is NOT enough on
                    // its own — see the "no declaration at all" guard after the loop.
                    continue;
                }

                declaredCohorts++;

                var cohortName = root.TryGetProperty("cohort", out var name)
                        && name.ValueKind == JsonValueKind.String
                    ? name.GetString()!
                    : Path.GetFileNameWithoutExtension(file);

                if (!root.TryGetProperty("companies", out var companies)
                    || companies.ValueKind != JsonValueKind.Array)
                {
                    return ExcludedCohortSet.Unavailable(
                        $"Cohort file '{Path.GetFileName(file)}' declares excludeFromPrimaryScreen but carries "
                            + "no 'companies' array.");
                }

                foreach (var company in companies.EnumerateArray())
                {
                    var ticker = company.ValueKind == JsonValueKind.Object
                            && company.TryGetProperty("ticker", out var t)
                            && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(ticker))
                    {
                        // A member Radar cannot resolve is a member Radar cannot exclude. Refusing is the
                        // only honest response to "exclude this company" with no company named.
                        return ExcludedCohortSet.Unavailable(
                            $"Cohort file '{Path.GetFileName(file)}' contains a company entry with no ticker; "
                                + "an unresolvable member cannot be excluded, so the declaration is refused "
                                + "rather than partially applied.");
                    }

                    var cik = company.TryGetProperty("cik", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? string.Empty
                        : string.Empty;

                    members.Add(new ExcludedCohortMember(cohortName, ticker.Trim(), cik.Trim()));
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Cohort file '{File}' is malformed JSON.", file);
                return ExcludedCohortSet.Unavailable(
                    $"Cohort file '{Path.GetFileName(file)}' is malformed JSON: {ex.Message}");
            }
        }

        // THE FAIL-CLOSED GUARD. A directory that exists but declares no exclusion cohort at all — the file
        // renamed, deleted, or having simply lost its flag — is the one failure mode that would otherwise
        // fall through to "available, zero members", and it is the WORST one: the primary screen would then
        // run over the full universe including the eight event-enriched companies and emit a completely
        // normal-looking artifact with a real ScreenStatus. AD-16's 2026-07-31 amendment makes membership
        // append-only and removable "only via a new AD-16 amendment", so while that amendment stands an empty
        // exclusion set is not a legitimate state — it is a missing declaration.
        if (declaredCohorts == 0)
        {
            return ExcludedCohortSet.Unavailable(
                $"No file under the cohorts directory '{_options.RootDirectory}' declares "
                    + "\"excludeFromPrimaryScreen\": true. AD-16's cohort exclusion is BINDING and its "
                    + "membership is append-only, so a missing declaration suppresses the primary screen "
                    + "rather than silently including every company — which would let the screen clear "
                    + "because of the event-enriched cohort it exists to keep out. Restore the committed "
                    + "declaration (docs/cohorts/event-enriched-2026-07.json), or amend AD-16.");
        }

        _logger.LogInformation(
            "Loaded {MemberCount} excluded-cohort member(s) from {CohortCount} declared cohort(s) in "
                + "'{RootDirectory}'.",
            members.Count,
            declaredCohorts,
            _options.RootDirectory);

        return ExcludedCohortSet.Available(
        [
            .. members
                .OrderBy(m => m.Cohort, StringComparer.Ordinal)
                .ThenBy(m => m.Ticker, StringComparer.Ordinal),
        ]);
    }
}
