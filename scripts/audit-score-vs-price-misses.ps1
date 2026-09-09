<#
.SYNOPSIS
    Read-only audit that pairs each accrued score with the forward price move that followed it, and lists
    the point-in-time MISSES (scored low, then rose) and FALSE POSITIVES (scored high, then fell).

.DESCRIPTION
    For every (company, scoreDate) pair in the accrued efficacy series this script computes two things:

        1. the company's Opportunity PERCENTILE within that score date's own cohort - so a name is only
           ever compared against what Radar ranked on the SAME day, never against a later distribution;
        2. the forward return over the next N TRADING SESSIONS, taken from {DataRoot}/prices/<ticker>.json
           (adjClose), anchored on the first bar at or after the score date.

    It then reports:

        MISSES          scored at or below -MissPercentile, then rose by at least -MovePercent
        FALSE POSITIVES scored at or above -HitPercentile,  then fell by at least -MovePercent

    PRICE IS VALIDATION-ONLY (AD-14). It is never a scoring input, and nothing here feeds scoring. This
    script surfaces candidates for human investigation; it is not an efficacy result and carries no
    verdict about any strategy. Pooled rank-correlation belongs in the leaderboard, not here.

    WHAT IT DELIBERATELY REFUSES TO DO. A percentile over a small cohort is meaningless (an early date
    with 6 scored companies puts the 1st of 6 at the 0th percentile), so dates below -MinCohort are
    EXCLUDED AND COUNTED rather than ranked. A company under a pending acquisition is a closed thesis, not
    a miss (spec 217: MarineMax's +51% take-out bid was the largest "miss" in the store and Radar could not
    have earned it) - pass those tickers to -ExcludeTickers; the exclusion is named and counted in the
    output. Every company with no price file, and every pair whose forward window has not completed, is
    likewise COUNTED and reported, never silently dropped.

    WHERE A COMPANY HAS SEVERAL ROWS FOR ONE SCORE DATE the LAST row in file order wins (file order is
    run order - FileScoreSnapshotStore sorts by CreatedAtUtc then Id - so this is the latest scoring pass
    of that day). Collapses are counted, and those carrying a DIFFERENT Opportunity are counted separately
    again. The CSV carries no run identifier, so on a date where companies have unequal row counts the
    surviving rows may come from different passes; that is an accepted approximation, counted not hidden.

    A score's comparability across eras is NOT this script's concern: the percentile is computed within a
    single date, so a fingerprint move between dates does not distort it. The scoringConfigVersion of each
    row is reported per finding so a reader can see which identity produced it.

    STRICTLY READ-ONLY over the data root: the script only reads, and refuses an -OutFile that would land
    inside -DataRoot. Output ordering is deterministic and total (forward return by magnitude, then ticker, then
    score date) - misses descending, false positives ascending.
    PowerShell 5.1-compatible.

.PARAMETER DataRoot
    The durable store root (holds efficacy/ and prices/). Default: 'data' beside the scripts folder.

.PARAMETER Series
    The seriesKey to read from the per-company efficacy CSVs. Default: 'default' (the storage primary).

.PARAMETER ForwardSessions
    Trading sessions in the forward window, counted from the first price bar at or after the score date.
    Default: 21 (roughly one month). A pair whose series holds fewer bars is excluded and counted.

.PARAMETER MovePercent
    Minimum absolute forward move, in percent, for a pair to be reported. Default: 15.

.PARAMETER MissPercentile
    A pair is a MISS candidate when its Opportunity percentile is at or below this. Default: 40.

.PARAMETER HitPercentile
    A pair is a FALSE POSITIVE candidate when its Opportunity percentile is at or above this. Default: 70.

.PARAMETER MinCohort
    Minimum number of companies scored on a date for that date's percentiles to be trusted. Dates below it
    are excluded and counted. Default: 20.

.PARAMETER ExcludeTickers
    Tickers to exclude (e.g. a pending acquisition, spec 217). Named and counted in the output.

.PARAMETER AllPairs
    Report every qualifying pair rather than only the strongest one per ticker.

