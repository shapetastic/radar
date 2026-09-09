<#
.SYNOPSIS
    Read-only diagnosis of ONE (company, score date) divergence: what Radar had actually read by the time it
    scored, what it concluded, and what only arrived afterwards.

.DESCRIPTION
    audit-score-vs-price-misses.ps1 FINDS the rows where the score and the forward price move disagreed. It
    stops there, on purpose. This script takes one of those rows and reconstructs the provenance behind it:

        1. THE DIVERGENCE          the score, its within-date percentile, and the forward move that followed.
        2. WHAT REACHED THE SCORE  every signal the score snapshot links to, with the type, direction,
                                   strength and confidence recorded on the link itself - and, on the same
                                   line, the article/filing behind it (published date, reason, title).
        3. WHAT THE JUDGE CONCLUDED  the news judgment(s) available at score time: BusinessTrajectory,
                                     status, and the cited rationale verbatim.
        4. WHAT ARRIVED AFTER THE SCORE  evidence for this company published AFTER the score date but inside
                                   the forward window. This is the discriminator that matters: news Radar
                                   could NOT have seen is not a Radar failure, and news it COULD have seen is.
        5. OBSERVATIONS            mechanical conditions only, each one checkable against the sections above.

    The numbering above IS the rendered section numbering - the two must not drift.

    IT DIAGNOSES, IT DOES NOT VERDICT. Every line is a recorded fact plus its source; the OBSERVATIONS
    section states only mechanical, deterministic conditions (for example "every contributing signal was
    Neutral", or "no judgment covered this window"). It never says a company was a buy, a sell, cheap, or
    mispriced, and it never says a strategy is good or bad - AD-9 and AD-14 both hold here. Price is
    validation-only and nothing this script reads or writes feeds scoring.

    WHAT IT CANNOT KNOW is stated rather than guessed. A link whose evidence file cannot be resolved, a
    contributionReason it cannot parse, a month outside the scanned range - each is COUNTED and reported.
    An empty section prints "(none recorded)", never a silent blank, because an absent record and a
    measured zero are different facts.

    STRICTLY READ-ONLY over the data root, sharing scripts/lib/radar-audit-common.ps1 with the finder so
    the two cannot drift on path safety, the (company, scoreDate) collapse, or the forward-return anchor.
    PowerShell 5.1-compatible.

.PARAMETER Ticker
    The company to diagnose, e.g. HZO. Resolved to a companyId through {DataRoot}/companies.json.

.PARAMETER ScoreDate
    The score date to diagnose, yyyy-MM-dd - the scoreDate column from the finder's table.

.PARAMETER DataRoot
    The durable store root. Default: 'data' beside the scripts folder.

.PARAMETER Series
    The strategy/series key whose snapshot to diagnose. Default: 'default' (the storage primary).

.PARAMETER ForwardSessions
    Trading sessions in the forward window. Default: 21 - keep it equal to the finder's setting, or the
    move reported here will not be the move that flagged the row.

.PARAMETER MaxItems
    Maximum rows to print per list section. The remainder is COUNTED and named as withheld, never dropped
    silently. Default: 25.

.PARAMETER OutFile
    Optional path to also write the rendered report to. Must NOT be at or inside -DataRoot.

.EXAMPLE
    powershell -File scripts/audit-miss-diagnosis.ps1 -Ticker HZO -ScoreDate 2026-07-31

.EXAMPLE
    powershell -File scripts/audit-miss-diagnosis.ps1 -Ticker AGX -ScoreDate 2026-07-30 -MaxItems 50
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Ticker,
    [Parameter(Mandatory=$true)][string]$ScoreDate,
    [string]$DataRoot = '',
    [string]$Series = 'default',
    [ValidateRange(1, 5000)][int]$ForwardSessions = 21,
    [ValidateRange(1, 100000)][int]$MaxItems = 25,
    [string]$OutFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$ScriptDir = $PSScriptRoot
if (-not $ScriptDir -and $PSCommandPath) { $ScriptDir = Split-Path -Parent $PSCommandPath }
if (-not $ScriptDir -and $MyInvocation.MyCommand.Path) { $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ScriptDir) { throw "Could not determine script directory; pass -DataRoot explicitly." }
if (-not $DataRoot) { $DataRoot = Join-Path (Split-Path $ScriptDir -Parent) 'data' }

. (Join-Path $ScriptDir 'lib\radar-audit-common.ps1')

$paths = Resolve-RadarAuditPaths -DataRoot $DataRoot -OutFile $OutFile -RequiredSubdirectories @('efficacy', 'prices', 'scores', 'evidence')
$DataRootFull = $paths.DataRoot
$outFull      = $paths.OutFile

$tickerUpper = $Ticker.Trim().ToUpperInvariant()
$parsedDate = [datetime]::MinValue
if (-not [datetime]::TryParseExact($ScoreDate, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::None, [ref]$parsedDate)) {
    throw "-ScoreDate '$ScoreDate' is not a yyyy-MM-dd date."
}
$scoreDateIso = $parsedDate.ToString('yyyy-MM-dd')

$lines = New-Object System.Collections.Generic.List[string]
function Emit([string]$s) { $lines.Add($s); Write-Host $s }
function EmitNone() { Emit "  (none recorded)" }

$c = [ordered]@{
    SnapshotsForCompany        = 0
    SnapshotsUnreadable        = 0
    SnapshotsOnScoreDate       = 0
    SnapshotsWrongSeries       = 0
    LinksTotal                 = 0
    LinksReasonUnparsed        = 0
    LinksEvidenceUnresolved    = 0
    LinksZeroWeight            = 0
    EvidenceFilesScanned       = 0
    EvidenceFilesUnreadable    = 0
    EvidenceMonthsMissing      = 0
    ForwardEvidenceForCompany  = 0
    JudgmentFilesFound         = 0
    JudgmentFilesUnreadable    = 0
    JudgmentsInWindow          = 0
    PriceFilesUnreadable       = 0
    PriceFilesTooShort         = 0
    PriceFilesDuplicateTicker  = 0
    BarsUnusable               = 0
    BarDatesDuplicated         = 0
    ItemsWithheldByMaxItems    = 0
    ForwardAnchorGapDays       = 0
}

# --- resolve the company -------------------------------------------------------------------------------
$companiesFile = Join-Path $DataRootFull 'companies.json'
if (-not (Test-Path -LiteralPath $companiesFile)) { throw "companies.json not found: $companiesFile" }
$companyDoc = Get-Content -LiteralPath $companiesFile -Raw -Encoding UTF8 | ConvertFrom-Json
$companyList = if ($companyDoc -is [array]) { $companyDoc }
               elseif ($companyDoc.PSObject.Properties.Match('companies').Count) { $companyDoc.companies }
               else { throw "Unrecognised companies.json shape: expected an array or a 'companies' property." }
$company = $null
foreach ($cand in $companyList) {
    if ($cand.PSObject.Properties.Match('ticker').Count -and $cand.ticker -and
        ([string]$cand.ticker).ToUpperInvariant() -eq $tickerUpper) { $company = $cand; break }
}
if ($null -eq $company) { throw "Ticker '$tickerUpper' is not in the seeded universe ($companiesFile)." }
$companyId = [string]$company.id

# --- the divergence: score, within-date percentile, forward move ----------------------------------------
# The cohort and the collapse rule come from the SHARED library, so the percentile printed here is the same
# number the finder printed - a diagnosis that disagreed with the row it explains would be worse than none.
$notPerCompany = New-Object System.Collections.Generic.List[string]
$rowCounters = [ordered]@{
    CsvFiles = 0; CsvFilesNotPerCompany = 0; CsvUnreadable = 0; RowsRead = 0; RowsWrongSeries = 0
    RowsUnparseable = 0; RowsDuplicatePairCollapsed = 0; RowsDuplicatePairDivergent = 0
}
$rows   = Import-RadarScoreRows -EfficacyDirectory $paths.efficacy -Series $Series -Counters $rowCounters -NonPerCompanyFiles $notPerCompany
$byDate = Group-RadarScoreRowsByDate $rows
$prices = Import-RadarPriceSeries -PricesDirectory $paths.prices -Counters $c

$row = $null
foreach ($r in $rows) { if ($r.Ticker -eq $tickerUpper -and $r.ScoreDate -eq $scoreDateIso) { $row = $r; break } }

$cohortCount = 0
$percentile  = $null
if ($byDate.ContainsKey($scoreDateIso)) {
    $cohortCount = $byDate[$scoreDateIso].Count
    if ($null -ne $row) { $percentile = Get-RadarCohortPercentile -Cohort $byDate[$scoreDateIso] -Value $row.Opportunity }
}
$move = if ($prices.ContainsKey($tickerUpper)) {
            Get-RadarForwardMove -Series $prices[$tickerUpper] -ScoreDate $scoreDateIso -ForwardSessions $ForwardSessions
        } else { [pscustomobject]@{ Status = 'NoPriceFile' } }

# --- the score snapshot that produced the row -----------------------------------------------------------
# Same rule as the finder's collapse: the LAST snapshot created on that date wins, because a date can carry
# several scoring passes and the last one is the row the finder ranked.
$snapshotDir = Join-Path $paths.scores $companyId
$snapshot = $null
if (Test-Path -LiteralPath $snapshotDir) {
    $candidates = New-Object System.Collections.Generic.List[object]
    foreach ($sf in (Get-ChildItem -LiteralPath $snapshotDir -Filter '*.json' -File | Sort-Object Name)) {
        $c.SnapshotsForCompany++
        try { $s = Get-Content -LiteralPath $sf.FullName -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { $c.SnapshotsUnreadable++; continue }
        if ($s.PSObject.Properties.Match('createdAtUtc').Count -eq 0 -or -not $s.createdAtUtc) { $c.SnapshotsUnreadable++; continue }
        $created = [datetime]::Parse([string]$s.createdAtUtc, [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        if ($created.ToString('yyyy-MM-dd') -ne $scoreDateIso) { continue }
        $c.SnapshotsOnScoreDate++
        $snapStrategy = if ($s.PSObject.Properties.Match('strategyName').Count -and $s.strategyName) { [string]$s.strategyName } else { 'default' }
        if ($snapStrategy.ToLowerInvariant() -ne $Series.ToLowerInvariant()) { $c.SnapshotsWrongSeries++; continue }
        $candidates.Add([pscustomobject]@{ Created = $created; Doc = $s }) | Out-Null
    }
    $ordered = @($candidates | Sort-Object -Property Created)
    if ($ordered.Count -gt 0) { $snapshot = $ordered[$ordered.Count - 1].Doc }
}

# --- the links the score actually carries ---------------------------------------------------------------
$links = New-Object System.Collections.Generic.List[object]
$neededEvidence = @{}
if ($null -ne $snapshot -and $snapshot.PSObject.Properties.Match('links').Count -and $snapshot.links) {
    foreach ($l in $snapshot.links) {
        $c.LinksTotal++
        $reason = if ($l.PSObject.Properties.Match('contributionReason').Count) { [string]$l.contributionReason } else { '' }
        $evId   = if ($l.PSObject.Properties.Match('evidenceId').Count) { [string]$l.evidenceId } else { '' }
        $weight = if ($l.PSObject.Properties.Match('contributionWeight').Count -and $null -ne $l.contributionWeight) { [double]$l.contributionWeight } else { [double]::NaN }
        if (-not [double]::IsNaN($weight) -and $weight -eq 0) { $c.LinksZeroWeight++ }
        # "Type (Direction), strength N, confidence N.NN [(collapsed K same-event media items)]".
        # An unparsed reason is COUNTED and still printed verbatim - it is a recorded fact either way.
        $type = $null; $dir = $null; $strength = $null; $conf = $null
        if ($reason -match '^(?<type>[A-Za-z]+)\s+\((?<dir>[A-Za-z]+)\),\s*strength\s+(?<s>\d+),\s*confidence\s+(?<c>[0-9.]+)') {
            $type = $Matches['type']; $dir = $Matches['dir']; $strength = [int]$Matches['s']; $conf = [double]$Matches['c']
        } else { $c.LinksReasonUnparsed++ }
        if ($evId) { $neededEvidence[$evId] = $true }
        $links.Add([pscustomobject]@{
            EvidenceId = $evId; Reason = $reason; Weight = $weight
            Type = $type; Direction = $dir; Strength = $strength; Confidence = $conf
        }) | Out-Null
    }
}

# --- evidence scan --------------------------------------------------------------------------------------
# Evidence is filed under {sourceType}/{yyyy}/{MM}/{contentHash}.json - the file NAME is the content hash,
# not the evidenceId, so the id must be matched from the content. Only the months the window can touch are
# scanned; a month directory that does not exist is COUNTED, so a short scan can never masquerade as an
# empty result.
function Get-MonthRange([datetime]$startUtc, [datetime]$endUtc) {
    $months = New-Object System.Collections.Generic.List[string]
    $cursor = [datetime]::new($startUtc.Year, $startUtc.Month, 1)
    $last   = [datetime]::new($endUtc.Year, $endUtc.Month, 1)
    while ($cursor -le $last) {
        # Built from two separate components, NOT a 'yyyy\MM' format string: in a .NET format string the
        # backslash ESCAPES the next character, so 'yyyy\MM' renders as "2026M7" and silently matches no
        # month directory at all - which the EvidenceMonthsMissing counter is what caught.
        $months.Add(($cursor.ToString('yyyy') + [System.IO.Path]::DirectorySeparatorChar + $cursor.ToString('MM'))) | Out-Null
        $cursor = $cursor.AddMonths(1)
    }
    return $months
}

$windowStart = if ($null -ne $snapshot -and $snapshot.PSObject.Properties.Match('windowStartUtc').Count -and $snapshot.windowStartUtc) {
                   [datetime]::Parse([string]$snapshot.windowStartUtc, [System.Globalization.CultureInfo]::InvariantCulture,
                       [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
               } else { $parsedDate.AddDays(-90) }
$scanEnd = if ($move.Status -eq 'Ok') { [datetime]::ParseExact($move.WindowEnd, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture) }
           else { $parsedDate.AddDays(45) }

$evidenceRoot = Join-Path $paths.evidence 'raw'
$linkedEvidence = @{}
$forwardEvidence = New-Object System.Collections.Generic.List[object]
$tickerToken = '"' + $tickerUpper + '"'
if (Test-Path -LiteralPath $evidenceRoot) {
    foreach ($typeDir in (Get-ChildItem -LiteralPath $evidenceRoot -Directory | Sort-Object Name)) {
        foreach ($rel in (Get-MonthRange $windowStart $scanEnd)) {
            $monthDir = Join-Path $typeDir.FullName $rel
            if (-not (Test-Path -LiteralPath $monthDir)) { $c.EvidenceMonthsMissing++; continue }
            foreach ($ef in (Get-ChildItem -LiteralPath $monthDir -Filter '*.json' -File | Sort-Object Name)) {
                $c.EvidenceFilesScanned++
                try { $raw = [System.IO.File]::ReadAllText($ef.FullName) } catch { $c.EvidenceFilesUnreadable++; continue }
                $isLinked  = $false
                foreach ($needed in $neededEvidence.Keys) { if ($raw.Contains($needed)) { $isLinked = $true; break } }
                $mentions = $raw.Contains($tickerToken)
                if (-not $isLinked -and -not $mentions) { continue }
                try { $doc = $raw | ConvertFrom-Json } catch { $c.EvidenceFilesUnreadable++; continue }
                $evId = if ($doc.PSObject.Properties.Match('evidenceId').Count) { [string]$doc.evidenceId } else { '' }
                $published = if ($doc.PSObject.Properties.Match('publishedAt').Count -and $doc.publishedAt) { [string]$doc.publishedAt } else { '' }
                $item = [pscustomobject]@{
                    EvidenceId = $evId
                    SourceType = if ($doc.PSObject.Properties.Match('sourceType').Count) { [string]$doc.sourceType } else { '(not recorded)' }
                    SourceName = if ($doc.PSObject.Properties.Match('sourceName').Count -and $doc.sourceName) { [string]$doc.sourceName } else { '(not recorded)' }
                    Title      = if ($doc.PSObject.Properties.Match('title').Count -and $doc.title) { [string]$doc.title } else { '(no title recorded)' }
                    Published  = if ($published) { $published.Substring(0, [Math]::Min(10, $published.Length)) } else { '(not recorded)' }
                }
                if ($evId -and $neededEvidence.ContainsKey($evId)) { $linkedEvidence[$evId] = $item }
                # "Arrived too late" is judged on the PUBLISHED date, strictly after the score date and no
                # later than the forward window end - news Radar could not have seen when it scored.
                if ($mentions -and $item.Published -ne '(not recorded)' -and
                    $item.Published -gt $scoreDateIso -and
                    ($move.Status -ne 'Ok' -or $item.Published -le $move.WindowEnd)) {
                    $c.ForwardEvidenceForCompany++
                    $forwardEvidence.Add($item) | Out-Null
                }
            }
        }
    }
}
foreach ($needed in $neededEvidence.Keys) { if (-not $linkedEvidence.ContainsKey($needed)) { $c.LinksEvidenceUnresolved++ } }

# --- what the judge concluded ---------------------------------------------------------------------------
$judgments = New-Object System.Collections.Generic.List[object]
$judgmentRoot = Join-Path $DataRootFull 'news-risk\judgments'
if (Test-Path -LiteralPath $judgmentRoot) {
    foreach ($modelDir in (Get-ChildItem -LiteralPath $judgmentRoot -Directory | Sort-Object Name)) {
        $companyDir = Join-Path $modelDir.FullName $companyId
        if (-not (Test-Path -LiteralPath $companyDir)) { continue }
        foreach ($jf in (Get-ChildItem -LiteralPath $companyDir -Filter '*.json' -File | Sort-Object Name)) {
            $c.JudgmentFilesFound++
            try { $j = Get-Content -LiteralPath $jf.FullName -Raw -Encoding UTF8 | ConvertFrom-Json }
            catch { $c.JudgmentFilesUnreadable++; continue }
            if ($j.PSObject.Properties.Match('createdAtUtc').Count -eq 0 -or -not $j.createdAtUtc) { $c.JudgmentFilesUnreadable++; continue }
            $made = [datetime]::Parse([string]$j.createdAtUtc, [System.Globalization.CultureInfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
            # In window = made no later than the score date: a judgment recorded afterwards was NOT available
            # to this score, and reporting it as an input would invent provenance the score never had.
            if ($made -gt $parsedDate.AddDays(1).AddSeconds(-1)) { continue }
            if ($made -lt $windowStart.AddDays(-1)) { continue }
            $c.JudgmentsInWindow++
            $judgments.Add([pscustomobject]@{
                Made       = $made.ToString('yyyy-MM-dd')
                Trajectory = if ($j.PSObject.Properties.Match('businessTrajectory').Count -and $j.businessTrajectory) { [string]$j.businessTrajectory } else { '(not recorded)' }
                Status     = if ($j.PSObject.Properties.Match('status').Count -and $j.status) { [string]$j.status } else { '(not recorded)' }
                Rationale  = if ($j.PSObject.Properties.Match('rationale').Count -and $j.rationale) { [string]$j.rationale } else { '(not recorded)' }
                Findings   = if ($j.PSObject.Properties.Match('findingsAccepted').Count -and $null -ne $j.findingsAccepted) { [int]$j.findingsAccepted } else { $null }
                Dropped    = if ($j.PSObject.Properties.Match('findingsDropped').Count -and $null -ne $j.findingsDropped) { [int]$j.findingsDropped } else { $null }
            }) | Out-Null
        }
    }
}

# --- render -----------------------------------------------------------------------------------------------
function Show-Limited($items, [string]$label) {
    $shown = 0
    foreach ($i in $items) {
        if ($shown -ge $MaxItems) { break }
        Emit $i
        $shown++
    }
    $withheld = $items.Count - $shown
    if ($withheld -gt 0) {
        $c.ItemsWithheldByMaxItems += $withheld
        Emit ("  ... {0} further {1} withheld by -MaxItems {2} (counted, not dropped)" -f $withheld, $label, $MaxItems)
    }
}

Emit ""
Emit "==== Why did this row go wrong? - point-in-time diagnosis ===="
Emit ("Company         : {0} ({1})" -f $company.name, $tickerUpper)
Emit ("Score date      : {0}" -f $scoreDateIso)
Emit ("Series          : {0}" -f $Series)
Emit ("DataRoot        : {0}" -f $DataRootFull)
Emit ""
Emit "PRICE IS VALIDATION-ONLY (AD-14). This explains a recorded divergence; it is not advice, not an"
Emit "efficacy result, and carries no verdict about the company or the strategy."
Emit ""

Emit "---- 1. THE DIVERGENCE ----"
if ($null -eq $row) {
    Emit ("  No accrued efficacy row for {0} on {1} in series '{2}'." -f $tickerUpper, $scoreDateIso, $Series)
} else {
    Emit ("  Opportunity score      : {0:N0}" -f $row.Opportunity)
    if ($null -eq $percentile) { Emit "  Within-date percentile : (cohort not recorded)" }
    else { Emit ("  Within-date percentile : {0:N0} (cohort: {1} companies scored that day)" -f $percentile, $cohortCount) }
    Emit ("  Scoring identity       : {0}" -f $(if ($row.ConfigVer) { $row.ConfigVer } else { '(not recorded)' }))
}
if ($move.Status -eq 'Ok') {
    Emit ("  Forward {0,2} sessions   : {1:N1}%  ({2} {3:N2} -> {4} {5:N2})" -f $ForwardSessions, $move.ForwardPct, $move.AnchorDate, $move.AnchorClose, $move.WindowEnd, $move.EndClose)
    # A weekend/holiday gap is a few days; anything larger means the score date sits before the price
    # history and this is NOT the move that followed the score. Said out loud rather than left to a reader
    # who happens to compare two dates in the line above.
    if ($move.AnchorGapDays -gt 7) {
        $c.ForwardAnchorGapDays = $move.AnchorGapDays
        Emit ("  !! ANCHOR IS {0} DAYS AFTER THE SCORE DATE - the price series does not cover {1}, so this is" -f $move.AnchorGapDays, $scoreDateIso)
        Emit  "     the move that followed the FIRST AVAILABLE BAR, not the move that followed the score."
    }
} else {
    Emit ("  Forward {0,2} sessions   : not measurable ({1})" -f $ForwardSessions, $move.Status)
}
Emit ""

Emit "---- 2. WHAT REACHED THE SCORE ----"
if ($null -eq $snapshot) {
    Emit ("  No '{0}' score snapshot recorded for this company on this date." -f $Series)
} else {
    Emit ("  {0}" -f [string]$snapshot.explanation)
    $byDirection = @{}
    foreach ($l in $links) {
        $k = if ($l.Direction) { $l.Direction } else { '(unparsed)' }
        if (-not $byDirection.ContainsKey($k)) { $byDirection[$k] = 0 }
        $byDirection[$k]++
    }
    if ($byDirection.Count -eq 0) { Emit "  Contributing links     : (none recorded)" }
    else {
        $parts = @($byDirection.Keys | Sort-Object | ForEach-Object { "{0} {1}" -f $byDirection[$_], $_ })
        Emit ("  Contributing links     : {0} total - {1}" -f $links.Count, ($parts -join ', '))
    }
    $rendered = New-Object System.Collections.Generic.List[string]
    foreach ($l in ($links | Sort-Object -Property @{Expression={$_.Type}}, @{Expression={$_.Direction}}, EvidenceId)) {
        $ev = if ($l.EvidenceId -and $linkedEvidence.ContainsKey($l.EvidenceId)) { $linkedEvidence[$l.EvidenceId] } else { $null }
        $when  = if ($null -ne $ev) { $ev.Published } else { '(unresolved)' }
        $title = if ($null -ne $ev) { $ev.Title } else { '(evidence file not found in scanned months)' }
        if ($title.Length -gt 78) { $title = $title.Substring(0, 75) + '...' }
        $rendered.Add(("    {0,-12} {1,-52} {2}" -f $when, $l.Reason, $title)) | Out-Null
    }
    if ($rendered.Count -eq 0) { EmitNone } else { Show-Limited $rendered 'link(s)' }
}
Emit ""
Emit "---- 3. WHAT THE JUDGE CONCLUDED (news judgments available at score time) ----"
if ($judgments.Count -eq 0) { EmitNone }
else {
    foreach ($j in ($judgments | Sort-Object -Property Made)) {
        $findings = if ($null -eq $j.Findings) { '(not recorded)' } else { [string]$j.Findings }
        $dropped  = if ($null -eq $j.Dropped)  { '(not recorded)' } else { [string]$j.Dropped }
        Emit ("  {0}  trajectory={1}  status={2}  findingsAccepted={3}  findingsDropped={4}" -f $j.Made, $j.Trajectory, $j.Status, $findings, $dropped)
        # The rationale is printed VERBATIM and wrapped, never summarised: it is the judge's cited reasoning
        # and a paraphrase here would be a second, unattributable claim.
        $text = $j.Rationale
        while ($text.Length -gt 0) {
            $take = [Math]::Min(104, $text.Length)
            if ($take -lt $text.Length) {
                $cut = $text.LastIndexOf(' ', $take - 1)
                if ($cut -gt 40) { $take = $cut }
            }
            Emit ("      {0}" -f $text.Substring(0, $take).Trim())
            $text = $text.Substring($take).TrimStart()
        }
    }
}
Emit ""

Emit "---- 4. WHAT ARRIVED AFTER THE SCORE ----"
# The upper bound is NAMED, because it is not always the forward window: when the move could not be
# measured there IS no window, and the list is bounded by the scanned months instead. Printing "inside the
# forward window" in that case would describe a bound that was never applied.
if ($move.Status -eq 'Ok') { Emit ("  Bounded by the forward window: published after {0}, up to and including {1}." -f $scoreDateIso, $move.WindowEnd) }
else { Emit ("  No forward window could be measured ({0}), so this list is bounded by the scanned months only." -f $move.Status) }
Emit "  Radar could NOT have read these when it scored. News here explains the move without implicating"
Emit "  the score. An EMPTY list is NOT evidence that nothing drove the move: it means only that no item"
Emit "  carrying this ticker hint, in the months scanned below, was published inside the window."
if ($forwardEvidence.Count -eq 0) { EmitNone }
else {
    $rendered2 = New-Object System.Collections.Generic.List[string]
    foreach ($e in ($forwardEvidence | Sort-Object -Property Published, SourceName, EvidenceId)) {
        $title = $e.Title
        if ($title.Length -gt 92) { $title = $title.Substring(0, 89) + '...' }
        $rendered2.Add(("    {0,-12} {1,-22} {2}" -f $e.Published, $e.SourceName, $title)) | Out-Null
    }
    Show-Limited $rendered2 'later item(s)'
}
Emit ""

# --- observations: MECHANICAL conditions only, each one checkable against the sections above -------------
Emit "---- 5. OBSERVATIONS (mechanical, no verdict) ----"
$obs = New-Object System.Collections.Generic.List[string]
if ($null -eq $snapshot) {
    $obs.Add("  * No score snapshot for this date/series: nothing above can be attributed to a scoring pass.") | Out-Null
} else {
    # Reported as a SHARE, not only at 100%: "11 of 12 Neutral" is the same finding as "all Neutral" and
    # an all-or-nothing test would have stayed silent on it.
    $neutralCount = @($links | Where-Object { $_.Direction -eq 'Neutral' }).Count
    if ($links.Count -gt 0 -and $neutralCount -gt 0) {
        $share = 100.0 * $neutralCount / $links.Count
        if ($share -ge 75) {
            $obs.Add(("  * {0} of {1} contributing signal(s) were Neutral ({2:N0}%): the score moved on news VOLUME, not direction." -f $neutralCount, $links.Count, $share)) | Out-Null
        }
    }
    if ($links.Count -gt 0 -and $c.LinksZeroWeight -eq $links.Count) {
        $obs.Add(("  * Every link recorded contributionWeight 0, so no single link is attributable as the driver.")) | Out-Null
    }
    if ($links.Count -eq 0) { $obs.Add("  * The snapshot carries NO evidence links: this score has no traceable contributors.") | Out-Null }
}
if ($c.JudgmentsInWindow -eq 0) {
    # "None available" and "none exist" are DIFFERENT facts, and conflating them would misattribute a
    # timing gap to a missing subsystem. The stored-but-later count is named.
    if ($c.JudgmentFilesFound -gt 0) {
        $obs.Add(("  * No news judgment was available at score time, though {0} exist for this company - all recorded AFTER this score date, so the AI read could not have informed it." -f $c.JudgmentFilesFound)) | Out-Null
    } else {
        $obs.Add("  * No news judgment exists for this company at all: the AI read did not reach this score.") | Out-Null
    }
} else {
    $traj = @($judgments | Select-Object -ExpandProperty Trajectory -Unique | Sort-Object)
    $obs.Add(("  * {0} judgment(s) available at score time, trajectory: {1}." -f $c.JudgmentsInWindow, ($traj -join ', '))) | Out-Null
    if ($null -ne $percentile) {
        if ($traj -contains 'Improving' -and $percentile -le 40) {
            $obs.Add(("  * A judged-Improving trajectory sat at the {0:N0}th percentile: the judgment did not lift the rank." -f $percentile)) | Out-Null
        }
        if ($traj -contains 'Deteriorating' -and $percentile -ge 70) {
            $obs.Add(("  * A judged-Deteriorating trajectory sat at the {0:N0}th percentile: the judgment did not lower the rank." -f $percentile)) | Out-Null
        }
    }
}
if ($c.ForwardEvidenceForCompany -gt 0) {
    $obs.Add(("  * {0} item(s) mentioning {1} were published AFTER the score date and inside the forward window." -f $c.ForwardEvidenceForCompany, $tickerUpper)) | Out-Null
} else {
    # Stated as a property of THIS SCAN, not of the store: the match is a ticker-hint heuristic over a
    # bounded month range, so "found none" and "none exist" are different facts and must read differently.
    $obs.Add(("  * No item carrying the {0} ticker hint was found after the score date in the scanned months; this scan does not explain the move (it does not establish that nothing does)." -f $tickerUpper)) | Out-Null
}
if ($c.LinksEvidenceUnresolved -gt 0) {
    $obs.Add(("  * {0} link(s) point at evidence not found in the scanned months: their titles are unknown, not absent." -f $c.LinksEvidenceUnresolved)) | Out-Null
}
foreach ($o in $obs) { Emit $o }
Emit ""

Emit "---- accounting (nothing is dropped without being counted) ----"
foreach ($k in $c.Keys) { Emit ("  {0,-28}{1}" -f $k, $c[$k]) }
Emit ""
Emit "Evidence is matched to this company by its recorded ticker hint; an item filed under a different hint"
Emit "would not appear above. Section 4 is bounded by the scanned months named in the accounting."

if ($outFull) {
    Write-RadarAuditReport -Path $outFull -Lines $lines
    Write-Host ""
    Write-Host ("Report written to {0}" -f $outFull)
}
