using Radar.Application.Collectors;
using Radar.Domain.Signals;

namespace Radar.Application.SignalExtraction;

/// <summary>
/// The ONE definition of the provenance keys a judgment-derived news signal carries. Keys are declared here
/// and nowhere else, and the envelope is composed through the SHARED <c>EvidenceMetadata.Compose</c> / read
/// back through <c>EvidenceMetadata.TryRead</c> — the repo's single metadata-envelope definition — rather
/// than a second hand-rolled JSON composer.
/// <para>
/// <b>SPEC 194 — these keys are now READ before they are written.</b> Spec 191 wrote them onto directional
/// news signals minted during extraction, from a company judgment that had never read the matched article.
/// That producer is retired and its <c>Compose</c> overload with it, but the keys survive deliberately: the
/// signals it wrote are on disk, they are append-only, and they must not be deleted or rewritten. They are
/// the shape the spec-194 §1.4 legacy-inheritance admission transform matches on in order to fail those
/// accrued directions CLOSED to Neutral, and the shape the §1.2 materializer's versioned envelope extends
/// with <c>newsJudgmentSignalVersion</c>. Do not add a second metadata parser beside this one.
/// </para>
/// <para>
/// The envelope's <c>companyHints</c> array is written EMPTY: a signal carries no collector company hints.
/// That one artifact is the price of having exactly one envelope definition instead of two, and it keeps a
/// signal's metadata readable by the same reader every evidence item already uses.
/// </para>
/// </summary>
public static class NewsDirectionalSignalMetadata
{
    /// <summary>The admitted stage-2 judgment record id (spec 185).</summary>
    public const string JudgmentIdKey = "newsJudgmentId";

    /// <summary>The judgment's stage-2 cohort key — the prospectively designated presentation cohort.</summary>
    public const string JudgmentCohortKeyKey = "newsJudgmentCohortKey";

    /// <summary>The matched point-in-time news observation id (spec 177).</summary>
    public const string ObservationIdKey = "newsObservationId";

    /// <summary>The judge's business-trajectory display token, so the direction's REASON is legible on the signal.</summary>
    public const string TrajectoryKey = "newsBusinessTrajectory";

    /// <summary>
    /// SPEC 194 §1.2 — the versioned marker a judgment-DERIVED news signal carries. Declared here, and only
    /// here, in the §1.4 pass so that the §1.4 admission transform can distinguish "a direction grounded in
    /// the evidence the judgment actually cited" from "an accrued spec-191 direction inherited by an article
    /// no judgment ever read". The §1.2 materializer WRITES it; nothing writes it yet, which is exactly why
    /// the transform reading it must treat an absent marker as the LEGACY case and never the reverse.
    /// </summary>
    public const string JudgmentSignalVersionKey = "newsJudgmentSignalVersion";

    /// <summary>
    /// The one value <see cref="JudgmentSignalVersionKey"/> currently carries. A signal claiming this
    /// version is asserting the full §1.2 provenance chain (judgment → cited facts → observations →
    /// evidence); a signal claiming it without carrying that provenance is malformed, and the §1.4 transform
    /// fails it closed to Neutral rather than trusting the claim.
    /// </summary>
    public const string JudgmentSignalVersionValue = "news-judgment-signal-v1";

    /// <summary>The ordered distinct fact ids the judge said ESTABLISH the trajectory (spec 187 §1's <c>TrajectoryFactIds</c>).</summary>
    public const string TrajectoryFactIdsKey = "newsJudgmentTrajectoryFactIds";

    /// <summary>The ordered distinct news observation ids those cited facts were extracted from.</summary>
    public const string SourceObservationIdsKey = "newsJudgmentObservationIds";

    /// <summary>The ordered distinct news evidence ids those observations resolved to through the join.</summary>
    public const string CitedEvidenceIdsKey = "newsJudgmentEvidenceIds";