.PARAMETER OutFile
    Optional path to also write the rendered report text to. Must NOT be inside -DataRoot.

.EXAMPLE
    powershell -File scripts/audit-score-vs-price-misses.ps1 -ExcludeTickers HZO

.EXAMPLE
    powershell -File scripts/audit-score-vs-price-misses.ps1 -ForwardSessions 10 -MovePercent 20 -AllPairs
#>
[CmdletBinding()]
param(
    [string]$DataRoot = '',   # defaults to <repo>/data; $PSScriptRoot can be EMPTY at param binding under `powershell -File` from a wrapped host (the run-next.ps1 precedent)
    [string]$Series = 'default',
    [ValidateRange(1, 5000)][int]$ForwardSessions = 21,
    [ValidateRange(0.0001, 10000)][double]$MovePercent = 15,
    [ValidateRange(0, 100)][double]$MissPercentile = 40,
    [ValidateRange(0, 100)][double]$HitPercentile = 70,
    [ValidateRange(1, 100000)][int]$MinCohort = 20,
    [string[]]$ExcludeTickers = @(),
    [switch]$AllPairs,
    [string]$OutFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# --- path resolution (mirrors audit-label-thresholds.ps1) ---
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir -and $PSCommandPath) { $ScriptDir = Split-Path -Parent $PSCommandPath }
if (-not $ScriptDir -and $MyInvocation.MyCommand.Path) { $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ScriptDir) { throw "Could not determine script directory; pass -DataRoot explicitly." }
if (-not $DataRoot) { $DataRoot = Join-Path (Split-Path $ScriptDir -Parent) 'data' }

# Resolve BOTH paths against the PowerShell provider location, not the .NET process CWD: the two can
# differ, and a mismatch would let a relative -OutFile pass the guard here and then be written inside
# -DataRoot by Set-Content. Same form as audit-label-thresholds.ps1.
$rootCandidate = if ([System.IO.Path]::IsPathRooted($DataRoot)) { $DataRoot } else { Join-Path (Get-Location).ProviderPath $DataRoot }
$DataRootFull = [System.IO.Path]::GetFullPath($rootCandidate)
# GetFullPath PRESERVES a trailing separator ('data' stays 'C:\...\data'), which would make the guard
# below test StartsWith('...data' + '') - a doubled separator that can never match - and let an -OutFile
# land inside the store. '.\data' is exactly what tab-completion produces, so normalise before any use.
$trimmedRoot = $DataRootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
if ($trimmedRoot -and -not $trimmedRoot.EndsWith(':')) { $DataRootFull = $trimmedRoot }   # keep 'C:' intact
if (-not (Test-Path -LiteralPath $DataRootFull)) { throw "DataRoot not found: $DataRootFull" }
$outFull = $null
if ($OutFile) {
    $outCandidate = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path (Get-Location).ProviderPath $OutFile }
    $outFull = [System.IO.Path]::GetFullPath($outCandidate)
    # Trim-then-append exactly one separator so a drive-root store ('C:') compares as 'C:', not 'C:\'
    # - the doubled form can never match, which would let any -OutFile on that drive through.
    $rootPrefix = $DataRootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) +
                  [System.IO.Path]::DirectorySeparatorChar
    if ($outFull.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        $outFull.Equals($DataRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "-OutFile '$outFull' is inside -DataRoot '$DataRootFull'; this audit never writes inside the store."
    }
}
$efficacyDir = Join-Path $DataRootFull 'efficacy'
$pricesDir   = Join-Path $DataRootFull 'prices'
foreach ($d in @($efficacyDir, $pricesDir)) {
    if (-not (Test-Path -LiteralPath $d)) { throw "Required directory not found: $d" }
}

$excluded = @{}
# `powershell -File script.ps1 -ExcludeTickers A,B` binds ONE string "A,B", so split every supplied
# element: the documented plural case (several pending acquisitions) must not silently exclude nothing.
foreach ($raw in $ExcludeTickers) {
    if (-not $raw) { continue }
    foreach ($t in ($raw -split '[,\s]+')) { if ($t) { $excluded[$t.ToUpperInvariant()] = $true } }
}

