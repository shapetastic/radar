using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Acquisitions;
using Radar.Application.EntityResolution;
using Radar.Application.Filings;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;

namespace Radar.IntegrationTests;

/// <summary>
/// SPEC 229 — the ONE directory guard every acquisition live harness uses (spec 227's
/// <see cref="AcquisitionScanV2LiveMeasurementTests"/>, spec 228's <see cref="AcquisitionReadPrimary8KLiveMeasurementTests"/>
/// and spec 229's <see cref="AcquisitionScanV4LiveMeasurementTests"/>). A harness is read-only against the data root,
/// so any directory it writes to (a body directory, a document cache) must lie OUTSIDE it.
/// <para>
/// It was extracted rather than copied a third time because the copies had already drifted: spec 227's harness
/// checked with a TEXT PREFIX (<c>StartsWith(root)</c>), which wrongly rejects <c>C:\data-cache</c> for root
/// <c>C:\data</c>; spec 228's copy was corrected to a path-boundary check after review and 227's was not.
/// </para>
/// </summary>
internal static class LiveHarnessPaths
{
    /// <summary>
    /// The full path of <paramref name="directory"/> when it lies outside <paramref name="root"/> (asserted — a
    /// harness whose directory lies under the data root FAILS rather than writing there); null when no directory is
    /// configured. <paramref name="create"/> creates it.
    /// </summary>
    public static string? OutsideDataRoot(string root, string? directory, bool create)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var full = Path.GetFullPath(directory);
        Assert.True(IsOutside(Path.GetFullPath(root), full), $"{full} must lie OUTSIDE the data root — the harness writes nothing under it.");
        if (create)
        {
            Directory.CreateDirectory(full);
        }

        return full;
    }

    /// <summary>A path-BOUNDARY check, not a prefix check: root <c>C:\data</c> must not reject <c>C:\data-cache</c>.</summary>
    internal static bool IsOutside(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        return Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}

/// <summary>
/// Counts every SEC Archives request a harness's clients issue and, optionally, keeps each 200 response in a document
/// cache OUTSIDE the data root (spec 228; shared with spec 229). <paramref name="readOnlyCacheDirectories"/> are
/// consulted after <paramref name="cacheDirectory"/> and never written — spec 229 reads spec 228's cache that way, so
/// a response only the newer run fetched never lands in the older run's recorded cache.
/// </summary>
internal sealed class SecResponseLog(string? cacheDirectory, IReadOnlyList<string>? readOnlyCacheDirectories = null)
{
    private int _requests;
    private int _live;

    public int Requests => _requests;

    public int LiveRequests => _live;

    /// <summary>The cached path of <paramref name="uri"/> to READ: the writable cache first, then each read-only cache.</summary>
    public string? ExistingPathFor(Uri uri)
    {
        var name = FileNameFor(uri);
        if (name is null)
        {
            return null;
        }

        foreach (var directory in new[] { cacheDirectory }.Concat(readOnlyCacheDirectories ?? []))
        {
            if (directory is not null && File.Exists(Path.Combine(directory, name)))
            {
                return Path.Combine(directory, name);
            }
        }

        return null;
    }

    /// <summary>The path a live 200 response for <paramref name="uri"/> is kept at, or null when no writable cache is configured.</summary>
    public string? WritablePathFor(Uri uri) =>
        cacheDirectory is not null && FileNameFor(uri) is { } name ? Path.Combine(cacheDirectory, name) : null;

    public void Counted() => Interlocked.Increment(ref _requests);

    public void Live() => Interlocked.Increment(ref _live);

    private static string? FileNameFor(Uri uri) =>
        uri.AbsolutePath.StartsWith("/Archives/", StringComparison.Ordinal)
            ? uri.AbsolutePath.Trim('/').Replace('/', '_')
            : null;
}

/// <summary>
/// The OUTERMOST handler on the reader's client: counts the request, answers from the document cache when it holds the
/// URL, and otherwise passes it to the production pipeline (the shared SEC pacer) and keeps a 200. A new instance per
/// handler-chain build; the state is shared (spec 228; shared with spec 229).
/// </summary>
internal sealed class RecordingCacheHandler(SecResponseLog log) : DelegatingHandler
{
    private const string ContentTypeSuffix = ".content-type";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isArchive = request.RequestUri!.AbsolutePath.StartsWith("/Archives/", StringComparison.Ordinal);
        if (isArchive)
        {
            log.Counted();
        }

