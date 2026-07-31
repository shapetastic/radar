<#
.SYNOPSIS
    Spec 162/163 - AI filing-read calibration audit: joins blinded labels to the sealed worksheet,
    VALIDATES the study artifacts against the pinned study contract, and renders the study report
    (PowerShell 5.1-compatible; no scoring math, no runtime value ever flows from here).

.DESCRIPTION
    Inputs:
      - labels.jsonl       one JSON object per line (canonical schema below), produced by the blinded
                           labeling protocol.
      - worksheet.csv      the SEALED model answers, written by the Radar.CalibrationAudit console
                           (src/Radar.CalibrationAudit). The sealed model answer is joined HERE, at
                           analysis time - it is never stored in the label file.
      - exhibit-manifest.csv  the archive manifest written by Radar.CalibrationAudit (default: the
                           worksheet's sibling). Every label's modelInputHash is verified against it.
      - study-contract.json   the PINNED precommitted protocol values (labeler, protocol version,
                           prompt-template hash), loaded from THIS SCRIPT'S OWN DIRECTORY - deliberately
                           not a parameter, so no production switch can substitute contract values.
      - labeling-prompt.md the prompt template; its hash is RECOMPUTED here and checked against the
                           contract AND against every label's protocol.promptHash.
      - pilot CSV          docs/162-calibration-pilot-labels.csv (schema pilot-flat, lossy legacy) for the
                           input-path stability table.

    HASH CANONICALIZATION (spec 163): the prompt-template hash is computed over CRLF->LF-normalized UTF-8
    bytes (decode the file as UTF-8, replace "\r\n" with "\n", re-encode, SHA-256). The repo checks the
    template out CRLF on Windows and LF on CI (core.autocrlf, no .gitattributes), so the raw-byte hash
    differs between machines while the LF-normalized hash is stable. Phase-B labels must record this same
    LF-normalized hash as protocol.promptHash.

    MODES (spec 163 - the ONE incomplete-vs-fail rule):
      - FINAL (default): ANY incompleteness (missing labels, unadjudicated sample members, pending miss
        candidates) or ANY correctness violation (membership mismatch, provenance mismatch, precommitment
        violation) => nonzero exit and NO report artifact is written (-OutFile untouched) - a partial
        final report on disk is indistinguishable from a real one later.
      - -Interim: incompleteness renders the affected sections with INCOMPLETE headers and explicit
        missing-accession lists (exit 0); correctness violations still FAIL (nonzero, nothing written).
        Interim relaxes COMPLETENESS only, never correctness.
      - -EmitSample: renders ONLY the calibration probability sample selection (the adjudicator's
        worklist). Label-independent by construction (the sample derives from the sealed worksheet
        alone), so it bypasses the gating entirely and is stable at any time.

    Report sections (all headline rates carry Wilson 95% intervals):
      1. "Inter-model agreement curve"  - reader-confidence bins x skeptic-agreement rate. AGREEMENT IS
         NOT CALIBRATION: two models can agree and both be wrong.
      2. Calibration probability sample - derived from the COMPLETE directional worksheet alone:
         min(10, bin size) rows per sealed-reader-confidence bin, selected by SHA-256(accession) hex
         ASCENDING within the bin, IRRESPECTIVE of agreement status or which labels exist.
      3. Calibration table - bins x adjudicated correctness (adjudication = the resolved
         adjudication.finalDirection verdicts; HOW they were resolved - blinded human vs ratified agent
         rereads - is a property of the study run, recorded in the findings doc, not of this script),
         computed EXCLUSIVELY over the DERIVED
         sample members (a label CLAIMING selectionReason "calibration-sample" outside the derived set is
         a correctness FAILURE - the claim is a checked assertion, not an input). Disagreement/doubt-queued
         adjudications are NEVER pooled into these rates. Empty bins render "no adjudicated labels",
         never interpolated.
      4. Error-diagnosis set - ALL remaining disagreements + ALL doubt-flagged labels, reported separately.
      5. Clean-rate, comparability-item frequency (the cmpscan-v2 evidence table), materiality x
         constant-strength cross-tab.
      6. False-omission section - the no-signal cohort's precommitted sample (first 60 by
         SHA-256(accession) hex order, one-shot extension to 90), membership-validated; a "miss" counts
         ONLY from adjudication (finalDirection directional); the reported rate is the FALSE-OMISSION
         rate P(directional | reader emitted no signal), not a recall; the extension trigger is ALWAYS
         computed on rows 1-60 and an explicit EXTENSION decision block is emitted.
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

    PROTOCOL RULES THIS SCRIPT ENFORCES (spec 163 upgraded these from warnings to failures):
      - Confidence bins are EXACT half-open intervals: [0,0.60), [0.60,0.70), [0.70,0.80), [0.80,0.90),
        [0.90,0.95), [0.95,1.00]  (the last is CLOSED at 1.00).
      - The calibration sample is DERIVED from the sealed worksheet; recorded membership claims are
        cross-checked against it exactly.
      - FINAL mode requires FULL directional coverage: the labeled directional accession set must equal
        the complete directional worksheet set exactly.
      - The labeled no-signal set must be exactly the first 60 - or exactly the first 90, and only when
        the rows-1-60 trigger fired - by SHA-256(accession) hex order. Anything else fails listing the
        difference.
      - Adjudicated correctness REQUIRES adjudication.finalDirection: a row is correct iff finalDirection
        equals the sealed model direction (ordinal, case-insensitive). Rows without finalDirection count as
        unresolved, never guessed.
      - Retries: the effective label per accession is the HIGHEST protocol.attempt; replaced attempts are
        reported but excluded from rates.
      - Every effective label's protocol.version, labeler.provider/model and promptHash must equal the
        study contract's pinned values; every modelInputHash must equal the manifest's modelInputSha256.
      - Labels that join to no worksheet row are listed loudly, never silently dropped.

