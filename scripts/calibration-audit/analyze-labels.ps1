<#
.SYNOPSIS
    Spec 162 - AI filing-read calibration audit: joins blinded labels to the sealed worksheet and renders
    the study report (PowerShell 5.1-compatible; no scoring math, no runtime value ever flows from here).

.DESCRIPTION
    Inputs:
      - labels.jsonl       one JSON object per line (canonical schema below), produced by the blinded
                           labeling protocol.
      - worksheet.csv      the SEALED model answers, written by the Radar.CalibrationAudit console
                           (src/Radar.CalibrationAudit). The sealed model answer is joined HERE, at
                           analysis time - it is never stored in the label file.
      - pilot CSV          docs/162-calibration-pilot-labels.csv (schema pilot-flat, lossy legacy) for the
                           input-path stability table.

    Report sections (all headline rates carry Wilson 95% intervals):
      1. "Inter-model agreement curve"  - reader-confidence bins x skeptic-agreement rate. AGREEMENT IS
         NOT CALIBRATION: two models can agree and both be wrong.
      2. Calibration probability sample - min(10, bin size) rows per confidence bin, selected by
         SHA-256(accession) hex ASCENDING within the bin, IRRESPECTIVE of agreement status. Rendered so
         the adjudicator knows exactly which rows to adjudicate (the -EmitSample switch renders ONLY this).
      3. Calibration table - bins x human-adjudicated correctness, computed EXCLUSIVELY over rows whose
         adjudication.selectionReason == "calibration-sample". Disagreement/doubt-queued adjudications are
         NEVER pooled into these rates (conditioning on a set containing every failure but only a slice of
         successes biases accuracy downward; Wilson intervals cannot repair selection bias). Empty bins
         render "no adjudicated labels", never interpolated.
      4. Error-diagnosis set - ALL remaining disagreements + ALL doubt-flagged labels, reported separately.
      5. Clean-rate, comparability-item frequency (the cmpscan-v2 evidence table), materiality x
         constant-strength cross-tab.
      6. False-negative table - no-signal cohort rows whose blinded label is directional; the one-shot
         extension rule threshold (Wilson upper bound on the miss rate > 10%) is stated with the numbers.
      7. Input-path stability table - pilot labels vs canonical-input relabels (direction/clean deltas).
      8. Adjudication queue listing with selectionReason.

    CANONICAL LABEL SCHEMA (labels.jsonl, one JSON object per filing - copied from spec 162):

      { accession, ticker, cik, batch, modelInputHash,
        protocol: { version: "cal-v2", labeler: { provider, model }, promptHash, labeledAtUtc,
                    attempt: 1|2|..., replacedLabelOfAttempt?: n },
        label: { direction, directionConfidence, comparisonClean, comparabilityItems[], material, keyFacts[] },
        adjudication: { status: pending|confirmed|overturned|n/a,
                        selectionReason: calibration-sample|disagreement|doubt,
                        blindCall?: { direction, comparisonClean },   // recorded BEFORE unblinding
                        finalDirection?, note? } }

    PROTOCOL RULES THIS SCRIPT ENFORCES / CHECKS:
      - Confidence bins are EXACT half-open intervals: [0,0.60), [0.60,0.70), [0.70,0.80), [0.80,0.90),
        [0.90,0.95), [0.95,1.00]  (the last is CLOSED at 1.00).
      - `selectionReason` "calibration-sample" takes precedence when a row qualifies both ways: a
        disagreeing row inside the probability sample is still a sample row (that is the point of sampling
        irrespective of agreement).
      - Adjudicated correctness REQUIRES adjudication.finalDirection: a row is correct iff finalDirection
        equals the sealed model direction (ordinal, case-insensitive). Rows without finalDirection count as
        unresolved, never guessed.
      - Retries: the effective label per accession is the HIGHEST protocol.attempt; replaced attempts are
        reported but excluded from rates.
      - Precommitted labeler for the whole study: anthropic:claude-fable-5 (the radar-skeptic-reviewer
        agent). A label from any other labeler, a protocol version other than cal-v2, or a promptHash drift
        across labels is reported as a PROTOCOL WARNING (changing the labeler mid-study is a
        protocol-version bump that restarts the affected labels).
      - Labels that join to no worksheet row are listed loudly, never silently dropped.

