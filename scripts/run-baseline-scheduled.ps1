# run-baseline-scheduled.ps1 - unattended wrapper around run-radar.ps1 for the scheduled baseline measurement.
#
# Why it exists: since spec 119 the baseline earnings read is DeepSeek-V4-Flash on DeepInfra, so the run needs an
# API key in the environment. A Windows scheduled task does not inherit an interactive session's variables, so the
# task invokes THIS script, which loads the key from a file into $env:DEEPINFRA_API_KEY for the child process only.
#
# Secret hygiene (the hard rule):
#   - No key VALUE is ever committed, printed, logged or written by this script - it is read from -KeyFile and
#     placed in the process environment, nothing else. Only the env-var NAME appears in output.
#   - No machine-specific path is committed either: -KeyFile is a required parameter supplied at task-registration
#     time by scripts/setup-baseline-task.ps1.
#   - A missing/empty key file FAILS LOUD (non-zero exit) rather than letting the run silently degrade.
#   - Spec 171 added output capture, which CHANGES THE THREAT MODEL, so the rule is restated rather than assumed:
#     this script still holds the key in exactly one place (the child process's environment) and adds no
#     statement that echoes it. The log is a capture of the CHILD's streams, so the standing obligation is to
#     confirm on a real run that the produced log contains no key material and that the Worker never echoes
#     Radar:Ai credentials at the log level the run uses. Start-Transcript was deliberately NOT used: it
#     captures the whole session and is far easier to widen by accident later.
#
# Keep-awake (spec 171 section 1): this wrapper holds ES_CONTINUOUS|ES_SYSTEM_REQUIRED for the duration of the
# run via scripts/lib/keep-awake.ps1 (shared with run-radar.ps1), because the measurement host enters connected
# standby on display timeout and was suspending the run mid-flight - losing wall clock AND collector sources.
# The display is deliberately NOT held, and the request is released in a `finally`. It fails OPEN; see that file.
#
# Run log (spec 171 section 2): the child run's output is captured to <repo>\logs\baseline-<utc>.log (gitignored,
# pruned by -LogRetentionDays) while still printing to the console, so "did this run get suspended?" is
# answerable from the log alone. Two honest limitations, both deliberate:
#   - The child's STDERR is not merged into the capture. Under Windows PowerShell 5.1 (which is how the
#     scheduled task launches this script) with $ErrorActionPreference='Stop', merging a native command's
#     stderr turns its first stderr line into a TERMINATING error and would abort the run - a far worse
#     outcome than an incomplete log. Stdout, Write-Host and warnings are captured; a terminating failure is
#     caught and written to the log by this script, and stderr still reaches the console/task as it always did.
#   - Console colour is lost for the child's lines (they are re-emitted as plain text so the console and the
#     log agree exactly). Nothing watches a scheduled run's console; interactive run-radar.ps1 is unaffected.
#
# Mode passthrough (spec 144): -Mode selects which pass the scheduled run performs - 'full' (the default, the
# combined collect+score run this task has always performed), 'collect' (stages 1-5 only) or 'score' (stage 6+7
# over the accrued store, running NO collector - so no SEC / GDELT / Google News traffic and no AI spend).
# A score pass is NOT request-free: Radar:Prices:Enabled is independent of Radar:RunMode, so with the default
# profile it still fetches daily price history per ticker - see the -Mode score caveats in run-radar.ps1 and the
# scheduling notes in setup-baseline-task.ps1. The key is still loaded for EVERY mode: a score pass needs the
# same Radar:Ai configuration as a collect pass because the AI descriptor is a ScoringConfigVersion input, even
# though it never issues an AI read.
#
# Example (interactive smoke test before registering the task):
#   powershell -File scripts/run-baseline-scheduled.ps1 -KeyFile C:\path\to\key.txt -SecUserAgent "Name email" -WhatIf

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$KeyFile,                                   # Path to a file whose ENTIRE contents are the API key. Never committed, never echoed.
    [string]$KeyEnvVar     = "DEEPINFRA_API_KEY",       # The env var NAME the run profile's Radar:Ai:OpenAi:ApiKeyEnvVar declares.
    [string]$Profile       = "default",                 # The baseline profile; override only for a scheduled experiment.
    [ValidateSet("full", "collect", "score")]
    [string]$Mode          = "full",                    # Which pass to run (spec 144). Default keeps the existing combined baseline behaviour.
    [string]$SecUserAgent  = $(if ($env:RADAR_SEC_UA) { $env:RADAR_SEC_UA } else { "" }),  # SEC EDGAR needs a real "Name email"; falls back to $env:RADAR_SEC_UA.
    [string]$LogDirectory  = "",                        # Where the dated run log goes; blank = <repo>\logs. Host-local, gitignored.
    [ValidateRange(0, 3650)]
    [int]$LogRetentionDays = 30,                        # Prune baseline-*.log older than this. 0 = keep everything (an unattended daily task should not).
    [switch]$SkipBuild,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# Write to stderr and exit with a distinct non-zero code. Used instead of Write-Error so the exit code is
