<#
.SYNOPSIS
    Read-only audit of what actually followed insider Form 4 activity - does a discretionary sale
    precede weakness, a buy precede strength, and does either earn the weight Radar gives it?

.DESCRIPTION
    Radar mints an `InsiderActivity` signal from every Form 4 it collects, and a discretionary sale
    carries Strength 8 - the same weight class as an AI directional earnings read, and one of the
    heaviest inputs in the system. That weight has never been checked against outcomes.

    This script reads the sec-form4 evidence already in the store, takes each filing's own recorded
    classification (`insiderClassificationReason`: discretionary-sale / discretionary-buy /
    plan-10b5-1 / no-discretionary-transactions) and dollar value (`insiderNetValue`), and measures
    the forward price move that followed - EXCESS of the equal-weight universe mean over the same
    window, so a market-wide drift is not read as an insider effect.

    PRICE IS VALIDATION-ONLY (AD-14). It is read strictly after the fact and is never a scoring
    input. This measures whether an existing weight is earned; it does not feed anything.

    WHY EXCESS, NOT RAW. If every company rises 3% over a window, a 3% move after a sale means
    nothing. For each anchor date the universe mean forward return is computed over every ticker
    with a complete window, and each event is reported as its return MINUS that mean. The benchmark
    denominator is reported per date-group so a thin benchmark is visible rather than silent.

    WHAT IT DELIBERATELY DOES NOT DO. It draws no conclusion about any company, and it is not a
    trading study: a median excess return over a few hundred filings is a descriptive statistic about
    one signal's weight, not evidence about any name. Sample sizes are printed beside every figure
    precisely so a thin cell is not over-read.

    Every filing without a usable ticker, date, classification or price window is EXCLUDED AND
    COUNTED on its own named axis. A defaulted value is never rendered as a measured one: a filing
    with no recorded net value reports `(not recorded)`, never 0.

    STRICTLY READ-ONLY over the data root: the script only reads, and refuses an -OutFile that would
    land inside -DataRoot. Output ordering is deterministic. PowerShell 5.1-compatible.

.PARAMETER DataRoot
    The durable store root (holds evidence/ and prices/). Default: 'data' beside the scripts folder.

.PARAMETER ForwardSessions
    Trading sessions in the forward window, from the first price bar at or after the filing date.
    Default: 21. A filing whose series holds fewer bars is excluded and counted.

.PARAMETER MinNetValue
    Ignore filings whose recorded net value is below this (dollars). Excluded filings are counted
    separately from those with no recorded value at all. Default: 0 (keep everything).

.PARAMETER Reasons
    Which `insiderClassificationReason` values to report. Default: the three that carry a direction.

.PARAMETER AllRows
    Also print every individual event, not just the grouped distributions.

.PARAMETER OutFile
    Optional path to also write the rendered report text to. Must NOT be inside -DataRoot.

.EXAMPLE
    powershell -File scripts/audit-insider-forward-returns.ps1

.EXAMPLE
    powershell -File scripts/audit-insider-forward-returns.ps1 -ForwardSessions 10 -MinNetValue 100000
#>
[CmdletBinding()]
param(
    [string]$DataRoot = '',
    [ValidateRange(1, 5000)][int]$ForwardSessions = 21,
    [ValidateRange(0, 1e12)][double]$MinNetValue = 0,
    [string[]]$Reasons = @('discretionary-sale','discretionary-buy','plan-10b5-1'),
    [switch]$AllRows,
    [string]$OutFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# --- path resolution (mirrors audit-score-vs-price-misses.ps1) ---
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir -and $PSCommandPath) { $ScriptDir = Split-Path -Parent $PSCommandPath }
if (-not $ScriptDir -and $MyInvocation.MyCommand.Path) { $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ScriptDir) { throw "Could not determine script directory; pass -DataRoot explicitly." }
if (-not $DataRoot) { $DataRoot = Join-Path (Split-Path $ScriptDir -Parent) 'data' }

$rootCandidate = if ([System.IO.Path]::IsPathRooted($DataRoot)) { $DataRoot } else { Join-Path (Get-Location).ProviderPath $DataRoot }
$DataRootFull = [System.IO.Path]::GetFullPath($rootCandidate)
# GetFullPath PRESERVES a trailing separator; normalise before the guard or 'data\' defeats it.
$trimmedRoot = $DataRootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
if ($trimmedRoot -and -not $trimmedRoot.EndsWith(':')) { $DataRootFull = $trimmedRoot }
if (-not (Test-Path -LiteralPath $DataRootFull)) { throw "DataRoot not found: $DataRootFull" }

