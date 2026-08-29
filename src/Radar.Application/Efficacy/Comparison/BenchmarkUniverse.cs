using System.Security.Cryptography;
using System.Text;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// One member of a frozen benchmark universe: the identity a snapshot carries (<see cref="CompanyId"/>) plus
/// the price-series key the member's forward return resolves through. Self-contained on purpose (spec 183 §1):
/// benchmark resolution must NEVER consult the mutable <c>companies.json</c> — a member resolves through the
/// artifact's own <see cref="PriceSeriesKey"/>, so a later seed edit (ticker change, company added or removed)
/// cannot move a single historical benchmark value.
/// </summary>
public sealed record BenchmarkUniverseMember(
    Guid CompanyId,
    string Ticker,
    string Exchange,
    string PriceSeriesKey);

/// <summary>
/// A FROZEN, VERSIONED benchmark universe (spec 183 §1) — the committed
/// <c>data/efficacy/benchmark-universe-v1.json</c> artifact, parsed. One fixed pond, applied uniformly to
/// every as-of date, so excess returns are byte-stable regardless of later seed edits. The watch universe has
/// changed repeatedly (8 → 19 → 29 → 43 → 66 → 74 → 94); benchmarking historical dates against the CURRENT seed
/// would retroactively insert later-selected companies — mutable-universe leakage. A future expansion is a
/// NEW <c>benchmark-universe-v2</c> declared prospectively; it never restates v1-era results.
/// </summary>
/// <param name="SchemaVersion">The artifact schema version (<c>benchmark-universe-schema-v1</c>).</param>
/// <param name="UniverseVersion">The cohort identity every adjusted observation records (<c>benchmark-universe-v1</c>).</param>
/// <param name="FrozenAtUtc">
/// When the freeze was taken. Dates BEFORE it are retrospective: applying v1 to earlier dates is reproducible
/// but later-selected members' prices were backfilled, so pre-freeze excess results carry a
/// retrospective/descriptive label in every artifact.
/// </param>
/// <param name="SourceSeedHash">SHA-256 of the <c>companies.json</c> the freeze was taken FROM — provenance only, never re-read.</param>
/// <param name="ContentHash">SHA-256 over the canonical member content (see <see cref="BenchmarkUniverseContentHash"/>).</param>
/// <param name="Members">The frozen members, in artifact order (the deterministic accumulation order).</param>
public sealed record BenchmarkUniverse(
    string SchemaVersion,
    string UniverseVersion,
    DateTimeOffset FrozenAtUtc,
    string SourceSeedHash,
    string ContentHash,
    IReadOnlyList<BenchmarkUniverseMember> Members);

/// <summary>
/// THE canonical content hash of a benchmark universe — one definition, shared by the artifact reader's
/// integrity check and the tests, so the committed hash and the verifying code cannot drift. The canonical
/// string is the universe version followed by one line per member in ARTIFACT order:
/// <c>{companyId:D lowercase}|{ticker}|{exchange}|{priceSeriesKey}</c>. Deliberately excluded:
/// <c>frozenAtUtc</c> and <c>sourceSeedHash</c> — they are provenance about the freeze act, not the pond's
/// identity, and folding them in would make two byte-identical member sets hash differently.
/// </summary>
public static class BenchmarkUniverseContentHash
{
    public static string Compute(string universeVersion, IReadOnlyList<BenchmarkUniverseMember> members)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(universeVersion);
        ArgumentNullException.ThrowIfNull(members);

        var canonical = new StringBuilder(universeVersion).Append('\n');
        foreach (var member in members)
        {
            canonical
                .Append(member.CompanyId.ToString("D"))
                .Append('|')
                .Append(member.Ticker)
                .Append('|')
                .Append(member.Exchange)
                .Append('|')
                .Append(member.PriceSeriesKey)
                .Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
