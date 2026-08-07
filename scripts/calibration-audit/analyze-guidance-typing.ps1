<#
.SYNOPSIS
    Spec 173 - GuidanceChange mis-typing audit: a read-only, one-shot measurement of how much live
    Opportunity score rests on `GuidanceChange` signals whose rationale never states a guidance action.

.DESCRIPTION
    `DirectionalFilingSignalSource` hardcodes every passing AI read to SignalType "GuidanceChange"
    (spec 168's deferred target). This script classifies EVERY GuidanceChange signal in the accrued
    store by its rationale (the signal's `reason` field), splits the result by origin (AI read vs the
    deterministic KeywordSignalExtractor), and weights it by the live primary-strategy score snapshots:
    for the most recent as-of date per company it finds the highest-contribution directional signal and
    reports which companies - and how many of the current top 10 by Opportunity - rest on a
    `results-only`-classified GuidanceChange.

    READ-ONLY over the store: writes ONLY under {DataRoot}/guidance-typing-audit/. No signal is
    retyped, deleted or superseded; no score is recomputed; no network; no production code.

    ORIGIN SPLIT RULE (recorded verbatim in the artifacts): reasons written by the deterministic
    KeywordSignalExtractor begin with the literal  Matched phrase '  ; anything else is AI-read prose
    from DirectionalFilingSignalSource.

    CLASSIFICATION RULE (spec 168 section 5, quoted verbatim in the artifacts): "a `GuidanceChange`
    phrase must state a guidance/outlook ACTION (raise / cut / lower / withdraw of guidance or
    outlook) — not merely mention results near the word." Spec 173 extends the action list to:
    raise / cut / lower / withdraw / introduce / reaffirm. The concrete deterministic rule implemented
    here (printed verbatim into the artifacts so it is reproducible):
      - results-only     : the rationale matches neither 'guidance' nor 'outlook' (case-insensitive).
      - guidance-action  : an action token occurs within 4 words of 'guidance' or 'outlook', in either
                           order (the exact regex is printed in the output).
      - ambiguous        : mentions 'guidance' or 'outlook' but no action token per the rule.
    The extractor's matched phrases run through the SAME classifier (that is the point of the
    origin x classification split): "Matched phrase 'results of operations'" contains neither word
    => results-only; "Matched phrase 'raises guidance'" => guidance-action.

    FIXTURES (spec 173 section 3): the UFPT and IOSP signals from the two live skeptic reviews are
    pinned by signalId and MUST classify results-only; otherwise the classifier is wrong, the
    aggregate is worthless, and this script exits 1 WITHOUT writing artifacts.

    HARD LIMITATION (stated in the rendered output, per the spec): Radar does not store filing text -
    for an 8-K the persisted rawText is the EDGAR index summary (item codes/titles), so this audit
    classifies the AI RATIONALE, not the filing. A rationale that never mentions guidance is strong
    evidence but not proof about the underlying document.

    DETERMINISM (AD-3): identical store => byte-identical artifacts. Invariant culture for all number
    formatting, ordinal sorts everywhere, no run wall-clock timestamps in the artifacts.

.PARAMETER DataRoot
    Root of the accrued store (the directory holding signals/, scores/, companies.json). Default: the
    repo-relative 'data' directory resolved from this script's location, like the sibling audit
    scripts. Point it at the live store to measure: -DataRoot C:\Users\...\repos\radar\data

.EXAMPLE
    pwsh -File scripts/calibration-audit/analyze-guidance-typing.ps1 -DataRoot C:\Users\scm9d\source\repos\radar\data
