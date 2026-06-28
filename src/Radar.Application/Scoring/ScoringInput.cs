using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// The complete, pre-windowed input to a single company score computation. The engine (task 15) is
/// responsible for selecting the window and the signals; the formula is a pure function of this input.
/// Each <see cref="ScoringSignal"/>'s <c>Evidence.Id</c> equals its <c>Signal.EvidenceId</c> — the
/// engine guarantees this and the formula may assume it.
///
/// <para><see cref="Signals"/> is the CURRENT window (start, end] — each paired with its source
/// evidence for provenance. <see cref="PreviousSignals"/> is the immediately-preceding window of the
/// same length (start - window, start], carried as signals ONLY (no evidence): it exists so the
/// formula can measure signal-activity acceleration (velocity). It must NOT be used to build
/// contributions / ScoreEvidenceLinks — only the current-window signals carry provenance.</para>
/// </summary>
public sealed record ScoringInput(
    Guid CompanyId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<ScoringSignal> Signals,
    IReadOnlyList<Signal> PreviousSignals);
