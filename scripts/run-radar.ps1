# run-radar.ps1 - Run the Radar Worker for a live measurement from a named JSON profile.
#
# Purpose: capture HOW we run (the 'default' profile = the canonical baseline) and let us switch
# scoring/collector config easily for experiments WITHOUT losing the baseline. Every profile under
# scripts/run-profiles/ is loaded ON TOP of default.json (a shallow overlay by flattened config key),
# so an experiment profile only carries its delta (e.g. low-media.json overrides MediaReachWeight).
#
# The Worker takes config from command-line args (which override appsettings), so this script flattens
# the merged JSON into --Radar:A:B:C=value args. Machine-specific bits are supplied here, not committed:
#   - the output directories (default -> <repo>\data ; a named profile -> <repo>\data\experiments\<profile>)
#   - the SEC User-Agent (a real contact email), via -SecUserAgent.
#
# SECRETS: the baseline earnings read (spec 119) is DeepSeek-V4-Flash on DeepInfra via the OpenAI-compatible
# provider, so the baseline REQUIRES the API key in the environment variable named by the profile's
# Radar:Ai:OpenAi:ApiKeyEnvVar (DEEPINFRA_API_KEY). The key VALUE is never committed, never printed and never
# logged - only the variable NAME. Set it for the session before running, e.g.
#   $env:DEEPINFRA_API_KEY = (Get-Content <your-key-file> -Raw).Trim()
# (or use scripts/run-baseline-scheduled.ps1 -KeyFile <path>, which does exactly that for an unattended run).
# A missing key warns here and then fails the Worker fast - it never silently degrades to no earnings read.
# To run without a hosted model, overlay a profile that points Radar:Ai back at local Ollama.
#
# REPLAY (spec 139): -Replay turns the run into a read-only, OFFLINE historical as-of replay instead of a
# pipeline run. It re-scores the ALREADY-STORED signals at each as-of instant in -ReplayFrom..-ReplayTo (step
# -ReplayStep) through the same scoring seam the live pipeline uses, and writes ONLY under <outRoot>\replays\.
# It collects nothing, mutates no live store, reads no price, and produces no report - the forward efficacy
# series under <outRoot>\scores\ is untouched. Without -Replay the baseline behaviour is unchanged.
#
# Examples:
#   powershell -File scripts/run-radar.ps1                       # baseline run -> data\
#   powershell -File scripts/run-radar.ps1 -Profile low-media    # experiment  -> data\experiments\low-media\
#   powershell -File scripts/run-radar.ps1 -Profile low-media -WhatIf   # print the resolved command, do not run
#   powershell -File scripts/run-radar.ps1 -Replay -ReplayFrom 2026-05-01 -ReplayTo 2026-07-25 -ReplayStep 1d

[CmdletBinding()]
param(
    [string]$Profile       = "default",
    [string]$RepoPath      = "",
    [string]$SecUserAgent  = $(if ($env:RADAR_SEC_UA) { $env:RADAR_SEC_UA } else { "Radar Research your-contact-email@example.com" }),  # SEC EDGAR needs a real name+email: pass -SecUserAgent or set $env:RADAR_SEC_UA. NOT committed (public repo).
    [switch]$Replay,                    # run a read-only historical as-of replay INSTEAD of the pipeline
    [string]$ReplayFrom    = "",        # first as-of instant, UTC (e.g. 2026-05-01); required with -Replay
    [string]$ReplayTo      = "",        # upper bound of the as-of series, UTC; required with -Replay
    [string]$ReplayStep    = "1d",      # spacing: 1d / 12h / 30m, or a plain TimeSpan string
    [string]$ReplayLabel   = "",        # output directory segment; blank = derived from the series (idempotent)
    [switch]$SkipBuild,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
# Resolve the script's own directory robustly ($PSScriptRoot can be empty at param-binding under `powershell -File`).
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($RepoPath)) { $RepoPath = Split-Path -Parent $scriptDir }
$profilesDir = Join-Path $scriptDir "run-profiles"
$inv = [System.Globalization.CultureInfo]::InvariantCulture