    /// <summary>
    /// The ONE delimiter for every GUID list in this envelope. A comma is chosen because the <c>D</c>
    /// format is <c>8-4-4-4-12</c> hex digits and a hyphen — it can never contain a comma — so the split is
    /// unambiguous and needs no escaping, and because the values ride inside a JSON string where a comma is
    /// not special. Declared as a const so the compose side and the parse side cannot disagree.
    /// </summary>
    public const string GuidListDelimiter = ",";

    /// <summary>
    /// SPEC 194 §1.2 — composes the versioned provenance envelope for one judgment-DERIVED news signal.
    /// This is the only producer of <see cref="JudgmentSignalVersionValue"/>, and the envelope it writes is
    /// exactly what the §1.4 admission transform's validity gate reads: version token + judgment id +
    /// judgment cohort key + trajectory, plus the three id chains that make the claim walkable
    /// (signal → judgment → cited facts → source observations → resolved evidence).
    /// <para>
    /// Every list is written ORDERED DISTINCT by its rendered lowercase <c>D</c> form under
    /// <see cref="StringComparer.Ordinal"/>. Sorting the RENDERED form rather than the <see cref="Guid"/>
    /// itself is deliberate: the persisted bytes are what a reader sees, so ordering them by the same
    /// comparison a reader would apply makes the envelope's determinism (AD-3) checkable by eye, and it
    /// removes any dependence on <see cref="Guid.CompareTo(Guid)"/>'s field-wise ordering, which does not
    /// match the textual order the value is written in.
    /// </para>
    /// </summary>
    public static string ComposeJudgmentSignal(
        Guid judgmentId,
        string judgmentCohortKey,
        string trajectoryToken,
        IReadOnlyList<Guid> trajectoryFactIds,
        IReadOnlyList<Guid> sourceObservationIds,
        IReadOnlyList<Guid> citedEvidenceIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(judgmentCohortKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(trajectoryToken);
        ArgumentNullException.ThrowIfNull(trajectoryFactIds);
        ArgumentNullException.ThrowIfNull(sourceObservationIds);
        ArgumentNullException.ThrowIfNull(citedEvidenceIds);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JudgmentSignalVersionKey] = JudgmentSignalVersionValue,
            [JudgmentIdKey] = judgmentId.ToString("D"),
            [JudgmentCohortKeyKey] = judgmentCohortKey,
            // NOTE — a deliberate, recorded deviation from spec 194 §1.2's prose, which names this key
            // `newsTrajectory`. The key ALREADY EXISTS as `newsBusinessTrajectory` (declared above for the
            // spec-191 producer) and the §1.4 admission transform ALREADY READS it to decide whether a v1
            // envelope is well formed. Introducing a second spelling for the same fact would mean two
            // trajectory keys and, in practice, two readers — precisely the duplicate-parser failure the
            // same section forbids two sentences later. One key definition wins over the prose.
            [TrajectoryKey] = trajectoryToken,
            [TrajectoryFactIdsKey] = ComposeGuidList(trajectoryFactIds),
            [SourceObservationIdsKey] = ComposeGuidList(sourceObservationIds),
            [CitedEvidenceIdsKey] = ComposeGuidList(citedEvidenceIds),
        };

