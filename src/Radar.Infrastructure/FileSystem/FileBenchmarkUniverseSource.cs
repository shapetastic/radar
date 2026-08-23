using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Efficacy.Comparison;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Reads the committed frozen benchmark-universe artifact (spec 183 §1 —
/// <c>data/efficacy/benchmark-universe-v1.json</c>). The artifact is SELF-CONTAINED: members resolve to price
/// series through their own <c>priceSeriesKey</c>, and this reader never consults the mutable
/// <c>companies.json</c> — a seed edit cannot move a single benchmark value.
/// <para>
/// <b>Fails visible, never silent:</b> a missing, unparseable or structurally invalid artifact — and, above
/// all, one whose recomputed content hash does not match the committed <c>contentHash</c> (a hand-edited pond
/// would silently redefine every published excess number) — logs the specific failure and returns
/// <c>null</c>. The consumers then record every excess observation as <c>BenchmarkUnavailable</c>, which the
/// rendered artifacts state explicitly.
/// </para>
/// </summary>
public sealed class FileBenchmarkUniverseSource : IBenchmarkUniverseSource
{
    /// <summary>The fixed artifact file name under the efficacy directory.</summary>
    public const string FileName = "benchmark-universe-v1.json";

    /// <summary>The one artifact schema this reader understands.</summary>
    public const string SupportedSchemaVersion = "benchmark-universe-schema-v1";

    private readonly FileBenchmarkUniverseSourceOptions _options;
    private readonly ILogger<FileBenchmarkUniverseSource> _logger;

    public FileBenchmarkUniverseSource(
        FileBenchmarkUniverseSourceOptions options, ILogger<FileBenchmarkUniverseSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<BenchmarkUniverse?> ReadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_options.FilePath))
            {
                _logger.LogError(
                    "Benchmark universe artifact not found at {Path} — it is a committed file and its "
                        + "absence is a deployment defect, not routine attrition.",
                    _options.FilePath);
                return null;
            }

            await using var stream = File.OpenRead(_options.FilePath);
            var dto = await JsonSerializer
                .DeserializeAsync<UniverseDto>(stream, RadarFileStoreJson.Options, ct)
                .ConfigureAwait(false);

            if (dto is null
                || string.IsNullOrWhiteSpace(dto.SchemaVersion)
                || string.IsNullOrWhiteSpace(dto.UniverseVersion)
                || string.IsNullOrWhiteSpace(dto.SourceSeedHash)
                || string.IsNullOrWhiteSpace(dto.ContentHash)
                || dto.Members is null
                || dto.Members.Count == 0)
            {
                _logger.LogError(
                    "Benchmark universe artifact at {Path} is structurally invalid (missing version, hash "
                        + "or members); refusing to benchmark against it.",
                    _options.FilePath);
                return null;
            }

            if (!string.Equals(dto.SchemaVersion, SupportedSchemaVersion, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "Benchmark universe artifact at {Path} declares schema '{Schema}' but this reader "
                        + "supports only '{Supported}'.",
                    _options.FilePath,
                    dto.SchemaVersion,
                    SupportedSchemaVersion);
                return null;
            }

            var members = new List<BenchmarkUniverseMember>(dto.Members.Count);
            var seenIds = new HashSet<Guid>();
            foreach (var member in dto.Members)
            {
                if (member is null
                    || member.CompanyId == Guid.Empty
                    || string.IsNullOrWhiteSpace(member.Ticker)
                    || string.IsNullOrWhiteSpace(member.PriceSeriesKey)
                    || !seenIds.Add(member.CompanyId))
                {
                    _logger.LogError(
                        "Benchmark universe artifact at {Path} carries a blank or duplicate member; "
                            + "refusing to benchmark against it.",
                        _options.FilePath);
                    return null;
                }

                members.Add(new BenchmarkUniverseMember(
                    member.CompanyId, member.Ticker, member.Exchange ?? string.Empty, member.PriceSeriesKey));
            }

            // The integrity check, through the SAME canonical-hash definition the freeze used: a universe
            // whose members have drifted from the committed hash must not silently redefine "excess".
            var recomputed = BenchmarkUniverseContentHash.Compute(dto.UniverseVersion, members);
            if (!string.Equals(recomputed, dto.ContentHash, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "Benchmark universe artifact at {Path} fails its content-hash integrity check: committed "
                        + "{Committed}, recomputed {Recomputed}. The frozen pond is immutable by design "
                        + "(spec 183) — an expansion is a NEW benchmark-universe-v2 artifact, never an edit.",
                    _options.FilePath,
                    dto.ContentHash,
                    recomputed);
                return null;
            }

            return new BenchmarkUniverse(
                dto.SchemaVersion,
                dto.UniverseVersion,
                dto.FrozenAtUtc,
                dto.SourceSeedHash,
                dto.ContentHash,
                members);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(
                ex,
                "Benchmark universe artifact at {Path} could not be read; every excess observation this "
                    + "process computes will be BenchmarkUnavailable.",
                _options.FilePath);
            return null;
        }
    }

    private sealed record UniverseDto(
        string? SchemaVersion,
        string? UniverseVersion,
        DateTimeOffset FrozenAtUtc,
        string? SourceSeedHash,
        string? ContentHash,
        List<MemberDto?>? Members);

    private sealed record MemberDto(
        Guid CompanyId, string? Ticker, string? Exchange, string? PriceSeriesKey);
}
