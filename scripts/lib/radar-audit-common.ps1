<#
.SYNOPSIS
    Shared read-only primitives for the score-vs-price audits: audit-score-vs-price-misses.ps1 FINDS the
    divergences, audit-miss-diagnosis.ps1 EXPLAINS them.

.DESCRIPTION
    Extracted rather than copied. Both audits resolve the same store paths, refuse the same unsafe -OutFile,
    read the same per-company efficacy CSVs with the same (company, scoreDate) collapse, and anchor forward
    returns on the same bar. A second copy of any of those would drift, and only one copy would get the next
    fix. PowerShell 5.1-compatible; every function here is READ-ONLY over the data root.
#>

# Trailing separators stripped so two spellings of one directory compare equal; a drive/UNC root keeps its
# separator, because 'C:' is NOT the same path as 'C:\'.
function Format-RadarDirectoryPath {
    param([Parameter(Mandatory=$true)][string]$Path)
    $trimmed = $Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    if ([string]::IsNullOrEmpty($trimmed) -or $trimmed.EndsWith(':')) { return $Path }
    return $trimmed
}

# Resolves -DataRoot and -OutFile against the PowerShell PROVIDER location (not the .NET process CWD: the two
# can differ, and a mismatch would let a relative -OutFile pass the guard and then be written inside the
# store), NORMALISES a trailing separator, and refuses an -OutFile at or under the root.
# The normalisation is load-bearing: GetFullPath PRESERVES a trailing separator on .NET Framework and
# `-DataRoot .\data\` is exactly what tab-completion produces, so without it the separator-plus-equality
# guard tested for a DOUBLED separator, matched nothing, and let the report be written into the store.
function Resolve-RadarAuditPaths {
    param(
        [Parameter(Mandatory=$true)][AllowEmptyString()][string]$DataRoot,
        [AllowEmptyString()][AllowNull()][string]$OutFile,
        [string[]]$RequiredSubdirectories = @()
    )

    $providerCwd = (Get-Location).ProviderPath
    $rootCandidate = if ([System.IO.Path]::IsPathRooted($DataRoot)) { $DataRoot } else { Join-Path $providerCwd $DataRoot }
    $dataRootFull = Format-RadarDirectoryPath ([System.IO.Path]::GetFullPath($rootCandidate))
    if (-not (Test-Path -LiteralPath $dataRootFull)) { throw "DataRoot not found: $dataRootFull" }

    $outFull = $null
    if ($OutFile) {
        $outCandidate = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path $providerCwd $OutFile }
        $outFull = [System.IO.Path]::GetFullPath($outCandidate)
        $outCompare = Format-RadarDirectoryPath $outFull
        if ($outCompare.StartsWith($dataRootFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
            $outCompare.Equals($dataRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "-OutFile '$outFull' is inside -DataRoot '$dataRootFull'; this audit never writes inside the store."
        }
    }

    $resolved = [ordered]@{ DataRoot = $dataRootFull; OutFile = $outFull }
    foreach ($name in $RequiredSubdirectories) {
        $dir = Join-Path $dataRootFull $name
        if (-not (Test-Path -LiteralPath $dir)) { throw "Required directory not found: $dir" }
        $resolved[$name] = $dir
    }
    return [pscustomobject]$resolved
}

# UTF-8 WITHOUT a BOM (Set-Content -Encoding UTF8 emits one under PS 5.1), to the RESOLVED path the guard
# actually checked, never the raw parameter.
function Write-RadarAuditReport {
    param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)]$Lines)
    [System.IO.File]::WriteAllText($Path, ($Lines -join [System.Environment]::NewLine) + [System.Environment]::NewLine)
}

# -ExcludeTickers split on commas AND whitespace: `powershell -File script.ps1 -ExcludeTickers A,B` binds the
# whole list as ONE string under -File (the repo's canonical invocation form), which silently excluded nothing.
function Expand-RadarTickerList {
    param([AllowNull()][string[]]$Values)
    $out = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Values) { return $out }
    foreach ($v in $Values) {
        if (-not $v) { continue }
        foreach ($piece in ($v -split '[,;\s]+')) {
            if ($piece) { $out.Add($piece.ToUpperInvariant()) | Out-Null }
        }
    }
    return $out
}

