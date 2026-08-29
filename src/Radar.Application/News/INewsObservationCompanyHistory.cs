namespace Radar.Application.News;

/// <summary>
/// SPEC 198 §2 — the read seam that answers "which companies has Radar already archived at least one news
/// observation for?", so a company's FIRST collection can issue the UNFILTERED query exactly as every
/// collection did before spec 198 and every subsequent one can carry the recency window.
/// <para>
/// <b>It is a PERSISTED-STATE question, never a clock comparison (AD-3).</b> The rule is "does the archive
/// already hold an observation for this company", not "was the last run recent enough" — a clock-derived
/// answer would make two runs over identical data disagree, would silently re-issue the unfiltered query
/// after any gap, and would be unreproducible in a replay. The unfiltered query is what makes SEEDING
/// acquire back history; the window is what stops every later night re-reading it.
/// </para>
/// <para>
/// <b>Fail-closed to "no improvement".</b> A composition that does not register this seam simply never
/// narrows any query, which is byte-identical to pre-198 behaviour. A company Radar has never observed is
/// likewise unfiltered. Neither outcome can drop results; the worst case is that the budget saving does not
/// materialise.
/// </para>
/// <para>
/// Observational only, exactly like the archive it reads: it feeds no evidence, no signal, no score and no
/// fingerprint. The CONFIGURED window is the hashed input (<c>NewsQueryScoringIdentity</c>); which companies
/// happened to be on their first collection is per-run provenance, recorded on the coverage diagnostic.
/// </para>
/// </summary>
public interface INewsObservationCompanyHistory
{
    /// <summary>
    /// The set of company ids that already hold at least one archived observation. Records carrying no
    /// company contribute nothing (they are attributable to no company's history). The returned set is
    /// deterministic and must not be mutated by the caller.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetCompaniesWithObservationsAsync(CancellationToken ct);
}