.PARAMETER LabelsPath
    Path to labels.jsonl.

.PARAMETER WorksheetPath
    Path to the sealed worksheet.csv written by Radar.CalibrationAudit.

.PARAMETER ManifestPath
    Path to exhibit-manifest.csv (default: the worksheet's sibling exhibit-manifest.csv).

.PARAMETER PromptTemplatePath
    Path to the labeling prompt template (default: this script's sibling labeling-prompt.md).

.PARAMETER PilotCsvPath
    Path to the lossy legacy pilot summary (default docs/162-calibration-pilot-labels.csv relative to the
    repo root).

.PARAMETER OutFile
    Optional path to also write the markdown report to. NEVER written when the run fails validation.

.PARAMETER EmitSample
    Render ONLY the calibration probability sample selection (the adjudicator's worklist).

.PARAMETER Interim
    Interim mode: incompleteness renders INCOMPLETE sections instead of failing. Correctness violations
    still fail. Without this switch the run is a FINAL report and requires completeness.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LabelsPath,
    [Parameter(Mandatory = $true)][string]$WorksheetPath,
    [string]$ManifestPath = '',
    [string]$PromptTemplatePath = '',
    [string]$PilotCsvPath = '',
    [string]$OutFile,
    [switch]$EmitSample,
    [switch]$Interim
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Defaults resolved here, not in the param block: $PSScriptRoot is not reliably populated during
# parameter-default evaluation under Windows PowerShell 5.1.
if ([string]::IsNullOrEmpty($PilotCsvPath)) {
    # Repo root = two levels above this script (scripts/calibration-audit/ -> repo). When the script runs
    # from a SHALLOW directory the second Split-Path yields an EMPTY string ('/tmp/<sandbox>' on Linux —
    # exactly where the CI test sandbox lives — or 'C:\<dir>' on Windows), and Join-Path throws
    # "Cannot bind argument to parameter 'Path' because it is an empty string" before anything is emitted.
    # No repo root to find => leave the path empty; the pilot-table section renders "not found" instead.
    # Child path is composed with Join-Path (never a '\' literal, which is a filename character on Linux).
    $scriptParent = Split-Path -Parent $PSScriptRoot
    $repoRoot = if ([string]::IsNullOrEmpty($scriptParent)) { '' } else { Split-Path -Parent $scriptParent }
    if (-not [string]::IsNullOrEmpty($repoRoot)) {
        $PilotCsvPath = Join-Path $repoRoot (Join-Path 'docs' '162-calibration-pilot-labels.csv')
    }
}
if ([string]::IsNullOrEmpty($ManifestPath)) {
    $ManifestPath = Join-Path (Split-Path -Parent $WorksheetPath) 'exhibit-manifest.csv'
}
if ([string]::IsNullOrEmpty($PromptTemplatePath)) {
    $PromptTemplatePath = Join-Path $PSScriptRoot 'labeling-prompt.md'
}

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

function Get-LfNormalizedFileSha256 {
    # The spec-163 canonical prompt-template hash: decode the file's bytes as UTF-8, normalize CRLF->LF,
    # re-encode as UTF-8, SHA-256, lowercase hex. Byte-level reads (never Get-Content) because Windows
    # PowerShell 5.1 would otherwise decode a BOM-less UTF-8 file as ANSI and corrupt non-ASCII characters.
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

# --- study contract (spec 163: pinned beside this script, NEVER a parameter) -----------------------------

$ContractPath = Join-Path $PSScriptRoot 'study-contract.json'
if (-not (Test-Path -LiteralPath $ContractPath)) {
    throw "No study contract at '$ContractPath'. The precommitted protocol values (labeler, protocol version, prompt-template hash) are pinned there; the analyzer cannot validate anything without them."
}
try {
    $contractBytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ContractPath).ProviderPath)
    $Contract = ConvertFrom-Json -InputObject ([System.Text.Encoding]::UTF8.GetString($contractBytes))
} catch {
    throw "Study contract '$ContractPath' is unreadable: $($_.Exception.Message)"
}
$ContractProtocolVersion = [string](Get-Prop $Contract 'protocolVersion' '')
$ContractLabelerProvider = [string](Get-Prop (Get-Prop $Contract 'labeler') 'provider' '')
$ContractLabelerModel = [string](Get-Prop (Get-Prop $Contract 'labeler') 'model' '')
$ContractPromptSha = ([string](Get-Prop $Contract 'promptTemplateSha256' '')).ToLowerInvariant()
$ContractCanonicalization = [string](Get-Prop $Contract 'hashCanonicalization' '')
if ([string]::IsNullOrEmpty($ContractProtocolVersion) -or [string]::IsNullOrEmpty($ContractLabelerProvider) `
        -or [string]::IsNullOrEmpty($ContractLabelerModel) -or [string]::IsNullOrEmpty($ContractPromptSha)) {
    throw "Study contract '$ContractPath' is missing required fields (protocolVersion, labeler.provider, labeler.model, promptTemplateSha256)."
}
if ($ContractCanonicalization -ne 'utf8-crlf-to-lf') {
    throw "Study contract '$ContractPath' declares hashCanonicalization '$ContractCanonicalization'; this analyzer implements exactly 'utf8-crlf-to-lf' (decode UTF-8, replace CRLF with LF, re-encode, SHA-256)."
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
foreach ($line in @(Get-Content -LiteralPath $LabelsPath)) {
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

# --- worksheet cohorts ------------------------------------------------------------------------------------

$directionalRows = @($worksheet | Where-Object { $_.outcome -eq 'DirectionalSignalProduced' })
$noSignalRows = @($worksheet | Where-Object { $_.outcome -eq 'NoDirectionalSignal' })

$protocolWarnings = @()
$protocolWarnings += $duplicateLabelWarnings
$orphanLabels = @()
foreach ($acc in $labelsByAccession.Keys) {
    if (-not $worksheetByAccession.ContainsKey($acc)) { $orphanLabels += $acc }
}

# Directional worksheet rows annotated with the bin + ordering key (label-INDEPENDENT: the sample below
# derives from these rows alone, so -EmitSample is stable whether zero or all labels exist).
$wsDirectional = @()
foreach ($row in $directionalRows) {
    $bin = Get-ConfidenceBin ([double]$row.confidence)
    if ($null -eq $bin) {
        $protocolWarnings += ('{0}: sealed model confidence {1} outside [0,1] - excluded from every bin and sample table, never silently dropped' -f $row.accession, $row.confidence)
    }
    $wsDirectional += (New-Object psobject -Property @{
        Accession       = $row.accession
        Sha             = $row.accessionSha256
        Ticker          = $row.ticker
        ModelDirection  = $row.direction
        ModelConfidence = [double]$row.confidence
        ModelStrength   = $row.strength
        Bin             = $bin
    })
}

# --- calibration probability sample (derived from the COMPLETE directional worksheet alone; spec 163) ---

# min(10, bin size) per confidence bin, SHA-256(accession) hex ASCENDING within the bin, IRRESPECTIVE of
# agreement status AND of which labels exist. Deterministic: same worksheet => same sample, any machine,
# any time. Recorded selectionReason claims are cross-checked against THIS set exactly (never trusted).
$sampleByBin = @{}
foreach ($bin in $BinOrder) {
    $inBin = @($wsDirectional | Where-Object { $_.Bin -eq $bin } | Sort-Object -Property Sha)
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

# --- join (labeled directional rows) ---------------------------------------------------------------------

$joined = @()
foreach ($ws in $wsDirectional) {
    if (-not $labelsByAccession.ContainsKey($ws.Accession)) { continue }
    $label = $labelsByAccession[$ws.Accession]
    $labelBody = Get-Prop $label 'label'
    $labelDirection = [string](Get-Prop $labelBody 'direction' '')
    $agree = ($labelDirection -ieq $ws.ModelDirection)
    $joined += (New-Object psobject -Property @{
        Accession       = $ws.Accession
        Sha             = $ws.Sha
        Ticker          = $ws.Ticker
        ModelDirection  = $ws.ModelDirection
        ModelConfidence = $ws.ModelConfidence
        ModelStrength   = $ws.ModelStrength
        Bin             = $ws.Bin
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
$joinedByAccession = @{}
foreach ($row in $joined) { $joinedByAccession[$row.Accession] = $row }

# --- report scaffold + sample section (label-independent; -EmitSample exits here) ------------------------

$md = New-Object System.Collections.Generic.List[string]
function Add-Line { param([string]$Text = '') $md.Add($Text) | Out-Null }

Add-Line '# Spec 162 - filing-read calibration analysis'
Add-Line ''
Add-Line ('Generated (UTC): {0:o}' -f [DateTime]::UtcNow)
Add-Line ('Mode: {0}' -f $(if ($EmitSample) { 'sample emission' } elseif ($Interim) { 'INTERIM (completeness relaxed; correctness still enforced)' } else { 'FINAL (completeness and correctness enforced)' }))
Add-Line ('Labels: {0} raw entries, {1} effective (highest attempt per accession). Worksheet: {2} rows ({3} directional, {4} no-signal).' -f `
    $rawLabels.Count, $labelsByAccession.Keys.Count, $worksheet.Count, $directionalRows.Count, $noSignalRows.Count)
Add-Line ('Directional rows with an effective label: {0}.' -f $joined.Count)
Add-Line ''

# -- Section: calibration probability sample (the adjudicator worklist) --
Add-Line '## Calibration probability sample (derived from the sealed worksheet)'
Add-Line ''
Add-Line 'min(10, bin size) rows per sealed-reader-confidence bin over the COMPLETE directional worksheet,'
Add-Line 'SHA-256(accession) hex ASCENDING within the bin, selected IRRESPECTIVE of agreement status and of'
Add-Line 'which labels exist (label-independent by construction; spec 163). These rows - and ONLY these'
Add-Line 'rows - feed the calibration table; adjudicate each with the two-step blinded flow (blindCall'
Add-Line 'first) and record selectionReason "calibration-sample" (it takes precedence when a row also'
Add-Line 'disagrees). Membership claims are cross-checked against this derived set exactly.'
Add-Line ''
Add-Line '| bin | n in bin | sampled | accessions (hash order) |'
Add-Line '| --- | --- | --- | --- |'
foreach ($bin in $BinOrder) {
    $inBinCount = @($wsDirectional | Where-Object { $_.Bin -eq $bin }).Count
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
    exit 0
}

# =========================================================================================================
# VALIDATION (spec 163). Two buckets, the ONE incomplete-vs-fail rule:
#   $correctnessFailures - fail in BOTH modes, nonzero exit, nothing written.
#   $incompleteness      - INCOMPLETE sections under -Interim (exit 0); failure in final mode.
# =========================================================================================================

$correctnessFailures = New-Object System.Collections.Generic.List[string]
$incompleteness = New-Object System.Collections.Generic.List[string]

# --- provenance: recompute and verify (never compare claims to each other) -------------------------------

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "No exhibit manifest at '$ManifestPath' (pass -ManifestPath; every label's modelInputHash is verified against it)."
}
if (-not (Test-Path -LiteralPath $PromptTemplatePath)) {
    throw "No prompt template at '$PromptTemplatePath' (pass -PromptTemplatePath; its recomputed hash is verified against the study contract and every label)."
}

$manifestByAccession = @{}
foreach ($mrow in @(Import-Csv -LiteralPath $ManifestPath)) {
    $manifestByAccession[$mrow.accession] = $mrow
}

$RecomputedPromptSha = Get-LfNormalizedFileSha256 -Path $PromptTemplatePath
if ($RecomputedPromptSha -ne $ContractPromptSha) {
    $correctnessFailures.Add(("prompt template hash mismatch: recomputed '{0}' (CRLF->LF-normalized UTF-8) from '{1}' != study contract promptTemplateSha256 '{2}' - template drift or contract drift; a mid-study template edit is a protocol-version bump (cal-v2 -> cal-v3)" -f $RecomputedPromptSha, $PromptTemplatePath, $ContractPromptSha)) | Out-Null
}

foreach ($acc in @($labelsByAccession.Keys | Sort-Object)) {
    if (-not $worksheetByAccession.ContainsKey($acc)) { continue } # orphans are listed separately, loudly
    $label = $labelsByAccession[$acc]
    $protocol = Get-Prop $label 'protocol'

    $version = [string](Get-Prop $protocol 'version' '')
    if ($version -ne $ContractProtocolVersion) {
        $correctnessFailures.Add(("{0}: protocol.version '{1}' != study contract '{2}'" -f $acc, $version, $ContractProtocolVersion)) | Out-Null
    }

    $labeler = Get-Prop $protocol 'labeler'
    $provider = [string](Get-Prop $labeler 'provider' '')
    $model = [string](Get-Prop $labeler 'model' '')
    if ($provider -ne $ContractLabelerProvider -or $model -ne $ContractLabelerModel) {
        $correctnessFailures.Add(("{0}: labeler '{1}:{2}' != study contract '{3}:{4}' (a labeler change is a protocol-version bump restarting the affected labels)" -f $acc, $provider, $model, $ContractLabelerProvider, $ContractLabelerModel)) | Out-Null
    }

    $promptHash = ([string](Get-Prop $protocol 'promptHash' '')).ToLowerInvariant()
    if ([string]::IsNullOrEmpty($promptHash)) {
        $incompleteness.Add(("{0}: missing protocol.promptHash (must record the CRLF->LF-normalized template hash '{1}')" -f $acc, $RecomputedPromptSha)) | Out-Null
    } elseif ($promptHash -ne $RecomputedPromptSha) {
        $correctnessFailures.Add(("{0}: protocol.promptHash '{1}' != recomputed prompt-template hash '{2}'" -f $acc, $promptHash, $RecomputedPromptSha)) | Out-Null
    }

    $labelInputHash = ([string](Get-Prop $label 'modelInputHash' '')).ToLowerInvariant()
    if ([string]::IsNullOrEmpty($labelInputHash)) {
        $incompleteness.Add(("{0}: missing modelInputHash (must equal the manifest's modelInputSha256)" -f $acc)) | Out-Null
    } elseif (-not $manifestByAccession.ContainsKey($acc)) {
        $incompleteness.Add(("{0}: no exhibit-manifest row - modelInputHash cannot be verified" -f $acc)) | Out-Null
    } else {
        $manifestHash = ([string]$manifestByAccession[$acc].modelInputSha256).ToLowerInvariant()
        if ($labelInputHash -ne $manifestHash) {
            $correctnessFailures.Add(("{0}: modelInputHash '{1}' != manifest modelInputSha256 '{2}' (the label was produced from a different input than the archived study input)" -f $acc, $labelInputHash, $manifestHash)) | Out-Null
        }
    }
}

# --- calibration-sample membership: derived set is the admission rule ------------------------------------

foreach ($acc in @($labelsByAccession.Keys | Sort-Object)) {
    if (-not $worksheetByAccession.ContainsKey($acc)) { continue }
    $adj = Get-Prop $labelsByAccession[$acc] 'adjudication'
    if (([string](Get-Prop $adj 'selectionReason' '')) -eq 'calibration-sample' -and -not $sampleAccessions.ContainsKey($acc)) {
        $correctnessFailures.Add(("{0}: label claims selectionReason 'calibration-sample' but the accession is NOT in the derived probability sample - membership is derived from the sealed worksheet, never from the claim" -f $acc)) | Out-Null
    }
}

$calibMissing = @()
foreach ($bin in $BinOrder) {
    foreach ($member in $sampleByBin[$bin]) {
        $hasFinal = $false
        if ($labelsByAccession.ContainsKey($member.Accession)) {
            $adj = Get-Prop $labelsByAccession[$member.Accession] 'adjudication'
            $hasFinal = -not [string]::IsNullOrEmpty([string](Get-Prop $adj 'finalDirection' ''))
        }
        if (-not $hasFinal) { $calibMissing += $member.Accession }
    }
}
foreach ($acc in $calibMissing) {
    $incompleteness.Add(("calibration sample member without an adjudicated finalDirection: {0}" -f $acc)) | Out-Null
}

# --- full directional coverage (final-mode requirement; INCOMPLETE headers under -Interim) --------------

$directionalMissing = @($wsDirectional | Where-Object { -not $labelsByAccession.ContainsKey($_.Accession) } | ForEach-Object { $_.Accession })
if ($directionalMissing.Count -gt 0) {
    $incompleteness.Add(("directional coverage incomplete: {0}/{1} directional worksheet rows labeled; missing: {2}" -f $joined.Count, $wsDirectional.Count, ($directionalMissing -join ', '))) | Out-Null
}

# --- no-signal membership + extension state machine (trigger ALWAYS over rows 1-60) ----------------------

$noSignalOrdered = @($noSignalRows | Sort-Object -Property accessionSha256)
$K = $noSignalOrdered.Count
$first60Count = [math]::Min(60, $K)
$first90Count = [math]::Min(90, $K)
$first60 = @(); if ($first60Count -gt 0) { $first60 = @($noSignalOrdered[0..($first60Count - 1)]) }
$extensionRows = @(); if ($first90Count -gt $first60Count) { $extensionRows = @($noSignalOrdered[$first60Count..($first90Count - 1)]) }
$positionByAccession = @{}
for ($i = 0; $i -lt $K; $i++) { $positionByAccession[$noSignalOrdered[$i].accession] = $i + 1 } # 1-based

$labeledNoSignal = @($noSignalOrdered | Where-Object { $labelsByAccession.ContainsKey($_.accession) })
$labeledNoSignalSet = @{}
foreach ($row in $labeledNoSignal) { $labeledNoSignalSet[$row.accession] = $true }

$first60Missing = @($first60 | Where-Object { -not $labeledNoSignalSet.ContainsKey($_.accession) } | ForEach-Object { $_.accession })
$first60FullyLabeled = ($first60Missing.Count -eq 0 -and $first60Count -gt 0)
$labeledExtension = @($extensionRows | Where-Object { $labeledNoSignalSet.ContainsKey($_.accession) } | ForEach-Object { $_.accession })
$extensionMissing = @($extensionRows | Where-Object { -not $labeledNoSignalSet.ContainsKey($_.accession) } | ForEach-Object { $_.accession })
$outsidePrefix = @($labeledNoSignal | Where-Object { $positionByAccession[$_.accession] -gt $first90Count } | ForEach-Object { $_.accession })

# Miss accounting over the labeled no-signal rows. A "miss" is ONLY an adjudicated verdict:
# adjudication.finalDirection directional on a no-signal row. A reader-flagged candidate (blinded label
# direction directional) WITHOUT a finalDirection is pending - never a rate. Rows whose blinded label is
# not directional need no adjudication and count as non-misses.
function Get-NoSignalOutcome {
    param($LabelObject)
    $labelBody = Get-Prop $LabelObject 'label'
    $blinded = [string](Get-Prop $labelBody 'direction' '')
    $adj = Get-Prop $LabelObject 'adjudication'
    $final = [string](Get-Prop $adj 'finalDirection' '')
    if (Test-DirectionalLabel $final) { return 'confirmed-miss' }
    if (-not [string]::IsNullOrEmpty($final)) { return 'confirmed-non-miss' }
    if (Test-DirectionalLabel $blinded) { return 'pending-adjudication' }
    return 'non-miss'
}

$confirmedMisses60 = @()
$pendingCandidates60 = @()
foreach ($row in $first60) {
    if (-not $labeledNoSignalSet.ContainsKey($row.accession)) { continue }
    switch (Get-NoSignalOutcome $labelsByAccession[$row.accession]) {
        'confirmed-miss' { $confirmedMisses60 += $row.accession }
        'pending-adjudication' { $pendingCandidates60 += $row.accession }
    }
}
$pendingCandidatesAll = @()
$confirmedMissesAll = @()
foreach ($row in $labeledNoSignal) {
    switch (Get-NoSignalOutcome $labelsByAccession[$row.accession]) {
        'confirmed-miss' { $confirmedMissesAll += $row.accession }
        'pending-adjudication' { $pendingCandidatesAll += $row.accession }
    }
}
foreach ($acc in $pendingCandidatesAll) {
    $incompleteness.Add(("no-signal miss candidate awaiting adjudication (reader-flagged directional, no finalDirection): {0}" -f $acc)) | Out-Null
}

# The trigger is ALWAYS computed on rows 1-60 (hash order) - rows 61-90 NEVER enter it. Fired iff >= 1
# confirmed miss in rows 1-60 OR the Wilson 95% upper bound of the rows-1-60 miss rate exceeds 10%.
$triggerWilson = $null
if ($first60Count -gt 0) { $triggerWilson = Get-Wilson -Successes $confirmedMisses60.Count -N $first60Count }
$triggerState = 'undetermined' # fired | not-fired | undetermined
if ($confirmedMisses60.Count -ge 1) {
    $triggerState = 'fired'
} elseif ($first60FullyLabeled -and $pendingCandidates60.Count -eq 0) {
    if ($null -ne $triggerWilson -and $triggerWilson.Upper -gt 0.10) { $triggerState = 'fired' } else { $triggerState = 'not-fired' }
}

$extensionDecision = ''
if ($triggerState -eq 'fired') {
    $extensionDecision = ('EXTENSION: TRIGGERED ({0} confirmed miss(es) in rows 1-{1}, Wilson upper {2:P1} vs 10% threshold) - label the next 30 by hash order, report at N=90 (one-shot)' -f `
        $confirmedMisses60.Count, $first60Count, $triggerWilson.Upper)
} elseif ($triggerState -eq 'not-fired') {
    $extensionDecision = ('EXTENSION: NOT-TRIGGERED ({0} confirmed misses in rows 1-{1}, Wilson upper {2:P1} <= 10%)' -f `
        $confirmedMisses60.Count, $first60Count, $triggerWilson.Upper)
} elseif ($pendingCandidates60.Count -gt 0) {
    $extensionDecision = ('EXTENSION: PENDING ({0} reader-flagged candidate(s) in rows 1-{1} await adjudication; the trigger cannot be decided yet)' -f `
        $pendingCandidates60.Count, $first60Count)
} else {
    $extensionDecision = ('EXTENSION: PENDING (rows 1-{0} not fully labeled yet; the trigger is computed only over the complete precommitted sample)' -f $first60Count)
}

# Membership state machine (correctness in BOTH modes; incompleteness where labeling is merely unfinished).
if ($K -gt 0) {
    if ($outsidePrefix.Count -gt 0) {
        $correctnessFailures.Add(("no-signal label(s) outside the precommitted first-90 hash-order prefix: {0} (the sample is the first 60 - extended once to 90 - by SHA-256(accession) hex order over all {1} no-signal rows)" -f `
            (($outsidePrefix | ForEach-Object { '{0} (position {1})' -f $_, $positionByAccession[$_] }) -join ', '), $K)) | Out-Null
    }
    if ($labeledExtension.Count -gt 0 -and -not $first60FullyLabeled) {
        $correctnessFailures.Add(("no-signal labeled set does not match the precommitted hash-order prefix: missing from the first {0}: {1}; unexpectedly labeled beyond position {0}: {2}" -f `
            $first60Count, ($first60Missing -join ', '), ($labeledExtension -join ', '))) | Out-Null
    } elseif (-not $first60FullyLabeled) {
        if ($labeledNoSignal.Count -eq 0) {
            $incompleteness.Add(("no-signal precommitted sample not started: 0/{0} labeled (first {0} by hash order)" -f $first60Count)) | Out-Null
        } else {
            $incompleteness.Add(("no-signal precommitted sample incomplete: {0}/{1} labeled; pending: {2}" -f `
                ($first60Count - $first60Missing.Count), $first60Count, ($first60Missing -join ', '))) | Out-Null
        }
    } elseif ($labeledExtension.Count -gt 0) {
        # Extension rows are labeled: legitimate ONLY when the rows-1-60 trigger fired.
        if ($triggerState -eq 'fired') {
            if ($extensionMissing.Count -gt 0) {
                $incompleteness.Add(("no-signal extension in progress: {0}/{1} extension rows labeled; pending: {2}" -f `
                    $labeledExtension.Count, $extensionRows.Count, ($extensionMissing -join ', '))) | Out-Null
            }
            # $labeledExtension == all extension rows => the complete one-shot N=90 state: valid.
        } elseif ($triggerState -eq 'not-fired') {
            $correctnessFailures.Add(("no-signal rows 61-{0} are labeled but the rows-1-{1} trigger did NOT fire ({2} confirmed misses, Wilson upper {3:P1} <= 10%): unplanned extension violating the precommitment: {4}" -f `
                $first90Count, $first60Count, $confirmedMisses60.Count, $triggerWilson.Upper, ($labeledExtension -join ', '))) | Out-Null
        } else {
            $correctnessFailures.Add(("no-signal rows 61-{0} are labeled before the rows-1-{1} trigger was decidable ({2} candidate(s) await adjudication): the extension may only be labeled AFTER the trigger fired: {3}" -f `
                $first90Count, $first60Count, $pendingCandidates60.Count, ($labeledExtension -join ', '))) | Out-Null
        }
    } else {
        # Exactly the first 60 labeled. In FINAL mode a fired trigger means the report cannot be written
        # from a triggered-but-unextended state.
        if ($triggerState -eq 'fired' -and -not $Interim -and $extensionRows.Count -gt 0) {
            $correctnessFailures.Add(("no-signal extension trigger FIRED at N={0} ({1} confirmed miss(es), Wilson upper {2:P1}) - next 30 required: label rows {3}-{4} by hash order before a final report can be written (one-shot extension)" -f `
                $first60Count, $confirmedMisses60.Count, $triggerWilson.Upper, ($first60Count + 1), $first90Count)) | Out-Null
        }
    }
}

