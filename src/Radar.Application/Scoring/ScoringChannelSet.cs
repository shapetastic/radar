using System.Collections.ObjectModel;
using System.Globalization;

namespace Radar.Application.Scoring;

/// <summary>
/// The validated, canonical channel array of ONE <c>radar-formula-v9</c> strategy (spec 146) — modelled on
/// <see cref="SignalTypeFilter"/>'s shape: canonicalisation at construction, a canonical descriptor segment
/// folded into the strategy's <c>ScoringConfigVersion</c>, value equality, and an <see cref="Empty"/>
/// instance whose <see cref="Describe"/> is a verbatim passthrough so a strategy that declares no channels
/// hashes exactly what it hashed before this type existed.
/// <para>
/// <b>THE CORE INVARIANT: WEIGHTS ARE NEVER RENORMALISED.</b> <c>score = Σ (weight · channelScore)</c> over
/// the DECLARED channels, and a channel that produced no signals contributes 0 with the denominator
/// unchanged. That is the entire reason this type exists. v8 computes every component over the signals that
/// happened to arrive, so a source going dark is invisible — worse, it is incoherent, because
/// <c>SignalVelocity</c> correctly falls while <c>AttentionScore</c> enters Opportunity as an INVERSE
/// discount and perversely rises. Under a declared budget, a strategy whose 0.50-weight patents channel is
/// dark today is down by up to 0.50, while a strategy that never declared patents is completely unaffected.
/// Renormalising the surviving weights is the obvious-looking "fix" and it would erase exactly the penalty
/// this design exists to create — do not add it.
/// </para>
/// <para>
/// <b>Validation is fail-fast at construction</b> (which happens during DI registration, so a bad set never
/// scores anything) and every message names the strategy: a typo in the weights silently rescales every
/// score of that strategy, which is far worse than a startup crash.
/// </para>
/// <para>
/// Collector NAME validation is deliberately NOT here: this type does not know which collectors are actually
/// registered. That check lives where the registry is known (<see cref="ScoringStrategyFactory"/>, built
/// before Stage 1), so the two concerns fail fast in the two places that can actually judge them.
/// </para>
/// </summary>
public sealed class ScoringChannelSet : IEquatable<ScoringChannelSet>
{
    /// <summary>
    /// The absolute tolerance on the "weights sum to 1.0" rule. Deliberately tight: it exists only to
    /// forgive the representation error of summing a handful of doubles that were written as decimal
    /// literals in JSON (0.5 + 0.3 + 0.2 sums to 0.9999999999999999, not 1.0), NOT to forgive a budget that
    /// genuinely does not add up. A set summing to 0.99 is a typo and must fail.
    /// </summary>
    public const double WeightSumTolerance = 1e-9;

    /// <summary>
    /// The canonical "no channels" set — what a non-v9 strategy carries, and what an omitted or empty
    /// <c>Channels</c> array canonicalises onto. <see cref="Describe"/> returns its input verbatim for this
    /// instance, so the pinned default <c>ScoringConfigVersion</c> fingerprints do not move.
    /// </summary>
    public static ScoringChannelSet Empty { get; } = new();

    private readonly ReadOnlyCollection<ScoringChannel> _channels;
    private readonly string _segment;

    private ScoringChannelSet()
    {
        _channels = new ReadOnlyCollection<ScoringChannel>(Array.Empty<ScoringChannel>());
        _segment = string.Empty;
    }

    private ScoringChannelSet(IReadOnlyList<ScoringChannel> channels)
    {
        _channels = new ReadOnlyCollection<ScoringChannel>([.. channels]);

        // Canonical encoding, mirroring SignalTypeFilter's two choices: the channels are ORDERED BY NAME so
        // the order they were listed in config is irrelevant (two strategies declaring the same budget in a
        // different order are the same strategy and must hash the same), and every spliced value goes through
        // the shared DescriptorEscaping.EscapeNested so the nested `,` / `:` / `|` structure stays injective
        // (AD-3). Numbers use the same culture-invariant round-trip format ScoringConfigFingerprint uses, so
        // a weight cannot hash differently under a comma-decimal locale.
        var tokens = _channels.Select(c => string.Join(
            ':',
            DescriptorEscaping.EscapeNested(c.Name),
            KindToken(c.Kind),
            c.Weight.ToString("R", CultureInfo.InvariantCulture),
            c.Saturation.ToString("R", CultureInfo.InvariantCulture),
            string.Join('|', c.Collectors.Select(DescriptorEscaping.EscapeNested))));

        _segment = $"channels={string.Join(',', tokens)};";
    }

    /// <summary>
    /// The channels, ordered by <see cref="ScoringChannel.Name"/> (Ordinal). The runtime order is
    /// canonicalised too — not just the descriptor — so the composite's floating-point summation order is a
    /// function of the strategy, not of how its channels happened to be listed in a JSON file.
    /// </summary>
    public IReadOnlyList<ScoringChannel> Channels => _channels;

    /// <summary>True when no channels are declared (the default; a non-v9 strategy).</summary>
    public bool IsEmpty => _channels.Count == 0;

