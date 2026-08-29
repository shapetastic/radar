using System.Globalization;
using System.Reflection;

using Radar.Application.Scoring;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 198 §3 — the news-feed QUERY identity: the hashed record of the recency window applied to the
/// Google News RSS search. These pin the SEGMENT itself; <c>SignalSourceDescriptorTests</c> pins where it
/// lands in the composed descriptor and <c>ScoringConfigFingerprintTests</c> pins the resulting stamps.
/// </summary>
public sealed class NewsQueryScoringIdentityTests
{
    [Fact]
    public void DefaultRecencyWindowDays_IsTheShippedSevenDayWindow()
    {
        // THE one definition. Both NewsCollectorOptions (Infrastructure) and NewsWorkerOptions (Worker)
        // default off this const, so the value a live run SENDS and the value the fingerprint HASHES cannot
        // drift. Pinned as a literal here because moving it moves all six fingerprint pins, which must be a
        // conscious act.
        Assert.Equal(7, NewsQueryScoringIdentity.DefaultRecencyWindowDays);
        Assert.Equal(
            NewsQueryScoringIdentity.DefaultRecencyWindowDays, NewsQueryScoringIdentity.Default.WindowDays);
    }

    [Fact]
    public void DisabledWindow_RendersTheEmptySegment()
    {
        // THE additivity rule, and the reason this segment is conditional where spec 194's is not: a
        // disabled window must reproduce the post-197 descriptor byte-for-byte.
        Assert.Equal(string.Empty, NewsQueryScoringIdentity.None.Segment);
        Assert.Equal(0, NewsQueryScoringIdentity.None.WindowDays);
        Assert.Equal(string.Empty, NewsQueryScoringIdentity.ForWindowDays(0).Segment);
    }

    [Fact]
    public void PositiveWindow_RendersTheCanonicalSegment()
    {
        Assert.Equal("newsquery=7d;", NewsQueryScoringIdentity.Default.Segment);
        Assert.Equal("newsquery=1d;", NewsQueryScoringIdentity.ForWindowDays(1).Segment);
        Assert.Equal("newsquery=30d;", NewsQueryScoringIdentity.ForWindowDays(30).Segment);
    }

    [Fact]
    public void DifferentWindows_RenderDifferentSegments()
    {
        // Injectivity (AD-3): two genuinely different collection regimes must never share one segment, or
        // the whole fingerprint fold is decorative.
        var segments = new[] { 0, 1, 2, 7, 14, 30, 365 }
            .Select(d => NewsQueryScoringIdentity.ForWindowDays(d).Segment)
            .ToList();

        Assert.Equal(segments.Count, segments.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NegativeWindow_Throws()
    {
        // A negative window is configuration nonsense, not a disabled filter. Reading it as "off" is how a
        // typo becomes an invisible collection change — the fail-open shape spec 138/149 already had to
        // close once.
        Assert.Throws<ArgumentOutOfRangeException>(() => NewsQueryScoringIdentity.ForWindowDays(-1));
    }

    [Fact]
    public void Segment_IsCultureInvariant()
    {
        var invariant = NewsQueryScoringIdentity.ForWindowDays(1234).Segment;

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(invariant, NewsQueryScoringIdentity.ForWindowDays(1234).Segment);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal("newsquery=1234d;", invariant);
    }

    [Fact]
    public void Type_ReferencesNoNewsOrInfrastructureType_SoAScorePassCanComposeIt()
    {
        // THE spec-147 EnabledCollectorVocabulary / spec-194 NewsJudgmentScoringIdentity posture, asserted
        // on the TYPE GRAPH rather than trusted: this type holds a number and a string. It cannot construct
        // an HttpClient, cannot issue a request and references nothing that can — which is what lets a
        // spec-144 `score` pass and a spec-139 replay compose the SAME identity a `full` run composes,
        // WITHOUT registering the newssearch collector, and what keeps the spec-177/179 architecture guards
        // intact.
        var type = typeof(NewsQueryScoringIdentity);

        var referenced = type
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(f => f.FieldType)
            .Concat(type
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Select(pr => pr.PropertyType))
            .Concat(type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .SelectMany(m => m.GetParameters().Select(pa => pa.ParameterType).Append(m.ReturnType)))
            .Concat(type
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters().Select(pa => pa.ParameterType)))
            .Distinct()
            .ToList();

        Assert.NotEmpty(referenced);
        Assert.All(
            referenced,
            t => Assert.True(
                t.Namespace is null
                    || (!t.Namespace.StartsWith("Radar.Application.News", StringComparison.Ordinal)
                        && !t.Namespace.StartsWith("Radar.Application.NewsRisk", StringComparison.Ordinal)
                        && !t.Namespace.StartsWith("Radar.Application.NewsTyping", StringComparison.Ordinal)
                        && !t.Namespace.StartsWith("Radar.Infrastructure", StringComparison.Ordinal)),
                $"NewsQueryScoringIdentity must not carry {t.FullName} in its shape — that would invert the "
                    + "dependency the spec-177/179 architecture guards enforce and make the identity "
                    + "uncomposable by a score pass."));
    }
}
