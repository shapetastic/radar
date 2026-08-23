using System.Globalization;
using System.Text;

using Radar.Application.Efficacy.Claims;
using Radar.Application.Identity;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// THE semantic identity of an AD-15 composite gate VERDICT (spec 186 §3) — a content hash over the gate
/// contract, the admitted evidence and both halves of the verdict, computed by the paired-comparison writer
/// and carried as a run-level column in <c>strategy-paired-comparison.csv</c>.
/// <para>
/// <b>Why it exists.</b> Spec 184's reducer honoured a human <c>overridesGate</c> call only when the call
/// POST-DATED the verdict, and the "verdict instant" was the artifact's filesystem mtime — which the daily
/// efficacy re-write advances, so a valid override silently expired after one run (and a copy/restore had
/// the same effect, and the answer differed per machine). Time-comparing an override against a verdict is
/// the wrong primitive: an override is about a PARTICULAR verdict, so it binds to that verdict BY NAME. An
/// identical re-computation keeps the id (the override holds); any change to the evidence or to either half
/// of the verdict mints a new one (the gate default re-arms, visibly).
/// </para>
/// <para>
/// <b>Deterministic and machine-independent (AD-3):</b> no clock, no randomness, no file path, no file
/// timestamp, no machine name, no run id — nothing outside the comparison result and its verdict. Numbers
/// are rendered exactly as the artifact renders them (invariant culture, fixed precision), so the id is a
/// hash of the evidence the artifact actually DISCLOSES and a reader can recompute it from the artifact.
/// </para>
/// </summary>
public static class GateVerdictIdentity
{
    /// <summary>The canonical-string namespace prefix; bumping it re-mints every id, deliberately.</summary>
    public const string CanonicalPrefix = "radar:ad15-gate-verdict:v1";

    /// <summary>The value the artifact carries when NO verdict exists (see <see cref="VerdictExists"/>).</summary>
    public const string None = "";

    /// <summary>
    /// Whether this artifact expresses a gate VERDICT at all — the same condition
    /// <c>StrategyEvidenceStatusCalculator</c> uses to emit <c>GatePassed</c>/<c>GateFailed</c> (and hence
    /// the only condition under which the operating-call reducer consults a verdict at all), evaluated here
    /// over the STRUCTURED reasons instead of their rendered text:
    /// <list type="number">
    /// <item>the arm under test is the PREDECLARED primary and a boundary was precommitted — otherwise the
    /// artifact is exploratory and can judge nothing; and</item>
    /// <item>the composite gate either qualifies, or failed with merit reasons ONLY
    /// (<see cref="Ad15GateReasonCodes.MeritFailureCodes"/>) — an accrual/prerequisite reason means the gate
    /// could not yet evaluate, which is pending, never a verdict.</item>
    /// </list>
    /// When it is false the column is EMPTY: there is nothing to override, and an empty id can never match
    /// an override (so nothing is fabricated — AD-8).
    /// </summary>
    public static bool VerdictExists(PairedStrategyComparison result, Ad15ClaimVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(verdict);

        if (!result.PrimaryWasPredeclared || result.FirstEligibleAsOf is null)
        {
            return false;
        }

        if (verdict.Qualifies)
        {
            return true;
        }

        var codes = verdict.Reasons.Select(r => r.Code).ToList();
        var hasMerit = codes.Any(c => Ad15GateReasonCodes.MeritFailureCodes.Contains(c, StringComparer.Ordinal));
        var hasNonMerit = codes.Any(c => Ad15GateReasonCodes.NonMeritCodes.Contains(c, StringComparer.Ordinal));
        return hasMerit && !hasNonMerit;
    }

    /// <summary>
    /// The verdict id, or <see cref="None"/> when no verdict exists. Hashed, in this FIXED canonical order:
    /// <list type="number">
    /// <item><b>the gate CONTRACT</b> — predeclared primary strategy name + <c>PrimaryWasPredeclared</c>,
    /// the declared boundary <c>FirstEligibleAsOf</c>, and
    /// <see cref="Ad15GateReasonCodes.VocabularyVersion"/>;</item>
    /// <item><b>the ADMITTED purged outcome blocks</b> — each block's date and observed entry/exit, then,
    /// per baseline in result order, every admitted delta's (baseline name, block date, joint companies,
    /// primary rho, baseline rho, paired delta): exactly the per-block inputs the blocks CSV renders, i.e.
    /// the evidence the verdict rests on;</item>
    /// <item><b>the price-gate verdict</b> — <c>SatisfiesPriceGate</c> and the ordered price gate reason
    /// codes (each with the baseline it is about);</item>
    /// <item><b>the AD-16 prerequisite and the composite outcome</b> — <c>WasCalculated</c>, the screen
    /// outcome token, <c>Qualifies</c>, and the ordered composite reason codes.</item>
    /// </list>
    /// <b>Deliberately EXCLUDED</b> (they are provenance of the RUN, not identity of the VERDICT): every
    /// wall-clock instant, the artifact's path/mtime/size, the machine, the run id, the free-form
    /// <c>Detail</c> text of a reason (its variable parts — admitted-block counts — are already hashed as
    /// evidence), the dropped/candidate dates that never entered the claim, the support tallies, and the
    /// rendered markdown.
    /// </summary>
    public static string Compute(PairedStrategyComparison result, Ad15ClaimVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(verdict);

        return VerdictExists(result, verdict)
            ? CanonicalHash.Sha256Hex(CanonicalString(result, verdict))
            : None;
    }

