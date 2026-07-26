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

    private static SignalSourceDescriptor Build(params string[] names) =>
        new(names.Select(n => (IEvidenceCollector)new FakeCollector(n)));

    private static SignalSourceDescriptor BuildWithAi(string aiDescriptor, params string[] names) =>
        new(
            names.Select(n => (IEvidenceCollector)new FakeCollector(n)),
            new FakeAiFilingSource(aiDescriptor));

    private static string DescriptorFor(params string[] names) => Build(names).CanonicalDescriptor();

    private static string ProvenanceFor(params string[] names) => Build(names).CollectionProvenance();

    private static string DescriptorWithAi(string aiDescriptor, params string[] names) =>
        BuildWithAi(aiDescriptor, names).CanonicalDescriptor();

    [Fact]
    public void CollectorToggle_LeavesIdentityUnchanged_MovesOnlyCollectionProvenance()
    {
        // THE spec-141 property, asserted at its source: "what was collected" and "what hypothesis produced
        // this score" are separate facts. Adding, removing or swapping a collector must leave the IDENTITY
        // descriptor (the fingerprint input) byte-identical while the PROVENANCE descriptor moves.
        var baseline = Build("rss", "sec", "usaspending");
        var added = Build("rss", "sec", "usaspending", "fda");
        var removed = Build("rss", "sec");
        var none = Build();

        Assert.Equal(baseline.CanonicalDescriptor(), added.CanonicalDescriptor());
        Assert.Equal(baseline.CanonicalDescriptor(), removed.CanonicalDescriptor());
        Assert.Equal(baseline.CanonicalDescriptor(), none.CanonicalDescriptor());

        Assert.NotEqual(baseline.CollectionProvenance(), added.CollectionProvenance());
        Assert.NotEqual(baseline.CollectionProvenance(), removed.CollectionProvenance());
        Assert.NotEqual(baseline.CollectionProvenance(), none.CollectionProvenance());
    }

    [Fact]
    public void Identity_CarriesNoCollectorsSegment()
    {
        // The collector CSV must not merely be equal across toggles — it must be ABSENT from identity, so no
        // future edit can reintroduce it by accident.
        var identity = DescriptorFor("rss", "sec", "usaspending");

        Assert.Equal("rules=radar-keyword-rules-v6;", identity);
        Assert.DoesNotContain("collectors=", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("usaspending", identity, StringComparison.Ordinal);
    }

    [Fact]
    public void SameCollectorSet_ProducesSameProvenance()
    {
        Assert.Equal(
            ProvenanceFor("rss", "sec", "usaspending"),
            ProvenanceFor("rss", "sec", "usaspending"));
    }

    [Fact]
    public void DifferentCollectorSet_ProducesDifferentProvenance()
    {
        var baseline = ProvenanceFor("rss", "sec", "usaspending");

        Assert.NotEqual(baseline, ProvenanceFor("rss", "sec", "usaspending", "newssearch")); // added
        Assert.NotEqual(baseline, ProvenanceFor("rss", "sec")); // removed
    }

    [Fact]
    public void DuplicateCollectorName_DoesNotChangeProvenance()
    {
        Assert.Equal(
            ProvenanceFor("rss", "sec"),
            ProvenanceFor("rss", "sec", "rss"));
    }

    [Fact]
    public void RegistrationOrder_DoesNotMatter()
    {
        Assert.Equal(
            ProvenanceFor("rss", "sec", "usaspending"),
            ProvenanceFor("usaspending", "rss", "sec"));
    }

    [Fact]
    public void Descriptors_AreCultureInvariant()
    {
        var invariantIdentity = DescriptorFor("rss", "sec", "usaspending");
        var invariantProvenance = ProvenanceFor("rss", "sec", "usaspending");

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(invariantIdentity, DescriptorFor("rss", "sec", "usaspending"));
            Assert.Equal(invariantProvenance, ProvenanceFor("rss", "sec", "usaspending"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Identity_ContainsRuleSetVersion()
    {
        var descriptor = DescriptorFor("rss");

        Assert.Contains(KeywordSignalExtractor.RuleSetVersion, descriptor, StringComparison.Ordinal);
        Assert.Contains("radar-keyword-rules-v6", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void Provenance_OrdersCollectorsOrdinal()
    {
        Assert.Equal(
            "collectors=newssearch,rss,sec,sec-form4,usaspending;",
            ProvenanceFor("usaspending", "sec-form4", "rss", "newssearch", "sec"));
    }

    [Fact]
    public void EmptyCollectorSet_YieldsStableProvenance()
    {
        Assert.Equal(
            "collectors=;",
            new SignalSourceDescriptor(Array.Empty<IEvidenceCollector>()).CollectionProvenance());
    }

    [Fact]
    public void NullCollectors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SignalSourceDescriptor(null!));
    }

    [Fact]
    public void NullAiSource_YieldsRulesOnlyIdentity_NoAiSegment()
    {
        // AI-off parity: a null aiFilingSource appends nothing, so the AI-off identity is the bare rules
        // segment.
        var descriptor = DescriptorFor("rss", "sec", "usaspending");

        Assert.Equal("rules=radar-keyword-rules-v6;", descriptor);
        Assert.DoesNotContain("ai=", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void AiSource_AppendsEscapedAiSegmentToIdentity()
    {
        // A registered AI source appends exactly ai={Escape(descriptor)}; after the rules=…; segment. The real
        // descriptor's internal '=' and ';' delimiters are percent-escaped so the outer serialization stays
        // injective (the ai segment cannot spill into a fake extra field). The ai segment stays on the IDENTITY
        // side (spec 141): it carries per-signal magnitudes and the reading model, which change signal
        // DIRECTION — that is scoring identity, not a collector set.
        Assert.Equal(
            "rules=radar-keyword-rules-v6;ai=directional-filing:str%3D6%3Bnov%3D6%3Bminconf%3D0.6;",
            DescriptorWithAi("directional-filing:str=6;nov=6;minconf=0.6", "rss", "sec", "usaspending"));
    }

    [Fact]
    public void AiSource_DoesNotLeakIntoCollectionProvenance()
    {
        // The two strings stay disjoint: provenance is the collector set and nothing else.
        var provenance = BuildWithAi("directional-filing:str=6;nov=6;minconf=0.6", "rss").CollectionProvenance();

        Assert.Equal("collectors=rss;", provenance);
        Assert.DoesNotContain("ai=", provenance, StringComparison.Ordinal);
        Assert.DoesNotContain("rules=", provenance, StringComparison.Ordinal);
    }

    [Fact]
    public void AiSource_EscapesReservedDelimiters_KeepsSerializationInjective()
    {
        // A reserved delimiter (=, ;, ,, %) inside the AI descriptor must be percent-escaped so the ai= segment
        // cannot collide with a different descriptor (injectivity, AD-3).
        var descriptor = DescriptorWithAi("a=b;c,d%e", "rss");

        Assert.Equal("rules=radar-keyword-rules-v6;ai=a%3Db%3Bc%2Cd%25e;", descriptor);
    }

    [Fact]
    public void AiSource_ChangesIdentityVsNullAiSource()
    {
        // Enabling the AI path (vs. AI off) changes the IDENTITY descriptor — closing the AD-10 comparability
        // gap. Unlike a collector toggle, this one genuinely changes what is scored.
        Assert.NotEqual(
            DescriptorFor("rss"),
            DescriptorWithAi("directional-filing:str=6;nov=6;minconf=0.6", "rss"));
    }
}