# deterministic under $ErrorActionPreference='Stop' (a terminating error would collapse every failure to 1),
# which matters when the only record of a scheduled run is the task's last result.
function Exit-Loud([string]$message, [int]$code) {
    $Host.UI.WriteErrorLine($message)
    exit $code
}

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot  = Split-Path -Parent $scriptDir

# Shared with run-radar.ps1 - dot-sourced, never pasted (spec 171 section 3).
. (Join-Path $scriptDir "lib\keep-awake.ps1")

$runStartUtc = [datetime]::UtcNow
$logWriter   = $null
$logPath     = $null

# Render one pipeline object the way the console showed it, so the log and the console agree line for line.
function ConvertTo-RunLogText($item) {
    if ($null -eq $item) { return '' }
    if ($item -is [string]) { return $item }
    if ($item -is [System.Management.Automation.WarningRecord]) { return "WARNING: $($item.Message)" }
    if ($item -is [System.Management.Automation.ErrorRecord])   { return (($item | Out-String).TrimEnd()) }
    return (($item | Out-String).TrimEnd())
}

# Write one line to the console AND the run log. Used for this script's own lines; the child's output goes
# through the capture pipeline below.
#
# FLUSHING EVERY LINE IS THE POINT, NOT AN OVERSIGHT (recorded here because it reads like one). This log exists
# to diagnose runs that are SUSPENDED, killed, or lose the machine mid-flight - exactly the cases where the
# `finally` never runs and a buffered tail is lost, and the lost lines would be precisely the ones immediately
# before the failure. It also makes the file tail-able while a multi-hour run is in progress. The cost is
# negligible against a ~90-minute run: Worker output is modest (spec 145 aggregated the dropped-signal warnings
# from ~13,625 lines per run to 43). Do not "optimise" this back to buffered writes.
function Write-RunLog([string]$line, [string]$color) {
    if ($color) { Write-Host $line -ForegroundColor $color } else { Write-Host $line }
    if ($script:logWriter) { $script:logWriter.WriteLine($line); $script:logWriter.Flush() }
}

# Warn on the console through the real warning stream (so the console is unchanged) AND record the same text in
# the run log, so the log stays self-contained. Used for warnings THIS script raises.
function Write-RunWarning([string]$message) {
    Write-Warning $message
    if ($script:logWriter) { $script:logWriter.WriteLine("WARNING: $message"); $script:logWriter.Flush() }
}

# Record warnings raised INSIDE a called function (captured via -WarningVariable) in the run log. The console
# already showed them - -WarningVariable collects without suppressing - so this writes to the file only, and it
# does so WITHOUT any stream redirection: merging streams around a call that may reach a native command is the
# Windows PowerShell 5.1 trap documented in the header, and -WarningVariable avoids it entirely.
function Write-CapturedWarningsToLog($warnings) {
    if (-not $script:logWriter) { return }
    foreach ($w in $warnings) {
        if ($null -eq $w) { continue }
        $text = if ($w -is [System.Management.Automation.WarningRecord]) { $w.Message } else { [string]$w }
        $script:logWriter.WriteLine("WARNING: $text")
    }
    $script:logWriter.Flush()
}

# --- load the API key (fail loud; never echo the value) ---
if (-not (Test-Path -LiteralPath $KeyFile)) {
    Exit-Loud "Key file not found: '$KeyFile'. The scheduled baseline run needs the API key for `$env:$KeyEnvVar. (The key VALUE is never logged.)" 2
}

