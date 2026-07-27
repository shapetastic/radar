using Radar.Application.Collectors;
using Radar.Domain.Evidence;

namespace Radar.Application.Tests.Collectors;

/// <summary>
/// Spec 147 — the name-only collector vocabulary: THE ordered-distinct-Ordinal projection both the recorded
/// provenance CSV and a v9 channel's ran/did-not-run split are derived from, with no capacity to collect.
/// </summary>
public sealed class EnabledCollectorVocabularyTests
{
    /// <summary>
    /// A collector whose <see cref="CollectAsync"/> THROWS, so building a vocabulary from instances is proven
    /// to read the name only — the property that lets a name-only vocabulary exist without smuggling a fetch
    /// capability back into a score pass.
    /// </summary>
    private sealed class ThrowingCollector(string name) : IEvidenceCollector
    {
        public string CollectorName { get; } = name;

        public EvidenceSourceType SourceType => EvidenceSourceType.LocalFile;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            throw new InvalidOperationException("The vocabulary must never call CollectAsync.");
    }

    [Fact]
    public void FromNames_OrdersOrdinal_AndDeDupes()
    {
        var vocabulary = EnabledCollectorVocabulary.FromNames(
            ["usaspending", "sec-form4", "rss", "usaspending", "newssearch"]);

        Assert.Equal(["newssearch", "rss", "sec-form4", "usaspending"], vocabulary.CollectorNames);
    }

    [Fact]
    public void FromCollectors_ReadsOnlyTheName_NeverCollects()
    {
        var vocabulary = EnabledCollectorVocabulary.FromCollectors(
        [
            new ThrowingCollector("sec-edgar"),
            new ThrowingCollector("rss"),
            new ThrowingCollector("sec-edgar"),
        ]);

        Assert.Equal(["rss", "sec-edgar"], vocabulary.CollectorNames);
    }

    [Fact]
    public void FromNames_AndFromCollectors_ProduceTheSameProjection()
    {
        // ONE projection, two entry points — the composition root's config-derived path and the library-only
        // instance-derived path must never disagree about what the vocabulary is.
        Assert.Equal(
            EnabledCollectorVocabulary.FromNames(["patents", "fda"]).CollectorNames,
            EnabledCollectorVocabulary
                .FromCollectors([new ThrowingCollector("fda"), new ThrowingCollector("patents")])
                .CollectorNames);
    }

    [Fact]
    public void Empty_IsEmpty()
    {
        Assert.Empty(EnabledCollectorVocabulary.Empty.CollectorNames);
    }

    [Fact]
    public void CollectorNames_AreHandedOutReadOnly_NotAsAMutableBackingArray()
    {
        // This is a process-lifetime singleton every scoring engine reads; a bare array could be cast back to
        // string[] and mutated, silently rewriting what every later snapshot records.
        var names = EnabledCollectorVocabulary.FromNames(["rss"]).CollectorNames;

        Assert.IsNotType<string[]>(names);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)names)[0] = "tampered");
    }

    [Fact]
    public void NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EnabledCollectorVocabulary.FromNames(null!));
        Assert.Throws<ArgumentNullException>(() => EnabledCollectorVocabulary.FromCollectors(null!));
    }
}