    /// <summary>
    /// Canonicalises and validates a declared channel array. Null or empty returns <see cref="Empty"/>.
    /// </summary>
    /// <param name="channels">The declared channels, in any order.</param>
    /// <param name="strategyName">
    /// The owning strategy, used only to name the offender in a fail-fast message.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A blank or duplicate channel name; a <see cref="ScoringChannel.Weight"/> outside <c>[0,1]</c>; weights
    /// that do not sum to 1.0 within <see cref="WeightSumTolerance"/>; a non-positive
    /// <see cref="ScoringChannel.Saturation"/>; a breadth channel that declares collectors; or a collector
    /// channel that declares none.
    /// </exception>
    public static ScoringChannelSet Create(
        IEnumerable<ScoringChannel>? channels, string strategyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);

        if (channels is null)
        {
            return Empty;
        }

        var declared = channels.ToList();
        if (declared.Count == 0)
        {
            return Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sum = 0.0;
        foreach (var channel in declared)
        {
            if (channel is null)
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategyName}' declares a null channel; every channel must name what it "
                        + "measures, how much of the score it owns, and its saturation.");
            }

            if (string.IsNullOrWhiteSpace(channel.Name))
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategyName}' declares a channel with a blank Name; a channel name is "
                        + "recorded in the score explanation and in every consumed signal's contribution "
                        + "reason, so an anonymous channel would make its share unattributable.");
            }

            if (!seen.Add(channel.Name))
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategyName}' declares duplicate channel Name '{channel.Name}' (names are "
                        + "compared case-insensitively); two channels sharing a name would make the per-channel "
                        + "provenance breakdown ambiguous.");
            }

            if (double.IsNaN(channel.Weight) || channel.Weight < 0 || channel.Weight > 1)
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategyName}' channel '{channel.Name}' has Weight {Format(channel.Weight)}, "
                        + "which is outside [0, 1]. A channel's weight is its SHARE of the composite score, so a "
                        + "negative share would subtract and a share above 1 would exceed the whole budget.");
            }

            if (double.IsNaN(channel.Saturation) || channel.Saturation <= 0)
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategyName}' channel '{channel.Name}' has Saturation "
                        + $"{Format(channel.Saturation)}, which must be strictly positive: it is the denominator "
                        + "term in x/(x+S), the amount of that channel's traffic that counts as half its share.");
            }

            if (channel.Kind == ScoringChannelKind.Breadth && channel.Collectors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategyName}' breadth channel '{channel.Name}' declares collectors "
                        + $"({string.Join(", ", channel.Collectors)}); breadth measures distinct-publisher reach "
                        + "ACROSS every signal the strategy consumes and is inherently cross-source, so it cannot "
                        + "be scoped to a collector without losing its meaning.");
            }

            if (channel.Kind == ScoringChannelKind.Collector && channel.Collectors.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategyName}' collector channel '{channel.Name}' declares no collectors; it "
                        + "could only ever score 0, silently costing the strategy this channel's whole share. "
                        + "Name at least one collector, or declare the channel as Kind \"breadth\".");
            }

            sum += channel.Weight;
        }

        if (Math.Abs(sum - 1.0) > WeightSumTolerance)
        {
            throw new InvalidOperationException(
                $"Strategy '{strategyName}' channel weights sum to {Format(sum)}, not 1.0 (tolerance "
                    + $"{Format(WeightSumTolerance)}). Channel weights are shares of ONE score, so a sum that is "
                    + "not 1 silently rescales every score this strategy produces — which is invisible in the "
                    + "output and would corrupt any comparison against another strategy. Declared channels: "
                    + $"{string.Join(", ", declared.Select(c => $"{c.Name}={Format(c.Weight)}"))}.");
        }

        return new ScoringChannelSet([.. declared.OrderBy(c => c.Name, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Folds this channel set into the signal-source descriptor that the <c>ScoringConfigVersion</c>
    /// fingerprint hashes. Returns <paramref name="sourceDescriptor"/> <b>verbatim</b> when
    /// <see cref="IsEmpty"/> — a strategy with no channels must hash exactly what it hashed before this type
    /// existed, which is what keeps the pinned default fingerprints unmoved — and otherwise appends a
    /// canonical <c>channels=…;</c> segment after the existing segments (fixed field ordering, AD-3).
    /// <para>
    /// The channel array, its weights, its saturations and (via <c>_formula.Version</c>) the formula itself
    /// ARE this strategy's identity: two strategies allocating their score differently are genuinely
    /// different scorings and must never share a <c>ScoringConfigVersion</c>. Folding happens on the same
    /// <see cref="SignalTypeFilter.Describe"/> chain inside <see cref="ScoringEngine"/>, so the behavioural
    /// composition and the hashed identity cannot drift.
    /// </para>
    /// </summary>
    public string Describe(string sourceDescriptor)
    {
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        return IsEmpty ? sourceDescriptor : sourceDescriptor + _segment;
    }

    /// <inheritdoc />
    public bool Equals(ScoringChannelSet? other) =>
        other is not null && string.Equals(_segment, other._segment, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ScoringChannelSet);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_segment);

    /// <inheritdoc />
    public override string ToString() =>
        IsEmpty
            ? "no channels"
            : string.Join(", ", _channels.Select(c => $"{c.Name}({Format(c.Weight)})"));

    /// <summary>The canonical config/descriptor token for a channel kind.</summary>
    public static string KindToken(ScoringChannelKind kind) =>
        kind == ScoringChannelKind.Breadth ? "breadth" : "collector";

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
