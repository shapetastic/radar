<#
.SYNOPSIS
    Spec 212 - read-only audit of each scoring arm's accrued OpportunityScore distribution against its
    Investigate / Watch label lines.

.DESCRIPTION
    Walks every persisted score snapshot under {DataRoot}/scores (the storage primary, grouped by the
    snapshot's persisted strategyName - legacy unnamed snapshots are reported as their own series, never
    folded into 'default') and {DataRoot}/scores/strategies/<arm>/**, and prints per arm:

        n / dates / max / p99 / p95 / p90 / p50 / share >= each configured line

    plus fixed reference columns at >= 60 and >= 40 (the pre-212 lines) so the table stays comparable across
    profile edits. The configured lines come from a run profile's Radar.Strategies[i].Labels
    (scripts/run-profiles/<Profile>.json, default 'default'); an arm with no Labels there (or absent from the
    profile) is measured against 60 / 40 and marked as such.

    -Strategy X -MatchPrevalenceOf Y prints the PREVALENCE MATCH: X's Opportunity value at the share of
    snapshots Y puts at or above Y's Watch line (and Investigate line) - i.e. the k-th largest value of X,
    k = round(share_Y x n_X). A line's operational meaning is "the share of snapshots it puts in front of a
    reader"; this is how the spec-212 lines were chosen, and it is a WORKLOAD number, not evidence.

    Percentiles use the nearest-rank method over the ascending sorted values (p = ceil(P/100 x n)-th value).
    Every snapshot file that cannot be read, or lacks opportunityScore / createdAtUtc, is COUNTED and
    reported, never silently skipped.

    STRICTLY READ-ONLY over the data root: the script only ever reads files, and refuses an -OutFile that
    would land inside -DataRoot. Output ordering is deterministic (arm name, ordinal). PowerShell 5.1-
    compatible: the fields are extracted with anchored regular expressions rather than a JSON parser
    (37k+ snapshot files each carry a large componentJson payload).

.PARAMETER DataRoot
    The durable store root (holds scores/). Default: 'data' beside the scripts folder.

.PARAMETER Profile
    The run profile whose Radar.Strategies[i].Labels supply each arm's configured lines. Default: 'default'.

.PARAMETER Strategy
    With -MatchPrevalenceOf: the arm whose distribution is quantised to the reference arm's prevalence.

.PARAMETER MatchPrevalenceOf
    With -Strategy: the reference arm (e.g. 'default') whose share >= its lines is matched.

.PARAMETER FromDate
    Optional inclusive lower bound (yyyy-MM-dd, on createdAtUtc) so a measurement can be scoped to one
    era (e.g. 2026-07-29, the multi-strategy era the spec-212 table was measured over). Snapshots before
    it are COUNTED per arm as 'excluded', never silently dropped. Default: none (the whole store).

.PARAMETER OutFile
    Optional path to also write the rendered report text to. Must NOT be inside -DataRoot.
#>
[CmdletBinding()]
param(
    [string]$DataRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'data'),
    [string]$Profile = 'default',
    [string]$Strategy,
    [string]$MatchPrevalenceOf,
    [string]$FromDate,
    [string]$OutFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if (($Strategy -and -not $MatchPrevalenceOf) -or ($MatchPrevalenceOf -and -not $Strategy)) {
    throw '-Strategy and -MatchPrevalenceOf must be given together.'
}

# --- Resolve + guard paths (read-only over the store) -------------------------------------------------

$resolvedRoot = (Resolve-Path -LiteralPath $DataRoot).ProviderPath.TrimEnd('\', '/')
$scoresRoot = Join-Path $resolvedRoot 'scores'
$strategiesRoot = Join-Path $scoresRoot 'strategies'
if (-not (Test-Path -LiteralPath $scoresRoot)) { throw "No scores directory at '$scoresRoot'." }

if ($OutFile) {
    $outCandidate = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path (Get-Location).ProviderPath $OutFile }
    $outFull = [System.IO.Path]::GetFullPath($outCandidate)
    if ($outFull.StartsWith($resolvedRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
        $outFull.Equals($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "-OutFile '$outFull' is inside -DataRoot '$resolvedRoot'; the audit never writes inside the store."
    }
}

# --- Configured lines from the run profile ------------------------------------------------------------

$profilePath = Join-Path (Join-Path $PSScriptRoot 'run-profiles') ($Profile + '.json')
if (-not (Test-Path -LiteralPath $profilePath)) { throw "No run profile at '$profilePath'." }

$script:IsCoreEdition = $PSVersionTable.PSEdition -eq 'Core'
if (-not $script:IsCoreEdition) {
    Add-Type -AssemblyName 'System.Web.Extensions'
    $script:JsonSerializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $script:JsonSerializer.MaxJsonLength = [int]::MaxValue
}

function Read-JsonDictionary {
    param([string]$Path)
    $text = [System.IO.File]::ReadAllText($Path)
    if ($script:IsCoreEdition) {
        return ConvertFrom-Json -InputObject $text -AsHashtable
    }
    return $script:JsonSerializer.DeserializeObject($text)
}

function Get-DictValue {
    param($Dict, [string]$Key)
    if ($null -eq $Dict) { return $null }
    if ($Dict.ContainsKey($Key)) { return $Dict[$Key] }
    return $null
}

# arm -> @{ Investigate; Watch } from the profile; arms without Labels are absent here (=> 60/40 reference).
$configuredLines = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::OrdinalIgnoreCase)
$profileArms = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$profileJson = Read-JsonDictionary -Path $profilePath
$radar = Get-DictValue $profileJson 'Radar'
$strategies = Get-DictValue $radar 'Strategies'
if ($null -ne $strategies) {
    foreach ($entry in $strategies) {
        $name = [string](Get-DictValue $entry 'Name')
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        [void]$profileArms.Add($name)
        $labels = Get-DictValue $entry 'Labels'
        if ($null -ne $labels) {
            $configuredLines[$name] = @{
                Investigate = [int](Get-DictValue $labels 'Investigate')
                Watch       = [int](Get-DictValue $labels 'Watch')
            }
        }
    }
}

$defaultInvestigate = 60
$defaultWatch = 40

function Get-Lines {
    param([string]$Arm)
    if ($configuredLines.ContainsKey($Arm)) {
        return @{ Investigate = $configuredLines[$Arm].Investigate; Watch = $configuredLines[$Arm].Watch; Source = 'profile' }
    }
    return @{ Investigate = $defaultInvestigate; Watch = $defaultWatch; Source = 'none set (60/40 reference)' }
}

# --- Scan every snapshot ------------------------------------------------------------------------------

# Anchored field extraction: the persisted snapshot shape is one scalar per line at the top level.
$opportunityRegex = [regex]'"opportunityScore":\s*(-?\d+)'
$createdRegex = [regex]'"createdAtUtc":\s*"(\d{4}-\d{2}-\d{2})'
$strategyNameRegex = [regex]'"strategyName":\s*"([^"]*)"'
$scoringVersionRegex = [regex]'"scoringVersion":\s*"([^"]*)"'

# arm -> @{ Values = List[int]; Dates = HashSet[string]; Formulas = HashSet[string]; Unreadable = int; Files = int }
$arms = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)

function Get-Arm {
    param([string]$Name)
    if (-not $arms.ContainsKey($Name)) {
        $arms[$Name] = @{
            Values    = New-Object 'System.Collections.Generic.List[int]'
            Dates     = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
            Formulas  = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
            Unreadable = 0
            Excluded  = 0
            Files     = 0
        }
    }
    return $arms[$Name]
}

function Add-Snapshot {
    param([string]$File, [string]$ArmOverride)
    $text = [System.IO.File]::ReadAllText($File)
    $armName = $ArmOverride
    if (-not $armName) {
        $m = $strategyNameRegex.Match($text)
        $armName = if ($m.Success -and $m.Groups[1].Value.Length -gt 0) { $m.Groups[1].Value } else { '(primary, legacy unnamed)' }
    }
    $arm = Get-Arm $armName
    $arm.Files++
    $om = $opportunityRegex.Match($text)
    $cm = $createdRegex.Match($text)
    if (-not $om.Success -or -not $cm.Success) {
        $arm.Unreadable++
        return
    }
    if ($FromDate -and [string]::CompareOrdinal($cm.Groups[1].Value, $FromDate) -lt 0) {
        $arm.Excluded++
        return
    }
    $arm.Values.Add([int]$om.Groups[1].Value)
    [void]$arm.Dates.Add($cm.Groups[1].Value)
    $sm = $scoringVersionRegex.Match($text)
    if ($sm.Success) {
        # The persisted token is 'mvp-engine-v1+radar-formula-vN@revK'; the JSON writer escapes the '+'
        # as the six-character sequence backslash-u-0-0-2-B, which the regex below matches literally.
        $sv = $sm.Groups[1].Value -replace '\\u002[Bb]', '+'
        $plus = $sv.IndexOf('+')
        $at = $sv.IndexOf('@')
        $formula = if ($plus -ge 0) { if ($at -gt $plus) { $sv.Substring($plus + 1, $at - $plus - 1) } else { $sv.Substring($plus + 1) } } else { $sv }
        [void]$arm.Formulas.Add($formula)
    }
}

$scanned = 0
# Primary: {scores}/{companyId}/*.json - every directory except 'strategies'.
foreach ($companyDir in [System.IO.Directory]::EnumerateDirectories($scoresRoot)) {
    if ([System.IO.Path]::GetFileName($companyDir) -eq 'strategies') { continue }
    foreach ($file in [System.IO.Directory]::EnumerateFiles($companyDir, '*.json', [System.IO.SearchOption]::TopDirectoryOnly)) {
        $scanned++
        if (($scanned % 2000) -eq 0) { Write-Progress -Activity 'Scanning score snapshots' -Status "$scanned files" }
        try { Add-Snapshot -File $file } catch { (Get-Arm '(primary, unreadable)').Unreadable++ }
    }
}
# Strategy arms: {scores}/strategies/{arm}/{companyId}/*.json
if (Test-Path -LiteralPath $strategiesRoot) {
    foreach ($armDir in [System.IO.Directory]::EnumerateDirectories($strategiesRoot)) {
        $armName = [System.IO.Path]::GetFileName($armDir)
        [void](Get-Arm $armName)
        foreach ($file in [System.IO.Directory]::EnumerateFiles($armDir, '*.json', [System.IO.SearchOption]::AllDirectories)) {
            $scanned++
            if (($scanned % 2000) -eq 0) { Write-Progress -Activity 'Scanning score snapshots' -Status "$scanned files" }
            try { Add-Snapshot -File $file -ArmOverride $armName } catch { (Get-Arm $armName).Unreadable++ }
        }
    }
}
Write-Progress -Activity 'Scanning score snapshots' -Completed

# --- Statistics ---------------------------------------------------------------------------------------

function Get-NearestRank {
    param([int[]]$Sorted, [double]$Percent)
    if ($Sorted.Length -eq 0) { return $null }
    $k = [int][math]::Ceiling($Percent / 100.0 * $Sorted.Length)
    if ($k -lt 1) { $k = 1 }
    return $Sorted[$k - 1]
}

function Get-CountAtOrAbove {
    param([int[]]$Values, [int]$Line)
    $c = 0
    foreach ($v in $Values) { if ($v -ge $Line) { $c++ } }
    return $c
}

function Format-Pct {
    param([int]$Numerator, [int]$Denominator)
    if ($Denominator -eq 0) { return 'n/a' }
    return ('{0:P1}' -f ($Numerator / [double]$Denominator))
}

function Format-Value {
    param($Value)
    if ($null -eq $Value) { return '-' }
    return [string]$Value
}

$lines = New-Object 'System.Collections.Generic.List[string]'
function Out-Line { param([string]$Text = '') $lines.Add($Text) | Out-Null }

$scanInstant = [System.DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')

Out-Line '================================================================================'
Out-Line 'Spec 212 - label-threshold audit over accrued score snapshots (READ-ONLY)'
Out-Line '================================================================================'
Out-Line "Scanned at (UTC):        $scanInstant"
Out-Line "Data root:               $resolvedRoot"
Out-Line "Profile (lines):         $profilePath"
Out-Line "Snapshot files scanned:  $($scanned.ToString('N0'))"
Out-Line ("From date (createdAtUtc): {0}" -f $(if ($FromDate) { "$FromDate (earlier snapshots counted as excluded)" } else { '(none - whole store)' }))
Out-Line ''
Out-Line 'Lines: "I/W (source)" = the Investigate/Watch lines the share columns use - the profile''s'
Out-Line 'Radar.Strategies[i].Labels, or 60/40 when the arm sets none. ">=60 / >=40" are FIXED reference'
Out-Line 'columns (the pre-212 lines) so arms and profile edits stay comparable. Percentiles: nearest rank.'
Out-Line 'dates = distinct createdAtUtc days. unreadable = files lacking opportunityScore/createdAtUtc (counted).'
Out-Line ''

$header = '{0,-28} {1,-30} {2,6} {3,5} {4,4} {5,4} {6,4} {7,4} {8,4}  {9,-18} {10,6} {11,8} {12,6} {13,8} {14,6} {15,6} {16,8} {17,10}' -f `
    'arm', 'formula(s)', 'n', 'dates', 'max', 'p99', 'p95', 'p90', 'p50', 'lines I/W (source)', 'n>=I', 'share>=I', 'n>=W', 'share>=W', 'n>=60', 'n>=40', 'excluded', 'unreadable'
Out-Line $header
Out-Line ('-' * $header.Length)

$armNames = @($arms.Keys | Sort-Object -Property @{Expression = { $_ }; Descending = $false })
foreach ($name in $armNames) {
    $arm = $arms[$name]
    $values = [int[]]$arm.Values.ToArray()
    [System.Array]::Sort($values)
    $n = $values.Length
    $cfg = Get-Lines $name
    $nI = Get-CountAtOrAbove $values $cfg.Investigate
    $nW = Get-CountAtOrAbove $values $cfg.Watch
    $n60 = Get-CountAtOrAbove $values 60
    $n40 = Get-CountAtOrAbove $values 40
    $max = if ($n -gt 0) { $values[$n - 1] } else { $null }
    $formulas = if ($arm.Formulas.Count -gt 0) { (@($arm.Formulas | Sort-Object) -join '/') } else { '(unknown)' }
    $linesText = '{0}/{1} ({2})' -f $cfg.Investigate, $cfg.Watch, $(if ($cfg.Source -eq 'profile') { 'profile' } else { 'none set' })
    Out-Line ('{0,-28} {1,-30} {2,6} {3,5} {4,4} {5,4} {6,4} {7,4} {8,4}  {9,-18} {10,6} {11,8} {12,6} {13,8} {14,6} {15,6} {16,8} {17,10}' -f `
        $name, $formulas, $n, $arm.Dates.Count, (Format-Value $max), (Format-Value (Get-NearestRank $values 99)), `
        (Format-Value (Get-NearestRank $values 95)), (Format-Value (Get-NearestRank $values 90)), (Format-Value (Get-NearestRank $values 50)), `
        $linesText, $nI, (Format-Pct $nI $n), $nW, (Format-Pct $nW $n), $n60, $n40, $arm.Excluded, $arm.Unreadable)
}
Out-Line ''

$notInProfile = @($armNames | Where-Object { -not $profileArms.Contains($_) -and -not $_.StartsWith('(') })
if ($notInProfile.Count -gt 0) {
    Out-Line ("Arms accrued on disk but absent from the '{0}' profile (measured at 60/40 reference): {1}" -f $Profile, ($notInProfile -join ', '))
    Out-Line ''
}

# --- Prevalence match ---------------------------------------------------------------------------------

if ($Strategy) {
    Out-Line '--------------------------------------------------------------------------------'
    Out-Line ("PREVALENCE MATCH - '{0}' quantised to '{1}''s share at its lines" -f $Strategy, $MatchPrevalenceOf)
    Out-Line '--------------------------------------------------------------------------------'
    if (-not $arms.ContainsKey($Strategy)) { throw "No accrued snapshots for strategy '$Strategy'." }
    if (-not $arms.ContainsKey($MatchPrevalenceOf)) { throw "No accrued snapshots for reference strategy '$MatchPrevalenceOf'." }

    $target = [int[]]$arms[$Strategy].Values.ToArray()
    [System.Array]::Sort($target)
    [System.Array]::Reverse($target)   # descending: index k-1 is the k-th largest
    $reference = [int[]]$arms[$MatchPrevalenceOf].Values.ToArray()
    $refLines = Get-Lines $MatchPrevalenceOf
    $targetLines = Get-Lines $Strategy
    $nT = $target.Length
    $nR = $reference.Length

    Out-Line ("  '{0}' currently runs at Investigate {1} / Watch {2} ({3}):" -f $Strategy, $targetLines.Investigate, $targetLines.Watch, $targetLines.Source)
    $atI = Get-CountAtOrAbove $target $targetLines.Investigate
    $atW = Get-CountAtOrAbove $target $targetLines.Watch
    Out-Line ("    share >= {0} (Investigate): {1:N0} of {2:N0} ({3});  share >= {4} (Watch): {5:N0} of {2:N0} ({6})" -f `
        $targetLines.Investigate, $atI, $nT, (Format-Pct $atI $nT), $targetLines.Watch, $atW, (Format-Pct $atW $nT))
    Out-Line ''

    foreach ($which in @(@{ Label = 'Watch'; Line = $refLines.Watch }, @{ Label = 'Investigate'; Line = $refLines.Investigate })) {
        $refCount = Get-CountAtOrAbove $reference $which.Line
        $share = if ($nR -gt 0) { $refCount / [double]$nR } else { 0 }
        Out-Line ("  {0} line of '{1}' = {2}: {3:N0} of {4:N0} snapshots at or above it (share {5})" -f `
            $which.Label, $MatchPrevalenceOf, $which.Line, $refCount, $nR, (Format-Pct $refCount $nR))
        if ($refCount -eq 0) {
            Out-Line ("    -> no prevalence to match: '{0}' has 0 snapshots >= {1}; a {2} line for '{3}' is a stated judgement, not a match" -f `
                $MatchPrevalenceOf, $which.Line, $which.Label, $Strategy)
            continue
        }
        $k = [int][math]::Round($share * $nT, [System.MidpointRounding]::AwayFromZero)
        if ($k -lt 1) { $k = 1 }
        if ($k -gt $nT) { $k = $nT }
        $value = $target[$k - 1]
        $actualAtValue = Get-CountAtOrAbove $target $value
        $actualAbove = Get-CountAtOrAbove $target ($value + 1)
        Out-Line ("    -> '{0}' at the same share: k = round({1:F4} x {2:N0}) = {3}; the {3}-th largest value is {4}" -f `
            $Strategy, $share, $nT, $k, $value)
        Out-Line ("       share of '{0}' >= {1}: {2:N0} ({3});  >= {4}: {5:N0} ({6})   [ties widen the >= share; the line is the value, not the rank]" -f `
            $Strategy, $value, $actualAtValue, (Format-Pct $actualAtValue $nT), ($value + 1), $actualAbove, (Format-Pct $actualAbove $nT))
    }
    Out-Line ''
    Out-Line "  This is a WORKLOAD match (how many names a report puts in front of a reader), not evidence that"
    Out-Line "  a value is a strong opportunity; the lines are fixed once chosen, never daily quantiles."
}
Out-Line '================================================================================'

$report = $lines -join [System.Environment]::NewLine
Write-Output $report

if ($OutFile) {
    [System.IO.File]::WriteAllText($outFull, $report + [System.Environment]::NewLine)
    Write-Verbose "Report written to $outFull"
}
