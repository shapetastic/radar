using Radar.Application.Collectors;
using Radar.Application.SignalExtraction;

namespace Radar.Application.Scoring;

/// <summary>
/// Default <see cref="ISignalSourceDescriptor"/>: builds a canonical, deterministic descriptor of the run's
/// signal-production surface ONCE at construction — the distinct enabled collector names plus the
/// deterministic extractor's rule-set identity (<see cref="KeywordSignalExtractor.RuleSetVersion"/>). It
/// reads only <see cref="IEvidenceCollector.CollectorName"/> and NEVER calls
/// <see cref="IEvidenceCollector.CollectAsync"/>, so it has zero collection side effects and stays a pure
/// function of the composed collector set (AD-3).
/// </summary>
public sealed class SignalSourceDescriptor : ISignalSourceDescriptor
{
    private readonly string _descriptor;

    public SignalSourceDescriptor(IEnumerable<IEvidenceCollector> collectors)
    {
        ArgumentNullException.ThrowIfNull(collectors);

        // Read ONLY CollectorName — never CollectAsync (no collection side effects). De-dupe defensively so a
        // mis-registration listing a collector twice does not change the descriptor, and order by Ordinal so
        // registration order is irrelevant.
        var names = collectors
            .Select(c => c.CollectorName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(Escape);

        var csv = string.Join(',', names);

        // Collector names are a controlled, delimiter-free vocabulary today (rss/sec/sec-form4/usaspending/
        // newssearch/news/localfile), but escaping keeps the serialization injective (AD-3) — a name that ever
        // contained a reserved delimiter cannot collide with a different collector set.
        _descriptor = $"rules={KeywordSignalExtractor.RuleSetVersion};collectors={csv};";
    }

    /// <inheritdoc />
    public string CanonicalDescriptor() => _descriptor;

    // Percent-escape the reserved descriptor delimiters so the collectors=csv serialization stays injective,
    // mirroring ConfiguredAttentionSourceWeights.Escape. The % (escape marker) MUST be replaced first, before
    // the delimiters it encodes; ',' is added because it separates collector names here.
    private static string Escape(string name) =>
        name.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("=", "%3D", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal);
}
