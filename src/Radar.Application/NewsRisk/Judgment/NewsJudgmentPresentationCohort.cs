using Radar.Application.NewsTyping;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// The resolved presentation cohort: the designated judge reader, the designated stage-1 extractor cohort
/// from THIS run's typing pass, and the composed stage-2 cohort key that identifies their pairing.
/// <para>
/// The extractor cohort travels with the key deliberately. A consumer that needs the key almost always also
/// needs the cohort's <see cref="NewsTypingCohortRunResult.FactsById"/> or its per-company completeness map,
/// and handing back only the key would force it to re-find the cohort by name — a second copy of the very
/// matching rule this type exists to have exactly one of.
/// </para>
/// </summary>
public sealed record NewsJudgmentPresentationCohortResolution(
    NewsJudgmentReader Judge,
    NewsTypingCohortRunResult ExtractorCohort,
    string CohortKey);

/// <summary>
/// The ONE structural resolution of the prospectively designated presentation cohort (spec 185 §4): find
/// the stage-1 typing cohort whose reader NAME matches <see cref="NewsJudgmentOptions.PresentationExtractor"/>
/// and the judge whose identity NAME matches <see cref="NewsJudgmentOptions.PresentationJudge"/>, then
/// compose <see cref="NewsJudgmentReaderIdentity.CohortKeyFor"/> over that cohort's reader key.
/// <para>
/// <b>Why this is a shared type rather than an inlined lookup.</b> Two consumers now need the SAME answer:
/// <c>NewsJudgmentGenerator</c>'s leaders-marker derivation (spec 185 §4) and spec 194 §1.2's
/// judgment-signal materializer, which may only materialize a DIRECTION from the designated cohort. If those
/// two ever resolved it differently, Radar would score a direction from one cohort while displaying a marker
/// from another — the exact "the scored cohort and the displayed cohort cannot drift" property the
/// prospective designation exists to guarantee. Composing the key by hand at a second call site, or matching
/// the rendered cohort-key STRING instead of resolving structurally, are both ways of losing it.
/// </para>
/// <para>
/// Returns <c>null</c> when either half is absent from this run (e.g. the typing pass ran a different reader
/// set). That is a fail-closed answer: an undesignated cohort is never substituted, and each caller states
/// its own consequence — the generator keeps the honest pending markers, the materializer creates no signal.
/// Name matching is case-insensitive, matching the startup referential validation that accepted the names.
/// </para>
/// </summary>
public static class NewsJudgmentPresentationCohort
{
    public static NewsJudgmentPresentationCohortResolution? TryResolve(
        NewsJudgmentOptions options,
        NewsJudgmentReaderSet judges,
        NewsTypingRunResult typing)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(judges);
        ArgumentNullException.ThrowIfNull(typing);

        var extractorCohort = typing.Cohorts.FirstOrDefault(c => string.Equals(
            c.Reader.Name, options.PresentationExtractor, StringComparison.OrdinalIgnoreCase));
        var judge = judges.Readers.FirstOrDefault(j => string.Equals(
            j.Identity.Name, options.PresentationJudge, StringComparison.OrdinalIgnoreCase));

        return extractorCohort is null || judge is null
            ? null
            : new NewsJudgmentPresentationCohortResolution(
                judge, extractorCohort, ComposeCohortKey(judge.Identity, extractorCohort.Reader));
    }

    /// <summary>
    /// The ONE composition of a presentation cohort KEY from the two reader identities — extracted from
    /// <see cref="TryResolve"/> (which now calls it) so spec 194 §2's CONFIG-TIME resolution can reach the
    /// same answer without a typing pass having run.
    /// <para>
    /// <b>Why a config-time path is needed at all.</b> <see cref="TryResolve"/> answers "which cohort did
    /// THIS RUN actually produce", which is the right question for the marker and for the materializer —
    /// both consume that run's facts. The scoring identity asks a different question: "which cohort is this
    /// process CONFIGURED to read news through", and it must be answerable in <c>score</c> and
    /// <c>replay</c> modes, where no typing pass exists and no judgment step is registered. Both questions
    /// resolve to the same string for the same configuration precisely because both compose it here, through
    /// <see cref="NewsJudgmentReaderIdentity.CohortKeyFor"/> over
    /// <see cref="NewsTypingReaderIdentity.CohortKey"/> — one definition, two callers.
    /// </para>
    /// </summary>
    public static string ComposeCohortKey(
        NewsJudgmentReaderIdentity judge, NewsTypingReaderIdentity extractor)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(extractor);

        return judge.CohortKeyFor(extractor.CohortKey);
    }
}