        return EvidenceMetadata.Compose(metadata, []);
    }

    /// <summary>
    /// Renders one GUID list exactly as <see cref="ComposeJudgmentSignal"/> persists it: lowercase
    /// <c>D</c> format, distinct, ordinally ordered, joined by <see cref="GuidListDelimiter"/>.
    /// </summary>
    public static string ComposeGuidList(IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return string.Join(
            GuidListDelimiter,
            ids.Select(static id => id.ToString("D"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static rendered => rendered, StringComparer.Ordinal));
    }

    /// <summary>
    /// The PARSE side of <see cref="ComposeGuidList"/> — supplied here rather than left to each reader so
    /// the envelope has one writer and one reader, mirroring <c>EvidenceMetadata</c>'s own rule. Defensive
    /// like every metadata read in this repo: a null/blank value, or an entry that is not a parseable GUID,
    /// yields no element rather than throwing. An unparseable entry is DROPPED, never guessed at — a
    /// caller checking provenance completeness compares the parsed count against what it expected.
    /// </summary>
    public static IReadOnlyList<Guid> ParseGuidList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var parsed = new List<Guid>();
        foreach (var token in value.Split(GuidListDelimiter, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(token.Trim(), out var id))
            {
                parsed.Add(id);
            }
        }

        return parsed;
    }

    /// <summary>
    /// SPEC 194 — the ONE structural classification of a news signal's judgment provenance envelope, and the
    /// ONE place the <c>news-judgment-signal-v1</c> validity question is answered.
    /// <para>
    /// <b>Why it lives here and is shared by three call sites.</b> Three scoring-assembly steps ask a version
    /// of the same question and must never answer it differently: §1.4's
    /// <c>LegacyNewsInheritanceNeutralization</c> (is this an accrued spec-191 inherited direction, a broken
    /// v1 envelope, or none of my business?), §1.3's <c>NewsJudgmentSignalSupersede</c> (may this signal
    /// replace the ordinary article signal over its evidence?) and §1.5's <c>media-collapse-v2</c> (may this
    /// signal represent its event bucket?). A second copy of the predicate would let a signal be "valid
    /// enough to supersede" while "malformed enough to neutralize" — a score that both keeps and disowns the
    /// same direction. One classifier, one parse, one answer.
    /// </para>
    /// <para>
    /// Deterministic and total (AD-3): no clock, config, IO or randomness, and every input — including
    /// unreadable JSON — maps to exactly one member. It reads through the shared
    /// <c>EvidenceMetadata.TryRead</c>, never a second parser, and never throws.
    /// </para>
    /// <para>
    /// It classifies the ENVELOPE only. It deliberately does not look at <c>Direction</c>, <c>Type</c> or any
    /// other signal field: those gates belong to the callers, whose questions differ (the neutralization has
    /// nothing to suppress on an already-Neutral signal, while the supersede and the collapse care about the
    /// envelope regardless of what §1.4 may already have done to the direction).
    /// </para>
    /// </summary>
    public static NewsJudgmentSignalProvenance ClassifyProvenance(string? metadataJson)
    {
        // No envelope at all is NOT a broken envelope: the signal makes no judgment-grounding claim, so there
        // is nothing here to contradict. `null`/blank means NOT RECORDED (the trailing-nullable rule
        // Signal.MetadataJson was added under), and this classifier never invents a fact from an absence.
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return NewsJudgmentSignalProvenance.None;
        }

        if (!EvidenceMetadata.TryRead(metadataJson, out var metadata, out _))
        {
            // Present but unreadable. Deliberately treated as a MALFORMED judgment-signal envelope rather
            // than as "not our shape", and the decision is a fail-closed one: the version token that would
            // say whose envelope it is lives INSIDE the bytes that cannot be parsed, so "unrelated family" is
            // unprovable, while "provenance that cannot be read" is certain.
            return NewsJudgmentSignalProvenance.MalformedJudgmentEnvelope;
        }

        if (metadata.TryGetValue(JudgmentSignalVersionKey, out var version)
            && string.Equals(version, JudgmentSignalVersionValue, StringComparison.Ordinal))
        {
            // A spec-194 §1.2 judgment-DERIVED signal — or something claiming to be one. A claim without the
            // provenance the version promises is worth less than no claim at all, so it fails closed onto the
            // malformed axis rather than being trusted.
            return IsWellFormedJudgmentSignal(metadata)
                ? NewsJudgmentSignalProvenance.JudgmentDerived
                : NewsJudgmentSignalProvenance.MalformedJudgmentEnvelope;
        }

        // No v1 token (or a token that is not v1). This is the accrued spec-191 shape iff it carries that
        // retired producer's three provenance keys. Anything else — an unrelated metadata bag, a future
        // directional family with its own keys — is NONE, which is what "never match on Direction alone"
        // means in practice.
        return HasLegacyInheritanceKeys(metadata)
            ? NewsJudgmentSignalProvenance.LegacyInheritance
            : NewsJudgmentSignalProvenance.None;
    }

    /// <summary>
    /// The single question §1.3's supersede and §1.5's collapse ask: is this a structurally valid
    /// <c>news-judgment-signal-v1</c> signal — i.e. a direction grounded in the evidence a judgment actually
    /// cited, rather than an ordinary article event, an accrued spec-191 inherited direction or an
    /// unverifiable claim? Expressed in terms of <see cref="ClassifyProvenance"/> so there is exactly one
    /// definition; a caller needing to distinguish the failure modes calls the classifier directly.
    /// </summary>
    public static bool IsJudgmentDerived(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return ClassifyProvenance(signal.MetadataJson) == NewsJudgmentSignalProvenance.JudgmentDerived;
    }

    /// <summary>
    /// The accrued spec-191 shape: the retired extraction-time producer's judgment identity, its cohort and
    /// the matched observation, with no <c>news-judgment-signal-v1</c> token (established by the caller).
    /// </summary>
    private static bool HasLegacyInheritanceKeys(IReadOnlyDictionary<string, string> metadata) =>
        HasValue(metadata, JudgmentIdKey)
        && HasValue(metadata, JudgmentCohortKeyKey)
        && HasValue(metadata, ObservationIdKey);

    /// <summary>
    /// "Well formed" for the purposes of a SCORING admission: the envelope parses, claims
    /// <c>news-judgment-signal-v1</c> (already established by the caller) and carries the judgment identity,
    /// its cohort and the trajectory that produced the direction. <see cref="ComposeJudgmentSignal"/> writes a
    /// richer envelope than this (the ordered cited fact/observation/evidence id lists); validating those is
    /// the materializer's job at the point of CREATION. This gate is the minimum an admission needs in order
    /// to say the direction is attributable at all, and it is deliberately not a second copy of the
    /// materializer's rules.
    /// </summary>
    private static bool IsWellFormedJudgmentSignal(IReadOnlyDictionary<string, string> metadata) =>
        HasValue(metadata, JudgmentIdKey)
        && HasValue(metadata, JudgmentCohortKeyKey)
        && HasValue(metadata, TrajectoryKey);

    /// <summary>A key counts as carried only when its value is non-blank: a blank value records nothing.</summary>
    private static bool HasValue(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
}