$lines = New-Object System.Collections.Generic.List[string]
function Emit([string]$s) { $lines.Add($s); Write-Host $s }

# --- counters: nothing is dropped without being counted ---
$c = [ordered]@{
    CsvFiles                   = 0
    CsvFilesNotPerCompany      = 0
    CsvUnreadable              = 0
    RowsRead                   = 0
    RowsWrongSeries            = 0
    RowsUnparseable            = 0
    RowsDuplicatePairCollapsed = 0
    RowsDuplicatePairDivergent = 0
    BarsUnusable               = 0
    BarDatesDuplicated         = 0
    TickersExcludedRequested   = 0
    TickersExcludedMatched     = 0
    PairsExcludedByTicker      = 0
    PairsWithoutPriceFile      = 0
    TickersWithoutPriceFile    = 0
    PriceFilesUnreadable       = 0
    PriceFilesTooShort         = 0
    PairsNoPriceBarOnOrAfter   = 0
    PairsNonPositiveAnchor     = 0
    PairsWindowIncomplete      = 0
    DatesBelowMinCohort        = 0
    PairsInSmallCohortDates    = 0
    PairsEvaluated             = 0
}
$notPerCompanyFiles = New-Object System.Collections.Generic.List[string]

# --- load price series: ticker -> ordered date[] + date->adjClose ---
$prices = @{}
foreach ($pf in (Get-ChildItem -LiteralPath $pricesDir -Filter '*.json' -File | Sort-Object Name)) {
    try {
        $j = Get-Content -LiteralPath $pf.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        $c.PriceFilesUnreadable++
        continue
    }
    if (-not $j.PSObject.Properties.Match('bars').Count -or -not $j.bars) { $c.PriceFilesUnreadable++; continue }
    $map = @{}
    foreach ($b in $j.bars) {
        # Property presence is probed, never assumed: under Set-StrictMode a missing field would throw and
        # abort the whole audit rather than being counted as one unusable bar.
        $hasDate  = $b.PSObject.Properties.Match('date').Count -gt 0
        $hasClose = $b.PSObject.Properties.Match('adjClose').Count -gt 0
        if (-not $hasDate -or -not $hasClose -or -not $b.date -or $null -eq $b.adjClose) { $c.BarsUnusable++; continue }
        $close = 0.0
        if (-not [double]::TryParse([string]$b.adjClose, [System.Globalization.NumberStyles]::Float,
                [System.Globalization.CultureInfo]::InvariantCulture, [ref]$close)) { $c.BarsUnusable++; continue }
        $d = [string]$b.date
        if ($map.ContainsKey($d)) { $c.BarDatesDuplicated++ }
        $map[$d] = $close
    }
    if ($map.Count -eq 0) { $c.PriceFilesUnreadable++; continue }
    if ($map.Count -lt 2) { $c.PriceFilesTooShort++; continue }   # read fine, just too short to span a window
    $ticker = if ($j.PSObject.Properties.Match('ticker').Count -and $j.ticker) { [string]$j.ticker } else { $pf.BaseName }
    $prices[$ticker.ToUpperInvariant()] = [pscustomobject]@{
        Dates = ([string[]]($map.Keys) | Sort-Object)
        Close = $map
    }
}