$outFull = $null
if ($OutFile) {
    $outCandidate = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path (Get-Location).ProviderPath $OutFile }
    $outFull = [System.IO.Path]::GetFullPath($outCandidate)
    $rootPrefix = $DataRootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) +
                  [System.IO.Path]::DirectorySeparatorChar
    if ($outFull.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        $outFull.Equals($DataRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "-OutFile '$outFull' is inside -DataRoot '$DataRootFull'; this audit never writes inside the store."
    }
}
$filingDir = Join-Path (Join-Path $DataRootFull 'evidence') (Join-Path 'raw' 'filing')
$pricesDir = Join-Path $DataRootFull 'prices'
foreach ($d in @($filingDir, $pricesDir)) {
    if (-not (Test-Path -LiteralPath $d)) { throw "Required directory not found: $d" }
}

$lines = New-Object System.Collections.Generic.List[string]
function Emit([string]$s) { $lines.Add($s) | Out-Null; Write-Host $s }

$c = [ordered]@{
    FilingFilesScanned       = 0
    FilingFilesUnreadable    = 0
    NotSecForm4              = 0
    ReasonNotRecorded        = 0
    ReasonNotSelected        = 0
    TickerNotRecorded        = 0
    DateNotRecorded          = 0
    NetValueNotRecorded      = 0
    BelowMinNetValue         = 0
    NoPriceSeries            = 0
    NoBarOnOrAfterDate       = 0
    ForwardWindowIncomplete  = 0
    NonPositiveBar           = 0
    BenchmarkUnavailable     = 0
    EventsMeasured           = 0
}
$missingPrice = @{}