.PARAMETER LabelsPath
    Path to labels.jsonl.

.PARAMETER WorksheetPath
    Path to the sealed worksheet.csv written by Radar.CalibrationAudit.

.PARAMETER PilotCsvPath
    Path to the lossy legacy pilot summary (default docs/162-calibration-pilot-labels.csv relative to the
    repo root).

.PARAMETER OutFile
    Optional path to also write the markdown report to.

.PARAMETER EmitSample
    Render ONLY the calibration probability sample selection (the adjudicator's worklist).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LabelsPath,
    [Parameter(Mandatory = $true)][string]$WorksheetPath,
    [string]$PilotCsvPath = '',
    [string]$OutFile,
    [switch]$EmitSample
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Default the pilot CSV relative to the repo root (resolved here, not in the param block: $PSScriptRoot is
# not reliably populated during parameter-default evaluation under Windows PowerShell 5.1).
if ([string]::IsNullOrEmpty($PilotCsvPath)) {
    $PilotCsvPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'docs\162-calibration-pilot-labels.csv'
}

# The precommitted second reader (spec 162): the whole study runs on ONE labeler.
$PrecommittedLabelerProvider = 'anthropic'
$PrecommittedLabelerModel = 'claude-fable-5'
$PrecommittedProtocolVersion = 'cal-v2'

# --- helpers -------------------------------------------------------------------------------------------

function Get-Prop {
    # StrictMode-safe optional-property read (trailing/optional JSON fields).
    param($Object, [string]$Name, $Default = $null)
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) {
        return $Object.PSObject.Properties[$Name].Value
    }
    return $Default
}

$script:Sha256Cache = @{}
function Get-AccessionSha256 {
    # Lowercase hex of SHA-256(UTF-8(accession)) - the SAME ordering key the C# console uses
    # (Radar.CalibrationAudit.AccessionHash; pinned by AccessionHashTests to shared external vectors).
    param([string]$Accession)
    if (-not $script:Sha256Cache.ContainsKey($Accession)) {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Accession))
        } finally {
            $sha.Dispose()
        }
        $script:Sha256Cache[$Accession] = ([System.BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
    }
    return $script:Sha256Cache[$Accession]
}

function Get-ConfidenceBin {
    # EXACT half-open intervals; the last bin is CLOSED at 1.00. Anything outside [0,1] maps to $null and
    # is reported, never binned.
    param([double]$Confidence)
    if ($Confidence -lt 0) { return $null }
    if ($Confidence -lt 0.60) { return '[0.00,0.60)' }
    if ($Confidence -lt 0.70) { return '[0.60,0.70)' }
    if ($Confidence -lt 0.80) { return '[0.70,0.80)' }
    if ($Confidence -lt 0.90) { return '[0.80,0.90)' }
    if ($Confidence -lt 0.95) { return '[0.90,0.95)' }
    if ($Confidence -le 1.00) { return '[0.95,1.00]' }
    return $null
}
$BinOrder = @('[0.00,0.60)', '[0.60,0.70)', '[0.70,0.80)', '[0.80,0.90)', '[0.90,0.95)', '[0.95,1.00]')

function Get-Wilson {
    # Wilson score interval, 95% (z = 1.959963984540054). Returns $null when n = 0.
    param([int]$Successes, [int]$N)
    if ($N -le 0) { return $null }
    $z = 1.959963984540054
    $p = $Successes / [double]$N
    $z2 = $z * $z
    $denom = 1.0 + ($z2 / $N)
    $centre = ($p + ($z2 / (2.0 * $N))) / $denom
    $half = ($z * [math]::Sqrt((($p * (1.0 - $p)) + ($z2 / (4.0 * $N))) / $N)) / $denom
    New-Object psobject -Property @{
        P     = $p
        Lower = [math]::Max(0.0, $centre - $half)
        Upper = [math]::Min(1.0, $centre + $half)
    }
}

