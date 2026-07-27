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

Write-Host "==== Radar scheduled baseline ====" -ForegroundColor Cyan
Write-Host "Profile: $Profile"
Write-Host "Mode   : $Mode"
Write-Host "API key: loaded into `$env:$KeyEnvVar from the configured key file (value never logged)."

# --- run the measurement ---
# Splat a HASHTABLE, not an array: array splatting does not reliably bind named parameters of an advanced
# (`[CmdletBinding()]`) script like run-radar.ps1 - the -SecUserAgent value gets orphaned as a positional
# ("A positional parameter cannot be found ..."). Hashtable splatting binds by name and is the correct form.
$runRadar = Join-Path $scriptDir "run-radar.ps1"
$runArgs = @{ Profile = $Profile; SecUserAgent = $SecUserAgent; Mode = $Mode }
if ($SkipBuild) { $runArgs['SkipBuild'] = $true }
if ($WhatIf)    { $runArgs['WhatIf']    = $true }

& $runRadar @runArgs
exit $LASTEXITCODE
