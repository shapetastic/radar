namespace Radar.Domain.Companies;

public enum CompanyStatus
{
    Active,
    Delisted,
    WatchOnly,
    Unresolved,

    /// <summary>
    /// SPEC 217 — a pending acquisition of THIS company has been recognised deterministically from its own
    /// item-1.01 8-K (<c>acqscan-v1</c>): an agreement in which the company is the TARGET, with a stated
    /// per-share consideration, both verified verbatim in the filing text.
    /// <para>
    /// <b>It is DERIVED AT RUN TIME, never curated.</b> Unlike the four members above it is not settable in
    /// <c>data/companies.json</c>: the Worker resolves it from the append-only acquisitions store at each
    /// run and stamps it on every score snapshot the company receives from the announcement date onward.
    /// The seed file stays the curated source of the other statuses, and a maintainer moves a closed deal
    /// to <see cref="Delisted"/> when it actually delists — a conscious, journaled step.
    /// </para>
    /// <para>
    /// <b>What it means.</b> The thesis is closed: from the announcement the price sits at the bid and
    /// tracks the deal, not the business, so trajectory and opportunity are no longer the driver. Scoring
    /// continues (append-only; nothing is regenerated), but the weekly report labels the company
    /// <c>Ignore</c> by rule 0 and the forward efficacy series excludes its observations as
    /// <c>CorporateActionInWindow</c>.
    /// </para>
    /// </summary>
    PendingAcquisition,
}