# =========================================================================================================
# GATE (the ONE rule): correctness fails BOTH modes; incompleteness fails final mode only.
# The -OutFile artifact is NEVER written on a failing path (checked here, before any write).
# =========================================================================================================

if ($correctnessFailures.Count -gt 0) {
    Write-Output ('VALIDATION FAILED - {0} correctness violation(s); no report written{1}:' -f $correctnessFailures.Count, $(if ($Interim) { ' (interim mode relaxes completeness, never correctness)' } else { '' }))
    foreach ($f in $correctnessFailures) { Write-Output ('  CORRECTNESS: {0}' -f $f) }
    if ($incompleteness.Count -gt 0) {
        foreach ($f in $incompleteness) { Write-Output ('  (also incomplete: {0})' -f $f) }
    }
    exit 1
}

if (-not $Interim -and $incompleteness.Count -gt 0) {
    Write-Output ('FINAL MODE FAILED - {0} incompleteness item(s); a final report requires the complete labeled study (use -Interim for a progress report); no report written:' -f $incompleteness.Count)
    foreach ($f in $incompleteness) { Write-Output ('  INCOMPLETE: {0}' -f $f) }
    exit 1
}

# --- render (validated; interim may carry INCOMPLETE section headers) ------------------------------------

$coverageIncomplete = ($directionalMissing.Count -gt 0)
$coverageTag = ''
if ($coverageIncomplete) {
    $coverageTag = (' - INCOMPLETE ({0}/{1} directional rows labeled)' -f $joined.Count, $wsDirectional.Count)
}

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
if ($Interim -and $incompleteness.Count -gt 0) {
    Add-Line ('**INCOMPLETE (interim)** - {0} item(s) outstanding before a final report can be written:' -f $incompleteness.Count)
    foreach ($i in $incompleteness) { Add-Line ('- {0}' -f $i) }
    Add-Line ''
}
Add-Line ('Provenance verified against the study contract ({0}): protocol {1}, labeler {2}:{3}, prompt template {4} (CRLF->LF-normalized SHA-256), every modelInputHash checked against {5}.' -f `
    (Split-Path -Leaf $ContractPath), $ContractProtocolVersion, $ContractLabelerProvider, $ContractLabelerModel, $RecomputedPromptSha, (Split-Path -Leaf $ManifestPath))
Add-Line ''

# -- Section: inter-model agreement curve --
Add-Line ('## Inter-model agreement curve{0}' -f $coverageTag)
Add-Line ''
Add-Line 'Reader-confidence bins x skeptic-agreement rate. AGREEMENT IS NOT CALIBRATION: two models can'
Add-Line 'agree and both be wrong - adjudicated correctness lives in the calibration table below.'
if ($coverageIncomplete) {
    Add-Line ''
    Add-Line ('INCOMPLETE: {0} directional worksheet rows have no effective label yet: {1}' -f $directionalMissing.Count, ($directionalMissing -join ', '))
}
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

# -- Section: calibration table (derived probability sample ONLY) --
$sampleTotal = $sampleAccessions.Keys.Count
$sampleAdjudicated = $sampleTotal - $calibMissing.Count
$calibTag = ''
if ($calibMissing.Count -gt 0) { $calibTag = (' - INCOMPLETE ({0}/{1} sample members adjudicated)' -f $sampleAdjudicated, $sampleTotal) }
Add-Line ('## Calibration table (derived probability sample ONLY){0}' -f $calibTag)
Add-Line ''
Add-Line 'Adjudicated correctness over the DERIVED sample members exclusively (membership is computed'
Add-Line 'from the sealed worksheet, above; a recorded "calibration-sample" claim outside the derived set is'
Add-Line 'a correctness failure). Disagreement/doubt-queued adjudications are NEVER pooled into these rates.'
Add-Line 'A row is correct iff adjudication.finalDirection equals the sealed model direction; sample members'
Add-Line 'without a finalDirection are missing (listed below), never guessed.'
Add-Line ''
if ($calibMissing.Count -gt 0) {
    Add-Line ('INCOMPLETE - sample members without an adjudicated finalDirection: {0}' -f ($calibMissing -join ', '))
    Add-Line ''
}
Add-Line '| reader-confidence bin | adjudicated n | model correct | accuracy (Wilson 95%) | missing |'
Add-Line '| --- | --- | --- | --- | --- |'
foreach ($bin in $BinOrder) {
    $members = $sampleByBin[$bin]
    if ($members.Count -eq 0) {
        Add-Line ('| {0} | 0 | - | no sample members | - |' -f $bin)
        continue
    }
    $resolved = @()
    $missing = 0
    foreach ($member in $members) {
        $hasFinal = $false
        if ($labelsByAccession.ContainsKey($member.Accession)) {
            $adj = Get-Prop $labelsByAccession[$member.Accession] 'adjudication'
            if (-not [string]::IsNullOrEmpty([string](Get-Prop $adj 'finalDirection' ''))) {
                $resolved += (New-Object psobject -Property @{
                    Member = $member
                    Final  = [string](Get-Prop $adj 'finalDirection' '')
                })
                $hasFinal = $true
            }
        }
        if (-not $hasFinal) { $missing++ }
    }
    if ($resolved.Count -eq 0) {
        Add-Line ('| {0} | 0 | - | no adjudicated labels | {1} |' -f $bin, $missing)
        continue
    }
    $correct = @($resolved | Where-Object { $_.Final -ieq $_.Member.ModelDirection }).Count
    Add-Line ('| {0} | {1} | {2} | {3} | {4} |' -f $bin, $resolved.Count, $correct, (Format-Rate -Successes $correct -N $resolved.Count), $missing)
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
Add-Line ('## Comparison cleanliness{0}' -f $coverageTag)
Add-Line ''
$cleanTrue = @($joined | Where-Object { $_.Clean -eq $true }).Count
Add-Line ('Clean YoY comparison rate (labeled directional rows): {0}' -f (Format-Rate -Successes $cleanTrue -N $joined.Count))
Add-Line ''
Add-Line ('### Comparability-item frequency (cmpscan-v2 evidence){0}' -f $coverageTag)
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
Add-Line ('## Materiality x constant-strength cross-tab{0}' -f $coverageTag)
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

# -- Section: false negatives (no-signal cohort; membership-validated precommitted sample) --
$nsTag = ''
$nsIncompleteItems = @($incompleteness | Where-Object { $_ -like 'no-signal*' })
if ($nsIncompleteItems.Count -gt 0) { $nsTag = ' - INCOMPLETE' }
Add-Line ('## False-omission section (no-signal cohort){0}' -f $nsTag)
Add-Line ''
if ($K -eq 0) {
    Add-Line 'No no-signal rows in the worksheet.'
} else {
    Add-Line ('Precommitted sample (spec 162): the first {0} of {1} no-signal rows by SHA-256(accession) hex' -f $first60Count, $K)
    Add-Line ('order; one-shot extension to {0} iff the rows-1-{1} trigger fires (>= 1 confirmed miss, or the' -f $first90Count, $first60Count)
    Add-Line 'Wilson 95% upper bound of the rows-1-60 miss rate exceeds 10%). Membership is validated against'
    Add-Line 'that prefix exactly; a "miss" counts ONLY from adjudication (finalDirection directional).'
    Add-Line 'The rate below is the FALSE-OMISSION rate, P(directional | reader emitted no signal).'
    Add-Line ''
    Add-Line ('Labeled no-signal rows: {0} (precommitted sample {1}; extension rows labeled: {2}).' -f $labeledNoSignal.Count, $first60Count, $labeledExtension.Count)
    foreach ($item in $nsIncompleteItems) {
        Add-Line ('INCOMPLETE: {0}' -f $item)
    }
    Add-Line ''
    Add-Line ('Confirmed misses (adjudication.finalDirection directional, all labeled rows): {0}' -f (Format-Rate -Successes $confirmedMissesAll.Count -N $labeledNoSignal.Count))
    Add-Line ('Trigger inputs (rows 1-{0} ONLY - rows {1}-{2} never enter the trigger): {3} confirmed miss(es), {4} pending candidate(s).' -f `
        $first60Count, ($first60Count + 1), $first90Count, $confirmedMisses60.Count, $pendingCandidates60.Count)
    Add-Line ''
    Add-Line $extensionDecision
    if ($confirmedMissesAll.Count -gt 0) {
        Add-Line ''
        Add-Line '| accession | ticker | blinded label | finalDirection | materiality | position |'
        Add-Line '| --- | --- | --- | --- | --- | --- |'
        foreach ($acc in ($confirmedMissesAll | Sort-Object -Property @{Expression = { Get-AccessionSha256 $_ }})) {
            $label = $labelsByAccession[$acc]
            $labelBody = Get-Prop $label 'label'
            $adj = Get-Prop $label 'adjudication'
            $wsRow = $worksheetByAccession[$acc]
            Add-Line ('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $acc, $wsRow.ticker, `
                [string](Get-Prop $labelBody 'direction' ''), [string](Get-Prop $adj 'finalDirection' ''), `
                [string](Get-Prop $labelBody 'material' ''), $positionByAccession[$acc])
        }
    }
    if ($pendingCandidatesAll.Count -gt 0) {
        Add-Line ''
        Add-Line ('Pending adjudication (reader-flagged, no finalDirection - never counted in any rate): {0}' -f ($pendingCandidatesAll -join ', '))
    }
}
Add-Line ''

# -- Section: input-path stability (pilot vs relabel) --
Add-Line '## Input-path stability table (pilot vs canonical-input relabel)'
Add-Line ''
if ([string]::IsNullOrEmpty($PilotCsvPath) -or -not (Test-Path -LiteralPath $PilotCsvPath)) {
    # Empty path = the default could not resolve a repo root (shallow script directory); Test-Path
    # -LiteralPath '' would itself throw under $ErrorActionPreference = 'Stop', so check emptiness first.
    Add-Line ('Pilot CSV not found at {0}; table skipped.' -f $(if ([string]::IsNullOrEmpty($PilotCsvPath)) { '(no repo root resolvable from the script directory; pass -PilotCsvPath)' } else { $PilotCsvPath }))
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
    if ([string]::IsNullOrEmpty($reason)) { $reason = 'calibration-sample (derived member; not yet recorded in label)' }
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
    Add-Line ('Queue size: {0} (calibration-sample derived members: {1}).' -f $queue.Count, $sampleAccessions.Keys.Count)
}
Add-Line ''
Add-Line '---'
Add-Line 'Read-only analysis: labels never flow into runtime values; findings inform SPECS (spec 162).'

$report = $md -join [Environment]::NewLine
if ($OutFile) { Set-Content -LiteralPath $OutFile -Value $report -Encoding UTF8 }
Write-Output $report
exit 0
