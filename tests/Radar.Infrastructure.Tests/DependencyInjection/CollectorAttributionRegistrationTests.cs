using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Collectors;
using Radar.Infrastructure.DependencyInjection;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.DependencyInjection;

/// <summary>
/// Spec 151 — <c>Radar:Scoring:InferLegacyCollectorAttribution</c> binds ONE decision onto TWO services (the
/// resolver that performs the inference and the options the descriptor records it from), so behaviour and
/// recorded provenance cannot disagree.
/// </summary>
public sealed class CollectorAttributionRegistrationTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static ServiceProvider Provider(IConfiguration configuration) =>
        new ServiceCollection()
            .AddLogging()
            .AddRadarCollectorAttribution(configuration)
            .AddInMemoryRadarPersistence()
            .AddRadarApplicationServices()
            .BuildServiceProvider();

    /// <summary>A legacy news article: no recorded collector, but a newssearch-exclusive marker key.</summary>
    private static EvidenceItem LegacyNewsArticle() =>
        new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithMetadataJson(EvidenceMetadata.Compose(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["newsSearchFeedUrl"] = "https://news.google.com/rss/search?q=acme",
                },
                []))
            .Build();

    [Fact]
    public void AbsentSetting_BindsTheRecordedOnlyResolver_AndAnUnmarkedProvenance()
    {
        using var provider = Provider(Configuration());

        Assert.False(provider.GetRequiredService<CollectorAttributionOptions>().InferLegacyAttribution);
        Assert.IsType<RecordedOnlyCollectorAttributionResolver>(
            provider.GetRequiredService<ICollectorAttributionResolver>());

        // The legacy record stays unattributed, and the recorded provenance carries no attribution segment —
        // i.e. every deployment that never sets the key is byte-identical to pre-151.
        Assert.Equal(
            CollectorAttribution.Unattributed,
            provider.GetRequiredService<ICollectorAttributionResolver>().Resolve(LegacyNewsArticle()));
        Assert.DoesNotContain(
            "attribution=",
            provider.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledSetting_BindsTheInferringResolver_AndMarksTheProvenance()
    {
        using var provider = Provider(
            Configuration(("Radar:Scoring:InferLegacyCollectorAttribution", "true")));

        Assert.True(provider.GetRequiredService<CollectorAttributionOptions>().InferLegacyAttribution);
        Assert.IsType<InferringCollectorAttributionResolver>(
            provider.GetRequiredService<ICollectorAttributionResolver>());

        // Both halves of the one decision, together: the resolver infers…
        Assert.Equal(
            CollectorAttribution.Inferred("newssearch"),
            provider.GetRequiredService<ICollectorAttributionResolver>().Resolve(LegacyNewsArticle()));

        // …and every snapshot this process writes says so.
        Assert.Contains(
            "attribution=inferred-legacy;",
            provider.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("")]
    [InlineData("   ")]
    public void FalseOrBlank_LeavesInferenceOff(string value)
    {
        using var provider = Provider(
            Configuration(("Radar:Scoring:InferLegacyCollectorAttribution", value)));

        Assert.False(provider.GetRequiredService<CollectorAttributionOptions>().InferLegacyAttribution);
        Assert.IsType<RecordedOnlyCollectorAttributionResolver>(
            provider.GetRequiredService<ICollectorAttributionResolver>());
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("maybe")]
    public void UnparseableValue_FailsFast_RatherThanSilentlyReadingAsOff(string value)
    {
        // Reading "yes" as false would score every radar-formula-v9 collector channel against a near-empty
        // attributed set and emit a full series of near-zero scores that look like data — the exact failure
        // this slice exists to prevent.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRadarCollectorAttribution(
                Configuration(("Radar:Scoring:InferLegacyCollectorAttribution", value))));

        Assert.Contains("Radar:Scoring:InferLegacyCollectorAttribution", ex.Message, StringComparison.Ordinal);
        Assert.Contains(value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredRegistration_WinsOverTheLibraryDefaults()
    {
        // The established composition-root ordering rule (AddRadar* BEFORE AddRadarApplicationServices): the
        // library's TryAddSingleton defaults must not override the config-bound pair.
        using var provider = Provider(
            Configuration(("Radar:Scoring:InferLegacyCollectorAttribution", "true")));

        Assert.IsType<InferringCollectorAttributionResolver>(
            provider.GetRequiredService<ICollectorAttributionResolver>());
        Assert.True(provider.GetRequiredService<CollectorAttributionOptions>().InferLegacyAttribution);
    }

    [Fact]
    public void LibraryDefault_WithoutTheHelper_IsRecordedOnly()
    {
        // A composition that never calls AddRadarCollectorAttribution at all still resolves, and resolves to
        // the pre-151 behaviour.
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddInMemoryRadarPersistence()
            .AddRadarApplicationServices()
            .BuildServiceProvider();

        Assert.IsType<RecordedOnlyCollectorAttributionResolver>(
            provider.GetRequiredService<ICollectorAttributionResolver>());
        Assert.False(provider.GetRequiredService<CollectorAttributionOptions>().InferLegacyAttribution);
    }
}