# --- load scores from the per-company efficacy CSVs ---
$pairs = New-Object System.Collections.Generic.List[object]
$byPair = @{}   # "TICKER|scoreDate" -> row; collapses the store's repeats to one cohort member
foreach ($f in (Get-ChildItem -LiteralPath $efficacyDir -Filter '*.csv' -File | Sort-Object Name)) {
    if ($f.Name -like 'strategy-*' -or $f.Name -like 'attention-*' -or $f.Name -like 'directional-filing-*') {
        $c.CsvFilesNotPerCompany++
        $notPerCompanyFiles.Add($f.Name) | Out-Null
        continue
    }
    $c.CsvFiles++
    $ticker = $f.BaseName.ToUpperInvariant()
    try { $rows = Import-Csv -LiteralPath $f.FullName } catch { $c.CsvUnreadable++; continue }
    foreach ($r in $rows) {
        $c.RowsRead++
        # Column presence is probed: a legacy/future header without these columns must be COUNTED as
        # unparseable, not abort the run under Set-StrictMode.
        $props = $r.PSObject.Properties
        if ($props.Match('seriesKey').Count -eq 0 -or $props.Match('scoreDate').Count -eq 0 -or
            $props.Match('opportunity').Count -eq 0) { $c.RowsUnparseable++; continue }
        if ($r.seriesKey -ne $Series) { $c.RowsWrongSeries++; continue }
        $opp = 0.0
        $parsed = [double]::TryParse(
            [string]$r.opportunity,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$opp)
        if (-not $r.scoreDate -or -not $parsed) { $c.RowsUnparseable++; continue }
        $cfg = if ($props.Match('scoringConfigVersion').Count -gt 0) { [string]$r.scoringConfigVersion } else { '' }

        # ONE row per (company, scoreDate). The store holds repeats (a company can carry several rows for
        # one date), and counting them as independent cohort members would inflate the denominator, defeat
        # -MinCohort, and let a repeated company depress every other name's rank on that date - its own
        # repeats never count as "below" itself. Later rows win, deterministically (file order).
        $key = $ticker + '|' + [string]$r.scoreDate
        if ($byPair.ContainsKey($key)) {
            $c.RowsDuplicatePairCollapsed++
            # A repeat carrying a DIFFERENT opportunity is a genuine re-score, not a duplicate write:
            # counted separately so the collapse never hides a materially changed value.
            if ($byPair[$key].Opportunity -ne $opp) { $c.RowsDuplicatePairDivergent++ }
        }
        $byPair[$key] = [pscustomobject]@{
            Ticker      = $ticker
            ScoreDate   = [string]$r.scoreDate
            Opportunity = $opp
            ConfigVer   = $cfg
            Percentile  = [double]::NaN
            Forward     = [double]::NaN
            WindowEnd   = ''
        }
    }
}
foreach ($k in ($byPair.Keys | Sort-Object)) { $pairs.Add($byPair[$k]) | Out-Null }

# --- cohort percentiles, computed WITHIN each score date ---
$byDate = @{}
foreach ($p in $pairs) {
    if (-not $byDate.ContainsKey($p.ScoreDate)) { $byDate[$p.ScoreDate] = New-Object System.Collections.Generic.List[double] }
    $byDate[$p.ScoreDate].Add($p.Opportunity)
}
$smallDates = @{}
foreach ($d in $byDate.Keys) { if ($byDate[$d].Count -lt $MinCohort) { $smallDates[$d] = $byDate[$d].Count } }
$c.DatesBelowMinCohort = $smallDates.Count

