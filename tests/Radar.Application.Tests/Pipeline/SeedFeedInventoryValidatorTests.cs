using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Pipeline;
using Radar.Domain.Companies;

namespace Radar.Application.Tests.Pipeline;

public sealed class SeedFeedInventoryValidatorTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 2, 8, 12, 0, 0, TimeSpan.Zero);

    // A fake ICompanySeedSource returning a chosen CompanySeedData (companies/aliases irrelevant here —
    // the validator reconciles feed-type inventories only).
    private sealed class FakeSeedSource(CompanySeedData seed) : ICompanySeedSource
    {
        public Task<CompanySeedData> GetSeedAsync(CancellationToken ct) => Task.FromResult(seed);
    }

    private static SeedFeedInventoryValidator CreateValidator(CompanySeedData seed) =>
        new(new FakeSeedSource(seed), NullLogger<SeedFeedInventoryValidator>.Instance);

    private static CompanySeedData Seed(params CompanySourceFeed[] feeds) =>
        new(Companies: [], Aliases: [], SourceFeeds: feeds);

    private static CollectionContext Context(params CompanySourceFeed[] feeds) =>
        new([], feeds);

    private static IEnumerable<CompanySourceFeed> Feeds(string feedType, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var companyId = Guid.NewGuid();
            yield return new CompanySourceFeed(
                Id: Guid.NewGuid(),
                CompanyId: companyId,
                FeedType: feedType,
                Name: $"{feedType} feed {i}",
                Url: $"https://example.com/{feedType}/{i}",
                CreatedAtUtc: CreatedAt);
        }
    }

    [Fact]
    public async Task Shrinkage_DeclaredButNoneReached_FlagsOneWarning()
    {
        var seed = Seed([.. Feeds("sec", 7)]);
        var validator = CreateValidator(seed);

        var report = await validator.ValidateAsync(Context(), CancellationToken.None);

        var warning = Assert.Single(report.Warnings);
        Assert.Equal("feeds-lost-before-collection", warning.Code);
        Assert.Equal(CollectionHealthSeverity.Warning, warning.Severity);
        Assert.Equal("sec", warning.FeedType);
        Assert.Equal(7, warning.DeclaredInSeed);
        Assert.Equal(0, warning.ReachedCollectors);
        Assert.Contains("Seed declares 7 'sec' feed(s) but only 0 reached", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_WhenNothingLost_NoWarnings()
    {
        var sec = Feeds("sec", 7).ToArray();
        var form4 = Feeds("secform4", 7).ToArray();
        var seed = Seed([.. sec, .. form4]);
        var validator = CreateValidator(seed);

        // The context contains ALL 14 declared feeds (post-fix reality).
        var report = await validator.ValidateAsync(Context([.. sec, .. form4]), CancellationToken.None);

        Assert.False(report.HasWarnings);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task LegitimateAbsence_TypeInNeither_NoWarning()
    {
        // Seed declares only sec (no usaspending); the context also has no usaspending. A type absent
        // from BOTH sides must not warn.
        var sec = Feeds("sec", 3).ToArray();
        var seed = Seed(sec);
        var validator = CreateValidator(seed);

        var report = await validator.ValidateAsync(Context(sec), CancellationToken.None);

        Assert.False(report.HasWarnings);
        Assert.DoesNotContain(report.Warnings, w => w.FeedType == "usaspending");
    }

    [Fact]
    public async Task EmptySeed_NoWarnings()
    {
        var validator = CreateValidator(Seed());

        var report = await validator.ValidateAsync(
            Context([.. Feeds("sec", 5)]), CancellationToken.None);

        Assert.Same(CollectionHealthReport.Empty, report);
        Assert.False(report.HasWarnings);
    }

    [Fact]
    public async Task MultipleShrinkage_WarningsOrderedByFeedTypeOrdinal()
    {
        // Declare (and fully lose) three feed types in a non-sorted order; warnings must come back in
        // Ordinal order: newssearch, rss, sec.
        var seed = Seed([.. Feeds("sec", 2), .. Feeds("newssearch", 2), .. Feeds("rss", 2)]);
        var validator = CreateValidator(seed);

        var report = await validator.ValidateAsync(Context(), CancellationToken.None);

        Assert.Equal(3, report.Warnings.Count);
        Assert.Equal(["newssearch", "rss", "sec"], report.Warnings.Select(w => w.FeedType));
    }

    [Fact]
    public async Task PartialShrinkage_DeclaredMoreThanReached_FlagsWithCounts()
    {
        var sec = Feeds("sec", 7).ToArray();
        var seed = Seed(sec);
        var validator = CreateValidator(seed);

        // Only 3 of the 7 declared sec feeds reached the collectors.
        var report = await validator.ValidateAsync(
            Context(sec.Take(3).ToArray()), CancellationToken.None);

        var warning = Assert.Single(report.Warnings);
        Assert.Equal("sec", warning.FeedType);
        Assert.Equal(7, warning.DeclaredInSeed);
        Assert.Equal(3, warning.ReachedCollectors);
    }

    [Fact]
    public async Task DeclaredButDisabledCollectorKind_DeclaredEqualsReached_NoWarning()
    {
        // Spec-103/spec-98 interaction: the seed declares hiringats feeds but the hiringats COLLECTOR is
        // disabled (opt-in OFF by default). CollectionContext.SourceFeeds is populated from ALL seeded
        // feeds regardless of which collectors are enabled, so declared == reached and NO
        // feeds-lost-before-collection warning may fire — a disabled collector is not a lost feed.
        // No validator change is needed; this pins that.
        var hiring = Feeds("hiringats", 4).ToArray();
        var sec = Feeds("sec", 7).ToArray();
        var validator = CreateValidator(Seed([.. hiring, .. sec]));

        var report = await validator.ValidateAsync(
            Context([.. hiring, .. sec]), CancellationToken.None);

        Assert.False(report.HasWarnings);
        Assert.DoesNotContain(report.Warnings, w => w.FeedType == "hiringats");
    }

    [Fact]
    public async Task CaseInsensitive_ReachedTypeMatchesDeclaredType()
    {
        // Seed declares "sec"; the context reaches "SEC" (different case) — same type, so no shrinkage.
        var seedFeeds = Feeds("sec", 4).ToArray();
        var reached = seedFeeds
            .Select(f => f with { FeedType = "SEC" })
            .ToArray();
        var validator = CreateValidator(Seed(seedFeeds));

        var report = await validator.ValidateAsync(Context(reached), CancellationToken.None);

        Assert.False(report.HasWarnings);
    }
}
