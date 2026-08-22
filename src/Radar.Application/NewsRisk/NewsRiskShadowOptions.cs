namespace Radar.Application.NewsRisk;

/// <summary>
/// Resolved, validated shadow-read limits (spec 179 §11). Parsed and validated at the composition root (the
/// config→Application boundary — <c>IConfiguration</c> never crosses into this layer); every limit is a
/// cost/safety control, recorded on each assessment and hashed into NO scoring fingerprint.
/// </summary>
public sealed record NewsRiskShadowOptions
{
    public NewsRiskShadowOptions(
        string outputDirectory,
        int lookbackDays,
        int maxCompaniesPerRun,
        int maxArticlesPerCompany,
        int maxFetchedArticlesPerCompany,
        string newsSearchCollectorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(lookbackDays, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCompaniesPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxArticlesPerCompany, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFetchedArticlesPerCompany);
        ArgumentException.ThrowIfNullOrWhiteSpace(newsSearchCollectorName);

        OutputDirectory = outputDirectory;
        LookbackDays = lookbackDays;
        MaxCompaniesPerRun = maxCompaniesPerRun;
        MaxArticlesPerCompany = maxArticlesPerCompany;
        MaxFetchedArticlesPerCompany = maxFetchedArticlesPerCompany;
        NewsSearchCollectorName = newsSearchCollectorName;
    }

    public string OutputDirectory { get; }
    public int LookbackDays { get; }
    public int MaxCompaniesPerRun { get; }
    public int MaxArticlesPerCompany { get; }
    public int MaxFetchedArticlesPerCompany { get; }

    /// <summary>
    /// The provenance name of the coverage-recording news collector (spec 169's <c>newssearch</c>), whose
    /// per-company rows in the batch manifest are the §4 coverage gate. Resolved at the composition root
    /// from the SAME collector-name const the kind→collector table uses (the spec-169 AttentionArrival
    /// precedent), so it cannot drift from the collector that actually runs.
    /// </summary>
    public string NewsSearchCollectorName { get; }

    public NewsRiskShadowLimitsRecord ToLimitsRecord() => new(
        LookbackDays, MaxCompaniesPerRun, MaxArticlesPerCompany, MaxFetchedArticlesPerCompany);
}