# --- price series ------------------------------------------------------------------------------------
# ticker -> { Dates (ascending ISO strings), Close (date -> adjClose) }. Counters are incremented on the
# caller's ordered table, so nothing is dropped without being counted.
function Import-RadarPriceSeries {
    param(
        [Parameter(Mandatory=$true)][string]$PricesDirectory,
        [Parameter(Mandatory=$true)]$Counters
    )
    $prices = @{}
    foreach ($pf in (Get-ChildItem -LiteralPath $PricesDirectory -Filter '*.json' -File | Sort-Object Name)) {
        try {
            $j = Get-Content -LiteralPath $pf.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        } catch {
            $Counters.PriceFilesUnreadable++
            continue
        }
        if (-not $j.PSObject.Properties.Match('bars').Count -or -not $j.bars) { $Counters.PriceFilesUnreadable++; continue }
        $map = @{}
        foreach ($b in $j.bars) {
            # Property presence is probed, never assumed: under Set-StrictMode a missing field would throw and
            # abort the whole audit rather than being counted as one unusable bar.
            $hasDate  = $b.PSObject.Properties.Match('date').Count -gt 0
            $hasClose = $b.PSObject.Properties.Match('adjClose').Count -gt 0
            if (-not $hasDate -or -not $hasClose -or -not $b.date -or $null -eq $b.adjClose) { $Counters.BarsUnusable++; continue }
            # TryParse, not a [double] cast: a non-numeric adjClose must be COUNTED as one unusable bar, not
            # throw and kill the run - the neighbouring probes would be pointless if this line could abort.
            $close = 0.0
            if (-not [double]::TryParse([string]$b.adjClose, [System.Globalization.NumberStyles]::Float,
                    [System.Globalization.CultureInfo]::InvariantCulture, [ref]$close)) { $Counters.BarsUnusable++; continue }
            $d = [string]$b.date
            if ($map.ContainsKey($d)) { $Counters.BarDatesDuplicated++ }
            $map[$d] = $close
        }
        if ($map.Count -lt 2) { $Counters.PriceFilesTooShort++; continue }
        $ticker = if ($j.PSObject.Properties.Match('ticker').Count -and $j.ticker) { [string]$j.ticker } else { $pf.BaseName }
        $key = $ticker.ToUpperInvariant()
        if ($prices.ContainsKey($key)) { $Counters.PriceFilesDuplicateTicker++ }
        $prices[$key] = [pscustomobject]@{ Dates = ([string[]]($map.Keys) | Sort-Object); Close = $map }
    }
    return $prices
}

# The forward move for one (ticker, scoreDate), anchored on the first bar AT OR AFTER the score date.
# Returns a Status so the caller can count each refusal separately; only Status 'Ok' carries numbers. A
# non-positive bar at EITHER end is refused - a zero end bar would otherwise render as a measured -100% move.
function Get-RadarForwardMove {
    param(
        [Parameter(Mandatory=$true)]$Series,
        [Parameter(Mandatory=$true)][string]$ScoreDate,
        [Parameter(Mandatory=$true)][int]$ForwardSessions
    )
    $fut = @($Series.Dates | Where-Object { $_ -ge $ScoreDate })
    if ($fut.Count -lt 1) { return [pscustomobject]@{ Status = 'NoBarOnOrAfter' } }
    if ($fut.Count -le $ForwardSessions) { return [pscustomobject]@{ Status = 'WindowIncomplete' } }
    $p0 = $Series.Close[$fut[0]]
    $p1 = $Series.Close[$fut[$ForwardSessions]]
    if ($p0 -le 0) { return [pscustomobject]@{ Status = 'NonPositiveAnchor' } }
    if ($p1 -le 0) { return [pscustomobject]@{ Status = 'NonPositiveEnd' } }
    # The GAP between the score date and the bar the window anchors on is reported, never swallowed. A score
    # date before the price history begins anchors on the first available bar - possibly years later - and
    # would otherwise render as a measured forward move from the score date. The caller decides what an
    # unacceptable gap is; this function refuses to hide it.
    $anchorGap = ([datetime]::ParseExact($fut[0], 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture) -
                  [datetime]::ParseExact($ScoreDate, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)).Days
    return [pscustomobject]@{
        Status        = 'Ok'
        AnchorDate    = $fut[0]
        AnchorGapDays = $anchorGap
        WindowEnd     = $fut[$ForwardSessions]
        AnchorClose   = $p0
        EndClose      = $p1
        ForwardPct    = 100.0 * ($p1 / $p0 - 1.0)
    }
}