function Format-Rate {
    param([int]$Successes, [int]$N)
    if ($N -le 0) { return 'n/a (n=0)' }
    $w = Get-Wilson -Successes $Successes -N $N
    return ('{0}/{1} = {2:P1} (Wilson 95%: {3:P1}-{4:P1})' -f $Successes, $N, $w.P, $w.Lower, $w.Upper)
}

function Test-DirectionalLabel {
    param([string]$Direction)
    return ($Direction -eq 'Positive' -or $Direction -eq 'Negative')
}

# --- load inputs ---------------------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $LabelsPath)) { throw "No labels file at '$LabelsPath'." }
if (-not (Test-Path -LiteralPath $WorksheetPath)) { throw "No worksheet at '$WorksheetPath' (run Radar.CalibrationAudit first)." }

$worksheet = @(Import-Csv -LiteralPath $WorksheetPath)
if ($worksheet.Count -eq 0) { throw "Worksheet '$WorksheetPath' is empty." }

$worksheetByAccession = @{}
foreach ($row in $worksheet) {
    if ($worksheetByAccession.ContainsKey($row.accession)) {
        throw "Duplicate accession '$($row.accession)' in the sealed worksheet - the worksheet must be one row per accession."
    }
    $worksheetByAccession[$row.accession] = $row
}

$rawLabels = @()
foreach ($line in (Get-Content -LiteralPath $LabelsPath)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $rawLabels += (ConvertFrom-Json -InputObject $line)
}

# Effective label per accession = highest protocol.attempt (retries replace; replaced attempts reported).
$labelsByAccession = @{}
$replacedAttempts = @()
$duplicateLabelWarnings = @()
foreach ($label in $rawLabels) {
    $acc = $label.accession
    $attempt = [int](Get-Prop (Get-Prop $label 'protocol') 'attempt' 1)
    if (-not $labelsByAccession.ContainsKey($acc)) {
        $labelsByAccession[$acc] = $label
    } else {
        $existingAttempt = [int](Get-Prop (Get-Prop $labelsByAccession[$acc] 'protocol') 'attempt' 1)
        if ($attempt -gt $existingAttempt) {
            $replacedAttempts += ('{0} attempt {1} replaced by attempt {2}' -f $acc, $existingAttempt, $attempt)
            $labelsByAccession[$acc] = $label
        } elseif ($attempt -eq $existingAttempt) {
            # Two labels at the SAME attempt is a protocol violation, not a retry: keep the first
            # occurrence deterministically, but flag it loudly.
            $duplicateLabelWarnings += ('{0}: two labels at attempt {1} (first occurrence kept; a retry must carry a higher attempt number)' -f $acc, $attempt)
        } else {
            $replacedAttempts += ('{0} attempt {1} superseded by attempt {2}' -f $acc, $attempt, $existingAttempt)
        }
    }
}

# --- protocol checks -----------------------------------------------------------------------------------

$protocolWarnings = @()
$protocolWarnings += $duplicateLabelWarnings
$promptHashes = @{}
$orphanLabels = @()
foreach ($acc in $labelsByAccession.Keys) {
    $label = $labelsByAccession[$acc]
    $protocol = Get-Prop $label 'protocol'
    $version = Get-Prop $protocol 'version' ''
    if ($version -ne $PrecommittedProtocolVersion) {
        $protocolWarnings += ('{0}: protocol version "{1}" != precommitted "{2}"' -f $acc, $version, $PrecommittedProtocolVersion)
    }
    $labeler = Get-Prop $protocol 'labeler'
    $provider = Get-Prop $labeler 'provider' ''
    $model = Get-Prop $labeler 'model' ''
    if ($provider -ne $PrecommittedLabelerProvider -or $model -ne $PrecommittedLabelerModel) {
        $protocolWarnings += ('{0}: labeler "{1}:{2}" != precommitted "{3}:{4}" (labeler change = protocol-version bump restarting affected labels)' -f $acc, $provider, $model, $PrecommittedLabelerProvider, $PrecommittedLabelerModel)
    }
    $promptHash = Get-Prop $protocol 'promptHash' ''
    if (-not [string]::IsNullOrEmpty($promptHash)) { $promptHashes[$promptHash] = $true }
    if (-not $worksheetByAccession.ContainsKey($acc)) { $orphanLabels += $acc }
}
if ($promptHashes.Keys.Count -gt 1) {
    $protocolWarnings += ('promptHash drift: {0} distinct prompt hashes across labels ({1}) - the template changed mid-study' -f $promptHashes.Keys.Count, ($promptHashes.Keys -join ', '))
}