#>
[CmdletBinding()]
param(
    [string]$DataRoot = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Invariant culture for EVERY formatting operation ('-f' uses the current culture): two runs on any
# machine, any locale, must render the same bytes (AD-3).
[System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

# Default resolved here, not in the param block ($PSScriptRoot is not reliably populated during
# parameter-default evaluation) - repo root = two levels above scripts/calibration-audit/.
if ([string]::IsNullOrEmpty($DataRoot)) {
    $scriptParent = Split-Path -Parent $PSScriptRoot
    $repoRoot = if ([string]::IsNullOrEmpty($scriptParent)) { '' } else { Split-Path -Parent $scriptParent }
    if ([string]::IsNullOrEmpty($repoRoot)) {
        throw 'Cannot resolve a repo root from the script directory; pass -DataRoot explicitly.'
    }
    $DataRoot = Join-Path $repoRoot 'data'
}
$DataRoot = (Resolve-Path -LiteralPath $DataRoot).ProviderPath

$signalsDir = Join-Path $DataRoot 'signals'
$scoresDir = Join-Path $DataRoot 'scores'
$companiesPath = Join-Path $DataRoot 'companies.json'
if (-not (Test-Path -LiteralPath $signalsDir)) { throw "No signals directory at '$signalsDir' - point -DataRoot at the accrued store." }
if (-not (Test-Path -LiteralPath $scoresDir)) { throw "No scores directory at '$scoresDir' - point -DataRoot at the accrued store." }
if (-not (Test-Path -LiteralPath $companiesPath)) { throw "No companies.json at '$companiesPath' - point -DataRoot at the accrued store." }

# The ONLY directory this script ever writes to (read-only over everything else).
$outDir = Join-Path $DataRoot 'guidance-typing-audit'
$csvPath = Join-Path $outDir 'guidance-typing.csv'
$mdPath = Join-Path $outDir 'guidance-typing.md'

# --- helpers ---------------------------------------------------------------------------------------------

function Get-Prop {
    # StrictMode-safe optional-property read (trailing/optional JSON fields).
    param($Object, [string]$Name, $Default = $null)
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) {
        return $Object.PSObject.Properties[$Name].Value
    }
    return $Default
}

function Format-Pct {
    param([int]$Count, [int]$Total)
    if ($Total -le 0) { return 'n/a' }
    return ('{0:0.00}' -f (100.0 * $Count / $Total))
}

function Escape-MdCell {
    param([string]$Text)
    return $Text.Replace('|', '\|')
}

# --- the rules, verbatim (these exact strings are printed into both artifacts) ---------------------------

$Spec168RuleVerbatim = 'a `GuidanceChange` phrase must state a guidance/outlook ACTION (raise / cut / lower / withdraw of guidance or outlook) — not merely mention results near the word.'
$Spec173Extension = 'Spec 173 extends the action list to: raise / cut / lower / withdraw / introduce / reaffirm.'
$OriginRuleVerbatim = "reasons written by the deterministic KeywordSignalExtractor begin with the literal Matched phrase ' ; anything else is AI-read prose from DirectionalFilingSignalSource."

# Action token list. The six spec-173 action families plus ONE documented synonym family, 'boost'
# (common press-release phrasing for a guidance raise, e.g. 'boosts guidance'). Kept deliberately
# tight; the full alternation is printed verbatim below so the rule is reproducible.
$ActionAlternation = "rais(?:e|es|ed|ing)|cut(?:s|ting)?|lower(?:s|ed|ing)?|withdraw(?:s|ing|n)?|withdrew|introduc(?:e|es|ed|ing)|reaffirm(?:s|ed|ing)?|boost(?:s|ed|ing)?"
$MentionPattern = '(?i)guidance|outlook'
$ActionNearPattern = "(?i)\b(?:$ActionAlternation)\b(?:\W+\w+){0,4}\W+(?:guidance|outlook)\b|\b(?:guidance|outlook)\b(?:\W+\w+){0,4}\W+(?:$ActionAlternation)\b"
$ConcreteRuleText = "results-only: the rationale does not match the case-insensitive regex 'guidance|outlook'. guidance-action: the rationale matches the case-insensitive regex $ActionNearPattern (an action token within 4 words of guidance/outlook, either order). ambiguous: mentions guidance/outlook but the guidance-action regex does not match."

function Get-Classification {
    param([string]$Reason)
    if (-not [regex]::IsMatch($Reason, $MentionPattern)) { return 'results-only' }
    if ([regex]::IsMatch($Reason, $ActionNearPattern)) { return 'guidance-action' }
    return 'ambiguous'
}

# --- pass 1: scan every signal file; index signalId -> path; classify every GuidanceChange signal --------

Write-Output ("Scanning signals under {0} ..." -f $signalsDir)
$signalPaths = @([System.IO.Directory]::GetFiles($signalsDir, '*.json', [System.IO.SearchOption]::AllDirectories))
[Array]::Sort($signalPaths, [System.StringComparer]::Ordinal)
$totalSignalFiles = $signalPaths.Count

$signalPathById = @{}   # every signal on disk (filenames ARE the signalIds) - used to resolve score links
$signalCache = @{}      # signalId -> parsed signal object (GuidanceChange eagerly; others on demand)
$guidanceRows = New-Object System.Collections.Generic.List[object]
$guidanceById = @{}     # signalId -> classification row (GuidanceChange only)
$unreadableSignalFiles = 0

foreach ($path in $signalPaths) {
    $id = [System.IO.Path]::GetFileNameWithoutExtension($path)
    if (-not $signalPathById.ContainsKey($id)) { $signalPathById[$id] = $path }
    # ReadAllText detects and strips a UTF-8 BOM; only files containing the type token are parsed.
    $text = [System.IO.File]::ReadAllText($path)
    if ($text.IndexOf('"GuidanceChange"', [System.StringComparison]::Ordinal) -lt 0) { continue }
    try {
        $sig = ConvertFrom-Json -InputObject $text
    } catch {
        $unreadableSignalFiles++
        Write-Output ("  WARNING: unreadable signal file skipped: {0}" -f $path)
        continue
    }
    if ([string](Get-Prop $sig 'type' '') -ne 'GuidanceChange') { continue }
    $signalCache[$id] = $sig
    $reason = [string](Get-Prop $sig 'reason' '')
    $origin = if ($reason.StartsWith("Matched phrase '", [System.StringComparison]::Ordinal)) { 'extractor' } else { 'ai-read' }
    $row = [pscustomobject]@{
        SignalId       = $id
        CompanyId      = [string](Get-Prop $sig 'companyId' '')
        Origin         = $origin
        Classification = Get-Classification -Reason $reason
        Direction      = [string](Get-Prop $sig 'direction' '')
        Strength       = [int](Get-Prop $sig 'strength' 0)
        Confidence     = [double](Get-Prop $sig 'confidence' 0)
    }
    $guidanceRows.Add($row) | Out-Null
    $guidanceById[$id] = $row
}
Write-Output ("Signal files examined: {0}; GuidanceChange signals classified: {1}." -f $totalSignalFiles, $guidanceRows.Count)

# --- fixtures (spec 173 section 3): fail loudly BEFORE writing anything if either misclassifies ----------

$Fixtures = @(
    [pscustomobject]@{ Ticker = 'UFPT'; CompanyId = '3ba9066c-4280-42fd-8a7f-79045462c922'; SignalId = 'a62cbdf8-2d6f-4d58-807d-e69f19a2f7fe' },
    [pscustomobject]@{ Ticker = 'IOSP'; CompanyId = '1779a777-6a36-4d63-8d8b-7c5fe1ac860b'; SignalId = '3fdb6fe9-2b0a-4716-a5fa-540bc222c127' }
)
$fixtureResults = @()
$fixtureFailures = @()
foreach ($f in $Fixtures) {
    if (-not $guidanceById.ContainsKey($f.SignalId)) {
        $fixtureFailures += ("{0} fixture signal {1} was not found as a GuidanceChange signal in the store at '{2}'" -f $f.Ticker, $f.SignalId, $DataRoot)
        continue
    }
    $row = $guidanceById[$f.SignalId]
    if ($row.CompanyId -ne $f.CompanyId) {
        $fixtureFailures += ("{0} fixture signal {1} belongs to companyId '{2}', expected '{3}'" -f $f.Ticker, $f.SignalId, $row.CompanyId, $f.CompanyId)
        continue
    }
    if ($row.Classification -ne 'results-only') {
        $fixtureFailures += ("{0} fixture signal {1} classified '{2}', expected 'results-only' - the classifier is wrong and the aggregate is worthless" -f $f.Ticker, $f.SignalId, $row.Classification)
        continue
    }
    $fixtureResults += [pscustomobject]@{ Ticker = $f.Ticker; SignalId = $f.SignalId; Classification = $row.Classification; Origin = $row.Origin }
}
if ($fixtureFailures.Count -gt 0) {
    Write-Output ('FIXTURE VALIDATION FAILED - {0} failure(s); NO artifacts written:' -f $fixtureFailures.Count)
    foreach ($msg in $fixtureFailures) { Write-Output ('  FIXTURE: {0}' -f $msg) }
    exit 1
}
Write-Output 'Fixtures passed: UFPT and IOSP both classify results-only.'

# --- origin x classification matrix ----------------------------------------------------------------------

$Origins = @('ai-read', 'extractor')
$Classes = @('guidance-action', 'results-only', 'ambiguous')
$matrix = @{}
foreach ($o in $Origins) { foreach ($c in $Classes) { $matrix["$o|$c"] = 0 } }
foreach ($row in $guidanceRows) { $matrix["$($row.Origin)|$($row.Classification)"]++ }
$originTotals = @{}
foreach ($o in $Origins) {
    $t = 0
    foreach ($c in $Classes) { $t += $matrix["$o|$c"] }
    $originTotals[$o] = $t
}
$classTotals = @{}
foreach ($c in $Classes) {
    $t = 0
    foreach ($o in $Origins) { $t += $matrix["$o|$c"] }
    $classTotals[$c] = $t
}
$grandTotal = $guidanceRows.Count

# --- pass 2 (spec section 2): score-weighted view over the latest primary snapshot per company -----------

$companiesJson = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($companiesPath))
$companyById = @{}
foreach ($c in @(Get-Prop $companiesJson 'companies' @())) {
    $companyById[[string](Get-Prop $c 'id' '')] = $c
}

