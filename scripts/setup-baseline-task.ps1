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

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$KeyFile,                                   # Path to the API-key file (read at RUN time, not now).
    [Parameter(Mandatory = $true)]
    [string]$SecUserAgent,                              # SEC EDGAR contact, "Name email".
    [string]$TaskName      = "RadarBaselineDaily",
    [string]$At            = "09:00",
    [string]$Profile       = "default",
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
    '-SecUserAgent', ('"{0}"' -f $SecUserAgent)
) -join ' '

Write-Host "==== $TaskName ====" -ForegroundColor Cyan
Write-Host "Action    : powershell.exe $argumentString"
Write-Host "Working in: $RepoPath"
Write-Host "Trigger   : daily at $At"
Write-Host "Note      : the API key VALUE is not stored in the task - only the key-file PATH; the wrapper loads it at run time."

if ($WhatIf) { Write-Host "`n(-WhatIf: the scheduled task was NOT registered)" -ForegroundColor Yellow; return }

$action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argumentString -WorkingDirectory $RepoPath
$trigger   = New-ScheduledTaskTrigger -Daily -At $At
$settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType S4U -RunLevel Limited

# -Force re-points an existing task at the current wrapper/arguments instead of failing.
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null

Write-Host "Registered '$TaskName' (daily $At)." -ForegroundColor Green