# --- join ----------------------------------------------------------------------------------------------

# Directional joined rows: worksheet directional rows that have an effective label.
$directionalRows = @($worksheet | Where-Object { $_.outcome -eq 'DirectionalSignalProduced' })
$noSignalRows = @($worksheet | Where-Object { $_.outcome -eq 'NoDirectionalSignal' })

$joined = @()
foreach ($row in $directionalRows) {
    if (-not $labelsByAccession.ContainsKey($row.accession)) { continue }
    $label = $labelsByAccession[$row.accession]
    $labelBody = Get-Prop $label 'label'
    $labelDirection = [string](Get-Prop $labelBody 'direction' '')
    $agree = ($labelDirection -ieq $row.direction)
    $joined += (New-Object psobject -Property @{
        Accession       = $row.accession
        Sha             = Get-AccessionSha256 $row.accession
        Ticker          = $row.ticker
        ModelDirection  = $row.direction
        ModelConfidence = [double]$row.confidence
        ModelStrength   = $row.strength
        Bin             = Get-ConfidenceBin ([double]$row.confidence)
        LabelDirection  = $labelDirection
        LabelConfidence = Get-Prop $labelBody 'directionConfidence'
        Clean           = Get-Prop $labelBody 'comparisonClean'
        Items           = @(Get-Prop $labelBody 'comparabilityItems' @())
        Material        = [string](Get-Prop $labelBody 'material' '')
        Agree           = $agree
        Adjudication    = Get-Prop $label 'adjudication'
        Batch           = Get-Prop $label 'batch' ''
    })
}

# --- calibration probability sample (selection is a pure function of the joined set) -------------------

# min(10, bin size) per confidence bin, SHA-256(accession) hex ASCENDING within the bin, IRRESPECTIVE of
# agreement status. Deterministic: same joined set => same sample, on any machine.
$sampleByBin = @{}
foreach ($bin in $BinOrder) {
    $inBin = @($joined | Where-Object { $_.Bin -eq $bin } | Sort-Object -Property Sha)
    $take = [math]::Min(10, $inBin.Count)
    if ($take -gt 0) {
        $sampleByBin[$bin] = @($inBin[0..($take - 1)])
    } else {
        $sampleByBin[$bin] = @()
    }
}
$sampleAccessions = @{}
foreach ($bin in $BinOrder) {
    foreach ($row in $sampleByBin[$bin]) { $sampleAccessions[$row.Accession] = $true }
}

# --- report --------------------------------------------------------------------------------------------

$md = New-Object System.Collections.Generic.List[string]
function Add-Line { param([string]$Text = '') $md.Add($Text) | Out-Null }

