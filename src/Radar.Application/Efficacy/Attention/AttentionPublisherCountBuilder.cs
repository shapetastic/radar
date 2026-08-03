using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Efficacy.Attention;

/// <summary>Which of the two AD-16 windows a publisher count is being built for.</summary>
public enum AttentionWindow
{
    /// <summary>The PRIMARY comparator's trailing window <c>(T − 21d, T]</c> — <c>baseline-attention-persistence</c>.</summary>
    Comparator,

    /// <summary>The primary outcome window <c>(T, T + 21d]</c>.</summary>
    Outcome,
}

/// <summary>
/// Why a publisher count could not be produced for one company-window. Every token is window-scoped, because
/// "the comparator broke" and "the outcome broke" are different facts about the same company-date and AD-16 §5
/// names them separately.
/// </summary>
public enum AttentionPublisherCountFailure
{
    /// <summary>It exists; not a drop.</summary>
    None = 0,

    /// <summary>A relevant comparator signal's evidence did not resolve. Never silently omitted from the count.</summary>
    UnresolvedComparatorEvidence,

    /// <summary>A relevant outcome signal's evidence did not resolve. Never silently omitted from the count.</summary>
    UnresolvedOutcomeEvidence,

    /// <summary>A comparator article's real metadata publisher was blank. The feed-name fallback is NOT a third-party publisher.</summary>
    MissingComparatorPublisher,

    /// <summary>An outcome article's real metadata publisher was blank. The feed-name fallback is NOT a third-party publisher.</summary>
    MissingOutcomePublisher,

    /// <summary>A comparator article carried missing or unsupported collector attribution, so its coverage cannot be proved.</summary>
    UnresolvedComparatorProvenance,

    /// <summary>An outcome article carried missing or unsupported collector attribution, so its coverage cannot be proved.</summary>
    UnresolvedOutcomeProvenance,
}

/// <summary>
/// A company-window publisher count, or the named reason there is none. A DEFINED count of <b>zero</b> is a
/// real observation and stays in the sample (AD-16 §5's valid zero).
/// </summary>
public sealed record AttentionPublisherCountResult(
    bool IsDefined, int Count, AttentionPublisherCountFailure Failure)
{
    public static AttentionPublisherCountResult Defined(int count) =>
        new(IsDefined: true, Count: count, Failure: AttentionPublisherCountFailure.None);

    public static AttentionPublisherCountResult Undefined(AttentionPublisherCountFailure failure) =>
        new(IsDefined: false, Count: 0, Failure: failure);
}

/// <summary>
/// THE construction of AD-16 §1's primary attention metric: the count of DISTINCT third-party publishers with
/// at least one resolving <c>MediaAttention</c> signal for a company in an exact half-open interval
/// <c>(a, b]</c> (spec 169).
/// <para>
/// <b>One builder, called for BOTH windows, deliberately.</b> AD-16 §6 makes the primary comparator "the
/// trailing distinct-publisher count over <c>(D − 21, D]</c>, built by exactly the same construction as the
/// outcome". Two builders — however carefully written — would eventually acquire subtly different filters, and
/// the screen would then be measuring the difference between two metrics rather than between a score and a
/// persistence baseline. The caller supplies only which window it is, so the right failure token is emitted.
/// </para>
/// <para>
/// <b>No look-ahead and no rounding.</b> The interval is half-open on the exact instants the caller supplies:
/// a signal observed exactly AT <c>a</c> is out, exactly AT <c>b</c> is in. Using whole UTC dates would look
/// ahead to articles published later on the scoring day.
/// </para>
/// <para>
/// <b>No novelty test.</b> AD-16 §2 rules it out: 89.5 % of accrued evidence does not resolve on disk (spec
/// 142), so a publisher would appear "new" whenever its earlier evidence is simply missing — novelty would
/// measure the gap, not the market.
/// </para>
/// <para>
/// Read-only: it resolves signals and evidence and creates, amends and deletes nothing.
/// </para>
/// </summary>
public sealed class AttentionPublisherCountBuilder
{
    private readonly ISignalRepository _signals;
    private readonly IEvidenceRepository _evidence;
    private readonly ICollectorAttributionResolver _attribution;
    private readonly AttentionArrivalOptions _options;

    public AttentionPublisherCountBuilder(
        ISignalRepository signals,
        IEvidenceRepository evidence,
        ICollectorAttributionResolver attribution,
        AttentionArrivalOptions options)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(options);