# Primary strategy = top-level {scores}/{companyId Guid} dirs ONLY; {scores}/strategies/{name}/... holds
# NON-primary strategies and is excluded (dir names there are strategy names, not Guids - the Guid parse
# is the filter).
$companyDirs = @([System.IO.Directory]::GetDirectories($scoresDir))
[Array]::Sort($companyDirs, [System.StringComparer]::Ordinal)

$latestPerCompany = New-Object System.Collections.Generic.List[object]
foreach ($dir in $companyDirs) {
    $companyId = [System.IO.Path]::GetFileName($dir)
    $parsedGuid = [Guid]::Empty
    if (-not [Guid]::TryParse($companyId, [ref]$parsedGuid)) { continue } # e.g. the 'strategies' subtree
    $snapPaths = @([System.IO.Directory]::GetFiles($dir, '*.json', [System.IO.SearchOption]::TopDirectoryOnly))
    [Array]::Sort($snapPaths, [System.StringComparer]::Ordinal)
    $best = $null
    $bestCreated = [DateTimeOffset]::MinValue
    $bestId = ''
    foreach ($snapPath in $snapPaths) {
        try {
            $snap = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($snapPath))
        } catch {
            Write-Output ("  WARNING: unreadable snapshot skipped: {0}" -f $snapPath)
            continue
        }
        $created = [DateTimeOffset]::Parse([string](Get-Prop $snap 'createdAtUtc' '0001-01-01T00:00:00+00:00'), [System.Globalization.CultureInfo]::InvariantCulture)
        $snapId = [string](Get-Prop $snap 'snapshotId' '')
        # Most recent createdAtUtc wins; deterministic tie-break: lowest snapshotId ordinal.
        if ($null -eq $best -or $created -gt $bestCreated -or ($created -eq $bestCreated -and [string]::CompareOrdinal($snapId, $bestId) -lt 0)) {
            $best = $snap; $bestCreated = $created; $bestId = $snapId
        }
    }
    if ($null -eq $best) { continue }
    $latestPerCompany.Add([pscustomobject]@{
        CompanyId   = $companyId
        Snapshot    = $best
        Opportunity = [int](Get-Prop $best 'opportunityScore' 0)
    }) | Out-Null
}