# --- prices: ticker -> ordered dates + close map ---
$prices = @{}
foreach ($pf in (Get-ChildItem -LiteralPath $pricesDir -Filter '*.json' -File | Sort-Object Name)) {
    try { $j = Get-Content -LiteralPath $pf.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { continue }
    if ($j.PSObject.Properties.Match('bars').Count -eq 0 -or -not $j.bars) { continue }
    $map = @{}
    foreach ($b in $j.bars) {
        if ($b.PSObject.Properties.Match('date').Count -eq 0) { continue }
        if ($b.PSObject.Properties.Match('adjClose').Count -eq 0) { continue }
        if (-not $b.date -or $null -eq $b.adjClose) { continue }
        $v = 0.0
        if (-not [double]::TryParse([string]$b.adjClose, [System.Globalization.NumberStyles]::Float,
                [System.Globalization.CultureInfo]::InvariantCulture, [ref]$v)) { continue }
        $map[[string]$b.date] = $v
    }
    if ($map.Count -lt 2) { continue }
    $tk = if ($j.PSObject.Properties.Match('ticker').Count -gt 0 -and $j.ticker) { [string]$j.ticker } else { $pf.BaseName }
    $prices[$tk.ToUpperInvariant()] = [pscustomobject]@{ Dates = ([string[]]$map.Keys | Sort-Object); Close = $map }
}

# Forward move for one ticker from one anchor date. Returns $null when it cannot be measured.
function Get-ForwardMove([string]$ticker, [string]$onOrAfter, [int]$sessions) {
    if (-not $prices.ContainsKey($ticker)) { return $null }
    $ps = $prices[$ticker]
    $fut = @($ps.Dates | Where-Object { $_ -ge $onOrAfter })
    if ($fut.Count -le $sessions) { return $null }
    $p0 = $ps.Close[$fut[0]]; $p1 = $ps.Close[$fut[$sessions]]
    if ($p0 -le 0 -or $p1 -le 0) { return $null }
    return 100.0 * ($p1 / $p0 - 1.0)
}

# --- collect Form 4 events ---
$selected = @{}
foreach ($r in $Reasons) { foreach ($p in ($r -split '[,\s]+')) { if ($p) { $selected[$p] = $true } } }

$events = New-Object System.Collections.Generic.List[object]
foreach ($f in (Get-ChildItem -LiteralPath $filingDir -Recurse -Filter '*.json' -File | Sort-Object FullName)) {
    $c.FilingFilesScanned++
    try { $x = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $c.FilingFilesUnreadable++; continue }
    if ($x.PSObject.Properties.Match('metadata').Count -eq 0 -or -not $x.metadata) { $c.NotSecForm4++; continue }
    $m = $x.metadata
    $col = if ($m.PSObject.Properties.Match('collector').Count -gt 0) { [string]$m.collector } else { '' }
    if ($col -ne 'sec-form4') { $c.NotSecForm4++; continue }

    $reason = if ($m.PSObject.Properties.Match('insiderClassificationReason').Count -gt 0) { [string]$m.insiderClassificationReason } else { '' }
    if (-not $reason) { $c.ReasonNotRecorded++; continue }
    if (-not $selected.ContainsKey($reason)) { $c.ReasonNotSelected++; continue }

    $tk = ''
    if ($m.PSObject.Properties.Match('issuerTicker').Count -gt 0 -and $m.issuerTicker) { $tk = [string]$m.issuerTicker }
    elseif ($x.PSObject.Properties.Match('companyHints').Count -gt 0 -and $x.companyHints) { $tk = [string]($x.companyHints | Select-Object -First 1) }
    if (-not $tk) { $c.TickerNotRecorded++; continue }
    $tk = $tk.ToUpperInvariant()

    $dt = ''
    if ($m.PSObject.Properties.Match('filingDate').Count -gt 0 -and $m.filingDate) { $dt = ([string]$m.filingDate).Substring(0, 10) }
    elseif ($x.PSObject.Properties.Match('publishedAt').Count -gt 0 -and $x.publishedAt) { $dt = ([string]$x.publishedAt).Substring(0, 10) }
    if (-not $dt) { $c.DateNotRecorded++; continue }

    # A filing with no recorded value is kept and reported as NOT RECORDED - never as a measured 0.
    $val = $null
    if ($m.PSObject.Properties.Match('insiderNetValue').Count -gt 0 -and $m.insiderNetValue) {
        $tmp = 0.0
        if ([double]::TryParse([string]$m.insiderNetValue, [System.Globalization.NumberStyles]::Float,
                [System.Globalization.CultureInfo]::InvariantCulture, [ref]$tmp)) { $val = $tmp }
    }
    if ($null -eq $val) { $c.NetValueNotRecorded++ }
    elseif ($val -lt $MinNetValue) { $c.BelowMinNetValue++; continue }

    $events.Add([pscustomobject]@{ Ticker = $tk; Date = $dt; Reason = $reason; NetValue = $val }) | Out-Null
}

# --- benchmark: equal-weight universe mean forward move per anchor date ---
$benchmark = @{}
foreach ($d in (@($events | Select-Object -ExpandProperty Date -Unique) | Sort-Object)) {
    $vals = New-Object System.Collections.Generic.List[double]
    foreach ($tk in ($prices.Keys | Sort-Object)) {
        $mv = Get-ForwardMove $tk $d $ForwardSessions
        if ($null -ne $mv) { $vals.Add($mv) | Out-Null }
    }
    if ($vals.Count -ge 2) {
        $sum = 0.0; foreach ($v in $vals) { $sum += $v }
        $benchmark[$d] = [pscustomobject]@{ Mean = $sum / $vals.Count; N = $vals.Count }
    }
}

# --- measure ---
$measured = New-Object System.Collections.Generic.List[object]
foreach ($e in $events) {
    if (-not $prices.ContainsKey($e.Ticker)) { $c.NoPriceSeries++; $missingPrice[$e.Ticker] = $true; continue }
    $ps = $prices[$e.Ticker]
    $fut = @($ps.Dates | Where-Object { $_ -ge $e.Date })
    if ($fut.Count -lt 1) { $c.NoBarOnOrAfterDate++; continue }
    if ($fut.Count -le $ForwardSessions) { $c.ForwardWindowIncomplete++; continue }
    $p0 = $ps.Close[$fut[0]]; $p1 = $ps.Close[$fut[$ForwardSessions]]
    if ($p0 -le 0 -or $p1 -le 0) { $c.NonPositiveBar++; continue }
    if (-not $benchmark.ContainsKey($e.Date)) { $c.BenchmarkUnavailable++; continue }

    $raw = 100.0 * ($p1 / $p0 - 1.0)
    $bm = $benchmark[$e.Date]
    $measured.Add([pscustomobject]@{
        Ticker = $e.Ticker; Date = $e.Date; Reason = $e.Reason; NetValue = $e.NetValue
        Raw = $raw; Excess = $raw - $bm.Mean; BenchN = $bm.N; WindowEnd = $fut[$ForwardSessions]
    }) | Out-Null
    $c.EventsMeasured++
}

function Get-Median([double[]]$v) {
    if ($v.Count -eq 0) { return [double]::NaN }
    $s = @($v | Sort-Object)
    if ($s.Count % 2 -eq 1) { return $s[[int](($s.Count - 1) / 2)] }
    return ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2.0
}

# --- render ---
Emit ""
Emit "==== What followed insider Form 4 activity - excess of the universe mean ===="
Emit ("DataRoot       : {0}" -f $DataRootFull)
Emit ("Forward window : {0} trading session(s) from the first bar at or after the filing date" -f $ForwardSessions)
Emit ("Classifications: {0}" -f (($selected.Keys | Sort-Object) -join ', '))
if ($MinNetValue -gt 0) { Emit ("Min net value  : {0:N0}" -f $MinNetValue) }
Emit ""
Emit "PRICE IS VALIDATION-ONLY (AD-14) - read after the fact, never a scoring input."
Emit "Excess = the event's forward move MINUS the equal-weight universe mean over the same window."
Emit "Descriptive only. Not evidence about any company, and not a trading study."
Emit ""

Emit ("  {0,-26}{1,6}{2,12}{3,12}{4,12}{5,10}" -f 'classification','n','median exc%','mean exc%','median raw%','share <0')
foreach ($r in ($selected.Keys | Sort-Object)) {
    $grp = @($measured | Where-Object { $_.Reason -eq $r })
    if ($grp.Count -eq 0) { Emit ("  {0,-26}{1,6}{2,12}{3,12}{4,12}{5,10}" -f $r, 0, '-', '-', '-', '-'); continue }
    $ex = [double[]]@($grp | Select-Object -ExpandProperty Excess)
    $rawv = [double[]]@($grp | Select-Object -ExpandProperty Raw)
    $sum = 0.0; foreach ($v in $ex) { $sum += $v }
    $neg = @($ex | Where-Object { $_ -lt 0 }).Count
    Emit ("  {0,-26}{1,6}{2,12:N2}{3,12:N2}{4,12:N2}{5,9:N0}%" -f $r, $grp.Count, (Get-Median $ex), ($sum / $ex.Count), (Get-Median $rawv), (100.0 * $neg / $ex.Count))
}
Emit ""

# materiality split - Radar weights a sale by size, so report whether size tracks outcome
Emit "  by recorded value (sales only; a filing with no recorded value is shown separately)"
$saleGrp = @($measured | Where-Object { $_.Reason -eq 'discretionary-sale' })
$tiers = @(
    @{ Name = '< $100k';        Min = 0.0;       Max = 100000.0 },
    @{ Name = '$100k - $1m';    Min = 100000.0;  Max = 1000000.0 },
    @{ Name = '$1m - $10m';     Min = 1000000.0; Max = 10000000.0 },
    @{ Name = '>= $10m';        Min = 10000000.0; Max = [double]::MaxValue }
)
foreach ($t in $tiers) {
    $g = @($saleGrp | Where-Object { $null -ne $_.NetValue -and $_.NetValue -ge $t.Min -and $_.NetValue -lt $t.Max })
    if ($g.Count -eq 0) { Emit ("    {0,-20}{1,6}{2,12}" -f $t.Name, 0, '-'); continue }
    $ex = [double[]]@($g | Select-Object -ExpandProperty Excess)
    Emit ("    {0,-20}{1,6}{2,12:N2}" -f $t.Name, $g.Count, (Get-Median $ex))
}
$nv = @($saleGrp | Where-Object { $null -eq $_.NetValue })
if ($nv.Count -gt 0) {
    $ex = [double[]]@($nv | Select-Object -ExpandProperty Excess)
    Emit ("    {0,-20}{1,6}{2,12:N2}" -f '(not recorded)', $nv.Count, (Get-Median $ex))
}
Emit ""

if ($AllRows) {
    Emit "  every measured event (ticker, date, classification, excess%, raw%, benchmark n)"
    foreach ($e in (@($measured | Sort-Object -Property Date, Ticker, Reason))) {
        $vs = if ($null -eq $e.NetValue) { '(not recorded)' } else { '{0:N0}' -f $e.NetValue }
        Emit ("    {0,-8}{1,-12}{2,-24}{3,9:N2}{4,9:N2}{5,6}  {6}" -f $e.Ticker, $e.Date, $e.Reason, $e.Excess, $e.Raw, $e.BenchN, $vs)
    }
    Emit ""
}

Emit "---- accounting (nothing is dropped without being counted) ----"
foreach ($k in $c.Keys) { Emit ("  {0,-26}{1}" -f $k, $c[$k]) }
if ($missingPrice.Count -gt 0) {
    Emit ("  tickers with no price series: {0}" -f (($missingPrice.Keys | Sort-Object) -join ', '))
}
Emit ""
Emit "A filing with no recorded net value is kept and shown as (not recorded); it is never counted as 0."
Emit "An incomplete forward window is counted above, never treated as a zero move. A thin benchmark is"
Emit "visible via the per-event benchmark n rather than being silently averaged away."

if ($OutFile) {
    [System.IO.File]::WriteAllText($outFull, ($lines -join [System.Environment]::NewLine) + [System.Environment]::NewLine)
    Write-Host ""
    Write-Host ("Report written to {0}" -f $outFull)
}