        _signals = signals;
        _evidence = evidence;
        _attribution = attribution;
        _options = options;
    }

    /// <summary>The evidence metadata key carrying the article's REAL outlet, written by the news collector.</summary>
    private const string PublisherMetadataKey = "publisher";

    /// <summary>
    /// Reads one company's durable signals — the ONE read this builder performs, exposed so a caller
    /// evaluating many windows for the same company can perform it ONCE and hand the list back to
    /// <see cref="BuildAsync(IReadOnlyList{Signal}, Guid, DateTimeOffset, DateTimeOffset, AttentionWindow,
    /// CancellationToken)"/>.
    /// <para>
    /// It exists because the durable signal store scans (and de-duplicates) its whole index per call, while
    /// the screen asks about the same company across two windows per as-of date and across every candidate
    /// date. Exposing the read here rather than injecting the repository into the evaluator keeps the
    /// signal-store seam in exactly one type.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<Signal>> ReadCompanySignalsAsync(Guid companyId, CancellationToken ct) =>
        _signals.GetByCompanyAsync(companyId, ct);

    /// <summary>
    /// Counts distinct third-party publishers for <paramref name="companyId"/> over the exact interval
    /// <c>(<paramref name="exclusiveStartUtc"/>, <paramref name="inclusiveEndUtc"/>]</c>.
    /// </summary>
    public async Task<AttentionPublisherCountResult> BuildAsync(
        Guid companyId,
        DateTimeOffset exclusiveStartUtc,
        DateTimeOffset inclusiveEndUtc,
        AttentionWindow window,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var companySignals = await ReadCompanySignalsAsync(companyId, ct).ConfigureAwait(false);
        return await BuildAsync(
                companySignals, companyId, exclusiveStartUtc, inclusiveEndUtc, window, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The same construction over an ALREADY-READ signal list. Identical rules — the list is the only thing
    /// supplied from outside, and it is still filtered here, so the comparator and the outcome cannot acquire
    /// different filters by taking different routes in.
    /// </summary>
    public async Task<AttentionPublisherCountResult> BuildAsync(
        IReadOnlyList<Signal> companySignals,
        Guid companyId,
        DateTimeOffset exclusiveStartUtc,
        DateTimeOffset inclusiveEndUtc,
        AttentionWindow window,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(companySignals);
        ct.ThrowIfCancellationRequested();

        // Ordered so the FIRST failure encountered is the same one on every run over the same store (AD-3):
        // the result carries one reason, and which one it is must not depend on store enumeration order.
        var relevant = companySignals
            .Where(s => s.CompanyId == companyId)
            .Where(s => s.Type == SignalType.MediaAttention)
            .Where(s => s.ReviewStatus == SignalReviewStatus.Approved)
            .Where(s => s.ObservedAtUtc > exclusiveStartUtc && s.ObservedAtUtc <= inclusiveEndUtc)
            .OrderBy(s => s.ObservedAtUtc)
            .ThenBy(s => s.Id)
            .ToList();

        // Canonicalisation is whitespace + case ONLY (AD-16 §1 as operationalised by spec 169). No
        // hand-maintained Reuters/Reuters.com entity map: building one after seeing outcomes is precisely how
        // a metric gets tuned into agreement with the answer somebody wanted.
        var publishers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var signal in relevant)
        {
            ct.ThrowIfCancellationRequested();

            var evidence = await _evidence.GetByIdAsync(signal.EvidenceId, ct).ConfigureAwait(false);
            if (evidence is null)
            {
                // AD-16 §5: a relevant signal whose evidence does not resolve DROPS the company-date. It is
                // never treated as a lower publisher count — that would turn missing data into a measurement.
                return AttentionPublisherCountResult.Undefined(Failure(
                    window,
                    AttentionPublisherCountFailure.UnresolvedComparatorEvidence,
                    AttentionPublisherCountFailure.UnresolvedOutcomeEvidence));
            }

            if (evidence.SourceType != EvidenceSourceType.NewsArticle)
            {
                // Not third-party news coverage at all, so it is outside the metric rather than a failure of
                // it. A MediaAttention signal can attach to (say) a press release; AD-16 §1 counts the market
                // NOTICING, which is what a third-party news article is.
                continue;
            }

            var attribution = _attribution.Resolve(evidence);
            if (!attribution.IsAttributed
                || !string.Equals(attribution.CollectorName, _options.AttentionCollector, StringComparison.Ordinal))
            {
                // Missing OR unsupported attribution: this article's collection coverage cannot be proved, so
                // the company-date drops rather than being counted from a source whose completeness is
                // unknown. Ordinal match, mirroring ScoringChannel.Consumes — a case near-miss is a miss.
                return AttentionPublisherCountResult.Undefined(Failure(
                    window,
                    AttentionPublisherCountFailure.UnresolvedComparatorProvenance,
                    AttentionPublisherCountFailure.UnresolvedOutcomeProvenance));
            }

            // The REAL outlet, read through the shared envelope reader. Deliberately NOT EvidenceItem
            // .SourceName: the collector falls back to the per-company FEED NAME when the publisher is blank,
            // and a feed name is Radar's own label, not a third party noticing the company. Counting it would
            // manufacture an outlet.
            EvidenceMetadata.TryRead(evidence.MetadataJson, out var metadata, out _);
            var publisher = metadata.TryGetValue(PublisherMetadataKey, out var value) ? value : null;
            var canonical = Canonicalize(publisher);
            if (canonical.Length == 0)
            {
                return AttentionPublisherCountResult.Undefined(Failure(
                    window,
                    AttentionPublisherCountFailure.MissingComparatorPublisher,
                    AttentionPublisherCountFailure.MissingOutcomePublisher));
            }

            // Distinct URLs/articles from one canonical publisher count ONCE: one outlet syndicating itself is
            // not the market noticing (AD-16 §1).
            publishers.Add(canonical);
        }

        // A complete window with no relevant signals is a VALID INTEGER ZERO and stays in the sample. It is
        // the central negative case; selecting only companies where attention arrived would select on the
        // outcome and destroy the test (AD-16 §5).
        return AttentionPublisherCountResult.Defined(publishers.Count);
    }

    /// <summary>Trim + collapse internal whitespace. Case is handled by the comparer, so nothing else is normalised.</summary>
    private static string Canonicalize(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return string.Empty;
        }

        return string.Join(' ', publisher.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static AttentionPublisherCountFailure Failure(
        AttentionWindow window,
        AttentionPublisherCountFailure comparator,
        AttentionPublisherCountFailure outcome) =>
        window == AttentionWindow.Comparator ? comparator : outcome;
}