$key = (Get-Content -LiteralPath $KeyFile -Raw)
if ($null -ne $key) { $key = $key.Trim() }
if ([string]::IsNullOrWhiteSpace($key)) {
    Exit-Loud "Key file '$KeyFile' is empty. The scheduled baseline run needs a non-empty API key for `$env:$KeyEnvVar." 3
}

# Process-scoped only: this never touches the user/machine environment (no setx), so the key lives no longer
# than this run. Do NOT add any Write-Host of $key - the value must never reach a log or the task history.
Set-Item -Path ("Env:" + $KeyEnvVar) -Value $key
$key = $null

if ([string]::IsNullOrWhiteSpace($SecUserAgent)) {
    Exit-Loud "No SEC User-Agent. Pass -SecUserAgent 'Name email' (or set `$env:RADAR_SEC_UA) - SEC EDGAR HTTP 403s without a real contact." 4
}

# --- open the dated run log (spec 171 section 2) ---
# Opened AFTER the key/User-Agent validation on purpose, so no writer exists while the key is being handled -
# the cheapest way to keep the secret-hygiene rule true is for there to be nowhere to write it yet. The cost is
# that an Exit-Loud validation failure leaves no log file; that is acceptable because those failures are
# instant, fully described by their distinct exit codes (2/3/4) on stderr, and recorded in the task's history.
# The log exists to explain a LONG run, which by definition got past this point.
#
# FAIL OPEN, for the same reason keep-awake does: an unwritable log directory must not cost a night's run.
# The run's OUTPUT does not depend on the log, so warn and carry on without one.
if ([string]::IsNullOrWhiteSpace($LogDirectory)) { $LogDirectory = Join-Path $repoRoot "logs" }
try {
    if (-not (Test-Path -LiteralPath $LogDirectory)) { New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null }
    $logPath = Join-Path $LogDirectory ("baseline-{0}.log" -f $runStartUtc.ToString("yyyyMMdd'T'HHmmss'Z'"))
    # Explicit UTF-8 without BOM and append=$true, so every writer in this script agrees on one encoding
    # (Add-Content/Out-File/Tee-Object disagree about the default under Windows PowerShell 5.1).
    # APPEND, not overwrite, deliberately: the name is second-granularity and a baseline run takes hours, so a
    # collision is essentially unreachable - but if one ever happened, append keeps BOTH runs behind their own
    # start/end banners, whereas overwrite would silently destroy the earlier log.
    $logWriter = [System.IO.StreamWriter]::new($logPath, $true, (New-Object System.Text.UTF8Encoding($false)))
}
catch {
    # No writer exists here by definition, so this one can only reach the console - which is the whole point of
    # the warning: it says where the missing log went.
    $logWriter = $null
    Write-Warning "Could not open a run log under '$LogDirectory' ($($_.Exception.Message)). Continuing WITHOUT one - the run's output is unaffected."
}

Write-RunLog "==== Radar scheduled baseline ====" "Cyan"
Write-RunLog "Profile: $Profile"
Write-RunLog "Mode   : $Mode"
Write-RunLog "API key: loaded into `$env:$KeyEnvVar from the configured key file (value never logged)."
Write-RunLog "Started: $($runStartUtc.ToString('yyyy-MM-dd HH:mm:ss')) UTC / $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) local"
Write-RunLog "Log    : $(if ($logPath) { $logPath } else { '(none - see the warning above)' })"

# Retention: an unattended daily task must not fill the disk. Best-effort; a prune failure never stops a run.
if ($null -ne $logPath -and $LogRetentionDays -gt 0) {
    try {
        $cutoffUtc = $runStartUtc.AddDays(-$LogRetentionDays)
        $stale = @(Get-ChildItem -LiteralPath $LogDirectory -Filter "baseline-*.log" -File -ErrorAction Stop |
                   Where-Object { $_.LastWriteTimeUtc -lt $cutoffUtc })
        foreach ($f in $stale) { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop }
        if ($stale.Count -gt 0) { Write-RunLog "Prune  : removed $($stale.Count) log(s) older than $LogRetentionDays day(s)." }
    }
    catch {
        # Write-RunWarning, not Write-Warning: an unattended run's log must explain its own gaps. A prune that
        # silently stopped working would otherwise only show up as a full disk weeks later.
        Write-RunWarning "Could not prune old run logs in '$LogDirectory' ($($_.Exception.Message)). Continuing."
    }
}

