using System.Reflection;

using Radar.Application.Collectors;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Collectors;
using Radar.Infrastructure.DependencyInjection;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.Collectors;

/// <summary>
/// Spec 151 — the legacy collector-attribution inference table, and the resolver that consults it.
/// <para>
/// Three of these tests are load-bearing rather than illustrative:
/// <list type="bullet">
/// <item><see cref="GroundTruth_InferenceAgreesWithTheRecordedCollector"/> reproduces the live shapes of the
/// three collectors that actually have recorded exemplars (the 341-record cohort) and infers while IGNORING
/// the recorded value — the same holdout the pre-flight validation ran, at 341/341 agreement.</item>
/// <item><see cref="GdeltNewsArticle_InfersGdelt_NotNewssearch"/> pins the regression the naive
/// <c>sourceType ⇒ collector</c> rule would have shipped: it would have misattributed five live GDELT
/// records to <c>newssearch</c>.</item>
/// <item><see cref="EveryShippedCollector_IsCoveredByTheTable"/> and
/// <see cref="FilingCollectors_AreStillClosedAtThree"/> guard the two ways this table silently rots.</item>
/// </list>
/// </para>
/// </summary>
public sealed class LegacyCollectorAttributionInferenceTests
{
    private static EvidenceItem Evidence(
        EvidenceSourceType sourceType, params (string Key, string Value)[] metadata) =>
        new EvidenceBuilder()
            .WithSourceType(sourceType)
            .WithMetadataJson(EvidenceMetadata.Compose(
                metadata.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal), []))
            .Build();

    // ---- §1 the whole mapping table, INCLUDING the collectors with zero accrued records ----------------

    public static TheoryData<EvidenceSourceType, string, string, string> MappingTable() => new()
    {
        // sourceType, exclusive marker key, a realistic value, expected collector
        {
            EvidenceSourceType.NewsArticle, "newsSearchFeedUrl",
            "https://news.google.com/rss/search?q=%22Acme%22&hl=en-US", RadarCollectorNames.NewsSearch
        },
        {
            EvidenceSourceType.NewsArticle, "gdeltFeedUrl",
            "https://api.gdeltproject.org/api/v2/doc/doc?query=%22Acme%22", RadarCollectorNames.GdeltNews
        },
        {
            EvidenceSourceType.PressRelease, "rssFeedUrl",
            "https://ir.acme.com/rss/news-releases.xml", RadarCollectorNames.Rss
        },
        {
            EvidenceSourceType.GovernmentContract, "usaSpendingFeedUrl",
            "https://api.usaspending.gov/api/v2/search/spending_by_award/", RadarCollectorNames.UsaSpending
        },
        {
            EvidenceSourceType.Filing, "insiderDirection", "Positive", RadarCollectorNames.SecForm4
        },
        {
            EvidenceSourceType.Filing, "ownershipCategory", "PassiveInstitutional", RadarCollectorNames.Sec13DG
        },
        // The five collectors with zero accrued records — reasoned, not validated, and covered so that
        // enabling one of them later does not leave its historical evidence unattributed.
        {
            EvidenceSourceType.RegulatoryApproval, "fdaFeedUrl",
            "https://api.fda.gov/device/510k.json", RadarCollectorNames.Fda
        },
        {
            EvidenceSourceType.JobPosting, "hiringFeedUrl",
            "https://boards-api.greenhouse.io/v1/boards/acme/jobs", RadarCollectorNames.HiringAts
        },
        {
            EvidenceSourceType.Patent, "patentsFeedUrl",
            "https://api.uspto.gov/api/v1/patent/applications/search", RadarCollectorNames.Patents
        },
        {
            EvidenceSourceType.Trademark, "trademarkFeedUrl",
            "https://tmsearch.uspto.gov/api/v1/tmsearch", RadarCollectorNames.Trademarks
        },
        {
            EvidenceSourceType.LocalFile, "sourceFile", "acme-001.json", RadarCollectorNames.LocalFile
        },
    };

    [Theory]
    [MemberData(nameof(MappingTable))]
    public void MarkerKey_IdentifiesItsCollector(
        EvidenceSourceType sourceType, string markerKey, string markerValue, string expectedCollector)
    {
        var inferred = LegacyCollectorAttributionInference.Infer(Evidence(sourceType, (markerKey, markerValue)));

        Assert.Equal(expectedCollector, inferred);
    }

    [Fact]
    public void FilingWithoutEitherSecMarker_InfersSecEdgar_ByElimination()
    {
        // sec-edgar writes no exclusive key of its own, so it is the residue of a CLOSED three-collector set.
        // Deliberately NOT keyed on metadata.form: that discriminates only because Radar:Sec:Forms currently
        // excludes 4 and SC 13*, so a config edit would retroactively corrupt every historical inference.
        var eightK = Evidence(
            EvidenceSourceType.Filing,
            ("secFeedUrl", "https://data.sec.gov/submissions/CIK0000320193.json"),
            ("accessionNumber", "0000320193-26-000042"),
            ("form", "8-K"),
            ("filingDate", "2026-07-02"));

        Assert.Equal(RadarCollectorNames.SecEdgar, LegacyCollectorAttributionInference.Infer(eightK));
    }

    [Fact]
    public void FilingElimination_DoesNotDependOnTheFormString()
    {
        // The same record with the form key absent, and with a form the SEC collector would never fetch under
        // the shipped config: both still resolve to sec-edgar, because the discriminator is the ABSENCE of the
        // other two collectors' markers.
        Assert.Equal(
            RadarCollectorNames.SecEdgar,
            LegacyCollectorAttributionInference.Infer(Evidence(EvidenceSourceType.Filing)));
        Assert.Equal(
            RadarCollectorNames.SecEdgar,
            LegacyCollectorAttributionInference.Infer(Evidence(EvidenceSourceType.Filing, ("form", "S-1"))));
    }

    // ---- §2 the ground-truth holdout: the 341 records that DO carry recorded attribution ---------------

    /// <summary>
    /// Reproduces the live record shapes of the three collectors with recorded exemplars — <c>newssearch</c>
    /// (337 records), <c>sec-form4</c> (2) and <c>RssPressReleaseCollector</c> (2) — carrying BOTH the recorded
    /// collector key and their real metadata. The inference is run while ignoring the recorded value and must
    /// reproduce it exactly. That is the 341/341 pre-flight result, reduced to a regression test.
    /// <para>
    /// What it does NOT prove, stated because the split matters: <c>sec-edgar</c> (1,160 live records),
    /// <c>sec-13dg</c> (850), <c>usaspending</c> (21), GDELT <c>news</c> (5) and the five zero-record
    /// collectors have no recorded exemplar anywhere, so their mappings are REASONED — corroborated for
    /// <c>sec-form4</c>/<c>sec-13dg</c> by their marker keys appearing on 100% of their live records, and
    /// carried for the rest by the collector source itself.
    /// </para>
    /// </summary>
    public static TheoryData<string, EvidenceSourceType, (string Key, string Value)[]> GroundTruthRecords() => new()
    {
        {
            RadarCollectorNames.NewsSearch, EvidenceSourceType.NewsArticle,
            [
                ("quality", "Medium"),
                ("newsSearchFeedUrl", "https://news.google.com/rss/search?q=%22Acme+Corp%22&hl=en-US&gl=US"),
                ("url", "https://finance.yahoo.com/news/acme-corp-q2-123000000.html"),
                ("publisher", "Yahoo Finance"),
                ("feedName", "Acme Corp news search"),
                ("pubDate", "2026-07-14T11:02:00Z"),
            ]
        },
        {
            RadarCollectorNames.SecForm4, EvidenceSourceType.Filing,
            [
                ("quality", "High"),
                ("secFeedUrl", "https://data.sec.gov/submissions/CIK0000320193.json"),
                ("accessionNumber", "0000320193-26-000107"),
                ("form", "4"),
                ("filingDate", "2026-07-09"),
                ("insiderDirection", "Positive"),
                ("insiderNetValue", "412500"),
            ]
        },
        {
            RadarCollectorNames.Rss, EvidenceSourceType.PressRelease,
            [
                ("rssFeedUrl", "https://ir.acme.com/rss/news-releases.xml"),
                ("rssItemId", "https://ir.acme.com/news/2026/acme-wins-contract"),
                ("quality", "Medium"),
            ]
        },
    };

    [Theory]
    [MemberData(nameof(GroundTruthRecords))]
    public void GroundTruth_InferenceAgreesWithTheRecordedCollector(
        string recordedCollector, EvidenceSourceType sourceType, (string Key, string Value)[] metadata)
    {
        // The record as it exists on disk: real metadata PLUS the spec-146 recorded collector.
        var withRecorded = Evidence(
            sourceType,
            [.. metadata, (CollectionProvenanceMetadata.MetadataKey, recordedCollector)]);

        // The holdout: infer from the same record while ignoring what it recorded. Infer() does not read the
        // collector key at all, so passing the full record IS the ignoring — no stripped copy can differ.
        Assert.Equal(recordedCollector, LegacyCollectorAttributionInference.Infer(withRecorded));

        // …and it agrees for the legacy shape too (the same record as it would have been persisted before
        // spec 146 began recording), which is the shape the inference actually exists to serve.
        Assert.Equal(recordedCollector, LegacyCollectorAttributionInference.Infer(Evidence(sourceType, metadata)));
    }

    [Fact]
    public void GdeltNewsArticle_InfersGdelt_NotNewssearch()
    {
        // THE REGRESSION. The spec proposed `sourceType == news_article ⇒ newssearch`, which would have
        // misattributed the five live GDELT records — silently, and with no way for a reader to notice. Two
        // collectors share NewsArticle, so only the exclusive marker key separates them.
        var gdelt = Evidence(
            EvidenceSourceType.NewsArticle,
            ("quality", "Medium"),
            ("gdeltFeedUrl", "https://api.gdeltproject.org/api/v2/doc/doc?query=%22Acme%22&timespan=2w"),
            ("url", "https://www.reuters.com/business/acme-2026-07-11/"),
            ("publisher", "reuters.com"));

        Assert.Equal(RadarCollectorNames.GdeltNews, LegacyCollectorAttributionInference.Infer(gdelt));
        Assert.NotEqual(RadarCollectorNames.NewsSearch, LegacyCollectorAttributionInference.Infer(gdelt));
    }

    // ---- §3 ambiguity stays UNATTRIBUTED — never a best guess -----------------------------------------

    [Fact]
    public void NewsArticleWithNoMarker_IsUnattributed_NotGuessedAsTheCommonCase()
    {
        // 3,360 of the live NewsArticle records are newssearch and 5 are GDELT, so "guess the majority" would
        // be right 99.9% of the time. It is still refused: an unattributed record costs a channel some mass,
        // a wrong one corrupts a channel's meaning.
        Assert.Null(LegacyCollectorAttributionInference.Infer(
            Evidence(EvidenceSourceType.NewsArticle, ("publisher", "Some Outlet"))));
    }

    [Fact]
    public void ContradictoryMarkers_AreUnattributed()
    {
        // Two collector-exclusive keys on one record means the table's premise is wrong FOR THAT RECORD.
        // Picking one would be a coin flip dressed as provenance.
        Assert.Null(LegacyCollectorAttributionInference.Infer(Evidence(
            EvidenceSourceType.NewsArticle,
            ("newsSearchFeedUrl", "https://news.google.com/rss/search?q=acme"),
            ("gdeltFeedUrl", "https://api.gdeltproject.org/api/v2/doc/doc?query=acme"))));

        Assert.Null(LegacyCollectorAttributionInference.Infer(Evidence(
            EvidenceSourceType.Filing,
            ("insiderDirection", "Positive"),
            ("ownershipCategory", "Activist"))));
    }

    [Theory]
    [InlineData(EvidenceSourceType.Manual)]
    [InlineData(EvidenceSourceType.RssFeed)]
    [InlineData(EvidenceSourceType.CompanyBlog)]
    [InlineData(EvidenceSourceType.EarningsTranscript)]
    [InlineData(EvidenceSourceType.SocialMedia)]
    [InlineData(EvidenceSourceType.RegulatoryAnnouncement)]
    [InlineData(EvidenceSourceType.InsiderTransaction)]
    [InlineData(EvidenceSourceType.ConferenceMention)]
    public void SourceTypeWithNoRule_IsUnattributed(EvidenceSourceType sourceType)
    {
        // No shipped collector emits these, so there is nothing to infer FROM. Inventing a rule for a case
        // with no evidence behind it is exactly the over-confidence this table is built to avoid.
        Assert.Null(LegacyCollectorAttributionInference.Infer(
            Evidence(sourceType, ("rssFeedUrl", "https://ir.acme.com/rss.xml"))));
    }

    [Fact]
    public void BlankMarkerValue_DoesNotCount()
    {
        // A present-but-empty key is not a marker; for PressRelease there is no elimination rule, so this is
        // unattributed rather than falling through to the single known collector.
        Assert.Null(LegacyCollectorAttributionInference.Infer(
            Evidence(EvidenceSourceType.PressRelease, ("rssFeedUrl", "   "))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("}{ not json")]
    public void MalformedOrAbsentMetadata_NeverThrows(string? metadataJson)
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithMetadataJson(metadataJson)
            .Build();

        Assert.Null(LegacyCollectorAttributionInference.Infer(evidence));
    }

    [Fact]
    public void NullEvidence_IsUnattributed() =>
        Assert.Null(LegacyCollectorAttributionInference.Infer(null));

    // ---- §4 the resolver: recorded ALWAYS wins ---------------------------------------------------------

    [Fact]
    public void Resolver_PrefersTheRecordedCollector_EvenWhenTheInferenceContradictsIt()
    {
        // A GDELT-marked NewsArticle that RECORDS newssearch. The recorded stamp is the producing collector's
        // own answer and must win unconditionally — not "when they agree", not "when the inference is
        // confident". (This shape should not exist in the wild; the point is which one wins if it does.)
        var evidence = Evidence(
            EvidenceSourceType.NewsArticle,
            ("gdeltFeedUrl", "https://api.gdeltproject.org/api/v2/doc/doc?query=acme"),
            (CollectionProvenanceMetadata.MetadataKey, RadarCollectorNames.NewsSearch));

        var resolved = new InferringCollectorAttributionResolver().Resolve(evidence);

        Assert.Equal(CollectorAttribution.Recorded(RadarCollectorNames.NewsSearch), resolved);
        Assert.Equal(RadarCollectorNames.GdeltNews, LegacyCollectorAttributionInference.Infer(evidence));
    }

    [Fact]
    public void Resolver_ReportsARecordedValueAsRecorded_NeverAsInferred()
    {
        var evidence = Evidence(
            EvidenceSourceType.Filing,
            ("insiderDirection", "Positive"),
            (CollectionProvenanceMetadata.MetadataKey, RadarCollectorNames.SecForm4));

        var resolved = new InferringCollectorAttributionResolver().Resolve(evidence);

        Assert.Equal(CollectorAttributionSource.Recorded, resolved.Source);
        Assert.Equal(RadarCollectorNames.SecForm4, resolved.CollectorName);
    }

    [Fact]
    public void Resolver_MarksAReDerivedValueAsInferred()
    {
        var resolved = new InferringCollectorAttributionResolver().Resolve(
            Evidence(EvidenceSourceType.Filing, ("ownershipCategory", "Activist")));

        Assert.Equal(CollectorAttribution.Inferred(RadarCollectorNames.Sec13DG), resolved);
        Assert.Equal(CollectorAttributionSource.Inferred, resolved.Source);
    }

    [Fact]
    public void Resolver_LeavesAnAmbiguousRecordUnattributed()
    {
        Assert.Equal(
            CollectorAttribution.Unattributed,
            new InferringCollectorAttributionResolver().Resolve(
                Evidence(EvidenceSourceType.NewsArticle, ("publisher", "Some Outlet"))));
    }

    // ---- §5 anti-drift: the two ways this table silently rots ------------------------------------------

    /// <summary>
    /// Every collector shipped in <c>Radar.Infrastructure</c> must be nameable by the table. Without this, a
    /// new collector added later would leave its own accrued evidence permanently unattributable and nobody
    /// would find out until a v9 channel over it scored 0.
    /// </summary>
    [Fact]
    public void EveryShippedCollector_IsCoveredByTheTable()
    {
        var shipped = typeof(RadarCollectorNames).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IEvidenceCollector).IsAssignableFrom(t))
            .Select(t => t.GetField("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Select(f => f?.GetRawConstantValue() as string)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Not vacuous: the reflection must actually have found the shipped collectors.
        Assert.Equal(12, shipped.Length);

        var covered = LegacyCollectorAttributionInference.CoverageBySourceType
            .SelectMany(kvp => kvp.Value)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(shipped, covered);
    }

    /// <summary>
    /// The <c>Filing</c> elimination rule is sound ONLY while <c>sec-edgar</c> is the single Filing emitter
    /// without an exclusive marker. A fourth Filing collector would silently inherit the residue and be
    /// misattributed as <c>sec-edgar</c> — so the set is pinned, and adding one must be a conscious act.
    /// </summary>
    [Fact]
    public void FilingCollectors_AreStillClosedAtThree()
    {
        Assert.Equal(
            new[] { RadarCollectorNames.Sec13DG, RadarCollectorNames.SecEdgar, RadarCollectorNames.SecForm4 }
                .OrderBy(n => n, StringComparer.Ordinal),
            LegacyCollectorAttributionInference.CoverageBySourceType[EvidenceSourceType.Filing]);
    }
}
