using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>A reviewed signal paired with the source evidence it was extracted from.</summary>
public sealed record ScoringSignal(Signal Signal, EvidenceItem Evidence);