function Read-Profile([string]$name) {
    $path = Join-Path $profilesDir "$name.json"
    if (-not (Test-Path $path)) {
        $available = (Get-ChildItem $profilesDir -Filter *.json | ForEach-Object { $_.BaseName }) -join ', '
        throw "Profile '$name' not found at $path. Available: $available"
    }
    return (Get-Content $path -Raw | ConvertFrom-Json)
}

# Flatten a ConvertFrom-Json node into $acc (ordered dict) as "Radar:A:B" -> value. Skips _comment keys.
function Add-Flattened($node, [string]$prefix, [System.Collections.Specialized.OrderedDictionary]$acc) {
    if ($node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($p in $node.PSObject.Properties) {
            if ($p.Name -eq '_comment') { continue }
            $key = if ($prefix) { "$($prefix):$($p.Name)" } else { $p.Name }
            Add-Flattened $p.Value $key $acc
        }
    }
    elseif (($node -is [System.Collections.IEnumerable]) -and ($node -isnot [string])) {
        $i = 0
        foreach ($item in $node) { Add-Flattened $item "$($prefix):$i" $acc; $i++ }
    }
    else {
        if ($node -is [double] -or $node -is [single]) { $acc[$prefix] = $node.ToString($inv) }
        elseif ($node -is [bool])                       { $acc[$prefix] = $node.ToString().ToLowerInvariant() }
        else                                            { $acc[$prefix] = [string]$node }
    }
}

# --- merge default + (optional) profile overlay ---
$merged = [ordered]@{}
Add-Flattened (Read-Profile "default") "" $merged
if ($Profile -ne "default") {
    $overlay = [ordered]@{}
    Add-Flattened (Read-Profile $Profile) "" $overlay
    foreach ($k in $overlay.Keys) { $merged[$k] = $overlay[$k] }   # profile wins
}

# --- output directories: baseline -> data\ ; experiment -> data\experiments\<profile>\ (keeps baseline intact) ---
$outRoot = if ($Profile -eq "default") { Join-Path $RepoPath "data" } else { Join-Path $RepoPath "data\experiments\$Profile" }
$dirArgs = [ordered]@{
    "Radar:CompanySeedFilePath"      = (Join-Path $RepoPath "data\companies.json")   # shared read-only seed
    "Radar:EvidenceSourceDirectory"  = (Join-Path $outRoot  "evidence")
    "Radar:EvidenceRawDirectory"     = (Join-Path $outRoot  "evidence\raw")
    "Radar:SignalsDirectory"         = (Join-Path $outRoot  "signals")
    "Radar:ScoresDirectory"          = (Join-Path $outRoot  "scores")
    "Radar:ReportDirectory"          = (Join-Path $outRoot  "reports")
    "Radar:RunsDirectory"            = (Join-Path $outRoot  "runs")
    "Radar:ScoringConfigsDirectory"  = (Join-Path $outRoot  "scoring-configs")
    "Radar:PricesDirectory"          = (Join-Path $outRoot  "prices")   # AD-14 price-history reference store (only written when Radar:Prices:Enabled)
    "Radar:EfficacyDirectory"        = (Join-Path $outRoot  "efficacy") # AD-14 read side: per-company score-vs-price SVG/CSV (only written when Radar:Efficacy:Enabled)
    "Radar:ReplayDirectory"          = (Join-Path $outRoot  "replays")  # spec 139 historical as-of replay output; its OWN root, never under scores\ (only written when Radar:Replay:Enabled)
    "Radar:AnalyzedFilingCacheDirectory" = (Join-Path $outRoot "filings-cache")   # spec 107 per-accession earnings analysis-result cache (AD-14 analogue)
    "Radar:FilingReadDebugDirectory" = (Join-Path $outRoot "ai-debug\filings")   # spec 115 opt-in AI filing-read debug records (only written when Radar:Ai:Filings:PersistReadDebug)
    "Radar:Sec:UserAgent"            = $SecUserAgent
}
foreach ($k in $dirArgs.Keys) { $merged[$k] = $dirArgs[$k] }