# Primary ranking: opportunityScore desc, companyId asc (AD-3). Sort-Object compares strings with the
# culture-aware comparer, so an explicit CompareOrdinal comparison is used instead - same rule as the
# [Array]::Sort ordinal calls above, and locale-proof even if the session culture is changed.
$ranked = $latestPerCompany.ToArray()
if ($ranked.Length -gt 1) {
    [Array]::Sort($ranked, [System.Comparison[object]]{
        param($a, $b)
        $byScore = ([int]$b.Opportunity).CompareTo([int]$a.Opportunity)
        if ($byScore -ne 0) { return $byScore }
        return [string]::CompareOrdinal([string]$a.CompanyId, [string]$b.CompanyId)
    })
}

$unresolvableLinks = 0
$companyReports = New-Object System.Collections.Generic.List[object]
$rank = 0
foreach ($entry in $ranked) {
    $rank++
    $links = @(Get-Prop $entry.Snapshot 'links' @())
    $resolved = @()
    foreach ($link in $links) {
        $sid = [string](Get-Prop $link 'signalId' '')
        $sig = $null
        if ($signalCache.ContainsKey($sid)) {
            $sig = $signalCache[$sid]
        } elseif ($signalPathById.ContainsKey($sid)) {
            try {
                $sig = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($signalPathById[$sid]))
                $signalCache[$sid] = $sig
            } catch {
                $sig = $null
            }
        }
        if ($null -eq $sig) { $unresolvableLinks++; continue } # link whose signal file is missing on disk
        $resolved += [pscustomobject]@{
            SignalId  = $sid
            Direction = [string](Get-Prop $sig 'direction' '')
            Type      = [string](Get-Prop $sig 'type' '')
            Weight    = [double](Get-Prop $link 'contributionWeight' 0)
            Signal    = $sig
        }
    }
    # Highest-contribution DIRECTIONAL signal: direction != Neutral (per the signal file's own
    # direction), max contributionWeight, tie-break signalId ordinal asc.
    $directional = @($resolved | Where-Object { $_.Direction -ne 'Neutral' -and -not [string]::IsNullOrEmpty($_.Direction) })
    $top = $null
    foreach ($cand in $directional) {
        if ($null -eq $top -or $cand.Weight -gt $top.Weight -or ($cand.Weight -eq $top.Weight -and [string]::CompareOrdinal($cand.SignalId, $top.SignalId) -lt 0)) {
            $top = $cand
        }
    }
    $company = $null
    if ($companyById.ContainsKey($entry.CompanyId)) { $company = $companyById[$entry.CompanyId] }
    $topClassification = ''
    $topOrigin = ''
    if ($null -ne $top -and $guidanceById.ContainsKey($top.SignalId)) {
        $topClassification = $guidanceById[$top.SignalId].Classification
        $topOrigin = $guidanceById[$top.SignalId].Origin
    }
    $companyReports.Add([pscustomobject]@{
        Rank              = $rank
        CompanyId         = $entry.CompanyId
        Name              = if ($null -ne $company) { [string](Get-Prop $company 'name' $entry.CompanyId) } else { $entry.CompanyId }
        Ticker            = if ($null -ne $company) { [string](Get-Prop $company 'ticker' '') } else { '' }
        Opportunity       = $entry.Opportunity
        LinkCount         = $links.Count
        Top               = $top
        TopType           = if ($null -ne $top) { $top.Type } else { '' }
        TopClassification = $topClassification
        TopOrigin         = $topOrigin
    }) | Out-Null
}