# --- accrued score rows ------------------------------------------------------------------------------
# One row per (company, scoreDate) from the per-company efficacy CSVs. The store holds REPEATS (several
# scoring passes can land on one date), and counting them as independent cohort members would inflate the
# percentile denominator, defeat -MinCohort, and let a repeated company depress every other name's rank on
# that date - its own repeats never count as "below" itself. The LAST row wins, which is both deterministic
# and meaningful: FileScoreSnapshotStore.ReadAllForCompanyAsync sorts ascending CreatedAtUtc then Id, so file
# order IS run order and the last row is that date's most recent scoring pass.
function Import-RadarScoreRows {
    param(
        [Parameter(Mandatory=$true)][string]$EfficacyDirectory,
        [Parameter(Mandatory=$true)][string]$Series,
        [Parameter(Mandatory=$true)]$Counters,
        [Parameter(Mandatory=$true)]$NonPerCompanyFiles
    )
    $byPair = @{}
    $divergent = @{}
    foreach ($f in (Get-ChildItem -LiteralPath $EfficacyDirectory -Filter '*.csv' -File | Sort-Object Name)) {
        if ($f.Name -like 'strategy-*' -or $f.Name -like 'attention-*' -or $f.Name -like 'directional-filing-*') {
            $Counters.CsvFilesNotPerCompany++
            $NonPerCompanyFiles.Add($f.Name) | Out-Null
            continue
        }
        $Counters.CsvFiles++
        $ticker = $f.BaseName.ToUpperInvariant()
        try { $rows = Import-Csv -LiteralPath $f.FullName } catch { $Counters.CsvUnreadable++; continue }
        foreach ($r in $rows) {
            $Counters.RowsRead++
            # Column presence is probed: a legacy/future header without these columns must be COUNTED as
            # unparseable, not abort the run under Set-StrictMode.
            $props = $r.PSObject.Properties
            if ($props.Match('seriesKey').Count -eq 0 -or $props.Match('scoreDate').Count -eq 0 -or
                $props.Match('opportunity').Count -eq 0) { $Counters.RowsUnparseable++; continue }
            if ($r.seriesKey -ne $Series) { $Counters.RowsWrongSeries++; continue }
            $opp = 0.0
            $parsed = [double]::TryParse([string]$r.opportunity, [System.Globalization.NumberStyles]::Float,
                [System.Globalization.CultureInfo]::InvariantCulture, [ref]$opp)
            if (-not $r.scoreDate -or -not $parsed) { $Counters.RowsUnparseable++; continue }
            $cfg = if ($props.Match('scoringConfigVersion').Count -gt 0) { [string]$r.scoringConfigVersion } else { '' }

            $key = $ticker + '|' + [string]$r.scoreDate
            if ($byPair.ContainsKey($key)) {
                $Counters.RowsDuplicatePairCollapsed++
                # A repeat carrying a DIFFERENT score is not the same fact as an exact repeat, and the collapse
                # count alone cannot tell them apart - so the divergent pairs are counted separately.
                if ($byPair[$key].Opportunity -ne $opp) { $divergent[$key] = $true }
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
    $Counters.RowsDuplicatePairDivergent = $divergent.Count
    $ordered = New-Object System.Collections.Generic.List[object]
    foreach ($k in ($byPair.Keys | Sort-Object)) { $ordered.Add($byPair[$k]) | Out-Null }
    return $ordered
}

# Cohort opportunity values per score date, one entry per company (the rows are already collapsed).
function Group-RadarScoreRowsByDate {
    param([Parameter(Mandatory=$true)]$Rows)
    $byDate = @{}
    foreach ($p in $Rows) {
        if (-not $byDate.ContainsKey($p.ScoreDate)) { $byDate[$p.ScoreDate] = New-Object System.Collections.Generic.List[double] }
        $byDate[$p.ScoreDate].Add($p.Opportunity)
    }
    return $byDate
}

# Percentile of a value WITHIN its own date's cohort: the share of the cohort strictly below it, so the
# lowest of N sits at 0. A name is only ever compared against what Radar ranked on the SAME day.
function Get-RadarCohortPercentile {
    param([Parameter(Mandatory=$true)]$Cohort, [Parameter(Mandatory=$true)][double]$Value)
    $below = 0
    foreach ($v in $Cohort) { if ($v -lt $Value) { $below++ } }
    return 100.0 * $below / $Cohort.Count
}
