<#
.SYNOPSIS
    Spec 158 - INPUT-ONLY channel feasibility characterization (READ-ONLY launcher).

.DESCRIPTION
    Launches the C# audit entry point (src/Radar.ChannelFeasibilityAudit), which composes the PRODUCTION
    durable stores and scoring primitives and reports, at the PINNED as-of instant
    2026-07-28T08:04:27.7605621Z (run record 120c99e2-2b8d-4831-99aa-1f02a0d58896):

      - the scoring-input eligibility funnel (approved/known-at, evidence-unresolvable, resolved,
        supersede, media collapse, recorded/inferred/unattributed attribution);
      - per candidate collector channel, the v11 structural inputs (directional activity mass,
        preponderance, sign distribution) over the audited companies;
      - the spec 157 par.3 positive-only breadth answer;
      - the predeclared filings-led-v11 budget's in-memory integer OpportunityScore distribution, plus
        recommendation candidates through the same pass.

    THIS SCRIPT CONTAINS NO SCORING MATH WHATSOEVER (spec 158 par.4): every number is computed by the C#
    audit through the production ScoreSignalMath / ScoringChannelComposition path; this launcher only
    resolves paths, enforces the read-only output guard, invokes the audit, and relays its report.

    STRICTLY READ-ONLY over the data root: the audit only ever reads files, and this script refuses an
    -OutFile that would land inside -DataRoot (spec 156 precedent).

.PARAMETER DataRoot
    The durable store root (holds signals/, evidence/raw/ and companies.json).
    Default: 'data' beside the scripts folder.

.PARAMETER OutFile
    Optional path to also write the rendered report text to. Must NOT be inside -DataRoot.

.PARAMETER RecordedOnly
    Disable the spec-151 legacy collector-attribution inference (the audit's default is inference ON,
    reporting recorded / inferred / unattributed separately).

.PARAMETER Configuration
    Build configuration for the audit project. Default: Release.
#>
[CmdletBinding()]
param(
    [string]$DataRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'data'),
    [string]$OutFile,
    [switch]$RecordedOnly,
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# --- Resolve + guard paths (read-only over the store; spec 156 precedent) ------------------------------

$resolvedRoot = (Resolve-Path -LiteralPath $DataRoot).ProviderPath.TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath (Join-Path $resolvedRoot 'signals'))) {
    throw "No signals directory under '$resolvedRoot'."
}
if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $resolvedRoot 'evidence') 'raw'))) {
    throw "No raw evidence directory under '$resolvedRoot'."
}
if (-not (Test-Path -LiteralPath (Join-Path $resolvedRoot 'companies.json'))) {
    throw "No companies.json under '$resolvedRoot'."
}

$outFull = $null
if ($OutFile) {
    $outCandidate = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path (Get-Location).ProviderPath $OutFile }
    $outFull = [System.IO.Path]::GetFullPath($outCandidate)
    if ($outFull.StartsWith($resolvedRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
        $outFull.Equals($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "-OutFile '$outFull' is inside -DataRoot '$resolvedRoot'; the audit never writes inside the store."
    }
}

# --- Build + run the C# audit (all math lives there) ---------------------------------------------------

$repoRoot = Split-Path -Parent $PSScriptRoot
$auditProject = Join-Path $repoRoot 'src\Radar.ChannelFeasibilityAudit\Radar.ChannelFeasibilityAudit.csproj'

& dotnet build $auditProject -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Building the audit project failed (exit $LASTEXITCODE)." }

$auditArgs = @('--data-root', $resolvedRoot)
if ($RecordedOnly) { $auditArgs += '--recorded-only' }

# stdout carries exactly the report (logs go to stderr); capture it line-wise for relay + optional file.
$report = & dotnet run --project $auditProject -c $Configuration --no-build -- @auditArgs
if ($LASTEXITCODE -ne 0) { throw "The audit exited with code $LASTEXITCODE." }

$reportText = ($report -join [System.Environment]::NewLine)
Write-Output $reportText

if ($outFull) {
    [System.IO.File]::WriteAllText($outFull, $reportText + [System.Environment]::NewLine)
    Write-Verbose "Report written to $outFull"
}
