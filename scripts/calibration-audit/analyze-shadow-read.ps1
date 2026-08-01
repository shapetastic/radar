<#
.SYNOPSIS
    Spec 164 - shadow-mode forced-choice second pass: joins the shadow read records to the sealed worksheet
    and the spec-162 Phase B labels, and renders the recovery / stability / distribution tables plus the
    PRECOMMITTED decision block (PowerShell 5.1-compatible; deterministic; no scoring math, no runtime value
    ever flows from here).

.DESCRIPTION
    Inputs:
      - shadow records     one JSON per accession written by `Radar.CalibrationAudit --shadow-read`
                           ({output-root}/shadow/{accession}.json). -ShadowRoot may point at either that
                           `shadow` directory or its parent.
      - worksheet.csv      the SEALED model answers (docs/162-study-worksheet.csv).
      - labels.jsonl       the spec-162 Phase B labels (docs/162-calibration-labels-full.jsonl).
      - shadow-prompt.md   the COMMITTED forced-choice instruction, loaded from THIS SCRIPT'S OWN DIRECTORY
                           (deliberately not a parameter by default): its LF-normalized SHA-256 is recomputed
                           here and checked against every record's promptSha256, so a report can never pool
                           reads taken under two different instructions.

    VOCABULARY MAPPING - DEFINED ONCE, HERE, AND NOWHERE ELSE:

        shadow `Improving`      <-> worksheet/label `Positive`
        shadow `Deteriorating`  <-> worksheet/label `Negative`
        shadow `Mixed`          <-> label `Mixed`
        shadow `Neutral`        <-> label `Neutral`

    No other equivalence is permitted. In particular `Mixed` NEVER counts as agreeing with a directional
    label in the STRICT recovery rate, and it is never "directional".

    HASH CANONICALIZATION (spec 163, reused): decode the file as UTF-8, replace CRLF with LF, re-encode as
    UTF-8, SHA-256, lowercase hex. The repo checks text files out CRLF on Windows and LF on CI, so the
    raw-byte hash differs by machine while the LF-normalized hash is stable.

    STANDING CAVEATS - they apply to EVERY rate in this report:
      1. The reference labels are EXPLORATORY ratified same-family verdicts (spec 162's status section), not
         ground truth.
      2. Filings cluster within tickers, so observations are not independent and the Wilson intervals are
         somewhat narrower than the truth.
      3. The reads are SINGLE-SHOT and the model is non-deterministic: this is a RECORD of one pass, not an
         average. Re-running may move individual rows.

    REPORT SECTIONS:
      0. Provenance + status coverage (prompt version/hash, model identity, ok/parse-failed/call-failed).
      1. Recovery table (the headline) over the 90 labeled no-signal rows: strict recovery, loose recovery
         and false-alarm rate, broken out by FORCED-read confidence bin.
      2. Stability table over the 145 directional rows: forced direction vs the SEALED production direction
         (agree / flipped-to-Mixed-or-Neutral / inverted), by SEALED confidence bin.
      3. Unlabeled distribution: the no-signal rows outside the labeled 90 - direction and confidence
         DISTRIBUTION ONLY, explicitly not accuracy (there are no reference labels for them).
      4. The PRECOMMITTED DECISION BLOCK (frozen in spec 164 before any read ran).

    THE PRECOMMITTED RULE (frozen; this script implements it and nothing else grounds a recommendation):
      - COMPLETENESS GATE FIRST. The decision is evaluable ONLY when all 235 labeled rows (90 no-signal +
        145 directional) have a shadow record with status `ok`. Otherwise:
            SHADOW: INCONCLUSIVE (n rows unresolved: ...accessions...)
        and the remedy is to rerun the failures - never to decide on a subset. The criteria below are
        ABSOLUTE COUNTS over the FIXED denominators 33 / 57 / 145, so excluding failures would silently
        redefine what "17/33" means. Failures among the UNLABELED rows do NOT block the decision.
      - THRESHOLD SEMANTICS (tau is the evaluation threshold): a forced read counts as DIRECTIONAL at tau
        iff its direction is `Improving` or `Deteriorating` AND its confidence >= tau. Any other result -
        `Mixed`, `Neutral`, or a directional read below tau - is NONDIRECTIONAL at tau.
          * no-signal cohort: STRICT RECOVERY = directional-at-tau whose mapped direction equals the
            adjudicated finalDirection; LOOSE RECOVERY = directional-at-tau (either direction);
            FALSE ALARM = directional-at-tau on a provisional NON-miss.
          * directional cohort: INVERSION = directional-at-tau with the direction OPPOSITE the sealed one
            (a below-threshold opposite read is a FLIP, not an inversion); FLIP = any nondirectional-at-tau
            result on a sealed-directional row.
      - PRIMARY EVALUATION at tau = 0.80 EXACTLY. SUPPORTED iff ALL of: strict recovery >= 17 of 33;
        false alarms <= 9 of 57; ZERO inversions; flips <= 15 of 145.
      - ONE-SHOT FALLBACK: if the primary fails, the SAME four criteria are evaluated ONCE at tau = 0.90.
        No further threshold shopping - the rule is evaluated at most twice, at these two precommitted points.
      - OUTCOME IS MACHINE-READABLE: exactly one of
            SHADOW: SUPPORTED (tau=..., ...numbers...)
            SHADOW: NOT-SUPPORTED (...numbers at both tau...)
            SHADOW: INCONCLUSIVE (...)
        EVERY OTHER NUMBER IN THIS REPORT - the descriptive threshold sweep included - IS DESCRIPTIVE ONLY
        AND GROUNDS NO PRODUCTION RECOMMENDATION.

    EXIT CODE: 0 for SUPPORTED / NOT-SUPPORTED (a decision was reached); 1 for INCONCLUSIVE (the run needs
    action: rerun the failed rows, or fix the provenance). Malformed/missing inputs throw. The report is
    ALWAYS written to -OutFile when supplied - an INCONCLUSIVE report is a legitimate artifact, unlike
    analyze-labels.ps1's partial FINAL report.

.PARAMETER ShadowRoot
    The shadow output directory (or its parent) holding {accession}.json records.

.PARAMETER WorksheetPath
    Path to the sealed worksheet.csv.

.PARAMETER LabelsPath
    Path to the spec-162 Phase B labels.jsonl.

.PARAMETER PromptPath
    Path to the committed shadow-prompt.md (default: this script's sibling).

.PARAMETER OutFile
    Optional path to also write the markdown report to.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ShadowRoot,
    [Parameter(Mandatory = $true)][string]$WorksheetPath,
    [Parameter(Mandatory = $true)][string]$LabelsPath,
    [string]$PromptPath = '',
    [string]$OutFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Defaults resolved here, not in the param block: $PSScriptRoot is not reliably populated during parameter
# default evaluation under Windows PowerShell 5.1.
if ([string]::IsNullOrEmpty($PromptPath)) {
    $PromptPath = Join-Path $PSScriptRoot 'shadow-prompt.md'
}

# The tau symbol is built from its code point so this file stays pure ASCII: Windows PowerShell 5.1 decodes a
# BOM-less UTF-8 script as ANSI and would otherwise corrupt the character in every emitted line.
$TAU = [string][char]0x03C4

# --- precommitted constants (FROZEN in spec 164 before any read ran) -------------------------------------

$PrimaryTau = 0.80
$FallbackTau = 0.90
$MissDenominator = 33
$NonMissDenominator = 57
$DirectionalDenominator = 145
$LabeledDenominator = 235
$MinStrictRecovery = 17   # >= 17 of 33
$MaxFalseAlarms = 9       # <= 9 of 57 (9 is WITHIN the bound)
$MaxInversions = 0        # one disqualifies outright
$MaxFlips = 15            # <= 15 of 145 (15 is WITHIN the bound)

# --- helpers ---------------------------------------------------------------------------------------------

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
    # (Radar.CalibrationAudit.AccessionHash).
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

function Get-LfNormalizedFileSha256 {
    # The spec-163 canonical hash: decode the file's bytes as UTF-8, normalize CRLF->LF, re-encode as UTF-8,
    # SHA-256, lowercase hex. Byte-level reads (never Get-Content) because Windows PowerShell 5.1 would
    # otherwise decode a BOM-less UTF-8 file as ANSI and corrupt non-ASCII characters.
    param([string]$Path)
    $fullPath = (Resolve-Path -LiteralPath $Path).ProviderPath
    $raw = [System.IO.File]::ReadAllBytes($fullPath)
    $text = [System.Text.Encoding]::UTF8.GetString($raw)
    $normalized = $text.Replace("`r`n", "`n")
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($normalized)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($bytes)
    } finally {
        $sha.Dispose()
    }
    return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
}

function Get-ConfidenceBin {
    # EXACT half-open intervals; the last bin is CLOSED at 1.00. Anything outside [0,1] maps to $null and is
    # reported, never binned. Identical to analyze-labels.ps1's bins (spec 162).
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

function Convert-ShadowDirection {
    # THE vocabulary map (see the header). Returns $null for anything else - never a guess.
    param([string]$Direction)
    switch ($Direction) {
        'Improving'     { return 'Positive' }
        'Deteriorating' { return 'Negative' }
        'Mixed'         { return 'Mixed' }
        'Neutral'       { return 'Neutral' }
        default         { return $null }
    }
}

function Test-ShadowDirectional {
    # DIRECTIONAL at tau: an `ok` read whose direction is Improving/Deteriorating AND whose confidence >= tau.
    param($Record, [double]$Tau)
    if ($null -eq $Record) { return $false }
    if ([string](Get-Prop $Record 'status' '') -ne 'ok') { return $false }
    $mapped = Convert-ShadowDirection ([string](Get-Prop $Record 'direction' ''))
    if ($mapped -ne 'Positive' -and $mapped -ne 'Negative') { return $false }
    $confidence = Get-Prop $Record 'confidence' $null
    if ($null -eq $confidence) { return $false }
    return ([double]$confidence -ge $Tau)
}

function Get-ShadowMappedDirection {
    param($Record)
    if ($null -eq $Record) { return $null }
    if ([string](Get-Prop $Record 'status' '') -ne 'ok') { return $null }
    return (Convert-ShadowDirection ([string](Get-Prop $Record 'direction' '')))
}

function Get-OppositeDirection {
    param([string]$Direction)
    if ($Direction -eq 'Positive') { return 'Negative' }
    if ($Direction -eq 'Negative') { return 'Positive' }
    return $null
}

# --- load inputs -----------------------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $WorksheetPath)) { throw "No worksheet at '$WorksheetPath'." }
if (-not (Test-Path -LiteralPath $LabelsPath)) { throw "No labels file at '$LabelsPath'." }
if (-not (Test-Path -LiteralPath $ShadowRoot)) { throw "No shadow root at '$ShadowRoot'." }
if (-not (Test-Path -LiteralPath $PromptPath)) {
    throw "No shadow prompt at '$PromptPath'. Its LF-normalized SHA-256 is recomputed here and checked against every record's promptSha256; the report cannot certify what instruction produced the reads without it."
}

# -ShadowRoot may name the shadow directory itself or its parent (the console's --output-root).
$shadowDir = (Resolve-Path -LiteralPath $ShadowRoot).ProviderPath
$nestedShadow = Join-Path $shadowDir 'shadow'
if (Test-Path -LiteralPath $nestedShadow) { $shadowDir = (Resolve-Path -LiteralPath $nestedShadow).ProviderPath }

$worksheet = @(Import-Csv -LiteralPath $WorksheetPath)
if ($worksheet.Count -eq 0) { throw "Worksheet '$WorksheetPath' is empty." }

$worksheetByAccession = @{}
foreach ($row in $worksheet) {
    if ($worksheetByAccession.ContainsKey($row.accession)) {
        throw "Duplicate accession '$($row.accession)' in the sealed worksheet - it must be one row per accession."
    }
    $worksheetByAccession[$row.accession] = $row
}

$rawLabels = @()
foreach ($line in @(Get-Content -LiteralPath $LabelsPath)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $rawLabels += (ConvertFrom-Json -InputObject $line)
}

# Effective label per accession = highest protocol.attempt (analyze-labels.ps1's rule, reused verbatim).
$labelsByAccession = @{}
foreach ($label in $rawLabels) {
    $acc = $label.accession
    $attempt = [int](Get-Prop (Get-Prop $label 'protocol') 'attempt' 1)
    if (-not $labelsByAccession.ContainsKey($acc)) {
        $labelsByAccession[$acc] = $label
    } else {
        $existingAttempt = [int](Get-Prop (Get-Prop $labelsByAccession[$acc] 'protocol') 'attempt' 1)
        if ($attempt -gt $existingAttempt) { $labelsByAccession[$acc] = $label }
    }
}

$records = @{}
$orphanRecords = @()
foreach ($file in @(Get-ChildItem -LiteralPath $shadowDir -Filter '*.json' -File | Sort-Object -Property Name)) {
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $obj = ConvertFrom-Json -InputObject ([System.Text.Encoding]::UTF8.GetString($bytes))
    $acc = [string](Get-Prop $obj 'accession' '')
    if ([string]::IsNullOrEmpty($acc)) {
        throw "Shadow record '$($file.FullName)' carries no accession."
    }
    if ($records.ContainsKey($acc)) {
        throw "Duplicate shadow record for accession '$acc' under '$shadowDir'."
    }
    $records[$acc] = $obj
    if (-not $worksheetByAccession.ContainsKey($acc)) { $orphanRecords += $acc }
}

$RecomputedPromptSha = Get-LfNormalizedFileSha256 -Path $PromptPath

# --- cohorts ---------------------------------------------------------------------------------------------

$directionalRows = @($worksheet | Where-Object { $_.outcome -eq 'DirectionalSignalProduced' } | Sort-Object -Property accessionSha256)
$noSignalRows = @($worksheet | Where-Object { $_.outcome -eq 'NoDirectionalSignal' } | Sort-Object -Property accessionSha256)

$labeledDirectional = @($directionalRows | Where-Object { $labelsByAccession.ContainsKey($_.accession) })
$labeledNoSignal = @($noSignalRows | Where-Object { $labelsByAccession.ContainsKey($_.accession) })
$unlabeledNoSignal = @($noSignalRows | Where-Object { -not $labelsByAccession.ContainsKey($_.accession) })

# A "provisional miss" counts ONLY from adjudication: adjudication.finalDirection directional (spec 162's
# rule, reused). Everything else in the labeled no-signal set - including a label with no finalDirection - is
# a provisional NON-miss.
$missRows = @()
$nonMissRows = @()
foreach ($row in $labeledNoSignal) {
    $final = [string](Get-Prop (Get-Prop $labelsByAccession[$row.accession] 'adjudication') 'finalDirection' '')
    if ($final -eq 'Positive' -or $final -eq 'Negative') {
        $missRows += (New-Object psobject -Property @{ Accession = $row.accession; Ticker = $row.ticker; Final = $final })
    } else {
        $nonMissRows += (New-Object psobject -Property @{ Accession = $row.accession; Ticker = $row.ticker; Final = $final })
    }
}

# --- measurement primitives (used by BOTH the descriptive tables and the frozen decision) ----------------

function Measure-Recovery {
    # Strict / loose recovery over the provisional misses, and false alarms over the provisional non-misses.
    param([double]$Tau)
    $strict = 0; $loose = 0; $falseAlarms = 0
    foreach ($m in $missRows) {
        $rec = $null
        if ($records.ContainsKey($m.Accession)) { $rec = $records[$m.Accession] }
        if (Test-ShadowDirectional -Record $rec -Tau $Tau) {
            $loose++
            if ((Get-ShadowMappedDirection $rec) -eq $m.Final) { $strict++ }
        }
    }
    foreach ($n in $nonMissRows) {
        $rec = $null
        if ($records.ContainsKey($n.Accession)) { $rec = $records[$n.Accession] }
        if (Test-ShadowDirectional -Record $rec -Tau $Tau) { $falseAlarms++ }
    }
    New-Object psobject -Property @{ Strict = $strict; Loose = $loose; FalseAlarms = $falseAlarms }
}

function Measure-Stability {
    # Over the sealed-directional rows: inversions (directional-at-tau, OPPOSITE the sealed direction) and
    # flips (any NONDIRECTIONAL-at-tau result). Agreement is the remainder.
    param([double]$Tau)
    $agree = 0; $inverted = 0; $flipped = 0
    foreach ($row in $labeledDirectional) {
        $rec = $null
        if ($records.ContainsKey($row.accession)) { $rec = $records[$row.accession] }
        if (Test-ShadowDirectional -Record $rec -Tau $Tau) {
            if ((Get-ShadowMappedDirection $rec) -eq $row.direction) { $agree++ } else { $inverted++ }
        } else {
            $flipped++
        }
    }
    New-Object psobject -Property @{ Agree = $agree; Inverted = $inverted; Flipped = $flipped }
}

# --- report ----------------------------------------------------------------------------------------------

$md = New-Object System.Collections.Generic.List[string]
function Add-Line { param([string]$Text = '') $md.Add($Text) | Out-Null }

Add-Line '# Spec 164 - shadow-mode forced-choice second pass'
Add-Line ''
Add-Line ('Generated (UTC): {0:o}' -f [DateTime]::UtcNow)
Add-Line ('Shadow records: {0} under `{1}`.' -f $records.Keys.Count, $shadowDir)
Add-Line ('Worksheet: {0} rows ({1} directional, {2} no-signal). Labels: {3} raw, {4} effective.' -f `
    $worksheet.Count, $directionalRows.Count, $noSignalRows.Count, $rawLabels.Count, $labelsByAccession.Keys.Count)
Add-Line ('Labeled rows: {0} no-signal + {1} directional = {2}. Provisional misses: {3}; provisional non-misses: {4}.' -f `
    $labeledNoSignal.Count, $labeledDirectional.Count, ($labeledNoSignal.Count + $labeledDirectional.Count), `
    $missRows.Count, $nonMissRows.Count)
Add-Line ''
Add-Line '## Vocabulary mapping (defined once, in this script)'
Add-Line ''
Add-Line '| shadow direction | worksheet / label direction |'
Add-Line '| --- | --- |'
Add-Line '| `Improving` | `Positive` |'
Add-Line '| `Deteriorating` | `Negative` |'
Add-Line '| `Mixed` | `Mixed` |'
Add-Line '| `Neutral` | `Neutral` |'
Add-Line ''
Add-Line 'No other equivalence is permitted. `Mixed` is NEVER "directional" and never agrees with a'
Add-Line 'directional label in the strict recovery rate.'
Add-Line ''
Add-Line '## Standing caveats (they apply to EVERY rate below)'
Add-Line ''
Add-Line '1. The reference labels are EXPLORATORY ratified same-family verdicts (spec 162), not ground truth.'
Add-Line '2. Filings cluster within tickers, so observations are not independent and the Wilson intervals are'
Add-Line '   somewhat narrower than the truth.'
Add-Line '3. The reads are SINGLE-SHOT against a non-deterministic model. This is a RECORD of one pass, not an'
Add-Line '   average - re-running may move individual rows.'
Add-Line ''

# -- Section 0: provenance + status coverage --
Add-Line '## 0. Provenance and status coverage'
Add-Line ''
Add-Line ('Committed prompt: `{0}`' -f $PromptPath)
Add-Line ('Recomputed LF-normalized SHA-256: `{0}`' -f $RecomputedPromptSha)
Add-Line ''

$promptHashes = @{}
$modelIdentities = @{}
$versionTokens = @{}
foreach ($acc in $records.Keys) {
    $r = $records[$acc]
    $h = [string](Get-Prop $r 'promptSha256' '')
    $m = [string](Get-Prop $r 'modelIdentity' '')
    $v = [string](Get-Prop $r 'promptVersion' '')
    if (-not $promptHashes.ContainsKey($h)) { $promptHashes[$h] = 0 }
    if (-not $modelIdentities.ContainsKey($m)) { $modelIdentities[$m] = 0 }
    if (-not $versionTokens.ContainsKey($v)) { $versionTokens[$v] = 0 }
    $promptHashes[$h] = $promptHashes[$h] + 1
    $modelIdentities[$m] = $modelIdentities[$m] + 1
    $versionTokens[$v] = $versionTokens[$v] + 1
}

Add-Line '| recorded field | distinct values (count) |'
Add-Line '| --- | --- |'
Add-Line ('| promptVersion | {0} |' -f ((@($versionTokens.Keys | Sort-Object) | ForEach-Object { '`{0}` ({1})' -f $_, $versionTokens[$_] }) -join ', '))
Add-Line ('| promptSha256 | {0} |' -f ((@($promptHashes.Keys | Sort-Object) | ForEach-Object { '`{0}` ({1})' -f $_, $promptHashes[$_] }) -join ', '))
Add-Line ('| modelIdentity | {0} |' -f ((@($modelIdentities.Keys | Sort-Object) | ForEach-Object { '`{0}` ({1})' -f $_, $modelIdentities[$_] }) -join ', '))
Add-Line ''

$provenanceViolations = @()
foreach ($acc in @($records.Keys | Sort-Object { Get-AccessionSha256 $_ })) {
    $h = [string](Get-Prop $records[$acc] 'promptSha256' '')
    if ($h -ne $RecomputedPromptSha) { $provenanceViolations += $acc }
}
if ($provenanceViolations.Count -gt 0) {
    Add-Line ('**PROVENANCE VIOLATION** - {0} record(s) carry a promptSha256 other than the committed prompt''s recomputed hash: {1}' -f `
        $provenanceViolations.Count, (($provenanceViolations | Select-Object -First 20) -join ', '))
    Add-Line 'Reads taken under two different instructions must never be pooled; the decision block below is INCONCLUSIVE.'
    Add-Line ''
} else {
    Add-Line 'Every record carries the committed prompt hash: the reads below were all taken under one instruction.'
    Add-Line ''
}

if ($orphanRecords.Count -gt 0) {
    Add-Line ('Shadow records with no worksheet row (listed, never silently dropped): {0}' -f (($orphanRecords | Sort-Object) -join ', '))
    Add-Line ''
}

function Get-StatusTally {
    param($Rows)
    $ok = 0; $callFailed = 0; $parseFailed = 0; $missing = 0; $other = 0
    foreach ($row in $Rows) {
        if (-not $records.ContainsKey($row.accession)) { $missing++; continue }
        switch ([string](Get-Prop $records[$row.accession] 'status' '')) {
            'ok'           { $ok++ }
            'call-failed'  { $callFailed++ }
            'parse-failed' { $parseFailed++ }
            default        { $other++ }
        }
    }
    New-Object psobject -Property @{ Ok = $ok; CallFailed = $callFailed; ParseFailed = $parseFailed; Missing = $missing; Other = $other }
}

$statusLabeledNoSignal = Get-StatusTally $labeledNoSignal
$statusLabeledDirectional = Get-StatusTally $labeledDirectional
$statusUnlabeled = Get-StatusTally $unlabeledNoSignal

Add-Line '| cohort | n | ok | call-failed | parse-failed | no record | other |'
Add-Line '| --- | --- | --- | --- | --- | --- | --- |'
Add-Line ('| labeled no-signal | {0} | {1} | {2} | {3} | {4} | {5} |' -f $labeledNoSignal.Count, $statusLabeledNoSignal.Ok, $statusLabeledNoSignal.CallFailed, $statusLabeledNoSignal.ParseFailed, $statusLabeledNoSignal.Missing, $statusLabeledNoSignal.Other)
Add-Line ('| labeled directional | {0} | {1} | {2} | {3} | {4} | {5} |' -f $labeledDirectional.Count, $statusLabeledDirectional.Ok, $statusLabeledDirectional.CallFailed, $statusLabeledDirectional.ParseFailed, $statusLabeledDirectional.Missing, $statusLabeledDirectional.Other)
Add-Line ('| UNLABELED no-signal | {0} | {1} | {2} | {3} | {4} | {5} |' -f $unlabeledNoSignal.Count, $statusUnlabeled.Ok, $statusUnlabeled.CallFailed, $statusUnlabeled.ParseFailed, $statusUnlabeled.Missing, $statusUnlabeled.Other)
Add-Line ''
Add-Line 'A non-`ok` status is recorded SEPARATELY from the result: an infrastructure failure is never counted'
Add-Line 'as a `Neutral` (or any other) read. Failed rows are re-runnable - rerun the console, which retries'
Add-Line 'anything that is not `ok`.'
Add-Line ''

# -- Section 1: recovery (the headline) --
$recoveryAll = Measure-Recovery -Tau 0.0

Add-Line ('## 1. Recovery table - the {0} labeled no-signal rows (HEADLINE)' -f $labeledNoSignal.Count)
Add-Line ''
Add-Line 'Does the forced-choice prompt recover the provisional misses without flooding the non-misses?'
Add-Line 'Rates here use NO confidence threshold (any directional read counts); the frozen decision below'
Add-Line ('applies {0} = {1:N2}.' -f $TAU, $PrimaryTau)
Add-Line ''
Add-Line ('- STRICT recovery P(forced directional AND direction agrees with the adjudicated finalDirection | provisional miss): {0}' -f (Format-Rate -Successes $recoveryAll.Strict -N $missRows.Count))
Add-Line ('- LOOSE recovery P(forced directional | provisional miss): {0}' -f (Format-Rate -Successes $recoveryAll.Loose -N $missRows.Count))
Add-Line ('- FALSE ALARM P(forced Improving/Deteriorating | provisional NON-miss): {0}' -f (Format-Rate -Successes $recoveryAll.FalseAlarms -N $nonMissRows.Count))
Add-Line ''
Add-Line 'A miss recovered with the WRONG direction counts in the LOOSE rate and NOT in the STRICT one.'
Add-Line '`Mixed` and `Neutral` are not directional and count in neither.'
Add-Line ''
Add-Line 'Broken out by FORCED-read confidence bin (the operating point a production spec would need):'
Add-Line ''
Add-Line '| forced confidence bin | misses in bin | strict | loose | non-misses in bin | false alarms |'
Add-Line '| --- | --- | --- | --- | --- | --- |'

function Get-RecordBin {
    param($Record)
    if ($null -eq $Record) { return $null }
    if ([string](Get-Prop $Record 'status' '') -ne 'ok') { return $null }
    $c = Get-Prop $Record 'confidence' $null
    if ($null -eq $c) { return $null }
    return (Get-ConfidenceBin ([double]$c))
}

foreach ($bin in $BinOrder) {
    $missInBin = 0; $strictInBin = 0; $looseInBin = 0
    foreach ($m in $missRows) {
        $rec = $null
        if ($records.ContainsKey($m.Accession)) { $rec = $records[$m.Accession] }
        if ((Get-RecordBin $rec) -ne $bin) { continue }
        $missInBin++
        if (Test-ShadowDirectional -Record $rec -Tau 0.0) {
            $looseInBin++
            if ((Get-ShadowMappedDirection $rec) -eq $m.Final) { $strictInBin++ }
        }
    }
    $nonMissInBin = 0; $falseInBin = 0
    foreach ($n in $nonMissRows) {
        $rec = $null
        if ($records.ContainsKey($n.Accession)) { $rec = $records[$n.Accession] }
        if ((Get-RecordBin $rec) -ne $bin) { continue }
        $nonMissInBin++
        if (Test-ShadowDirectional -Record $rec -Tau 0.0) { $falseInBin++ }
    }
    Add-Line ('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $bin, $missInBin, $strictInBin, $looseInBin, $nonMissInBin, $falseInBin)
}
Add-Line ''
Add-Line 'Rows with no `ok` record fall in no bin and are listed in section 0.'
Add-Line ''

# -- Section 2: stability --
$stabilityAll = Measure-Stability -Tau 0.0

Add-Line ('## 2. Stability table - the {0} labeled directional rows' -f $labeledDirectional.Count)
Add-Line ''
Add-Line 'Forced direction vs the SEALED production direction. A forced prompt that degrades the directional'
Add-Line 'cohort is disqualifying evidence, and inversions are worse than abstentions.'
Add-Line ''
Add-Line ('- Agrees with the sealed direction: {0}' -f (Format-Rate -Successes $stabilityAll.Agree -N $labeledDirectional.Count))
Add-Line ('- Flipped to `Mixed`/`Neutral` (or unresolved): {0}' -f (Format-Rate -Successes $stabilityAll.Flipped -N $labeledDirectional.Count))
Add-Line ('- INVERTED (directional, opposite the sealed direction): {0}' -f (Format-Rate -Successes $stabilityAll.Inverted -N $labeledDirectional.Count))
Add-Line ''
Add-Line '| sealed confidence bin | n | agree | flipped to Mixed/Neutral | inverted | no `ok` record |'
Add-Line '| --- | --- | --- | --- | --- | --- |'
foreach ($bin in $BinOrder) {
    $inBin = @($labeledDirectional | Where-Object { (Get-ConfidenceBin ([double]$_.confidence)) -eq $bin })
    $agree = 0; $flip = 0; $inverted = 0; $unresolved = 0
    foreach ($row in $inBin) {
        $rec = $null
        if ($records.ContainsKey($row.accession)) { $rec = $records[$row.accession] }
        if ($null -eq $rec -or [string](Get-Prop $rec 'status' '') -ne 'ok') { $unresolved++; continue }
        $mapped = Get-ShadowMappedDirection $rec
        if ($mapped -eq $row.direction) { $agree++ }
        elseif ($mapped -eq (Get-OppositeDirection $row.direction)) { $inverted++ }
        else { $flip++ }
    }
    Add-Line ('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $bin, $inBin.Count, $agree, $flip, $inverted, $unresolved)
}
Add-Line ''
Add-Line ('Bins are the SEALED reader confidence. At {0} = 0 an "inverted" row is any directional read opposite' -f $TAU)
Add-Line ('the sealed direction; the decision block re-evaluates inversions and flips at {0} = {1:N2}.' -f $TAU, $PrimaryTau)
Add-Line ''

# -- Section 3: unlabeled distribution --
Add-Line ('## 3. Unlabeled no-signal rows - DISTRIBUTION ONLY ({0} rows)' -f $unlabeledNoSignal.Count)
Add-Line ''
Add-Line '**No reference labels exist for these rows: this is a DISTRIBUTION, not accuracy.** They enter no'
Add-Line 'recovery, false-alarm or stability rate anywhere in this report, and failures among them do not block'
Add-Line 'the decision below.'
Add-Line ''
Add-Line '| forced direction | n |'
Add-Line '| --- | --- |'
foreach ($direction in @('Improving', 'Deteriorating', 'Mixed', 'Neutral')) {
    $n = 0
    foreach ($row in $unlabeledNoSignal) {
        if (-not $records.ContainsKey($row.accession)) { continue }
        $rec = $records[$row.accession]
        if ([string](Get-Prop $rec 'status' '') -ne 'ok') { continue }
        if ([string](Get-Prop $rec 'direction' '') -eq $direction) { $n++ }
    }
    Add-Line ('| {0} | {1} |' -f $direction, $n)
}
Add-Line ('| (no `ok` record) | {0} |' -f ($statusUnlabeled.CallFailed + $statusUnlabeled.ParseFailed + $statusUnlabeled.Missing + $statusUnlabeled.Other))
Add-Line ''
Add-Line '| forced confidence bin | n |'
Add-Line '| --- | --- |'
foreach ($bin in $BinOrder) {
    $n = 0
    foreach ($row in $unlabeledNoSignal) {
        $rec = $null
        if ($records.ContainsKey($row.accession)) { $rec = $records[$row.accession] }
        if ((Get-RecordBin $rec) -eq $bin) { $n++ }
    }
    Add-Line ('| {0} | {1} |' -f $bin, $n)
}
Add-Line ''

# -- Section 4: the precommitted decision block --
Add-Line '## 4. PRECOMMITTED DECISION BLOCK (frozen in spec 164 before any read ran)'
Add-Line ''
Add-Line ('Threshold semantics: a forced read is DIRECTIONAL at {0} iff its direction is `Improving` or' -f $TAU)
Add-Line ('`Deteriorating` AND its confidence >= {0}. Anything else - `Mixed`, `Neutral`, or a directional read' -f $TAU)
Add-Line ('below {0} - is NONDIRECTIONAL at {0}. An INVERSION is a directional-at-{0} read OPPOSITE the sealed' -f $TAU)
Add-Line ('direction; a FLIP is any nondirectional-at-{0} result on a sealed-directional row.' -f $TAU)
Add-Line ''
Add-Line ('SUPPORTED requires ALL of: strict recovery >= {0} of {1}; false alarms <= {2} of {3}; inversions = {4}; flips <= {5} of {6}.' -f `
    $MinStrictRecovery, $MissDenominator, $MaxFalseAlarms, $NonMissDenominator, $MaxInversions, $MaxFlips, $DirectionalDenominator)
Add-Line ('Primary {0} = {1:N2}; ONE-SHOT fallback {0} = {2:N2} if the primary fails. No further threshold shopping.' -f $TAU, $PrimaryTau, $FallbackTau)
Add-Line ''

# Completeness gate FIRST.
$unresolvedLabeled = @()
foreach ($row in @($labeledNoSignal + $labeledDirectional)) {
    if (-not $records.ContainsKey($row.accession)) {
        $unresolvedLabeled += ('{0} (no record)' -f $row.accession)
        continue
    }
    $status = [string](Get-Prop $records[$row.accession] 'status' '')
    if ($status -ne 'ok') { $unresolvedLabeled += ('{0} ({1})' -f $row.accession, $status) }
}
$unresolvedLabeled = @($unresolvedLabeled | Sort-Object)

$denominatorProblems = @()
if ($missRows.Count -ne $MissDenominator) {
    $denominatorProblems += ('provisional misses {0}, precommitted {1}' -f $missRows.Count, $MissDenominator)
}
if ($nonMissRows.Count -ne $NonMissDenominator) {
    $denominatorProblems += ('provisional non-misses {0}, precommitted {1}' -f $nonMissRows.Count, $NonMissDenominator)
}
if ($labeledDirectional.Count -ne $DirectionalDenominator) {
    $denominatorProblems += ('labeled directional rows {0}, precommitted {1}' -f $labeledDirectional.Count, $DirectionalDenominator)
}
if (($labeledNoSignal.Count + $labeledDirectional.Count) -ne $LabeledDenominator) {
    $denominatorProblems += ('labeled rows {0}, precommitted {1}' -f ($labeledNoSignal.Count + $labeledDirectional.Count), $LabeledDenominator)
}

$primary = New-Object psobject -Property @{
    Tau = $PrimaryTau; Recovery = (Measure-Recovery -Tau $PrimaryTau); Stability = (Measure-Stability -Tau $PrimaryTau)
}
$fallback = New-Object psobject -Property @{
    Tau = $FallbackTau; Recovery = (Measure-Recovery -Tau $FallbackTau); Stability = (Measure-Stability -Tau $FallbackTau)
}

function Format-DecisionNumbers {
    # The MACHINE-READABLE line: tau is formatted INVARIANT so the outcome text is identical on every
    # machine regardless of the current culture's decimal separator (AD-3, determinism).
    param($Evaluation)
    $tauText = ([double]$Evaluation.Tau).ToString('0.00', [System.Globalization.CultureInfo]::InvariantCulture)
    return ('{0}={1}, strict recovery {2}/{3}, false alarms {4}/{5}, inversions {6}/{7}, flips {8}/{7}' -f `
        $TAU, $tauText, $Evaluation.Recovery.Strict, $MissDenominator, `
        $Evaluation.Recovery.FalseAlarms, $NonMissDenominator, `
        $Evaluation.Stability.Inverted, $DirectionalDenominator, $Evaluation.Stability.Flipped)
}

function Test-Criteria {
    param($Evaluation)
    return (($Evaluation.Recovery.Strict -ge $MinStrictRecovery) `
        -and ($Evaluation.Recovery.FalseAlarms -le $MaxFalseAlarms) `
        -and ($Evaluation.Stability.Inverted -le $MaxInversions) `
        -and ($Evaluation.Stability.Flipped -le $MaxFlips))
}

Add-Line '| criterion | bound | at primary | at fallback |'
Add-Line '| --- | --- | --- | --- |'
Add-Line ('| strict recovery | >= {0}/{1} | {2}/{1} | {3}/{1} |' -f $MinStrictRecovery, $MissDenominator, $primary.Recovery.Strict, $fallback.Recovery.Strict)
Add-Line ('| false alarms | <= {0}/{1} | {2}/{1} | {3}/{1} |' -f $MaxFalseAlarms, $NonMissDenominator, $primary.Recovery.FalseAlarms, $fallback.Recovery.FalseAlarms)
Add-Line ('| inversions | = {0} | {1}/{2} | {3}/{2} |' -f $MaxInversions, $primary.Stability.Inverted, $DirectionalDenominator, $fallback.Stability.Inverted)
Add-Line ('| flips | <= {0}/{1} | {2}/{1} | {3}/{1} |' -f $MaxFlips, $DirectionalDenominator, $primary.Stability.Flipped, $fallback.Stability.Flipped)
Add-Line ''

$outcomeLine = ''
if ($provenanceViolations.Count -gt 0) {
    $outcomeLine = ('SHADOW: INCONCLUSIVE (prompt provenance: {0} record(s) not taken under the committed prompt {1}: {2})' -f `
        $provenanceViolations.Count, $RecomputedPromptSha, (($provenanceViolations | Select-Object -First 20) -join ', '))
} elseif ($unresolvedLabeled.Count -gt 0) {
    $outcomeLine = ('SHADOW: INCONCLUSIVE ({0} rows unresolved: {1})' -f $unresolvedLabeled.Count, ($unresolvedLabeled -join ', '))
} elseif ($denominatorProblems.Count -gt 0) {
    $outcomeLine = ('SHADOW: INCONCLUSIVE (denominator drift - the frozen criteria are absolute counts over 33/57/145: {0})' -f `
        ($denominatorProblems -join '; '))
} elseif (Test-Criteria $primary) {
    $outcomeLine = ('SHADOW: SUPPORTED ({0})' -f (Format-DecisionNumbers $primary))
} elseif (Test-Criteria $fallback) {
    $outcomeLine = ('SHADOW: SUPPORTED ({0})' -f (Format-DecisionNumbers $fallback))
} else {
    $outcomeLine = ('SHADOW: NOT-SUPPORTED ({0}; {1})' -f (Format-DecisionNumbers $primary), (Format-DecisionNumbers $fallback))
}

Add-Line '```'
Add-Line $outcomeLine
Add-Line '```'
Add-Line ''
if ($unresolvedLabeled.Count -gt 0) {
    Add-Line 'The remedy for an unresolved row is to RERUN it - never to decide on a subset. The criteria are'
    Add-Line 'absolute counts over the fixed denominators 33 / 57 / 145.'
    Add-Line ''
}
Add-Line 'SUPPORTED means the production recall spec may proceed citing this rule. NOT-SUPPORTED means the'
Add-Line 'misses need a different mechanism (second model / pre-screen - their own specs).'
Add-Line ''
Add-Line '**EVERY OTHER NUMBER IN THIS REPORT IS DESCRIPTIVE ONLY AND GROUNDS NO PRODUCTION RECOMMENDATION.**'
Add-Line ''

# -- Descriptive threshold sweep (explicitly NOT part of the decision) --
Add-Line '### Descriptive threshold sweep - DESCRIPTIVE ONLY'
Add-Line ''
Add-Line 'This sweep exists to show the shape of the trade-off. It is NOT a menu: the decision above was'
Add-Line ('evaluated at the two precommitted points ({0} = {1:N2}, then once at {2:N2}) and nowhere else.' -f $TAU, $PrimaryTau, $FallbackTau)
Add-Line ''
Add-Line ('| {0} | strict recovery | loose recovery | false alarms | inversions | flips |' -f $TAU)
Add-Line '| --- | --- | --- | --- | --- | --- |'
foreach ($tau in @(0.00, 0.50, 0.60, 0.70, 0.80, 0.90, 0.95)) {
    $r = Measure-Recovery -Tau $tau
    $s = Measure-Stability -Tau $tau
    Add-Line ('| {0:N2} | {1}/{2} | {3}/{2} | {4}/{5} | {6}/{7} | {8}/{7} |' -f `
        $tau, $r.Strict, $missRows.Count, $r.Loose, $r.FalseAlarms, $nonMissRows.Count, `
        $s.Inverted, $labeledDirectional.Count, $s.Flipped)
}
Add-Line ''

$reportText = $md -join [Environment]::NewLine
if ($OutFile) { Set-Content -LiteralPath $OutFile -Value $reportText -Encoding UTF8 }
Write-Output $reportText

if ($outcomeLine -like 'SHADOW: INCONCLUSIVE*') { exit 1 }
exit 0