/// <summary>
/// What a news signal's metadata envelope claims about the judgment behind its direction. Total: every
/// signal — including one with no metadata and one whose metadata cannot be parsed — maps to exactly one
/// member, so no caller needs a fallback branch.
/// <para>
/// The two failure members are never pooled. <see cref="LegacyInheritance"/> is the known residue of a
/// RETIRED producer (spec 191's extraction-time read), while <see cref="MalformedJudgmentEnvelope"/> means a
/// CURRENT writer emitted provenance that cannot be verified — a live integrity failure that would otherwise
/// hide inside the first's expected, slowly-draining count.
/// </para>
/// </summary>
public enum NewsJudgmentSignalProvenance
{
    /// <summary>
    /// No judgment-provenance claim: no envelope, or an envelope of some other family. The signal is none of
    /// the spec-194 news transforms' business and passes through every one of them untouched.
    /// </summary>
    None = 0,

    /// <summary>
    /// A structurally valid <c>news-judgment-signal-v1</c> signal: its direction is grounded in the evidence
    /// the cited judgment actually read. This is the signal that supersedes the ordinary article event
    /// (§1.3) and that represents its event bucket ahead of an earlier Neutral member (§1.5).
    /// </summary>
    JudgmentDerived = 1,

    /// <summary>
    /// An envelope that could not be read, or that claims <c>news-judgment-signal-v1</c> without carrying the
    /// provenance that version promises. Fails closed everywhere: it neither keeps its direction (§1.4) nor
    /// wins anything (§1.3/§1.5).
    /// </summary>
    MalformedJudgmentEnvelope = 2,

    /// <summary>
    /// An accrued spec-191 directional news signal: the retired producer's judgment/cohort/observation
    /// provenance with no <c>news-judgment-signal-v1</c> token, so its direction came from a company-level
    /// judgment that had never read the matched article.
    /// </summary>
    LegacyInheritance = 3,
}