    /// <summary>
    /// The canonical string <see cref="Compute"/> hashes. Exposed (internal) so a test can pin the ORDER
    /// and the exclusions directly rather than only through the digest.
    /// </summary>
    internal static string CanonicalString(PairedStrategyComparison result, Ad15ClaimVerdict verdict)
    {
        var sb = new StringBuilder(CanonicalPrefix).Append('\n');

        // (i) the gate CONTRACT identity.
        sb.Append("contract:");
        Text(sb, result.PrimaryStrategyName);
        sb.Append(Bool(result.PrimaryWasPredeclared)).Append(';');
        sb.Append(result.FirstEligibleAsOf is { } boundary ? Date(boundary) : string.Empty).Append(';');
        Text(sb, Ad15GateReasonCodes.VocabularyVersion);
        sb.Append('\n');

        // (ii) the ADMITTED purged outcome blocks — the evidence the verdict rests on.
        var candidatesByDate = new Dictionary<DateOnly, PairedCandidateDate>();
        foreach (var candidate in result.CandidateDates)
        {
            candidatesByDate.TryAdd(candidate.Date, candidate);
        }

        sb.Append("blocks:").Append(Int(result.AdmittedBlocks.Count)).Append(';');
        foreach (var block in result.AdmittedBlocks)
        {
            sb.Append(Date(block.Date)).Append('~')
                .Append(Date(block.ObservedEntry)).Append('~')
                .Append(Date(block.ObservedExit)).Append(';');
        }

        sb.Append('\n').Append("deltas:").Append(Int(result.Baselines.Count)).Append(';');
        foreach (var baseline in result.Baselines)
        {
            Text(sb, baseline.BaselineName);
            sb.Append(Int(baseline.AdmittedDeltas.Count)).Append(';');
            foreach (var delta in baseline.AdmittedDeltas)
            {
                sb.Append(Date(delta.Date)).Append('~');

                // The block's per-baseline inputs, as the blocks CSV renders them. A delta whose candidate
                // date is absent is a structurally impossible input; it is MARKED rather than thrown on, so
                // an identity computation can never fail a run — and the marker stays distinguishable from
                // every real value.
                if (candidatesByDate.TryGetValue(delta.Date, out var candidate))
                {
                    var baselineRho = candidate.Baselines.FirstOrDefault(x =>
                        string.Equals(x.BaselineName, baseline.BaselineName, StringComparison.Ordinal));
                    sb.Append(Int(candidate.Companies)).Append('~')
                        .Append(Delta(candidate.PrimaryRho)).Append('~')
                        .Append(baselineRho is null ? "absent" : Delta(baselineRho.Rho)).Append('~');
                }
                else
                {
                    sb.Append("absent~absent~absent~");
                }

                sb.Append(Delta(delta.Delta)).Append(';');
            }
        }

        // (iii) the price-gate verdict itself.
        sb.Append('\n').Append("price:").Append(Bool(result.SatisfiesPriceGate)).Append(';');
        AppendReasonCodes(sb, result.PriceGateReasons);

        // (iv) the AD-16 prerequisite identity + the composite outcome.
        sb.Append('\n').Append("ad16:").Append(Bool(verdict.Prerequisite.WasCalculated)).Append(';');
        Text(sb, Ad15ClaimGate.OutcomeToken(verdict.Prerequisite.Outcome));
        sb.Append("composite:").Append(Bool(verdict.Qualifies)).Append(';');
        AppendReasonCodes(sb, verdict.Reasons);

        return sb.ToString();
    }

    private static void AppendReasonCodes(StringBuilder sb, IReadOnlyList<Ad15GateReason> reasons)
    {
        sb.Append(Int(reasons.Count)).Append(';');
        foreach (var reason in reasons)
        {
            Text(sb, reason.Code);
            Text(sb, reason.BaselineName ?? string.Empty);
        }
    }

    // Length-prefixed so a strategy/baseline name containing a separator can never forge a boundary
    // (two different inputs must never share one canonical string).
    private static void Text(StringBuilder sb, string value) =>
        sb.Append(Int(value.Length)).Append(':').Append(value).Append(';');

    private static string Date(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Delta(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";
}