Add-Line '# Spec 162 - filing-read calibration analysis'
Add-Line ''
Add-Line ('Generated (UTC): {0:o}' -f [DateTime]::UtcNow)
Add-Line ('Labels: {0} raw entries, {1} effective (highest attempt per accession). Worksheet: {2} rows ({3} directional, {4} no-signal).' -f `
    $rawLabels.Count, $labelsByAccession.Keys.Count, $worksheet.Count, $directionalRows.Count, $noSignalRows.Count)
Add-Line ('Directional rows with an effective label: {0}.' -f $joined.Count)
Add-Line ''

if ($replacedAttempts.Count -gt 0) {
    Add-Line ('Replaced attempts (excluded from every rate): {0}' -f ($replacedAttempts -join '; '))
    Add-Line ''
}
if ($orphanLabels.Count -gt 0) {
    Add-Line ('**PROTOCOL WARNING** - labels joining NO worksheet row (listed, never silently dropped): {0}' -f ($orphanLabels -join ', '))
    Add-Line ''
}
if ($protocolWarnings.Count -gt 0) {
    Add-Line '**PROTOCOL WARNINGS**:'
    foreach ($w in $protocolWarnings) { Add-Line ('- {0}' -f $w) }
    Add-Line ''
}

# -- Section: calibration probability sample (the adjudicator worklist) --
Add-Line '## Calibration probability sample (selected)'
Add-Line ''
Add-Line 'min(10, bin size) rows per confidence bin, SHA-256(accession) hex ASCENDING within the bin,'
Add-Line 'selected IRRESPECTIVE of agreement status. These rows - and ONLY these rows - feed the'
Add-Line 'calibration table; adjudicate each with the two-step blinded flow (blindCall first) and record'
Add-Line 'selectionReason "calibration-sample" (it takes precedence when a row also disagrees).'
Add-Line ''
Add-Line '| bin | n in bin | sampled | accessions (hash order) |'
Add-Line '| --- | --- | --- | --- |'
foreach ($bin in $BinOrder) {
    $inBinCount = @($joined | Where-Object { $_.Bin -eq $bin }).Count
    $sample = $sampleByBin[$bin]
    $accList = @($sample | ForEach-Object { $_.Accession }) -join ', '
    if ([string]::IsNullOrEmpty($accList)) { $accList = '-' }
    Add-Line ('| {0} | {1} | {2} | {3} |' -f $bin, $inBinCount, $sample.Count, $accList)
}
Add-Line ''

if ($EmitSample) {
    $sampleText = $md -join [Environment]::NewLine
    if ($OutFile) { Set-Content -LiteralPath $OutFile -Value $sampleText -Encoding UTF8 }
    Write-Output $sampleText
    return
}

# -- Section: inter-model agreement curve --
Add-Line '## Inter-model agreement curve'
Add-Line ''
Add-Line 'Reader-confidence bins x skeptic-agreement rate. AGREEMENT IS NOT CALIBRATION: two models can'
Add-Line 'agree and both be wrong - human-adjudicated correctness lives in the calibration table below.'
Add-Line ''
Add-Line '| reader-confidence bin | n | agree | agreement rate (Wilson 95%) |'
Add-Line '| --- | --- | --- | --- |'
foreach ($bin in $BinOrder) {
    $inBin = @($joined | Where-Object { $_.Bin -eq $bin })
    if ($inBin.Count -eq 0) {
        Add-Line ('| {0} | 0 | - | no labeled rows |' -f $bin)
        continue
    }
    $agreeCount = @($inBin | Where-Object { $_.Agree }).Count
    Add-Line ('| {0} | {1} | {2} | {3} |' -f $bin, $inBin.Count, $agreeCount, (Format-Rate -Successes $agreeCount -N $inBin.Count))
}
$totalAgree = @($joined | Where-Object { $_.Agree }).Count
Add-Line ''
Add-Line ('Overall agreement: {0}' -f (Format-Rate -Successes $totalAgree -N $joined.Count))
Add-Line ''

# -- Section: calibration table (probability sample ONLY) --
Add-Line '## Calibration table (calibration probability sample ONLY)'
Add-Line ''
Add-Line 'Human-adjudicated correctness over rows with adjudication.selectionReason == "calibration-sample"'
Add-Line 'EXCLUSIVELY. Disagreement/doubt-queued adjudications are NEVER pooled into these rates. A row is'
Add-Line 'correct iff adjudication.finalDirection equals the sealed model direction; rows without a'
Add-Line 'finalDirection are unresolved and excluded (counted below, never guessed).'
Add-Line ''
Add-Line '| reader-confidence bin | adjudicated n | model correct | accuracy (Wilson 95%) | unresolved |'
Add-Line '| --- | --- | --- | --- | --- |'
foreach ($bin in $BinOrder) {
    $inBin = @($joined | Where-Object { $_.Bin -eq $bin })
    $calibRows = @($inBin | Where-Object {
        $adj = $_.Adjudication
        ([string](Get-Prop $adj 'selectionReason' '')) -eq 'calibration-sample'
    })
    if ($calibRows.Count -eq 0) {
        Add-Line ('| {0} | 0 | - | no adjudicated labels | - |' -f $bin)
        continue
    }
    $resolved = @($calibRows | Where-Object { -not [string]::IsNullOrEmpty([string](Get-Prop $_.Adjudication 'finalDirection' '')) })
    $unresolved = $calibRows.Count - $resolved.Count
    if ($resolved.Count -eq 0) {
        Add-Line ('| {0} | 0 | - | no adjudicated labels | {1} |' -f $bin, $unresolved)
        continue
    }
    $correct = @($resolved | Where-Object { ([string](Get-Prop $_.Adjudication 'finalDirection' '')) -ieq $_.ModelDirection }).Count
    Add-Line ('| {0} | {1} | {2} | {3} | {4} |' -f $bin, $resolved.Count, $correct, (Format-Rate -Successes $correct -N $resolved.Count), $unresolved)
}
Add-Line ''

# -- Section: error-diagnosis set --
Add-Line '## Error-diagnosis set (reported separately - NEVER pooled into calibration rates)'
Add-Line ''
$diagRows = @($joined | Where-Object {
    $reason = [string](Get-Prop $_.Adjudication 'selectionReason' '')
    ($reason -eq 'disagreement' -or $reason -eq 'doubt')
})
if ($diagRows.Count -eq 0) {
    Add-Line 'No error-diagnosis adjudications recorded.'
} else {
    Add-Line '| accession | ticker | model | label | selectionReason | status | finalDirection | note |'
    Add-Line '| --- | --- | --- | --- | --- | --- | --- | --- |'
    foreach ($row in ($diagRows | Sort-Object -Property Sha)) {
        $adj = $row.Adjudication
        Add-Line ('| {0} | {1} | {2} ({3:0.00}) | {4} | {5} | {6} | {7} | {8} |' -f `
            $row.Accession, $row.Ticker, $row.ModelDirection, $row.ModelConfidence, $row.LabelDirection, `
            [string](Get-Prop $adj 'selectionReason' ''), [string](Get-Prop $adj 'status' 'pending'), `
            [string](Get-Prop $adj 'finalDirection' '-'), [string](Get-Prop $adj 'note' ''))
    }
}
Add-Line ''

