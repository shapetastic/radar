using Radar.Application.Identity;

namespace Radar.Application.Evidence;

/// <summary>
/// The single definition of an <see cref="Radar.Domain.Evidence.EvidenceItem"/>'s <b>stable identity</b>
/// (spec 145): an evidence id is a pure function of the evidence's CONTENT, so the same content resolves
/// to the same evidence record across runs and across collectors.
///
/// <para>
/// <b>Why this exists.</b> The mapper previously minted <c>Guid.NewGuid()</c> per run while
/// <c>FileRawEvidenceStore</c> keyed its files on <c>contentHash</c>. The id a signal referenced was
/// therefore unrelated to the id the persisted file carried, with two consequences measured on the live
/// store (2026-07-26): only <b>10.5 %</b> of 49,454 accrued signals had a resolvable evidence id, and the
/// spec-85 cross-run dedupe key <c>(CompanyId, EvidenceId, Type, Direction)</c> — which contains evidence
/// identity — collapsed the store by exactly <b>1.000×</b>, a no-op, while collapsing by CONTENT collapsed
/// it by <b>9.213×</b>. A key built on identity can never dedupe identity. Deriving identity from content
/// is what lets that key finally see the duplication.
/// </para>
///
/// <para>
/// <b>The rule.</b> Identity is a function of the normalized title+body <c>ContentHash</c> ALONE — the
/// SHA-256 the <see cref="EvidenceNormalizer"/> computes over the cleaned, whitespace-collapsed
/// <c>title + "\n" + body</c>.
/// </para>
///
/// <para>
/// <b>Deliberately EXCLUDED from identity</b> (each of these varies between two retrievals of the same
/// fact, so folding any of them in would re-create the per-run identity this replaces):
/// </para>
/// <list type="bullet">
/// <item><c>CollectedAt</c> — the retrieval timestamp;</item>
/// <item><c>PublishedAt</c> — a source may restate it, and it is absent for many collectors;</item>
/// <item>the run id and every minted id;</item>
/// <item>the collector / source name;</item>
/// <item>the source URL — and therefore every volatile query parameter, session token and tracking
/// parameter (<c>utm_*</c> and friends) it carries;</item>
/// <item>the metadata bag and the company hints;</item>
/// <item>the <see cref="Radar.Domain.Evidence.EvidenceSourceType"/>.</item>
/// </list>
///
/// <para>
/// <b>Cross-collector policy — the choice, stated.</b> The same content collected by two different
/// collectors is <b>ONE evidence record</b>, not two. The content hash is taken over the normalized
/// title+body, and identical content is one FACT; two collectors finding it is two retrieval paths to the
/// same fact, not two facts. Consequences, both accepted deliberately:
/// </para>
/// <list type="bullet">
/// <item><b>Attention breadth/diversity — one re-published identical copy counts ONCE.</b>
/// <c>RadarScoreFormulaV8</c> counts distinct publishers (<c>Evidence.SourceName</c>, tier-weighted) and
/// distinct <c>Evidence.SourceType</c>s over the RESOLVED evidence set; both are set cardinalities, so
/// dropping a member can only LOWER or hold them. Genuinely distinct coverage (different outlets writing
/// their OWN words about one event) still hashes differently and still counts as breadth — the spec-109
/// same-event media collapse remains the mechanism for THAT case.
/// <para>
/// <b>Stated precisely, because "lower breadth ⇒ lower score" is NOT universally true:</b>
/// <c>OpportunityScore</c> consumes <c>AttentionScore</c> as an INVERSE discount
/// (<c>1 − attention/OpportunityAttentionDivisor·…</c>), so a lower attention RAISES opportunity, all else
/// equal. The reason this slice cannot raise it anyway is structural rather than directional: the durable
/// evidence store has been path-keyed on <c>contentHash</c> since it was written, and the evidence
/// repository's <c>AddIfNewAsync</c> has always rejected a second item with an already-seen content hash.
/// So at most ONE record per distinct content could ever be persisted or resolved, and the breadth
/// contributed by a set of identical copies was already exactly 1 BEFORE this slice. Making identity
/// content-derived changes WHICH id that one record carries — not how many of them there are. Breadth is
/// therefore unchanged, not merely non-increasing, and the Opportunity coupling is never exercised.
/// </para></item>
/// <item><b>Provenance is retained, not collapsed.</b> Every contributing source's own raw file stays on
/// disk under its own <c>{sourceTypeFolder}/{yyyy}/{MM}/{contentHash}.json</c> path — insert-only (AD-1),
/// nothing deleted or rewritten. Only the identity INDEX collapses to one canonical record, chosen
/// deterministically by ordinal path order (see <c>FileRawEvidenceStore</c> hydration), and that collapse
/// is COUNTED and logged rather than silent.</item>
/// </list>
///
/// <para>
/// <b>Accrued history: left exactly as it is (the chosen option from the spec's §4 list).</b> This change
/// is COLLECTION-TIME ONLY — it changes the id minted for newly collected evidence. Legacy evidence files
/// keep their legacy ids and legacy signals keep their legacy references, so no score computed from
/// accrued history moves. Nothing is deleted, nothing is rewritten, and there is no backfill, migration or
/// "supersede" marker. The rationale is arithmetic: the live 30-day window scores 2,618 resolvable signals
/// out of 12,145, at ~1.03× content duplication. Retro-healing resolution would make all 12,145 scoreable
/// — a 4.6× inflation of the scored set — which is precisely the outcome this slice exists to prevent.
/// Historical series therefore stay exactly as they were actually scored.
/// </para>
/// </summary>
public static class EvidenceIdentity
{
    /// <summary>
    /// The identity namespace. Folded into the canonical string so an evidence id can never collide with
    /// any other deterministic-Guid family Radar derives (e.g. the seed alias / source-feed ids, which
    /// canonicalise as <c>"{companyId}|{kind}|{value}"</c>): the families hash disjoint string spaces.
    /// Changing this token re-mints every evidence id — treat it as a persisted format constant.
    /// </summary>
    private const string Namespace = "radar:evidence:";

    /// <summary>
    /// The stable id of the evidence carrying <paramref name="contentHash"/>. Deterministic,
    /// culture-invariant and pure: same hash in, same <see cref="Guid"/> out, in every process, on every
    /// machine, forever.
    /// </summary>
    /// <param name="contentHash">
    /// The normalized title+body content hash (<c>NormalizedEvidence.ContentHash</c>). Must be non-empty:
    /// evidence with no content hash could not dedupe, and giving it a shared identity would merge
    /// unrelated items.
    /// </param>
    public static Guid ForContentHash(string contentHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentHash);

        return DeterministicGuid.FromCanonicalString(Namespace + contentHash);
    }
}