# --- hold the host awake for the whole run (spec 171 section 1) ---
# Fail-open on purpose: a refused power request costs wall clock, never correctness - the reasoning is recorded
# in full in lib/keep-awake.ps1. Released in the `finally` below.
#
# -WarningVariable, NOT a stream merge. The helper explains a failed hold via Write-Warning, and that reason has
# to reach the LOG: this task runs unattended for many nights in a row, so if the hold fails on night one it
# fails on all of them, and the log is the only witness. -WarningVariable collects the WarningRecords WITHOUT
# suppressing their console display and WITHOUT any redirection operator, so it cannot reintroduce the
# 5.1 merged-stream terminating-error trap described in the header. The captured lines are written to the log
# immediately, i.e. BEFORE the "Awake :" line - the same order the console shows them in.
$keepAwakeWarnings = $null
$keepAwakeHeld = Enable-RadarKeepAwake -Reason "the scheduled Radar baseline run" -WarningVariable keepAwakeWarnings
Write-CapturedWarningsToLog $keepAwakeWarnings
if ($keepAwakeHeld) {
    Write-RunLog "Awake  : holding ES_CONTINUOUS|ES_SYSTEM_REQUIRED for this run (the display is NOT held)."
}
else {
    Write-RunLog "Awake  : NOT held - the WARNING line(s) immediately above give the reason. The run continues; expect it to take longer and to lose collector sources if this host suspends."
}

# --- run the measurement ---
# Splat a HASHTABLE, not an array: array splatting does not reliably bind named parameters of an advanced
# (`[CmdletBinding()]`) script like run-radar.ps1 - the -SecUserAgent value gets orphaned as a positional
# ("A positional parameter cannot be found ..."). Hashtable splatting binds by name and is the correct form.
$runRadar = Join-Path $scriptDir "run-radar.ps1"
$runArgs = @{ Profile = $Profile; SecUserAgent = $SecUserAgent; Mode = $Mode }
if ($SkipBuild) { $runArgs['SkipBuild'] = $true }
if ($WhatIf)    { $runArgs['WhatIf']    = $true }

$exitCode = $null
try {
    # Capture the child's success (1), warning (3) and information/Write-Host (6) streams and re-emit each
    # line to BOTH the console and the log. Stream 2 is deliberately NOT merged - see the header: under
    # Windows PowerShell 5.1 with $ErrorActionPreference='Stop' a merged native stderr line becomes a
    # terminating error and would abort the run. Unredirected, it reaches the console exactly as before.
    # (Flushed per line for the reason recorded on Write-RunLog above: a suspended or killed run must not lose
    # the tail, which is exactly the part that explains what happened.)
    & $runRadar @runArgs 3>&1 6>&1 | ForEach-Object {
        $text = ConvertTo-RunLogText $_
        Write-Host $text
        if ($logWriter) { $logWriter.WriteLine($text); $logWriter.Flush() }
    }
    $exitCode = $LASTEXITCODE
}
catch {
    # Record the failure in the log before it propagates, then re-throw so this script fails exactly as it did
    # before logging existed (stderr + non-zero exit; the task's last result is unchanged).
    Write-RunLog "FAILED: $($_.Exception.Message)"
    throw
}
finally {
    # Same capture as the acquire above: a failed RELEASE is the one that could leave the host awake, so its
    # reason belongs in the log too. The writer is still open here; it is disposed at the end of this block.
    $releaseWarnings = $null
    Disable-RadarKeepAwake -WarningVariable releaseWarnings
    Write-CapturedWarningsToLog $releaseWarnings

    $endUtc  = [datetime]::UtcNow
    $elapsed = $endUtc - $runStartUtc
    Write-RunLog "Ended  : $($endUtc.ToString('yyyy-MM-dd HH:mm:ss')) UTC / $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) local"
    Write-RunLog ("Elapsed: {0:00}:{1:00}:{2:00} (exit {3}) - compare against the awake-rate expectation; a large excess means the host was suspended mid-run." -f
                  [math]::Floor($elapsed.TotalHours), $elapsed.Minutes, $elapsed.Seconds, $(if ($null -ne $exitCode) { $exitCode } else { 'n/a' }))

    if ($logWriter) { $logWriter.Flush(); $logWriter.Dispose(); $logWriter = $null }
}

exit $exitCode