# -- Section: clean rate + comparability items --
Add-Line '## Comparison cleanliness'
Add-Line ''
$cleanTrue = @($joined | Where-Object { $_.Clean -eq $true }).Count
Add-Line ('Clean YoY comparison rate (labeled directional rows): {0}' -f (Format-Rate -Successes $cleanTrue -N $joined.Count))
Add-Line ''
Add-Line '### Comparability-item frequency (cmpscan-v2 evidence)'
Add-Line ''
$itemCounts = @{}
foreach ($row in $joined) {
    foreach ($item in $row.Items) {
        $key = [string]$item
        if ([string]::IsNullOrWhiteSpace($key)) { continue }
        if (-not $itemCounts.ContainsKey($key)) { $itemCounts[$key] = 0 }
        $itemCounts[$key] = $itemCounts[$key] + 1
    }
}
if ($itemCounts.Keys.Count -eq 0) {
    Add-Line 'No comparability items recorded.'
} else {
    Add-Line '| comparability item | filings |'
    Add-Line '| --- | --- |'
    foreach ($key in ($itemCounts.Keys | Sort-Object -Property @{Expression = { $itemCounts[$_] }; Descending = $true}, @{Expression = { $_ }})) {
        Add-Line ('| {0} | {1} |' -f $key, $itemCounts[$key])
    }
}
Add-Line ''

