namespace Radar.Application.NewsTyping;

/// <summary>
/// Resolved, validated news-typing limits (spec 181 §4/§6). Parsed and validated at the composition root
/// (the config→Application boundary — <c>IConfiguration</c> never crosses into this layer); every limit is a
/// cost/safety control, recorded on each typing record and hashed into NO scoring fingerprint.
/// </summary>
public sealed record NewsTypingOptions
{
    public NewsTypingOptions(string outputDirectory, int maxNewTypingsPerRun, int lookbackDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNewTypingsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lookbackDays, 1);

        OutputDirectory = outputDirectory;
        MaxNewTypingsPerRun = maxNewTypingsPerRun;
        LookbackDays = lookbackDays;
    }

    /// <summary>The news-typing output root (typings, family snapshots, live decomposition artifacts).</summary>
    public string OutputDirectory { get; }

    /// <summary>
    /// The per-READER per-run cap on NEW model calls (spec 181 §6: the 13k legacy backlog drains
    /// incrementally under this cap, sized from §1's measured ~21 s/read — never in one unbounded pass).
    /// </summary>
    public int MaxNewTypingsPerRun { get; }

    /// <summary>The decomposition/checkpoint window in days: (asOf − LookbackDays, asOf].</summary>
    public int LookbackDays { get; }

    public NewsTypingLimitsRecord ToLimitsRecord() => new(MaxNewTypingsPerRun, LookbackDays);
}
