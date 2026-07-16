using System.Globalization;

using Radar.Application.Collectors;
using Radar.Application.Filings;
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

    /// <summary>
    /// A stub AI directional-filing source returning a fixed <see cref="ScoringDescriptor"/>. Its
    /// <see cref="ProduceAsync"/> THROWS to prove the descriptor never triggers signal production — it reads
    /// only the scoring descriptor.
    /// </summary>
    private sealed class FakeAiFilingSource(string descriptor) : IDirectionalFilingSignalSource
    {
        public string ScoringDescriptor() => descriptor;

        public Task<IReadOnlyList<DirectionalFilingSignal>> ProduceAsync(
            IReadOnlyList<EvidenceItem> candidateEvidence, DateTimeOffset asOfUtc, CancellationToken ct) =>
            throw new InvalidOperationException("The descriptor must never call ProduceAsync.");
    }

    private static string DescriptorFor(params string[] names) =>
        new SignalSourceDescriptor(names.Select(n => (IEvidenceCollector)new FakeCollector(n)))
            .CanonicalDescriptor();

    private static string DescriptorWithAi(string aiDescriptor, params string[] names) =>
        new SignalSourceDescriptor(
                names.Select(n => (IEvidenceCollector)new FakeCollector(n)),
                new FakeAiFilingSource(aiDescriptor))
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
        Assert.Contains("radar-keyword-rules-v3", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptor_OrdersCollectorsOrdinal()
    {
        Assert.Equal(
            "rules=radar-keyword-rules-v3;collectors=newssearch,rss,sec,sec-form4,usaspending;",
            DescriptorFor("usaspending", "sec-form4", "rss", "newssearch", "sec"));
    }

    [Fact]
    public void EmptyCollectorSet_YieldsStableDescriptor()
    {
        Assert.Equal(
            "rules=radar-keyword-rules-v3;collectors=;",
            new SignalSourceDescriptor(Array.Empty<IEvidenceCollector>()).CanonicalDescriptor());
    }

    [Fact]
    public void NullCollectors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SignalSourceDescriptor(null!));
    }

    [Fact]
    public void NullAiSource_YieldsCollectorsOnlyDescriptor_NoAiSegment()
    {
        // Backward-compat / AI-off parity: a null aiFilingSource must reproduce the exact spec-95 descriptor
        // with NO ai= segment, so the pinned AI-off default fingerprint is unchanged.
        var descriptor = DescriptorFor("rss", "sec", "usaspending");

        Assert.Equal(
            "rules=radar-keyword-rules-v3;collectors=rss,sec,usaspending;",
            descriptor);
        Assert.DoesNotContain("ai=", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void AiSource_AppendsEscapedAiSegmentAfterCollectors()
    {
        // A registered AI source appends exactly ai={Escape(descriptor)}; after the collectors=…; segment. The
        // real descriptor's internal '=' and ';' delimiters are percent-escaped so the outer serialization stays
        // injective (the ai segment cannot spill into a fake extra field).
        Assert.Equal(
            "rules=radar-keyword-rules-v3;collectors=rss,sec,usaspending;"
                + "ai=directional-filing:str%3D6%3Bnov%3D6%3Bminconf%3D0.6;",
            DescriptorWithAi("directional-filing:str=6;nov=6;minconf=0.6", "rss", "sec", "usaspending"));
    }

    [Fact]
    public void AiSource_EscapesReservedDelimiters_KeepsSerializationInjective()
    {
        // A reserved delimiter (=, ;, ,, %) inside the AI descriptor must be percent-escaped so the ai= segment
        // cannot collide with a different descriptor (injectivity, AD-3).
        var descriptor = DescriptorWithAi("a=b;c,d%e", "rss");

        Assert.Equal(
            "rules=radar-keyword-rules-v3;collectors=rss;ai=a%3Db%3Bc%2Cd%25e;",
            descriptor);
    }

    [Fact]
    public void AiSource_ChangesDescriptorVsNullAiSource()
    {
        // Enabling the AI path (vs. AI off) changes the descriptor — closing the AD-10 comparability gap.
        Assert.NotEqual(
            DescriptorFor("rss"),
            DescriptorWithAi("directional-filing:str=6;nov=6;minconf=0.6", "rss"));
    }
}