# --- evaluate ---
$seenMissingPrice = @{}
$matchedExclusions = @{}
$evaluated = New-Object System.Collections.Generic.List[object]
foreach ($p in $pairs) {
    if ($excluded.ContainsKey($p.Ticker)) { $c.PairsExcludedByTicker++; $matchedExclusions[$p.Ticker] = $true; continue }
    if ($smallDates.ContainsKey($p.ScoreDate)) { $c.PairsInSmallCohortDates++; continue }
    if (-not $prices.ContainsKey($p.Ticker)) {
        $c.PairsWithoutPriceFile++
        if (-not $seenMissingPrice.ContainsKey($p.Ticker)) { $seenMissingPrice[$p.Ticker] = $true }
        continue
    }
    $ps = $prices[$p.Ticker]
    $fut = @($ps.Dates | Where-Object { $_ -ge $p.ScoreDate })
    if ($fut.Count -lt 1) { $c.PairsNoPriceBarOnOrAfter++; continue }
    if ($fut.Count -le $ForwardSessions) { $c.PairsWindowIncomplete++; continue }
    $p0 = $ps.Close[$fut[0]]
    $p1 = $ps.Close[$fut[$ForwardSessions]]
    # Both ends are checked: a zero END bar would otherwise render as a measured -100% move.
    if ($p0 -le 0 -or $p1 -le 0) { $c.PairsNonPositiveAnchor++; continue }

    $cohort = $byDate[$p.ScoreDate]
    $below = 0
    foreach ($v in $cohort) { if ($v -lt $p.Opportunity) { $below++ } }
    $p.Percentile = 100.0 * $below / $cohort.Count
    $p.Forward    = 100.0 * ($p1 / $p0 - 1.0)
    $p.WindowEnd  = $fut[$ForwardSessions]
    # A row that never recorded a scoring identity (the pre-fingerprint rows accrued 2026-06-30..07-02)
    # renders as NOT RECORDED, never as an empty cell: absent provenance must not read as a blank column.
    if ([string]::IsNullOrWhiteSpace($p.ConfigVer)) { $p.ConfigVer = '(not recorded)' }
    $c.PairsEvaluated++
    $evaluated.Add($p) | Out-Null
}
$c.TickersExcludedRequested = $excluded.Count
$c.TickersExcludedMatched   = $matchedExclusions.Count
$c.TickersWithoutPriceFile  = $seenMissingPrice.Count
# A requested exclusion that matched nothing is named: a typo'd ticker must not read as a real exclusion.
$unmatchedExclusions = @($excluded.Keys | Where-Object { -not $matchedExclusions.ContainsKey($_) } | Sort-Object)

# --- classify ---
$misses = @($evaluated | Where-Object { $_.Percentile -le $MissPercentile -and $_.Forward -ge $MovePercent })
$fps    = @($evaluated | Where-Object { $_.Percentile -ge $HitPercentile -and $_.Forward -le (-1 * $MovePercent) })

function Format-Table-Rows($rows, [bool]$descending) {
    # ScoreDate is the final key so the ordering is TOTAL: Sort-Object is not stable in PS 5.1, and ties on
    # (Forward, Ticker) are real (repeated score dates collapse onto one Monday anchor), so without it the
    # row printed in top-per-ticker mode - and the provenance column with it - would be arbitrary.
    $sorted = if ($descending) { @($rows | Sort-Object -Property @{Expression='Forward';Descending=$true}, Ticker, ScoreDate) }
              else            { @($rows | Sort-Object -Property @{Expression='Forward';Descending=$false}, Ticker, ScoreDate) }
    if (-not $AllPairs) {
        $seen = @{}
        $keep = New-Object System.Collections.Generic.List[object]
        foreach ($r in $sorted) { if (-not $seen.ContainsKey($r.Ticker)) { $seen[$r.Ticker] = $true; $keep.Add($r) | Out-Null } }
        # Returned collections are ENUMERATED by PowerShell; both call sites wrap the call in @() so that
        # 0 rows becomes an empty array and 1 row a one-element array. Without that wrap, `.Count` throws
        # under Set-StrictMode and kills the very "(none)" path this function exists to feed.
        return $keep
    }
    return $sorted
}

# --- render ---
Emit ""
Emit "==== Score vs forward price move - point-in-time misses and false positives ===="
Emit ("DataRoot        : {0}" -f $DataRootFull)
Emit ("Series          : {0}" -f $Series)
Emit ("Forward window  : {0} trading session(s), anchored on the first bar at or after the score date" -f $ForwardSessions)
Emit ("Move threshold  : {0}%" -f $MovePercent)
Emit ("Percentile lines: miss <= {0}th, false positive >= {1}th (computed WITHIN each score date)" -f $MissPercentile, $HitPercentile)
Emit ("Min cohort      : {0} companies per date" -f $MinCohort)
if ($excluded.Count -gt 0) { Emit ("Excluded tickers: {0}" -f (($excluded.Keys | Sort-Object) -join ', ')) }
Emit ""
Emit "PRICE IS VALIDATION-ONLY (AD-14). These are candidates for investigation, not an efficacy result."
Emit ""

