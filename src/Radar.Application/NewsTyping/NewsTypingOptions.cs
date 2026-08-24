namespace Radar.Application.NewsTyping;

/// <summary>
/// Resolved, validated news-typing limits (spec 181 §4/§6, extended by spec 186 §2). Parsed and validated at
/// the composition root (the config→Application boundary — <c>IConfiguration</c> never crosses into this
/// layer); every limit is a cost/safety control, recorded on each typing record and hashed into NO scoring
/// fingerprint.
/// </summary>
public sealed record NewsTypingOptions
{
    public NewsTypingOptions(
        string outputDirectory,
        int maxNewTypingsPerRun,
        int lookbackDays,
        int maxTypingAttempts,
        int maxRetryTypingsPerRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNewTypingsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lookbackDays, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTypingAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRetryTypingsPerRun, 1);

        OutputDirectory = outputDirectory;
        MaxNewTypingsPerRun = maxNewTypingsPerRun;
        LookbackDays = lookbackDays;
        MaxTypingAttempts = maxTypingAttempts;
        MaxRetryTypingsPerRun = maxRetryTypingsPerRun;
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

    /// <summary>
    /// The cap on HOSTED CALLS for one (cohort, observation, payload) — spec 186 §2. A provider/parse/
    /// validation failure is retried, but never forever: at this many recorded attempts the observation
    /// LEAVES selection, is counted as retry-exhausted and degrades its company's typing completeness.
    /// </summary>
    public int MaxTypingAttempts { get; }

    /// <summary>
    /// The per-READER per-run cap on the RETRY lane (spec 186 §2). The config boundary additionally rejects
    /// a value at or above <see cref="MaxNewTypingsPerRun"/> (the cross-field rule lives with the other
    /// config-shape rules; the generator still clamps the lane to the per-run cap defensively, so a
    /// hand-built options instance can never over-select). Retries fill the lane oldest-last-attempt first,
    /// so neither lane
    /// can monopolize the budget: a pending retry is reached within
    /// <c>ceil(pendingRetries / MaxRetryTypingsPerRun)</c> runs however many fresh failures arrive, and
    /// unused lane capacity flows back to first attempts.
    /// </summary>
    public int MaxRetryTypingsPerRun { get; }

    public NewsTypingLimitsRecord ToLimitsRecord() =>
        new(MaxNewTypingsPerRun, LookbackDays, MaxTypingAttempts, MaxRetryTypingsPerRun);
}