# -- Section: materiality x constant strength --
Add-Line '## Materiality x constant-strength cross-tab'
Add-Line ''
Add-Line 'Every AI directional read carries the same configured Strength; the blinded labels grade'
Add-Line 'materiality independently - this table is the evidence for (or against) encoding materiality.'
Add-Line ''
$materialities = @('low', 'moderate', 'high')
$strengths = @($joined | ForEach-Object { $_.ModelStrength } | Sort-Object -Unique)
Add-Line ('| labeled materiality | {0} |' -f (@($strengths | ForEach-Object { "strength $_" }) -join ' | '))
Add-Line ('| --- | {0} |' -f (@($strengths | ForEach-Object { '---' }) -join ' | '))
foreach ($mat in $materialities) {
    $cells = @()
    foreach ($s in $strengths) {
        $cells += @($joined | Where-Object { $_.Material -ieq $mat -and $_.ModelStrength -eq $s }).Count
    }
    Add-Line ('| {0} | {1} |' -f $mat, ($cells -join ' | '))
}
$unlabeledMat = @($joined | Where-Object { $materialities -notcontains $_.Material.ToLowerInvariant() }).Count
if ($unlabeledMat -gt 0) {
    Add-Line ''
    Add-Line ('Rows without a low/moderate/high materiality grade: {0}.' -f $unlabeledMat)
}
Add-Line ''

# -- Section: false negatives --
Add-Line '## False-negative table (no-signal cohort)'
Add-Line ''
$noSignalLabeled = @()
foreach ($row in $noSignalRows) {
    if (-not $labelsByAccession.ContainsKey($row.accession)) { continue }
    $label = $labelsByAccession[$row.accession]
    $labelBody = Get-Prop $label 'label'
    $noSignalLabeled += (New-Object psobject -Property @{
        Accession      = $row.accession
        Ticker         = $row.ticker
        LabelDirection = [string](Get-Prop $labelBody 'direction' '')
        Material       = [string](Get-Prop $labelBody 'material' '')
        Adjudication   = Get-Prop $label 'adjudication'
    })
}
$misses = @($noSignalLabeled | Where-Object { Test-DirectionalLabel $_.LabelDirection })
if ($noSignalLabeled.Count -eq 0) {
    Add-Line 'No no-signal rows labeled yet.'
} else {
    Add-Line ('Labeled no-signal rows: {0}. Blinded label directional (potential missed print): {1}' -f `
        $noSignalLabeled.Count, (Format-Rate -Successes $misses.Count -N $noSignalLabeled.Count))
    $missWilson = Get-Wilson -Successes $misses.Count -N $noSignalLabeled.Count
    $extensionText = 'One-shot extension rule (spec 162): extend by exactly the next 30 in hash order iff adjudication ' +
        'confirms >=1 genuinely-directional missed print OR the Wilson 95% upper bound on the miss rate ' +
        ('exceeds 10%. Current upper bound: {0:P1}. The trigger is evaluated ONCE, never re-applied to the extended set.' -f $missWilson.Upper)
    Add-Line $extensionText
    if ($misses.Count -gt 0) {
        Add-Line ''
        Add-Line '| accession | ticker | blinded label | materiality | adjudication status |'
        Add-Line '| --- | --- | --- | --- | --- |'
        foreach ($m in ($misses | Sort-Object -Property @{Expression = { Get-AccessionSha256 $_.Accession }})) {
            Add-Line ('| {0} | {1} | {2} | {3} | {4} |' -f $m.Accession, $m.Ticker, $m.LabelDirection, $m.Material, `
                [string](Get-Prop $m.Adjudication 'status' 'pending'))
        }
    }
}
Add-Line ''

