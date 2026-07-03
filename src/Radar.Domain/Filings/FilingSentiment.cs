namespace Radar.Domain.Filings;

/// <summary>
/// The directional trajectory a filing's earnings release describes, AS REPORTED. Radar has no analyst-
/// consensus feed, so this is NOT a beat-vs-consensus claim — it captures whether the release reads as an
/// improving or deteriorating business trajectory. Never advice.
/// </summary>
public enum FilingDirection
{
    /// <summary>Could not be read / low-signal / malformed AI output — the safe default.</summary>
    Unknown,

    /// <summary>Beat/growth/raised outlook — record bookings, organic growth, guidance raised.</summary>
    Improving,

    /// <summary>Miss/declines/cut outlook — revenue decline, guidance cut, impairment.</summary>
    Deteriorating,

    /// <summary>Materially both (e.g. revenue up, margins down / one segment up, another cut).</summary>
    Mixed,
}

/// <summary>
/// Typed, validated directional read of an earnings release, AS REPORTED (not a beat-vs-consensus claim —
/// Radar has no consensus feed). <see cref="Confidence"/> is in [0,1]; <see cref="Rationale"/> is a bounded,
/// plain-language, advice-free basis quoting the release (never "buy"/"sell"/etc.). It exists for
/// report/audit transparency, not for decisions.
/// </summary>
public sealed record FilingSentiment(FilingDirection Direction, decimal Confidence, string Rationale)
{
    /// <summary>The safe default: unreadable / low-signal / malformed — Direction Unknown, Confidence 0.</summary>
    public static FilingSentiment Unknown { get; } =
        new(FilingDirection.Unknown, 0m, string.Empty);
}