$restsOnResultsOnly = @($companyReports | Where-Object { $_.TopType -eq 'GuidanceChange' -and $_.TopClassification -eq 'results-only' })
$top10 = @($companyReports | Where-Object { $_.Rank -le 10 })
$top10Dependent = @($top10 | Where-Object { $_.TopType -eq 'GuidanceChange' -and $_.TopClassification -eq 'results-only' }).Count
$companiesWithDirectionalTop = @($companyReports | Where-Object { $null -ne $_.Top }).Count
$topIsGuidanceChange = @($companyReports | Where-Object { $_.TopType -eq 'GuidanceChange' }).Count

# --- render: markdown ------------------------------------------------------------------------------------

$md = New-Object System.Collections.Generic.List[string]
function Add-Line { param([string]$Text = '') $md.Add($Text) | Out-Null }

Add-Line '# GuidanceChange mis-typing audit (spec 173)'
Add-Line ''
Add-Line 'Read-only measurement over the accrued store: every `GuidanceChange` signal classified by its'
Add-Line 'rationale (the signal `reason` field), split by origin, then weighted by the live primary-strategy'
Add-Line 'score snapshots. No signal, type or score was changed; this artifact feeds - but does not satisfy -'
Add-Line "spec 168's un-defer gate."
Add-Line ''
Add-Line '## Headline'
Add-Line ''
Add-Line ('**{0} of the current top 10 by Opportunity depend on a results-only `GuidanceChange`** (their' -f $top10Dependent)
Add-Line 'highest-contribution directional signal is a `GuidanceChange` whose rationale never mentions'
Add-Line 'guidance or outlook).'
Add-Line ''
Add-Line '## Store examined'
Add-Line ''
Add-Line ('- Signal files examined: {0}' -f $totalSignalFiles)
Add-Line ('- `GuidanceChange` signals classified: {0} (ai-read {1}, extractor {2})' -f $grandTotal, $originTotals['ai-read'], $originTotals['extractor'])
Add-Line ('- Unreadable signal files skipped: {0}' -f $unreadableSignalFiles)
Add-Line ('- Companies with a primary score snapshot (ranked): {0}' -f $companyReports.Count)
Add-Line ('- Companies whose latest snapshot has a resolvable directional contributor: {0}' -f $companiesWithDirectionalTop)
Add-Line ('- Unresolvable score-snapshot links (signal file missing on disk; skipped): {0}' -f $unresolvableLinks)
Add-Line ''
Add-Line '## Origin split rule (verbatim)'
Add-Line ''
Add-Line ('> {0}' -f $OriginRuleVerbatim)
Add-Line ''
Add-Line '## Classification rule (verbatim)'
Add-Line ''
Add-Line ('Spec 168 section 5: "{0}"' -f $Spec168RuleVerbatim)
Add-Line ''
Add-Line $Spec173Extension
Add-Line ''
Add-Line 'Concrete deterministic rule implemented (applied identically to AI rationales and to the'
Add-Line 'extractor''s `Matched phrase ''...''` reasons):'
Add-Line ''
Add-Line ('- `results-only` - the rationale does not match the case-insensitive regex `guidance|outlook`.')
Add-Line ('- `guidance-action` - the rationale matches this case-insensitive regex (an action token within 4 words of guidance/outlook, either order):')
Add-Line ''
Add-Line ('      {0}' -f $ActionNearPattern)
Add-Line ''
Add-Line ('- `ambiguous` - mentions guidance/outlook but the guidance-action regex does not match.')
Add-Line ''
Add-Line 'Action token list (regex alternation, verbatim; the six spec-173 action families plus the'
Add-Line "documented synonym family 'boost', common press-release phrasing for a guidance raise):"
Add-Line ''
Add-Line ('      {0}' -f $ActionAlternation)
Add-Line ''
Add-Line '## Origin x classification matrix'
Add-Line ''
Add-Line 'Percentages are of the row origin''s total; the total row''s percentages are of all `GuidanceChange` signals.'
Add-Line ''
Add-Line '| origin | guidance-action | results-only | ambiguous | total |'
Add-Line '| --- | --- | --- | --- | --- |'
foreach ($o in $Origins) {
    $cells = @()
    foreach ($c in $Classes) {
        $cells += ('{0} ({1}%)' -f $matrix["$o|$c"], (Format-Pct -Count $matrix["$o|$c"] -Total $originTotals[$o]))
    }
    Add-Line ('| {0} | {1} | {2} |' -f $o, ($cells -join ' | '), $originTotals[$o])
}
$totalCells = @()
foreach ($c in $Classes) {
    $totalCells += ('{0} ({1}%)' -f $classTotals[$c], (Format-Pct -Count $classTotals[$c] -Total $grandTotal))
}
Add-Line ('| total | {0} | {1} |' -f ($totalCells -join ' | '), $grandTotal)
Add-Line ''
Add-Line '## Score-weighted section - most recent as-of per company, primary strategy only'
Add-Line ''
Add-Line 'For each company: the snapshot with max `createdAtUtc` in its primary score directory; ranking is'
Add-Line '`opportunityScore` desc, `companyId` asc. The highest-contribution directional signal is the link'
Add-Line 'whose resolved signal has direction != Neutral with max `contributionWeight` (tie-break: signalId'
Add-Line 'asc). Nothing is recomputed; everything is read off the stored snapshots.'
Add-Line ''
Add-Line ('Of {0} ranked companies, {1} have a resolvable directional top contributor; for {2} of those the' -f $companyReports.Count, $companiesWithDirectionalTop, $topIsGuidanceChange)
Add-Line ('top contributor is a `GuidanceChange`, and for {0} it is a **results-only** `GuidanceChange`:' -f $restsOnResultsOnly.Count)
Add-Line ''
if ($restsOnResultsOnly.Count -eq 0) {
    Add-Line 'No company''s highest-contribution directional signal is a results-only `GuidanceChange`.'
} else {
    Add-Line '| rank | company | ticker | opportunity | direction | strength | confidence | classification | origin | signalId |'
    Add-Line '| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |'
    foreach ($r in $restsOnResultsOnly) {
        $sig = $r.Top.Signal
        Add-Line ('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |' -f `
            $r.Rank, (Escape-MdCell $r.Name), (Escape-MdCell $(if ([string]::IsNullOrEmpty($r.Ticker)) { '-' } else { $r.Ticker })), `
            $r.Opportunity, $r.Top.Direction, [int](Get-Prop $sig 'strength' 0), ('{0:0.00}' -f [double](Get-Prop $sig 'confidence' 0)), `
            $r.TopClassification, $r.TopOrigin, $r.Top.SignalId)
    }
}
Add-Line ''
Add-Line '### Current top 10 by Opportunity (context for the headline)'
Add-Line ''
Add-Line '| rank | company | ticker | opportunity | top directional signal type | classification (if GuidanceChange) |'
Add-Line '| --- | --- | --- | --- | --- | --- |'
foreach ($r in $top10) {
    $typeCell = if ([string]::IsNullOrEmpty($r.TopType)) { '(no directional contributor)' } else { $r.TopType }
    $classCell = if ([string]::IsNullOrEmpty($r.TopClassification)) { '-' } else { $r.TopClassification }
    Add-Line ('| {0} | {1} | {2} | {3} | {4} | {5} |' -f `
        $r.Rank, (Escape-MdCell $r.Name), (Escape-MdCell $(if ([string]::IsNullOrEmpty($r.Ticker)) { '-' } else { $r.Ticker })), `
        $r.Opportunity, $typeCell, $classCell)
}
Add-Line ''
Add-Line '## Fixtures (spec 173 section 3 - pinned; the script fails without them)'
Add-Line ''
foreach ($fr in $fixtureResults) {
    Add-Line ('- {0} signal `{1}` -> **{2}** ({3}) - PASS' -f $fr.Ticker, $fr.SignalId, $fr.Classification, $fr.Origin)
}
Add-Line ''
Add-Line '## Limitation - this classifies the AI rationale, NOT the filing'
Add-Line ''
Add-Line 'Radar does not store filing text: for an 8-K the persisted evidence `rawText` is the EDGAR index'
Add-Line 'summary (item codes and item titles), not the exhibit. This audit therefore classifies the AI'
Add-Line 'RATIONALE (`Signal.Reason`), not the underlying document. A rationale that never mentions guidance'
Add-Line 'is strong evidence the read was not about guidance, but it is not proof about the filing itself.'
Add-Line 'A reader who takes this as a filing-level measurement will over-claim. Fetching 8-K exhibits to'
Add-Line 'classify the filing properly is separate, network-bound, SEC-fair-access-exposed work and is'
Add-Line 'explicitly out of scope (spec 173).'
Add-Line ''
Add-Line '---'
Add-Line 'Deterministic (AD-3): identical store => byte-identical artifacts (ordinal sorts, invariant-culture'
Add-Line 'formatting, no run timestamps). Read-only: this audit writes only under `guidance-typing-audit/`.'

# --- render: csv -----------------------------------------------------------------------------------------

# One CSV, fixed column set, section-typed rows:
#   meta                    key/detail pairs (the verbatim rules and token list)
#   summary                 key/count pairs (store totals, unresolvable links, the headline number)
#   matrix                  origin x classification counts + percentages (plus total rows)
#   company-top-contributor per-company rows whose top directional contributor is a results-only GuidanceChange
#   fixture                 the two pinned fixture outcomes
#   signal                  every classified GuidanceChange signal, signalId ordinal asc
$csvRows = New-Object System.Collections.Generic.List[object]
function Add-CsvRow {
    param([hashtable]$Values)
    $row = [ordered]@{
        section = ''; key = ''; detail = ''; origin = ''; classification = ''; count = ''
        pctOfOrigin = ''; pctOfTotal = ''; rank = ''; companyId = ''; ticker = ''; companyName = ''
        opportunityScore = ''; signalId = ''; direction = ''; strength = ''; confidence = ''
    }
    foreach ($k in $Values.Keys) { $row[$k] = $Values[$k] }
    $csvRows.Add([pscustomobject]$row) | Out-Null
}

Add-CsvRow @{ section = 'meta'; key = 'classification-rule-spec168s5-verbatim'; detail = $Spec168RuleVerbatim }
Add-CsvRow @{ section = 'meta'; key = 'classification-rule-spec173-extension'; detail = $Spec173Extension }
Add-CsvRow @{ section = 'meta'; key = 'classification-rule-concrete'; detail = $ConcreteRuleText }
Add-CsvRow @{ section = 'meta'; key = 'action-token-alternation'; detail = $ActionAlternation }
Add-CsvRow @{ section = 'meta'; key = 'origin-split-rule-verbatim'; detail = $OriginRuleVerbatim }
Add-CsvRow @{ section = 'meta'; key = 'limitation'; detail = 'Radar does not store filing text; for an 8-K the persisted rawText is the EDGAR index summary (item codes/titles), so this audit classifies the AI RATIONALE, not the filing. A rationale that never mentions guidance is strong evidence but not proof about the underlying document.' }
Add-CsvRow @{ section = 'summary'; key = 'signal-files-examined'; count = $totalSignalFiles }
Add-CsvRow @{ section = 'summary'; key = 'guidance-change-signals'; count = $grandTotal }
Add-CsvRow @{ section = 'summary'; key = 'unreadable-signal-files'; count = $unreadableSignalFiles }
Add-CsvRow @{ section = 'summary'; key = 'companies-ranked'; count = $companyReports.Count }
Add-CsvRow @{ section = 'summary'; key = 'companies-with-directional-top-contributor'; count = $companiesWithDirectionalTop }
Add-CsvRow @{ section = 'summary'; key = 'companies-top-contributor-is-guidance-change'; count = $topIsGuidanceChange }
Add-CsvRow @{ section = 'summary'; key = 'companies-top-contributor-is-results-only-guidance-change'; count = $restsOnResultsOnly.Count }
Add-CsvRow @{ section = 'summary'; key = 'top10-dependent-on-results-only-guidance-change'; count = $top10Dependent }
Add-CsvRow @{ section = 'summary'; key = 'unresolvable-links'; count = $unresolvableLinks }

foreach ($o in $Origins) {
    foreach ($c in $Classes) {
        Add-CsvRow @{ section = 'matrix'; origin = $o; classification = $c; count = $matrix["$o|$c"]
            pctOfOrigin = (Format-Pct -Count $matrix["$o|$c"] -Total $originTotals[$o])
            pctOfTotal = (Format-Pct -Count $matrix["$o|$c"] -Total $grandTotal) }
    }
    Add-CsvRow @{ section = 'matrix'; origin = $o; classification = 'total'; count = $originTotals[$o]
        pctOfOrigin = (Format-Pct -Count $originTotals[$o] -Total $originTotals[$o])
        pctOfTotal = (Format-Pct -Count $originTotals[$o] -Total $grandTotal) }
}
foreach ($c in $Classes) {
    Add-CsvRow @{ section = 'matrix'; origin = 'total'; classification = $c; count = $classTotals[$c]
        pctOfOrigin = (Format-Pct -Count $classTotals[$c] -Total $grandTotal)
        pctOfTotal = (Format-Pct -Count $classTotals[$c] -Total $grandTotal) }
}
Add-CsvRow @{ section = 'matrix'; origin = 'total'; classification = 'total'; count = $grandTotal
    pctOfOrigin = (Format-Pct -Count $grandTotal -Total $grandTotal)
    pctOfTotal = (Format-Pct -Count $grandTotal -Total $grandTotal) }

foreach ($r in $restsOnResultsOnly) {
    $sig = $r.Top.Signal
    Add-CsvRow @{ section = 'company-top-contributor'; rank = $r.Rank; companyId = $r.CompanyId
        ticker = $r.Ticker; companyName = $r.Name; opportunityScore = $r.Opportunity
        signalId = $r.Top.SignalId; direction = $r.Top.Direction
        strength = [int](Get-Prop $sig 'strength' 0); confidence = ('{0:0.00}' -f [double](Get-Prop $sig 'confidence' 0))
        classification = $r.TopClassification; origin = $r.TopOrigin }
}

foreach ($fr in $fixtureResults) {
    Add-CsvRow @{ section = 'fixture'; key = $fr.Ticker; signalId = $fr.SignalId
        classification = $fr.Classification; origin = $fr.Origin; detail = 'PASS' }
}

# Ordinal by signalId, for the same locale-independence reason as the ranking sort above.
$sortedGuidanceRows = $guidanceRows.ToArray()
if ($sortedGuidanceRows.Length -gt 1) {
    [Array]::Sort($sortedGuidanceRows, [System.Comparison[object]]{
        param($a, $b) [string]::CompareOrdinal([string]$a.SignalId, [string]$b.SignalId)
    })
}
foreach ($row in $sortedGuidanceRows) {
    Add-CsvRow @{ section = 'signal'; signalId = $row.SignalId; companyId = $row.CompanyId
        origin = $row.Origin; classification = $row.Classification; direction = $row.Direction
        strength = $row.Strength; confidence = ('{0:0.00}' -f $row.Confidence) }
}

# --- write (the ONLY writes this script performs) --------------------------------------------------------

if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($mdPath, (($md -join "`r`n") + "`r`n"), $utf8NoBom)
$csvRows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8

Write-Output ''
Write-Output ("Origin x classification matrix ({0} GuidanceChange signals):" -f $grandTotal)
foreach ($o in $Origins) {
    foreach ($c in $Classes) {
        Write-Output ("  {0} x {1}: {2} ({3}% of origin)" -f $o, $c, $matrix["$o|$c"], (Format-Pct -Count $matrix["$o|$c"] -Total $originTotals[$o]))
    }
}
Write-Output ("Top-10 headline: {0} of the current top 10 by Opportunity rest on a results-only GuidanceChange." -f $top10Dependent)
Write-Output ("Companies (any rank) whose top directional contributor is a results-only GuidanceChange: {0} of {1} ranked." -f $restsOnResultsOnly.Count, $companyReports.Count)
Write-Output ("Unresolvable links: {0}." -f $unresolvableLinks)
Write-Output ''
Write-Output ("Artifacts written:")
Write-Output ("  {0}" -f $mdPath)
Write-Output ("  {0}" -f $csvPath)
exit 0
