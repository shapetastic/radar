using Radar.Infrastructure.Fda;
using Radar.Infrastructure.Gdelt;
using Radar.Infrastructure.Hiring;
using Radar.Infrastructure.News;
using Radar.Infrastructure.Patents;
using Radar.Infrastructure.Rss;
using Radar.Infrastructure.Sec;
using Radar.Infrastructure.Sources;
using Radar.Infrastructure.Trademarks;
using Radar.Infrastructure.UsaSpending;

namespace Radar.Infrastructure.DependencyInjection;

/// <summary>
/// The public projection of each shipped collector's stable provenance name (spec 147). The collector
/// classes themselves are <c>internal</c> — deliberately, since Infrastructure's public surface is its
/// <c>Add*</c> composition helpers — so the composition root cannot name
/// <c>RssPressReleaseCollector.Name</c> directly. Every member here is a <c>const</c> ALIAS of that class's
/// own <c>Name</c> const, so this is a re-EXPORT rather than a second definition: the two cannot drift, by
/// construction, and changing a collector's name in one place changes it here too.
/// <para>
/// This exists because a spec-144 standalone <c>score</c> pass registers no collector at all: it needs the
/// collector VOCABULARY (names, for recorded provenance and for the spec-146 channel guard) without the
/// collection CAPABILITY. See <c>EnabledCollectorVocabulary</c>.
/// </para>
/// </summary>
public static class RadarCollectorNames
{
    /// <summary>Per-company RSS press releases (<c>Radar:Collectors</c> kind <c>"rss"</c>).</summary>
    public const string Rss = RssPressReleaseCollector.Name;

    /// <summary>Offline local-file evidence (kind <c>"localfile"</c>).</summary>
    public const string LocalFile = LocalFileEvidenceCollector.Name;

    /// <summary>SEC EDGAR filings (kind <c>"sec"</c>).</summary>
    public const string SecEdgar = SecEdgarFilingCollector.Name;

    /// <summary>SEC Form 4 insider transactions (kind <c>"secform4"</c>).</summary>
    public const string SecForm4 = SecForm4Collector.Name;

    /// <summary>SEC Schedule 13D/13G beneficial ownership (kind <c>"sec13dg"</c>).</summary>
    public const string Sec13DG = Sec13DGCollector.Name;

    /// <summary>USAspending federal contract awards (kind <c>"usaspending"</c>).</summary>
    public const string UsaSpending = UsaSpendingContractCollector.Name;

    /// <summary>GDELT news coverage (kind <c>"news"</c>).</summary>
    public const string GdeltNews = GdeltNewsCollector.Name;

    /// <summary>Google-News-style attention search (kind <c>"newssearch"</c>).</summary>
    public const string NewsSearch = NewsAttentionCollector.Name;

    /// <summary>Applicant-tracking-system hiring boards (kind <c>"hiringats"</c>).</summary>
    public const string HiringAts = HiringBoardCollector.Name;

    /// <summary>USPTO patent activity (kind <c>"patents"</c>).</summary>
    public const string Patents = PatentActivityCollector.Name;

    /// <summary>openFDA device clearances (kind <c>"fda"</c>).</summary>
    public const string Fda = FdaClearanceCollector.Name;

    /// <summary>USPTO trademark activity (kind <c>"trademarks"</c>).</summary>
    public const string Trademarks = TrademarkActivityCollector.Name;
}
