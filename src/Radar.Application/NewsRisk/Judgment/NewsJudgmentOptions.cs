namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// Resolved, validated direction-judge limits and presentation designation (spec 185 §4/§5). Parsed and
/// validated at the composition root (the config→Application boundary — <c>IConfiguration</c> never crosses
/// into this layer); every limit is a cost/safety control, recorded on each judgment record and hashed into
/// NO scoring fingerprint (spec 219 §6: the COVERAGE POLICY VERSION is hashed, because it decides which
/// companies are read at all — the budgets, which decide how much is spent reading one, are not). The presentation cohort is DECLARED PROSPECTIVELY in config (the
/// <c>PairedPrimaryStrategy</c> discipline): it names exactly one (judge reader, typing extractor) pair as
/// the leaders-marker source; every other cohort renders in the artifact only, and cohorts never pool.
/// </summary>
public sealed record NewsJudgmentOptions
{
    public NewsJudgmentOptions(
        string outputDirectory,
        int maxCompaniesPerRun,
        int maxFamiliesPerJudgment,
        int maxJudgmentAttempts,
        int maxFamiliesPerBreadthJudgment,
        string presentationJudge,
        string presentationExtractor,
        string newsSearchCollectorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCompaniesPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFamiliesPerJudgment, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxJudgmentAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFamiliesPerBreadthJudgment, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationJudge);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationExtractor);
        ArgumentException.ThrowIfNullOrWhiteSpace(newsSearchCollectorName);

        OutputDirectory = outputDirectory;
        MaxCompaniesPerRun = maxCompaniesPerRun;
        MaxFamiliesPerJudgment = maxFamiliesPerJudgment;
        MaxJudgmentAttempts = maxJudgmentAttempts;
        MaxFamiliesPerBreadthJudgment = maxFamiliesPerBreadthJudgment;
        PresentationJudge = presentationJudge;
        PresentationExtractor = presentationExtractor;
        NewsSearchCollectorName = newsSearchCollectorName;
    }

    /// <summary>The news-risk output root — judgments persist under <c>{root}/judgments/…</c> (spec 185 §5).</summary>
    public string OutputDirectory { get; }

    /// <summary>
    /// The per-run judged-candidate SAFETY VALVE over the COMBINED cohort — the spec-179 §3 depth traversal
    /// first, then the spec-219 §1 breadth pass filling the remainder. It was a coverage-shaping cost budget
    /// while the traversal was the only candidate source; since spec 219 it is a bound that should not
    /// normally bite, and a bite is COUNTED on the plan and named in the run's coverage line.
    /// </summary>
    public int MaxCompaniesPerRun { get; }

    /// <summary>Cap on families supplied to one DEPTH judgment; a bite records <see cref="NewsJudgmentFamilyBundle.Capped"/>.</summary>
    public int MaxFamiliesPerJudgment { get; }

    /// <summary>
    /// SPEC 219 §2 — the cap on families supplied to one BREADTH judgment (a company the spec-179 §3
    /// traversal did not select). The shape is deliberately inverted from the pre-219 one: a DEEP read of a
    /// few becomes a BOUNDED read of everyone. Shipped default
    /// <see cref="DefaultMaxFamiliesPerBreadthJudgment"/>; must be at least 1.
    /// <para>
    /// Like every other limit here it is a cost control and is hashed into NO scoring fingerprint. What IS
    /// hashed is the coverage POLICY VERSION (<see cref="NewsJudgmentCoveragePolicy.Version"/>), because
    /// that changes WHICH companies have a directional news read at all — see the "deliberately NOT here"
    /// paragraph on <c>NewsJudgmentScoringIdentity</c>.
    /// </para>
    /// </summary>
    public int MaxFamiliesPerBreadthJudgment { get; }

    /// <summary>
    /// The cap on HOSTED CALLS for one (stage-2 cohort, company, family set) — spec 187 §1. The stricter
    /// v2 validator makes a persistent <see cref="NewsJudgmentStatus.ValidationFailed"/> more likely, and a
    /// failure that is retried by EVERY later run is an unbounded provider bill. At this many call-producing
    /// attempts the pass makes NO call and records
    /// <see cref="NewsJudgmentStatus.AttemptsExhausted"/> instead.
    /// </summary>
    public int MaxJudgmentAttempts { get; }

    /// <summary>The judge reader NAME whose cohort supplies the leaders marker (declared before results, never switched after).</summary>
    public string PresentationJudge { get; }

    /// <summary>The typing extractor reader NAME whose stage-1 cohort feeds the presentation judgment.</summary>
    public string PresentationExtractor { get; }

    /// <summary>The coverage-recording news collector name (the spec-179/182 coverage-dimension input), resolved from the shared const.</summary>
    public string NewsSearchCollectorName { get; }

    /// <summary>The shipped default judgment-attempt bound (spec 187 §1), declared here and referenced by the Worker options so the documented default lives in one place.</summary>
    public const int DefaultMaxJudgmentAttempts = 3;

    /// <summary>
    /// The shipped default breadth family bound (spec 219 §2), declared here and referenced by the Worker
    /// options so the documented default lives in one place. 5, from MEASURED arithmetic rather than a
    /// projection: across the 322 accrued judgments on 2026-09-09 the families supplied per judgment ran
    /// min 0 / median 36 / mean 32.3 / p90 50 / max 50, so a ~19-company run supplied ~610 family-units,
    /// while 102 × 5 = 510. Universal coverage for LESS model work than the rank-gated read it replaces —
    /// before the depth cohort adds its own budget back on top.
    /// </summary>
    public const int DefaultMaxFamiliesPerBreadthJudgment = 5;

    /// <summary>
    /// The limits in force for one attempt. <paramref name="appliedMaxFamilies"/> is the family bound that
    /// ACTUALLY cut that attempt's input (spec 219 §2) — <see cref="MaxFamiliesPerBreadthJudgment"/> for a
    /// breadth read, <see cref="MaxFamiliesPerJudgment"/> for a full one. It is a REQUIRED argument here on
    /// purpose: a caller that forgot it would persist a bound that did not apply, which is the exact defect
    /// the field exists to close.
    /// </summary>
    public NewsJudgmentLimitsRecord ToLimitsRecord(int appliedMaxFamilies) =>
        new(MaxCompaniesPerRun, MaxFamiliesPerJudgment, MaxJudgmentAttempts, appliedMaxFamilies);

    /// <summary>The family bound that applies to an attempt read at <paramref name="depth"/> (spec 219 §2).</summary>
    public int MaxFamiliesFor(NewsJudgmentReadDepth depth) =>
        depth == NewsJudgmentReadDepth.Breadth ? MaxFamiliesPerBreadthJudgment : MaxFamiliesPerJudgment;
}