# --- optional: historical as-of replay (spec 139) instead of a pipeline run ---
# Fail here rather than at the Worker so the missing bound is obvious before a build happens. From/To are
# passed through verbatim; the Worker parses them as UTC and fails fast on anything unparseable.
if ($Replay) {
    if ([string]::IsNullOrWhiteSpace($ReplayFrom) -or [string]::IsNullOrWhiteSpace($ReplayTo)) {
        throw "-Replay requires both -ReplayFrom and -ReplayTo (UTC dates, e.g. -ReplayFrom 2026-05-01 -ReplayTo 2026-07-25). A replay range is never inferred."
    }
    $merged["Radar:Replay:Enabled"] = "true"
    $merged["Radar:Replay:From"]    = $ReplayFrom
    $merged["Radar:Replay:To"]      = $ReplayTo
    $merged["Radar:Replay:Step"]    = $ReplayStep
    if (-not [string]::IsNullOrWhiteSpace($ReplayLabel)) { $merged["Radar:Replay:Label"] = $ReplayLabel }
}

# --- build the arg array ---
$cliArgs = @()
foreach ($k in $merged.Keys) { $cliArgs += "--$k=$($merged[$k])" }

Write-Host "==== Radar run profile: $Profile ====" -ForegroundColor Cyan
Write-Host "Output root: $outRoot"
if ($Replay) {
    Write-Host "REPLAY MODE: as-of $ReplayFrom .. $ReplayTo step $ReplayStep -> $(Join-Path $outRoot 'replays') (read-only; the pipeline, price, report and efficacy steps do NOT run)" -ForegroundColor Yellow
}
if ($SecUserAgent -like "*example.com*") {
    Write-Warning "SEC User-Agent is the placeholder ('$SecUserAgent') - SEC EDGAR will HTTP 403. Pass -SecUserAgent 'Name email' or set `$env:RADAR_SEC_UA."
}
# Same secret precedent as the SEC User-Agent (spec 119): the OpenAI-compatible earnings-read key is NEVER in a
# profile - config only NAMES the env var holding it. Warn loudly BEFORE the run when that variable is unset, so a
# keyless baseline is obvious here rather than only as a Worker fail-fast. Only the NAME is ever printed.
if ($merged["Radar:Ai:Provider"] -eq 'openai') {
    $keyEnvVar = $merged["Radar:Ai:OpenAi:ApiKeyEnvVar"]
    if ([string]::IsNullOrWhiteSpace($keyEnvVar)) {
        Write-Warning "Radar:Ai:Provider is 'openai' but Radar:Ai:OpenAi:ApiKeyEnvVar is not set in the profile - the Worker will fail fast. It must NAME the environment variable holding the API key (e.g. DEEPINFRA_API_KEY); never put the key value in a profile."
    }
    elseif ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($keyEnvVar))) {
        Write-Warning "Environment variable '$keyEnvVar' (named by Radar:Ai:OpenAi:ApiKeyEnvVar) is not set - the AI earnings read will fail fast. Set it for this session, e.g. `$env:$keyEnvVar = (Get-Content <key-file> -Raw).Trim()  # never print or commit the value."
    }
}
Write-Host "Resolved --Radar args:" -ForegroundColor Cyan
$cliArgs | ForEach-Object { Write-Host "  $_" }

if ($WhatIf) { Write-Host "`n(-WhatIf: not running)" -ForegroundColor Yellow; return }

$proj = Join-Path $RepoPath "src\Radar.Worker"
if (-not $SkipBuild) {
    Write-Host "`n==== dotnet build ====" -ForegroundColor Cyan
    & dotnet build (Join-Path $RepoPath "Radar.sln") -c Release
    if ($LASTEXITCODE) { throw "build failed (exit $LASTEXITCODE)" }
}
Write-Host "`n==== dotnet run (profile: $Profile) ====" -ForegroundColor Cyan
& dotnet run --project $proj -c Release --no-launch-profile --no-build -- @cliArgs
exit $LASTEXITCODE