$mrows = @(Format-Table-Rows $misses $true)
Emit ("---- MISSES: scored <= {0}th percentile, then rose >= {1}% ----" -f $MissPercentile, $MovePercent)
if ($mrows.Count -eq 0) { Emit "  (none)" }
else {
    Emit ("  {0,-8}{1,-12}{2,7}{3,7}{4,10}  {5,-12}{6}" -f 'ticker','scoreDate','opp','pct','forward%','windowEnd','scoringConfigVersion')
    foreach ($r in $mrows) {
        Emit ("  {0,-8}{1,-12}{2,7:N0}{3,7:N0}{4,10:N1}  {5,-12}{6}" -f $r.Ticker,$r.ScoreDate,$r.Opportunity,$r.Percentile,$r.Forward,$r.WindowEnd,$r.ConfigVer)
    }
}
Emit ("  {0}{1} qualifying pair(s) across {2} distinct company/companies." -f $(if ($AllPairs) { '' } else { "showing the strongest 1 per ticker of " }), $misses.Count, (@($misses | Select-Object -ExpandProperty Ticker -Unique)).Count)
Emit ""

$frows = @(Format-Table-Rows $fps $false)
Emit ("---- FALSE POSITIVES: scored >= {0}th percentile, then fell >= {1}% ----" -f $HitPercentile, $MovePercent)
if ($frows.Count -eq 0) { Emit "  (none)" }
else {
    Emit ("  {0,-8}{1,-12}{2,7}{3,7}{4,10}  {5,-12}{6}" -f 'ticker','scoreDate','opp','pct','forward%','windowEnd','scoringConfigVersion')
    foreach ($r in $frows) {
        Emit ("  {0,-8}{1,-12}{2,7:N0}{3,7:N0}{4,10:N1}  {5,-12}{6}" -f $r.Ticker,$r.ScoreDate,$r.Opportunity,$r.Percentile,$r.Forward,$r.WindowEnd,$r.ConfigVer)
    }
}
Emit ("  {0}{1} qualifying pair(s) across {2} distinct company/companies." -f $(if ($AllPairs) { '' } else { "showing the strongest 1 per ticker of " }), $fps.Count, (@($fps | Select-Object -ExpandProperty Ticker -Unique)).Count)
Emit ""

Emit "---- accounting (nothing is dropped without being counted) ----"
foreach ($k in $c.Keys) { Emit ("  {0,-28}{1}" -f $k, $c[$k]) }
if ($notPerCompanyFiles.Count -gt 0) {
    Emit ("  non-per-company CSVs skipped: {0}" -f (($notPerCompanyFiles | Sort-Object) -join ', '))
}
if ($unmatchedExclusions.Count -gt 0) {
    Emit ("  -ExcludeTickers that matched NOTHING (check for a typo): {0}" -f ($unmatchedExclusions -join ', '))
}
if ($seenMissingPrice.Count -gt 0) {
    Emit ("  tickers with no price file: {0}" -f (($seenMissingPrice.Keys | Sort-Object) -join ', '))
}
if ($smallDates.Count -gt 0) {
    $shown = @($smallDates.Keys | Sort-Object | Select-Object -First 10)
    Emit ("  dates below min cohort ({0} of {1} shown): {2}" -f $shown.Count, $smallDates.Count, ($shown -join ', '))
}
Emit ""
Emit "A pair is reported ONLY when its forward window completed; an incomplete window is counted above,"
Emit "never treated as a zero move. Percentiles are within-date, so a fingerprint move between dates does"
Emit "not distort them - each finding carries the scoringConfigVersion that produced it."

if ($OutFile) {
    # WriteAllText, not Set-Content -Encoding UTF8: the latter emits a UTF-8 BOM under PS 5.1. Writes to
    # the RESOLVED path that the guard above actually checked, never to the raw parameter.
    [System.IO.File]::WriteAllText($outFull, ($lines -join [System.Environment]::NewLine) + [System.Environment]::NewLine)
    Write-Host ""
    Write-Host ("Report written to {0}" -f $outFull)
}