        var existing = log.ExistingPathFor(request.RequestUri);
        if (existing is not null)
        {
            var cached = new ByteArrayContent(await File.ReadAllBytesAsync(existing, cancellationToken));
            var contentTypePath = existing + ContentTypeSuffix;
            if (File.Exists(contentTypePath)
                && MediaTypeHeaderValue.TryParse(await File.ReadAllTextAsync(contentTypePath, cancellationToken), out var contentType))
            {
                cached.Headers.ContentType = contentType;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = cached, RequestMessage = request };
        }

        if (isArchive)
        {
            log.Live();
        }

        var response = await base.SendAsync(request, cancellationToken);
        var path = log.WritablePathFor(request.RequestUri);
        if (path is not null && response.StatusCode == HttpStatusCode.OK)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            var content = new ByteArrayContent(bytes);
            if (response.Content.Headers.ContentType is { } contentType)
            {
                // Kept beside the body so a cached replay decodes with the charset the wire declared.
                content.Headers.ContentType = contentType;
                await File.WriteAllTextAsync(path + ContentTypeSuffix, contentType.ToString(), cancellationToken);
            }

            var replay = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
            response.Dispose();
            return replay;
        }

        return response;
    }
}

/// <summary>One item-1.01 accession of the production recognition pass's population, with its resolved company and mentions.</summary>
internal sealed record ItemOneOhOneFiling(
    EvidenceItem Evidence,
    FilingEvidenceIdentifiers Identifiers,
    Company Company,
    IReadOnlyList<string> Mentions);

/// <summary>
/// SPEC 229 — the item-1.01 population every acquisition live harness measures, extracted from spec 228's harness so
/// spec 229's does not carry a second copy: the production pass's own <see cref="AcquisitionRecognitionPass.FormCode"/> /
/// <see cref="AcquisitionRecognitionPass.ItemCode"/>, <see cref="FilingEvidenceFacts.TryResolve"/>, the production
/// <see cref="ICompanyResolver"/> and <see cref="AcquisitionRecognitionPass.BuildMentionIndex"/>, oldest first, ONE
/// filing per accession. Every item that does not become a filing is COUNTED on its own axis.
/// </summary>
internal sealed record ItemOneOhOnePopulation(
    IReadOnlyList<ItemOneOhOneFiling> Filings,
    int CandidateItems,
    int Untrustworthy,
    int Unresolved,
    int DuplicateItems)
{
    public static async Task<ItemOneOhOnePopulation> LoadAsync(
        IServiceProvider provider, IReadOnlyList<Company> companies, CancellationToken ct)
    {
        var mentions = AcquisitionRecognitionPass.BuildMentionIndex(companies);
        var byId = companies.ToDictionary(c => c.Id);
        var resolver = provider.GetRequiredService<ICompanyResolver>();
        var evidence = await provider.GetRequiredService<IEvidenceRepository>().GetAllAsync(ct);

        var candidates = new List<(EvidenceItem Evidence, FilingEvidenceIdentifiers Identifiers)>();
        var untrustworthy = 0;
        foreach (var item in evidence)
        {
            if (FilingEvidenceFacts.TryResolve(
                    item, AcquisitionRecognitionPass.FormCode, AcquisitionRecognitionPass.ItemCode, out var identifiers, out var rejection))
            {
                candidates.Add((item, identifiers!));
            }
            else if (rejection is FilingEvidenceRejection.UnparseableSourceUrl or FilingEvidenceRejection.AccessionMismatch)
            {
                untrustworthy++;
            }
        }

        candidates.Sort(static (a, b) =>
        {
            var byWhen = (a.Evidence.PublishedAtUtc ?? a.Evidence.CollectedAtUtc).CompareTo(b.Evidence.PublishedAtUtc ?? b.Evidence.CollectedAtUtc);
            return byWhen != 0 ? byWhen : a.Evidence.Id.CompareTo(b.Evidence.Id);
        });

        var filings = new List<ItemOneOhOneFiling>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = 0;
        var duplicateItems = 0;
        foreach (var (item, identifiers) in candidates)
        {
            var resolution = await resolver.ResolveAsync(item.Title, AcquisitionScanV2LiveMeasurementTests.HintsOf(item), ct);
            if (resolution.CompanyId is not { } companyId || !byId.TryGetValue(companyId, out var company))
            {
                unresolved++;
                continue;
            }

            if (!seen.Add(identifiers.Accession))
            {
                // A second evidence item for an accession already measured: the read is per accession.
                duplicateItems++;
                continue;
            }

            filings.Add(new ItemOneOhOneFiling(item, identifiers, company, mentions.GetValueOrDefault(companyId, [])));
        }

        return new ItemOneOhOnePopulation(filings, candidates.Count, untrustworthy, unresolved, duplicateItems);
    }
}
