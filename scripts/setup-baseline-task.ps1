# setup-baseline-task.ps1 - (re)register the daily 'RadarBaselineDaily' scheduled task.
#
# MAINTAINER-RUN-ONCE, WITH ELEVATION. This is the single machine-mutating step of the baseline setup and is
# deliberately kept OUT of the coding pipeline: no agent/CI run executes it. It only registers a task that points
# at scripts/run-baseline-scheduled.ps1 with the machine-specific arguments (key-file path, SEC User-Agent) that
# must never be committed.
#
#   # from an ELEVATED PowerShell, in the repo root:
#   .\scripts\setup-baseline-task.ps1 -KeyFile 'C:\path\to\your\deepinfra-key.txt' -SecUserAgent 'Your Name you@example.com'
#
# Secret hygiene: the API key VALUE is never passed here, never stored in the task, and never logged - only the
# PATH to the key file (which run-baseline-scheduled.ps1 reads at run time into $env:DEEPINFRA_API_KEY for the
# duration of that process). Keep the key file outside the repo and ACL'd to your account.
#
# Use -WhatIf to print what would be registered without touching the scheduler.
#
# SPLITTING THE SCHEDULE (spec 144) - MAINTAINER ACTION, NOT DONE BY THIS SLICE.
# -Mode defaults to 'full', so re-running this script with the arguments you already use re-registers exactly
# today's combined RadarBaselineDaily and nothing changes. Splitting collection from scoring is an explicit,
# elevated, opt-in step: register two tasks with distinct -TaskName values and delete/disable the combined one.
#
#   # from an ELEVATED PowerShell, in the repo root:
#   .\scripts\setup-baseline-task.ps1 -TaskName RadarCollectDaily -Mode collect -At 09:00 `
#       -KeyFile 'C:\path\to\your\deepinfra-key.txt' -SecUserAgent 'Your Name you@example.com'
#   .\scripts\setup-baseline-task.ps1 -TaskName RadarScoreDaily   -Mode score   -At 09:30 `
#       -KeyFile 'C:\path\to\your\deepinfra-key.txt' -SecUserAgent 'Your Name you@example.com'
#   Unregister-ScheduledTask -TaskName RadarBaselineDaily -Confirm:$false   # only once the two above are proven
#
# Notes for that split:
#   * schedule the score task AFTER the collect task has had time to finish - they are independent processes and
#     nothing sequences them for you. A score pass that runs early simply scores slightly less recent evidence;
#     it never fails for that reason.
#   * a score task STILL needs -KeyFile and -SecUserAgent: the AI descriptor is a ScoringConfigVersion input, so
#     the AI seam is registered (never invoked) and the earnings reader it wires demands the SEC User-Agent.
#     Dropping either would re-stamp every strategy's fingerprint.
#   * a score task runs NO collector, so it costs no SEC / GDELT / Google News traffic and no AI spend - that
#     is what makes repeating it cheap. It is NOT request-free, though: Radar:Prices:Enabled is independent of
#     Radar:RunMode and the default profile sets it true, so each score pass also fetches daily price history
#     per ticker (AD-14 reference data, acquired outside the pipeline). Before registering a score task that
#     repeats often, point it at a profile overlay with "Prices": { "Enabled": false } (via -Profile) and leave
#     price acquisition on the collect task.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$KeyFile,                                   # Path to the API-key file (read at RUN time, not now).
    [Parameter(Mandatory = $true)]
    [string]$SecUserAgent,                              # SEC EDGAR contact, "Name email".
    [string]$TaskName      = "RadarBaselineDaily",
    # 22:30 UK, not 09:00 (spec 171 section 4). US market close is 21:00 UK year-round, and earnings 8-Ks
    # land from then; a 09:00 run saw them ~11h late (measured: UFPT's 2026-08-03 21:12 UTC results 8-K was
    # scored the following morning, a day after the market had repriced). 22:30 is >=1.5h after close AND is
    # DST-safe for the post-collection as-of instant: BST 21:30 UTC / GMT 22:30 UTC, both comfortably inside
    # the same UTC day. Do NOT use 23:30 - in GMT that is 23:30 UTC and collection pushes the as-of past
    # midnight, which doubles or gaps an as-of date in the efficacy series. Do NOT use 22:00 - it lands
    # inside the after-close filing wave, so capture depends on intra-run collector ordering.
    # When changing this, fire the first run on a date that has NOT already had one: exactly one run per UTC date.
    [string]$At            = "22:30",
    [string]$Profile       = "default",
    [ValidateSet("full", "collect", "score")]
    [string]$Mode          = "full",                    # Which pass the task runs (spec 144). Default leaves RadarBaselineDaily exactly as it is.
    [string]$KeyEnvVar     = "DEEPINFRA_API_KEY",
    [string]$RepoPath      = "",
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($RepoPath)) { $RepoPath = Split-Path -Parent $scriptDir }

$wrapper = Join-Path $scriptDir "run-baseline-scheduled.ps1"
if (-not (Test-Path -LiteralPath $wrapper)) { throw "Wrapper not found: $wrapper" }
if (-not (Test-Path -LiteralPath $KeyFile)) {
    throw "Key file not found: '$KeyFile'. Point -KeyFile at the file holding the API key (its contents are never read by this script)."
}

$argumentString = @(
    '-NoProfile'
    '-ExecutionPolicy', 'Bypass'
    '-File', ('"{0}"' -f $wrapper)
    '-KeyFile', ('"{0}"' -f $KeyFile)
    '-KeyEnvVar', $KeyEnvVar
    '-Profile', $Profile
    '-Mode', $Mode
    '-SecUserAgent', ('"{0}"' -f $SecUserAgent)
) -join ' '

Write-Host "==== $TaskName ====" -ForegroundColor Cyan
Write-Host "Action    : powershell.exe $argumentString"
Write-Host "Working in: $RepoPath"
Write-Host "Trigger   : daily at $At"
Write-Host "Mode      : $Mode$(if ($Mode -eq 'full') { '  (the combined collect+score run - unchanged)' })"
Write-Host "Note      : the API key VALUE is not stored in the task - only the key-file PATH; the wrapper loads it at run time."

if ($WhatIf) { Write-Host "`n(-WhatIf: the scheduled task was NOT registered)" -ForegroundColor Yellow; return }

$action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argumentString -WorkingDirectory $RepoPath
$trigger   = New-ScheduledTaskTrigger -Daily -At $At
$settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType S4U -RunLevel Limited

# -Force re-points an existing task at the current wrapper/arguments instead of failing.
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null

Write-Host "Registered '$TaskName' (daily $At, mode $Mode)." -ForegroundColor Green
