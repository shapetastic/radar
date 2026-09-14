namespace Radar.Application.SignalExtraction;

using Radar.Domain.Signals;

/// <param name="ItemHeading">
/// Spec 226: set ONLY on a rule whose phrase is an SEC 8-K item heading (<see cref="SecItemHeadingPhrases"/>).
/// Such a rule's Reason names the item — and every other item heading of the same type present in the same
/// text — through <see cref="KeywordSignalReasons.ItemHeading"/>. Null for every ordinary phrase rule.
/// </param>
internal sealed record KeywordSignalRule(
    string Phrase,
    SignalType Type,
    SignalDirection Direction,
    int Strength,
    int Novelty,
    decimal Confidence,
    SecItemHeadingPhrase? ItemHeading = null);
