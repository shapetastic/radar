using System.Globalization;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;

namespace Radar.Application.Tests.Scoring;

public sealed class SignalSourceDescriptorTests
{
    /// <summary>
    /// A tiny fake collector exposing a settable <see cref="CollectorName"/>. Its
    /// <see cref="CollectAsync"/> THROWS to prove the descriptor never triggers collection — it reads only
    /// the name.
    /// </summary>
    private sealed class FakeCollector(string name) : IEvidenceCollector
    {
        public string CollectorName { get; } = name;

        public EvidenceSourceType SourceType => EvidenceSourceType.LocalFile;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            throw new InvalidOperationException("The descriptor must never call CollectAsync.");
    }

    private static string DescriptorFor(params string[] names) =>
        new SignalSourceDescriptor(names.Select(n => (IEvidenceCollector)new FakeCollector(n)))
            .CanonicalDescriptor();

    [Fact]
    public void SameCollectorSet_ProducesSameDescriptor()
    {
        Assert.Equal(
            DescriptorFor("rss", "sec", "usaspending"),
            DescriptorFor("rss", "sec", "usaspending"));
    }

    [Fact]
    public void DifferentCollectorSet_ProducesDifferentDescriptor()
    {
        var baseline = DescriptorFor("rss", "sec", "usaspending");

        Assert.NotEqual(baseline, DescriptorFor("rss", "sec", "usaspending", "newssearch")); // added
        Assert.NotEqual(baseline, DescriptorFor("rss", "sec")); // removed
    }

    [Fact]
    public void DuplicateCollectorName_DoesNotChangeDescriptor()
    {
        Assert.Equal(
            DescriptorFor("rss", "sec"),
            DescriptorFor("rss", "sec", "rss"));
    }

    [Fact]
    public void RegistrationOrder_DoesNotMatter()
    {
        Assert.Equal(
            DescriptorFor("rss", "sec", "usaspending"),
            DescriptorFor("usaspending", "rss", "sec"));
    }

    [Fact]
    public void Descriptor_IsCultureInvariant()
    {
        var invariant = DescriptorFor("rss", "sec", "usaspending");

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(invariant, DescriptorFor("rss", "sec", "usaspending"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Descriptor_ContainsRuleSetVersion()
    {
        var descriptor = DescriptorFor("rss");

        Assert.Contains(KeywordSignalExtractor.RuleSetVersion, descriptor, StringComparison.Ordinal);
        Assert.Contains("radar-keyword-rules-v1", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptor_OrdersCollectorsOrdinal()
    {
        Assert.Equal(
            "rules=radar-keyword-rules-v1;collectors=newssearch,rss,sec,sec-form4,usaspending;",
            DescriptorFor("usaspending", "sec-form4", "rss", "newssearch", "sec"));
    }

    [Fact]
    public void EmptyCollectorSet_YieldsStableDescriptor()
    {
        Assert.Equal(
            "rules=radar-keyword-rules-v1;collectors=;",
            new SignalSourceDescriptor(Array.Empty<IEvidenceCollector>()).CanonicalDescriptor());
    }

    [Fact]
    public void NullCollectors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SignalSourceDescriptor(null!));
    }
}