# -- Section: input-path stability (pilot vs relabel) --
Add-Line '## Input-path stability table (pilot vs canonical-input relabel)'
Add-Line ''
if (-not (Test-Path -LiteralPath $PilotCsvPath)) {
    Add-Line ('Pilot CSV not found at {0}; table skipped.' -f $PilotCsvPath)
} else {
    $pilot = @(Import-Csv -LiteralPath $PilotCsvPath)
    Add-Line 'The 30 pilot filings were labeled from non-parity inputs (wire reproductions / ad-hoc stripping);'
    Add-Line 'their canonical-input relabels are the primary study rows. The delta below measures label'
    Add-Line 'sensitivity to input provenance. Pilot skeptic directions map Improving->Positive,'
    Add-Line 'Deteriorating->Negative; the pilot CSV is a lossy legacy summary (schema pilot-flat).'
    Add-Line ''
    $directionMap = @{ 'Improving' = 'Positive'; 'Deteriorating' = 'Negative'; 'Mixed' = 'Mixed'; 'Neutral' = 'Neutral' }
    $stabilityRows = @()
    $directionChanged = 0
    $cleanChanged = 0
    $relabeled = 0
    foreach ($p in $pilot) {
        if (-not $labelsByAccession.ContainsKey($p.Accession)) { continue }
        $relabeled++
        $label = $labelsByAccession[$p.Accession]
        $labelBody = Get-Prop $label 'label'
        $newDirection = [string](Get-Prop $labelBody 'direction' '')
        $newCleanValue = Get-Prop $labelBody 'comparisonClean'
        $newClean = ($newCleanValue -eq $true)
        $oldDirection = [string]$p.SkDirection
        if ($directionMap.ContainsKey($oldDirection)) { $oldDirection = $directionMap[$oldDirection] }
        $oldClean = ([string]$p.Clean -ieq 'true')
        $dirDelta = if ($oldDirection -ieq $newDirection) { 'same' } else { $directionChanged++; ('{0} -> {1}' -f $oldDirection, $newDirection) }
        $cleanDelta = if ($oldClean -eq $newClean) { 'same' } else { $cleanChanged++; ('{0} -> {1}' -f $oldClean, $newClean) }
        $stabilityRows += ('| {0} | {1} | {2} | {3} |' -f $p.Accession, $p.Ticker, $dirDelta, $cleanDelta)
    }
    if ($relabeled -eq 0) {
        Add-Line 'No pilot filings relabeled yet.'
    } else {
        Add-Line '| accession | ticker | direction (pilot -> relabel) | clean (pilot -> relabel) |'
        Add-Line '| --- | --- | --- | --- |'
        foreach ($line in $stabilityRows) { Add-Line $line }
        Add-Line ''
        Add-Line ('Relabeled: {0}/{1}. Direction changed: {2}. Clean changed: {3}.' -f $relabeled, $pilot.Count, $directionChanged, $cleanChanged)
    }
}
Add-Line ''

# -- Section: adjudication queue --
Add-Line '## Adjudication queue'
Add-Line ''
$queue = @()
foreach ($acc in $labelsByAccession.Keys) {
    $label = $labelsByAccession[$acc]
    $adj = Get-Prop $label 'adjudication'
    $reason = [string](Get-Prop $adj 'selectionReason' '')
    $status = [string](Get-Prop $adj 'status' '')
    if ([string]::IsNullOrEmpty($reason) -and -not $sampleAccessions.ContainsKey($acc)) { continue }
    if ([string]::IsNullOrEmpty($reason)) { $reason = 'calibration-sample (selected; not yet recorded in label)' }
    if ([string]::IsNullOrEmpty($status)) { $status = 'pending' }
    $queue += (New-Object psobject -Property @{
        Accession = $acc
        Sha       = Get-AccessionSha256 $acc
        Reason    = $reason
        Status    = $status
        Final     = [string](Get-Prop $adj 'finalDirection' '')
    })
}
if ($queue.Count -eq 0) {
    Add-Line 'Queue empty.'
} else {
    Add-Line '| accession | selectionReason | status | finalDirection |'
    Add-Line '| --- | --- | --- | --- |'
    foreach ($q in ($queue | Sort-Object -Property Sha)) {
        $final = $q.Final
        if ([string]::IsNullOrEmpty($final)) { $final = '-' }
        Add-Line ('| {0} | {1} | {2} | {3} |' -f $q.Accession, $q.Reason, $q.Status, $final)
    }
    Add-Line ''
    Add-Line ('Queue size: {0} (calibration-sample selected: {1}).' -f $queue.Count, $sampleAccessions.Keys.Count)
}
Add-Line ''
Add-Line '---'
Add-Line 'Read-only analysis: labels never flow into runtime values; findings inform SPECS (spec 162).'

$report = $md -join [Environment]::NewLine
if ($OutFile) { Set-Content -LiteralPath $OutFile -Value $report -Encoding UTF8 }
Write-Output $report
