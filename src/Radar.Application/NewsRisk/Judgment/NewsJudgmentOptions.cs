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
        string presentationJudge,
        string presentationExtractor,
        string newsSearchCollectorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCompaniesPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFamiliesPerJudgment, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationJudge);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationExtractor);
        ArgumentException.ThrowIfNullOrWhiteSpace(newsSearchCollectorName);

        OutputDirectory = outputDirectory;
        MaxCompaniesPerRun = maxCompaniesPerRun;
        MaxFamiliesPerJudgment = maxFamiliesPerJudgment;
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

    /// <summary>The judge reader NAME whose cohort supplies the leaders marker (declared before results, never switched after).</summary>
    public string PresentationJudge { get; }

    /// <summary>The typing extractor reader NAME whose stage-1 cohort feeds the presentation judgment.</summary>
    public string PresentationExtractor { get; }

    /// <summary>The coverage-recording news collector name (the spec-179/182 coverage-dimension input), resolved from the shared const.</summary>
    public string NewsSearchCollectorName { get; }

    public NewsJudgmentLimitsRecord ToLimitsRecord() => new(MaxCompaniesPerRun, MaxFamiliesPerJudgment);
}
