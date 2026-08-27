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

    /// <summary>
    /// Builds the vocabulary the way a library-only composition does — FROM the collector instances — so
    /// these tests still prove the descriptor never triggers collection (the fakes' CollectAsync throws).
    /// </summary>
    private static EnabledCollectorVocabulary Vocabulary(params string[] names) =>
        EnabledCollectorVocabulary.FromCollectors(names.Select(n => (IEvidenceCollector)new FakeCollector(n)));

    private static SignalSourceDescriptor Build(params string[] names) => new(Vocabulary(names));

    private static SignalSourceDescriptor BuildWithAi(string aiDescriptor, params string[] names) =>
        new(Vocabulary(names), new FakeAiFilingSource(aiDescriptor));

    private static string DescriptorFor(params string[] names) => Build(names).CanonicalDescriptor();

    private static string ProvenanceFor(params string[] names) => Build(names).CollectionProvenance();

    private static string DescriptorWithAi(string aiDescriptor, params string[] names) =>
        BuildWithAi(aiDescriptor, names).CanonicalDescriptor();

    /// <summary>
    /// The spec-194 §2 news-read segment every identity descriptor now ends with. Taken from the real type
    /// rather than written as a literal, so these expectations describe the composition rather than restating
    /// the segment's internals (which <see cref="NewsJudgmentScoringIdentityTests"/> pins directly).
    /// </summary>
    private static readonly string NewsDisabled = NewsJudgmentScoringIdentity.Disabled.Segment;

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

        Assert.Equal("rules=radar-keyword-rules-v8;" + NewsDisabled, identity);
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
        Assert.Contains("radar-keyword-rules-v8", descriptor, StringComparison.Ordinal);
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
        Assert.Equal("collectors=;", new SignalSourceDescriptor(EnabledCollectorVocabulary.Empty)
            .CollectionProvenance());
    }

    [Fact]
    public void NullCollectors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SignalSourceDescriptor(null!));
    }

    // ---- spec 147: the pass kind is a SECOND fact, and it never touches identity ----------------------

    [Fact]
    public void CollectedPass_RendersExactlyThePre147ProvenanceString()
    {
        // THE byte-identical criterion for full/collect/replay: a pass that collected renders the bare CSV
        // and NEVER a second segment, whether the pass kind is omitted or stated explicitly.
        const string expected = "collectors=rss,sec-edgar;";

        Assert.Equal(expected, Build("sec-edgar", "rss").CollectionProvenance());
        Assert.Equal(
            expected,
            new SignalSourceDescriptor(
                    Vocabulary("sec-edgar", "rss"),
                    aiFilingSource: null,
                    new CollectionPassOptions { Kind = CollectionPassKind.Collected })
                .CollectionProvenance());
    }

    [Fact]
    public void NoCollectionThisPass_RecordsTheConfiguredVocabulary_AndMarksThePass()
    {
        // The spec-147 fix: a score pass's snapshot must not claim "no collectors" over evidence a collect
        // pass genuinely gathered. The configured set is recorded, and the marker says collection did not
        // happen HERE.
        Assert.Equal(
            "collectors=rss,sec-edgar;collection=none-this-pass;",
            ScorePassDescriptor("sec-edgar", "rss").CollectionProvenance());
    }

    [Fact]
    public void NoCollectionThisPass_WithAnEmptyVocabulary_IsStillDistinguishableFromNoCollectorsConfigured()
    {
        // The two states this string used to conflate, side by side. Neither is empty, and they differ.
        var noCollectorsConfigured = new SignalSourceDescriptor(EnabledCollectorVocabulary.Empty)
            .CollectionProvenance();
        var noCollectionThisPass = ScorePassDescriptor().CollectionProvenance();

        Assert.Equal("collectors=;", noCollectorsConfigured);
        Assert.Equal("collectors=;collection=none-this-pass;", noCollectionThisPass);
        Assert.NotEqual(noCollectorsConfigured, noCollectionThisPass);
        Assert.NotEmpty(noCollectionThisPass);
    }

    [Fact]
    public void PassKind_DoesNotTouchIdentity_NorTheEnabledCollectorNames()
    {
        // The pass kind is provenance, exactly like the collector set (spec 141): it must not reach the
        // fingerprint input, and it must not change what a v9 channel sees as the vocabulary.
        var collected = Build("rss", "sec-edgar");
        var scorePass = ScorePassDescriptor("rss", "sec-edgar");

        Assert.Equal(collected.CanonicalDescriptor(), scorePass.CanonicalDescriptor());
        Assert.DoesNotContain("collection=", collected.CanonicalDescriptor(), StringComparison.Ordinal);
        Assert.DoesNotContain("collection=", scorePass.CanonicalDescriptor(), StringComparison.Ordinal);
        Assert.Equal(collected.EnabledCollectors(), scorePass.EnabledCollectors());
    }

    private static SignalSourceDescriptor ScorePassDescriptor(params string[] names) =>
        new(
            Vocabulary(names),
            aiFilingSource: null,
            new CollectionPassOptions { Kind = CollectionPassKind.NoCollectionThisPass });

    // ---- spec 151: the attribution mode is a THIRD fact, and it never touches identity either ----------

    private static SignalSourceDescriptor InferringDescriptor(
        CollectionPassKind kind, params string[] names) =>
        new(
            Vocabulary(names),
            aiFilingSource: null,
            new CollectionPassOptions { Kind = kind },
            new CollectorAttributionOptions { InferLegacyAttribution = true });

    [Fact]
    public void InferenceOff_RendersExactlyThePre151ProvenanceString()
    {
        // THE byte-identical criterion for every deployment that does not opt in: omitting the options, or
        // stating them with inference off, must render the string spec 147 rendered — no third segment.
        const string expected = "collectors=rss,sec-edgar;";

        Assert.Equal(expected, Build("sec-edgar", "rss").CollectionProvenance());
        Assert.Equal(
            expected,
            new SignalSourceDescriptor(
                    Vocabulary("sec-edgar", "rss"),
                    aiFilingSource: null,
                    collectionPass: null,
                    new CollectorAttributionOptions())
                .CollectionProvenance());
        Assert.Equal(
            expected,
            new SignalSourceDescriptor(
                    Vocabulary("sec-edgar", "rss"),
                    aiFilingSource: null,
                    collectionPass: null,
                    new CollectorAttributionOptions { InferLegacyAttribution = false })
                .CollectionProvenance());
    }

    [Fact]
    public void InferenceOn_MarksTheProvenance_AsATrailingSegment()
    {
        // A series scored over re-derived attribution must never read as one scored over first-hand
        // attribution. The marker trails the existing segments so a reader that only knows `collectors=`
        // (and `collection=`) is unaffected.
        Assert.Equal(
            "collectors=rss,sec-edgar;attribution=inferred-legacy;",
            InferringDescriptor(CollectionPassKind.Collected, "sec-edgar", "rss").CollectionProvenance());
    }

    [Fact]
    public void InferenceOn_ComposesWithTheNoCollectionMarker()
    {
        // The two markers are orthogonal facts (did this pass collect? does this pass infer?) and a standalone
        // score pass re-scoring accrued history is exactly where both are true at once.
        Assert.Equal(
            "collectors=rss,sec-edgar;collection=none-this-pass;attribution=inferred-legacy;",
            InferringDescriptor(CollectionPassKind.NoCollectionThisPass, "sec-edgar", "rss")
                .CollectionProvenance());
    }

    [Fact]
    public void AttributionMode_DoesNotTouchIdentity_NorTheEnabledCollectorNames()
    {
        // The spec-151 no-fingerprint-move criterion at its source: attribution is DATA, not scoring
        // configuration. It changes which evidence a v9 channel can see, not what hypothesis the strategy
        // scores — and folding it in would re-stamp every v8 strategy in the process for a setting that
        // cannot affect them.
        var recordedOnly = Build("rss", "sec-edgar");
        var inferring = InferringDescriptor(CollectionPassKind.Collected, "rss", "sec-edgar");

        Assert.Equal(recordedOnly.CanonicalDescriptor(), inferring.CanonicalDescriptor());
        Assert.DoesNotContain("attribution=", recordedOnly.CanonicalDescriptor(), StringComparison.Ordinal);
        Assert.DoesNotContain("attribution=", inferring.CanonicalDescriptor(), StringComparison.Ordinal);
        Assert.Equal(recordedOnly.EnabledCollectors(), inferring.EnabledCollectors());

        // …and it IS recorded, so the two are not merely equal everywhere.
        Assert.NotEqual(recordedOnly.CollectionProvenance(), inferring.CollectionProvenance());
    }

    [Fact]
    public void NullAiSource_YieldsRulesOnlyIdentity_NoAiSegment()
    {
        // AI-off parity: a null aiFilingSource appends nothing, so the AI-off identity is the bare rules
        // segment.
        var descriptor = DescriptorFor("rss", "sec", "usaspending");

        Assert.Equal("rules=radar-keyword-rules-v8;" + NewsDisabled, descriptor);
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
            "rules=radar-keyword-rules-v8;ai=directional-filing:str%3D6%3Bnov%3D6%3Bminconf%3D0.6;"
                + NewsDisabled,
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

        Assert.Equal("rules=radar-keyword-rules-v8;ai=a%3Db%3Bc%2Cd%25e;" + NewsDisabled, descriptor);
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

    // ---- spec 194 §2: the news read is in the identity, and it is appended LAST ------------------------

    private static SignalSourceDescriptor BuildWithNews(
        NewsJudgmentScoringIdentity news, params string[] names) =>
        new(Vocabulary(names), null, null, null, news);

    [Fact]
    public void NewsSegment_IsAlwaysPresent_EvenWhenJudgmentIsDisabled()
    {
        // A disabled judgment renders an EXPLICIT `news=disabled:…;` rather than nothing. Rendering nothing
        // would make "judgment off" byte-identical to a pre-194 composition, which is exactly the ambiguity
        // spec 147 removed from `collectors=;`. It also means an omitted identity — a library composition
        // that never configured the judgment — renders the same disabled segment, because that is what it
        // scores as.
        var omitted = DescriptorFor("rss");
        var explicitlyDisabled = BuildWithNews(NewsJudgmentScoringIdentity.Disabled, "rss")
            .CanonicalDescriptor();

        Assert.Equal(omitted, explicitlyDisabled);
        Assert.Contains("news=disabled:", omitted, StringComparison.Ordinal);
        Assert.EndsWith(";", omitted, StringComparison.Ordinal);
    }

    [Fact]
    public void NewsSegment_IsAppendedAfterRulesAndAi_SoThePrefixStaysByteStable()
    {
        // Segment ORDER is load-bearing: rules= then ai= then news=. Appending LAST keeps the pre-194 prefix
        // byte-stable, so a moved pin is unambiguously attributable to the new segment rather than to a
        // reshuffle of the old ones.
        var descriptor = new SignalSourceDescriptor(
            Vocabulary("rss"),
            new FakeAiFilingSource("directional-filing:str=6"),
            null,
            null,
            NewsJudgmentScoringIdentity.Disabled).CanonicalDescriptor();

        var rules = descriptor.IndexOf("rules=", StringComparison.Ordinal);
        var ai = descriptor.IndexOf("ai=", StringComparison.Ordinal);
        var news = descriptor.IndexOf("news=", StringComparison.Ordinal);

        Assert.Equal(0, rules);
        Assert.True(rules < ai && ai < news, descriptor);
        Assert.StartsWith("rules=radar-keyword-rules-v8;ai=", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void NewsJudgmentEnabled_ChangesIdentity_ButNotCollectionProvenance()
    {
        // THE spec-194 §2 acceptance criterion at the descriptor: turning the news judgment on is a change to
        // what is scored (a validated judgment can mint a directional signal), so it must move the identity —
        // while leaving the collector provenance, which is about collection and not about reading, untouched.
        var off = BuildWithNews(NewsJudgmentScoringIdentity.Disabled, "rss");
        var on = BuildWithNews(EnabledIdentity("cohort-a"), "rss");

        Assert.NotEqual(off.CanonicalDescriptor(), on.CanonicalDescriptor());
        Assert.Equal(off.CollectionProvenance(), on.CollectionProvenance());
        Assert.DoesNotContain("news=", on.CollectionProvenance(), StringComparison.Ordinal);
    }

    [Fact]
    public void NewsSegment_DoesNotRepeatTheMediaCollapseVersion()
    {
        // spec 194 §2, stated explicitly: media-collapse-v2 is already folded through
        // MediaAttentionCollapse.CanonicalDescriptor() as its OWN hashed field, so duplicating it inside the
        // news segment would hash one fact twice and make a future media-collapse bump look like two changes.
        Assert.DoesNotContain(
            MediaAttentionCollapse.Version,
            BuildWithNews(EnabledIdentity("cohort-a"), "rss").CanonicalDescriptor(),
            StringComparison.Ordinal);
    }

    private static NewsJudgmentScoringIdentity EnabledIdentity(string cohortKey) =>
        NewsJudgmentScoringIdentity.ForPresentationCohort(
            cohortKey, "news-judgment-signal-v1", ["Improving>Positive"], 4, 3, 1, 4, 0.5m);
}
