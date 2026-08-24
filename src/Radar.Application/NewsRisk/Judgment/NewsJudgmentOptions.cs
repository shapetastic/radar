namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// Resolved, validated direction-judge limits and presentation designation (spec 185 §4/§5). Parsed and
/// validated at the composition root (the config→Application boundary — <c>IConfiguration</c> never crosses
/// into this layer); every limit is a cost/safety control, recorded on each judgment record and hashed into
/// NO scoring fingerprint. The presentation cohort is DECLARED PROSPECTIVELY in config (the
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
        string presentationJudge,
        string presentationExtractor,
        string newsSearchCollectorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCompaniesPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFamiliesPerJudgment, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxJudgmentAttempts, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationJudge);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationExtractor);
        ArgumentException.ThrowIfNullOrWhiteSpace(newsSearchCollectorName);

        OutputDirectory = outputDirectory;
        MaxCompaniesPerRun = maxCompaniesPerRun;
        MaxFamiliesPerJudgment = maxFamiliesPerJudgment;
        MaxJudgmentAttempts = maxJudgmentAttempts;
        PresentationJudge = presentationJudge;
        PresentationExtractor = presentationExtractor;
        NewsSearchCollectorName = newsSearchCollectorName;
    }

    /// <summary>The news-risk output root — judgments persist under <c>{root}/judgments/…</c> (spec 185 §5).</summary>
    public string OutputDirectory { get; }

    /// <summary>The per-run candidate cost budget (traversal order, the spec-179 §3 selector reused).</summary>
    public int MaxCompaniesPerRun { get; }

    /// <summary>Cap on families supplied to one judgment; a bite records <see cref="NewsJudgmentFamilyBundle.Capped"/>.</summary>
    public int MaxFamiliesPerJudgment { get; }

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

    public NewsJudgmentLimitsRecord ToLimitsRecord() =>
        new(MaxCompaniesPerRun, MaxFamiliesPerJudgment, MaxJudgmentAttempts);
}
