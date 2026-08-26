namespace Radar.Application.NewsTyping;

/// <summary>
/// Resolved, validated news-typing limits (spec 181 §4/§6, extended by spec 186 §2 and spec 187 §2/§3).
/// Parsed and validated at the composition root (the config→Application boundary — <c>IConfiguration</c>
/// never crosses into this layer); every limit is a cost/safety control, recorded on each typing record and
/// hashed into NO scoring fingerprint.
/// </summary>
public sealed record NewsTypingOptions
{
    public NewsTypingOptions(
        string outputDirectory,
        int maxNewTypingsPerRun,
        int lookbackDays,
        int maxTypingAttempts,
        int maxRetryTypingsPerRun,
        int maxCandidateTypingsPerRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNewTypingsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lookbackDays, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTypingAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRetryTypingsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCandidateTypingsPerRun, 1);

        OutputDirectory = outputDirectory;
        MaxNewTypingsPerRun = maxNewTypingsPerRun;
        LookbackDays = lookbackDays;
        MaxTypingAttempts = maxTypingAttempts;
        MaxRetryTypingsPerRun = maxRetryTypingsPerRun;
        MaxCandidateTypingsPerRun = maxCandidateTypingsPerRun;
    }

    /// <summary>
    /// The AMBIENT default candidate-lane width (spec 187 §2). Declared HERE and referenced by the Worker
    /// options so the documented default lives in one place. It is deliberately NOT a default on the
    /// constructor parameter: every limit on this type is required, so a composition root that forgets one
    /// fails to compile rather than silently resolving a lane width nobody configured.
    /// <para>
    /// Spec 189 §1 raised the checked-in baseline PROFILE to 150 (inside a 350-call budget) while leaving
    /// this ambient default at 100 — see <see cref="MaxNewTypingsPerRun"/> for why the two differ.
    /// </para>
    /// </summary>
    public const int DefaultMaxCandidateTypingsPerRun = 100;

    /// <summary>The news-typing output root (typings, family snapshots, live decomposition artifacts).</summary>
    public string OutputDirectory { get; }

    /// <summary>
    /// The per-READER per-run cap on NEW model calls (spec 181 §6: the 13k legacy backlog drains
    /// incrementally under this cap, sized from §1's measured ~21 s/read — never in one unbounded pass).
    /// <para>
    /// <b>The AMBIENT code default stays 200; the shipped baseline PROFILE declares 350</b> (spec 189 §1).
    /// The increase is a measured operating decision for the checked-in scheduled profile — the 2026-08-24
    /// baseline captured 252 new observations against a 200-call cap while 2,017 in-window observations sat
    /// untyped — not permission for an arbitrary caller that merely enables typing to spend 75 % more.
    /// </para>
    /// </summary>
    public int MaxNewTypingsPerRun { get; }

    /// <summary>The decomposition/checkpoint window in days: (asOf − LookbackDays, asOf].</summary>
    public int LookbackDays { get; }

    /// <summary>
    /// The cap on HOSTED CALLS for one (cohort, observation, payload) — spec 186 §2, made STRICT by spec
    /// 187 §3. A provider/parse/validation failure is retried, but never forever: at this many OCCUPIED
    /// attempts the observation LEAVES selection, is counted as retry-exhausted and degrades its company's
    /// typing completeness.
    /// <para>
    /// The bound is enforced by the durable PRE-CALL <see cref="INewsTypingAttemptLedger"/>, not by counting
    /// outcome records: an outcome is written AFTER the call, so a crash, a cancellation or a failed outcome
    /// write used to consume a provider call while advancing the derived count by nothing. Occupancy is the
    /// union of reserved ordinals and LEGACY (pre-187, unlinked) outcome records — the derived counter
    /// survives only as that migration read. A reservation that produced no outcome conservatively consumes
    /// an attempt: this budget may be spent early, but it can never be overspent.
    /// </para>
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

    /// <summary>
    /// The per-READER per-run cap on the CANDIDATE first-attempt lane (spec 187 §2): how many of this
    /// pass's hosted calls may be spent, ahead of the global queue, on the companies THIS run is about to
    /// judge. It exists because the first live judgment run spent its entire budget on the global
    /// 30-day/backlog queue and then judged 18 companies whose motivating headlines were still untyped.
    /// <para>
    /// The lane sits INSIDE <see cref="MaxNewTypingsPerRun"/>, AFTER the retry lane, and is filled
    /// ROUND-ROBIN over the ordered candidate plan, so one noisy company cannot consume it before the other
    /// candidates receive an observation. The config boundary additionally enforces
    /// <c>MaxCandidateTypingsPerRun + MaxRetryTypingsPerRun &lt; MaxNewTypingsPerRun</c> whenever judgment is
    /// enabled, which reserves at least one GENERAL first-attempt slot under every valid configuration —
    /// candidate priority must never be able to stop the 13k legacy backlog draining. The generator clamps
    /// the lane to the remaining budget defensively, so a hand-built options instance can never over-select.
    /// </para>
    /// </summary>
    public int MaxCandidateTypingsPerRun { get; }

    public NewsTypingLimitsRecord ToLimitsRecord() => new(
        MaxNewTypingsPerRun,
        LookbackDays,
        MaxTypingAttempts,
        MaxRetryTypingsPerRun,
        MaxCandidateTypingsPerRun);
}
