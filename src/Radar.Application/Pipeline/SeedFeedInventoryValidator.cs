using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;

namespace Radar.Application.Pipeline;

/// <summary>
/// The reconciliation implementation of <see cref="ICollectionHealthValidator"/>. It re-reads the seed's
/// declared feed-type inventory (via <see cref="ICompanySeedSource"/>) and compares it, universe-wide, to
/// the feed-type inventory that actually reached the <see cref="CollectionContext"/>. For each feed type
/// where the declared count exceeds the reached count it emits one <see cref="CollectionHealthWarning"/>
/// — the regression guard for the spec-97 feed-Id collision, where same-URL feeds silently collapsed and
/// vanished before collection.
/// <para>
/// Purely diagnostic (AD-14 discipline): a finding is observational only — it references no evidence,
/// carries no label/score, and never influences scoring. It never throws for a data condition (only
/// <see cref="CancellationToken"/> propagates); an empty/unreadable seed degrades to an empty declared
/// inventory and therefore NO warnings (never invent a baseline). Comparison is case-insensitive by feed
/// type and warnings are emitted in deterministic <see cref="StringComparer.Ordinal"/> order (AD-3).
/// </para>
/// </summary>
public sealed class SeedFeedInventoryValidator : ICollectionHealthValidator
{
    private readonly ICompanySeedSource _seedSource;
    private readonly ILogger<SeedFeedInventoryValidator> _logger;

    public SeedFeedInventoryValidator(
        ICompanySeedSource seedSource,
        ILogger<SeedFeedInventoryValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(seedSource);
        ArgumentNullException.ThrowIfNull(logger);

        _seedSource = seedSource;
        _logger = logger;
    }

    public async Task<CollectionHealthReport> ValidateAsync(CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        // Re-read the declared inventory. The local-file seed source degrades gracefully to an empty
        // CompanySeedData on read failure, so an unreadable seed yields an empty declared map and NO
        // warnings — the correct fail-safe (never invent a warning when the baseline is unknown).
        var seed = await _seedSource.GetSeedAsync(ct).ConfigureAwait(false);

        // Declared count per feed type (case-insensitive key; preserve the group's representative
        // FeedType string for output).
        var declaredByType = seed.SourceFeeds
            .GroupBy(f => f.FeedType, StringComparer.OrdinalIgnoreCase)
            .Select(g => (FeedType: g.Key, Count: g.Count()))
            .ToList();

        // Reached count per feed type, built with the SAME comparer so "sec" matches "SEC".
        var reachedByType = context.SourceFeeds
            .GroupBy(f => f.FeedType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var warnings = new List<CollectionHealthWarning>();
        foreach (var (feedType, declared) in declaredByType)
        {
            var reached = reachedByType.GetValueOrDefault(feedType, 0);
            if (declared <= reached)
            {
                continue; // no shrinkage (legitimate absence contributes to neither count)
            }

            var message = string.Format(
                CultureInfo.InvariantCulture,
                "Seed declares {0} '{1}' feed(s) but only {2} reached the collectors — feeds were lost "
                    + "between seed-load and collection (duplicate/colliding feed Ids?).",
                declared,
                feedType,
                reached);

            warnings.Add(new CollectionHealthWarning(
                Code: "feeds-lost-before-collection",
                Severity: CollectionHealthSeverity.Warning,
                FeedType: feedType,
                DeclaredInSeed: declared,
                ReachedCollectors: reached,
                Message: message));
        }

        if (warnings.Count == 0)
        {
            _logger.LogDebug(
                "Collection health reconciled clean: no feed-type shrinkage between seed and collection "
                    + "({DeclaredTypes} declared feed type(s)).",
                declaredByType.Count);
            return CollectionHealthReport.Empty;
        }

        // Deterministic order (AD-3).
        warnings.Sort((a, b) => string.CompareOrdinal(a.FeedType, b.FeedType));
        _logger.LogDebug(
            "Collection health found {WarningCount} feed-type shrinkage warning(s).", warnings.Count);
        return new CollectionHealthReport(warnings);
    }
}
