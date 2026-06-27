namespace Radar.Application.Scoring;

/// <summary>
/// The complete, pre-windowed input to a single company score computation. The engine (task 15) is
/// responsible for selecting the window and the signals; the formula is a pure function of this input.
/// Each <see cref="ScoringSignal"/>'s <c>Evidence.Id</c> equals its <c>Signal.EvidenceId</c> — the
/// engine guarantees this and the formula may assume it.
/// </summary>
public sealed record ScoringInput(
    Guid CompanyId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<ScoringSignal> Signals);
