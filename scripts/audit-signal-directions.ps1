<#
.SYNOPSIS
    Spec 156 - read-only audit of WHY each accrued signal carries the direction it does.

.DESCRIPTION
    Walks the durable file store (signals under {DataRoot}/signals/{yyyy}/{MM}/*.json, raw evidence under
    {DataRoot}/evidence/raw/{sourceTypeFolder}/{yyyy}/{MM}/*.json) and reports THREE INDEPENDENT coverage
    dimensions, each over its OWN explicit denominator (spec 156 §1) - never conditional on each other:

      1. Evidence-source resolution  - does a signal's evidenceId resolve to a stored raw evidence file?
         Reported both over ALL signals and over the DISTINCT referenced evidence ids.
      2. Persisted extraction Reason - the Reason field persisted on the signal record itself, classified
         into keyword-phrase / news-branch / ai-read / unknown buckets.
      3. Upstream producer/classification reason (InsiderBuying scope) - does the signal's evidence resolve
         AND carry the 'insiderClassificationReason' metadata key (persisted only from spec 156 forward)?
         Anything else is reported as Unknown - NEVER estimated or inferred from the phrase or net value.

    STRICTLY READ-ONLY over the data root: the script only ever reads files, and refuses an -OutFile that
    would land inside -DataRoot. Output ordering is deterministic (count descending, then name ascending).
    PowerShell 5.1-compatible (uses JavaScriptSerializer on Desktop, ConvertFrom-Json -AsHashtable on Core).

.PARAMETER DataRoot
    The durable store root (holds signals/ and evidence/raw/). Default: 'data' beside the scripts folder.

.PARAMETER OutFile
    Optional path to also write the rendered report text to. Must NOT be inside -DataRoot.
#>
[CmdletBinding()]
param(
    [string]$DataRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'data'),
    [string]$OutFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# --- Resolve + guard paths (read-only over the store) -------------------------------------------------

$resolvedRoot = (Resolve-Path -LiteralPath $DataRoot).ProviderPath.TrimEnd('\', '/')
$signalsRoot = Join-Path $resolvedRoot 'signals'
$evidenceRoot = Join-Path (Join-Path $resolvedRoot 'evidence') 'raw'
if (-not (Test-Path -LiteralPath $signalsRoot)) { throw "No signals directory at '$signalsRoot'." }
if (-not (Test-Path -LiteralPath $evidenceRoot)) { throw "No raw evidence directory at '$evidenceRoot'." }

if ($OutFile) {
    $outCandidate = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path (Get-Location).ProviderPath $OutFile }
    $outFull = [System.IO.Path]::GetFullPath($outCandidate)
    if ($outFull.StartsWith($resolvedRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
        $outFull.Equals($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "-OutFile '$outFull' is inside -DataRoot '$resolvedRoot'; the audit never writes inside the store."
    }
}

# --- JSON reading (PS 5.1: JavaScriptSerializer; PS Core: ConvertFrom-Json -AsHashtable) ---------------

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

# --- Pass 1: index raw evidence by evidenceId ----------------------------------------------------------

$evidenceIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
# evidenceId -> insiderClassificationReason token (dimension 3; expected empty for accrued data).
$classificationReasonByEvidenceId = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::OrdinalIgnoreCase)
$evidenceFileCount = 0
$evidenceUnreadable = 0
$evidenceDuplicateIds = 0

$evidenceFiles = [System.IO.Directory]::EnumerateFiles($evidenceRoot, '*.json', [System.IO.SearchOption]::AllDirectories)
foreach ($file in $evidenceFiles) {
    $evidenceFileCount++
    if (($evidenceFileCount % 1000) -eq 0) {
        Write-Progress -Activity 'Indexing raw evidence' -Status "$evidenceFileCount files"
    }
    try {
        $obj = Read-JsonDictionary -Path $file
    } catch {
        $evidenceUnreadable++
        continue
    }
    $id = [string](Get-DictValue $obj 'evidenceId')
    if ([string]::IsNullOrWhiteSpace($id)) { $evidenceUnreadable++; continue }
    if (-not $evidenceIds.Add($id)) { $evidenceDuplicateIds++ }

    $metadata = Get-DictValue $obj 'metadata'
    if ($null -ne $metadata) {
        $token = [string](Get-DictValue $metadata 'insiderClassificationReason')
        if (-not [string]::IsNullOrWhiteSpace($token) -and -not $classificationReasonByEvidenceId.ContainsKey($id)) {
            $classificationReasonByEvidenceId[$id] = $token
        }
    }
}
Write-Progress -Activity 'Indexing raw evidence' -Completed

# --- Pass 2: walk every signal -------------------------------------------------------------------------

$totalSignals = 0
$signalUnreadable = 0
$signalsWithResolvedEvidence = 0
$referencedEvidenceIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$referencedResolved = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

$signalsWithReason = 0
# (type|direction|reasonKind) -> count, where reasonKind in keyword|news-branch|ai-read|unknown
$byTypeDirectionKind = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
# (type|direction|phrase) -> count, keyword bucket only
$byTypeDirectionPhrase = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
# direction -> count
$byDirection = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
# type|direction -> count
$byTypeDirection = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)

# Dimension 3 (InsiderBuying scope)
$insiderTotal = 0
$insiderAttributed = 0
$insiderAttributedByToken = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)

# Store-extent bookkeeping (lexical min/max works: all persisted instants carry the +00:00 offset).
$minObservedAt = $null; $maxObservedAt = $null
$minCreatedAt = $null; $maxCreatedAt = $null

$newsReason = 'Third-party news coverage (media attention)'
$phrasePrefix = "Matched phrase '"

function Add-Count {
    param($Dict, [string]$Key)
    if ($Dict.ContainsKey($Key)) { $Dict[$Key] = $Dict[$Key] + 1 } else { $Dict[$Key] = 1 }
}

$signalFiles = [System.IO.Directory]::EnumerateFiles($signalsRoot, '*.json', [System.IO.SearchOption]::AllDirectories)
foreach ($file in $signalFiles) {
    $totalSignals++
    if (($totalSignals % 2000) -eq 0) {
        Write-Progress -Activity 'Scanning signals' -Status "$totalSignals files"
    }
    try {
        $obj = Read-JsonDictionary -Path $file
    } catch {
        $signalUnreadable++
        continue
    }

    $evidenceId = [string](Get-DictValue $obj 'evidenceId')
    $type = [string](Get-DictValue $obj 'type')
    $direction = [string](Get-DictValue $obj 'direction')
    $reason = [string](Get-DictValue $obj 'reason')
    if ([string]::IsNullOrWhiteSpace($type)) { $type = '(missing)' }
    if ([string]::IsNullOrWhiteSpace($direction)) { $direction = '(missing)' }

    $observedAt = [string](Get-DictValue $obj 'observedAt')
    $createdAt = [string](Get-DictValue $obj 'createdAt')
    if ($observedAt) {
        if ($null -eq $minObservedAt -or [string]::CompareOrdinal($observedAt, $minObservedAt) -lt 0) { $minObservedAt = $observedAt }
        if ($null -eq $maxObservedAt -or [string]::CompareOrdinal($observedAt, $maxObservedAt) -gt 0) { $maxObservedAt = $observedAt }
    }
    if ($createdAt) {
        if ($null -eq $minCreatedAt -or [string]::CompareOrdinal($createdAt, $minCreatedAt) -lt 0) { $minCreatedAt = $createdAt }
        if ($null -eq $maxCreatedAt -or [string]::CompareOrdinal($createdAt, $maxCreatedAt) -gt 0) { $maxCreatedAt = $createdAt }
    }

    # Dimension 1 - evidence-source resolution.
    $resolved = $false
    if (-not [string]::IsNullOrWhiteSpace($evidenceId)) {
        [void]$referencedEvidenceIds.Add($evidenceId)
        if ($evidenceIds.Contains($evidenceId)) {
            $resolved = $true
            $signalsWithResolvedEvidence++
            [void]$referencedResolved.Add($evidenceId)
        }
    }

    # Dimension 2 - persisted extraction Reason (on the signal record itself; independent of dimension 1).
    $reasonKind = 'unknown'
    $phrase = $null
    if (-not [string]::IsNullOrWhiteSpace($reason)) {
        $signalsWithReason++
        if ($reason.StartsWith($phrasePrefix, [System.StringComparison]::Ordinal) -and
            $reason.EndsWith("'", [System.StringComparison]::Ordinal) -and
            $reason.Length -gt ($phrasePrefix.Length + 1)) {
            $reasonKind = 'keyword'
            $phrase = $reason.Substring($phrasePrefix.Length, $reason.Length - $phrasePrefix.Length - 1)
        } elseif ($reason -eq $newsReason) {
            $reasonKind = 'news-branch'
        } else {
            $reasonKind = 'ai-read'
        }
    }

    Add-Count $byDirection $direction
    Add-Count $byTypeDirection "$type|$direction"
    Add-Count $byTypeDirectionKind "$type|$direction|$reasonKind"
    if ($reasonKind -eq 'keyword') {
        Add-Count $byTypeDirectionPhrase "$type|$direction|$phrase"
    }

    # Dimension 3 - upstream classification reason, InsiderBuying scope ONLY. Mechanical rule: the signal's
    # evidence must resolve AND carry the insiderClassificationReason key. No inference from anything else.
    if ($type -eq 'InsiderBuying') {
        $insiderTotal++
        if ($resolved -and $classificationReasonByEvidenceId.ContainsKey($evidenceId)) {
            $insiderAttributed++
            Add-Count $insiderAttributedByToken $classificationReasonByEvidenceId[$evidenceId]
        }
    }
}
Write-Progress -Activity 'Scanning signals' -Completed

# --- Rendering -----------------------------------------------------------------------------------------

$lines = New-Object 'System.Collections.Generic.List[string]'
function Out-Line { param([string]$Text = '') $lines.Add($Text) | Out-Null }

function Format-Pct {
    param([int]$Numerator, [int]$Denominator)
    if ($Denominator -eq 0) { return 'n/a' }
    return ('{0:P2}' -f ($Numerator / [double]$Denominator))
}

# Renders "count  key-parts..." rows sorted deterministically: count descending, then key ascending (ordinal).
function Out-CountTable {
    param($Dict, [string[]]$Header)
    $entries = @($Dict.GetEnumerator() | Sort-Object -Property @{Expression = { $_.Value }; Descending = $true },
                                                              @{Expression = { $_.Key }; Descending = $false })
    Out-Line ('    {0,9}  {1}' -f 'count', ($Header -join ' | '))
    foreach ($entry in $entries) {
        Out-Line ('    {0,9:N0}  {1}' -f $entry.Value, ($entry.Key -replace '\|', ' | '))
    }
}

$scanInstant = [System.DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')

Out-Line '================================================================================'
Out-Line 'Spec 156 - signal direction/reason audit (READ-ONLY)'
Out-Line '================================================================================'
Out-Line "Scanned at (UTC):        $scanInstant"
Out-Line "Data root:               $resolvedRoot"
Out-Line "Signal files:            $($totalSignals.ToString('N0'))  (unreadable/skipped: $signalUnreadable)"
Out-Line "Raw evidence files:      $($evidenceFileCount.ToString('N0'))  (unreadable/skipped: $evidenceUnreadable; duplicate ids across files: $evidenceDuplicateIds)"
Out-Line "Distinct evidence ids:   $($evidenceIds.Count.ToString('N0'))"
Out-Line "Signal observedAt span:  $minObservedAt .. $maxObservedAt"
Out-Line "Signal createdAt span:   $minCreatedAt .. $maxCreatedAt"
Out-Line ''

Out-Line '--------------------------------------------------------------------------------'
Out-Line 'DIMENSION 1 - evidence-source resolution'
Out-Line "  (a) N = $($totalSignals.ToString('N0')) signals"
Out-Line ("      signals whose evidenceId resolves to a stored raw item: {0:N0}  ({1})" -f `
    $signalsWithResolvedEvidence, (Format-Pct $signalsWithResolvedEvidence $totalSignals))
Out-Line "  (b) N = $($referencedEvidenceIds.Count.ToString('N0')) distinct evidence ids referenced by signals"
Out-Line ("      distinct referenced ids that resolve:                   {0:N0}  ({1})" -f `
    $referencedResolved.Count, (Format-Pct $referencedResolved.Count $referencedEvidenceIds.Count))
Out-Line ''

Out-Line '--------------------------------------------------------------------------------'
Out-Line "DIMENSION 2 - persisted extraction Reason (on the signal record itself)"
Out-Line "  N = $($totalSignals.ToString('N0')) signals"
Out-Line ("      signals with a non-blank reason: {0:N0}  ({1})" -f `
    $signalsWithReason, (Format-Pct $signalsWithReason $totalSignals))
Out-Line ''
Out-Line "  Direction totals (N = $($totalSignals.ToString('N0')) signals):"
Out-CountTable $byDirection @('direction')
Out-Line ''
Out-Line "  Classification by (type, direction, reason-class) (N = $($totalSignals.ToString('N0')) signals):"
Out-Line "  reason-class: keyword = ""Matched phrase '<phrase>'"" | news-branch = fixed news reason |"
Out-Line "                ai-read = any other non-blank reason | unknown = blank/missing"
Out-CountTable $byTypeDirectionKind @('type', 'direction', 'reason-class')
Out-Line ''
Out-Line "  Per-(type, direction, phrase) frequency - keyword bucket only (N = $($totalSignals.ToString('N0')) signals):"
Out-CountTable $byTypeDirectionPhrase @('type', 'direction', 'phrase')
Out-Line ''

Out-Line '--------------------------------------------------------------------------------'
Out-Line 'DIMENSION 3 - upstream producer/classification reason (InsiderBuying scope)'
Out-Line "  N = $($insiderTotal.ToString('N0')) InsiderBuying signals"
Out-Line '  Rule: attributed ONLY when the signal''s evidence resolves AND carries the'
Out-Line '  ''insiderClassificationReason'' metadata key (persisted from spec 156 forward).'
Out-Line '  NOT inferred from phrases, net value, or anything else.'
Out-Line ("      attributable to a specific reader branch: {0:N0}  ({1})" -f `
    $insiderAttributed, (Format-Pct $insiderAttributed $insiderTotal))
Out-Line ("      Unknown (reason never persisted):         {0:N0}  ({1})" -f `
    ($insiderTotal - $insiderAttributed), (Format-Pct ($insiderTotal - $insiderAttributed) $insiderTotal))
if ($insiderAttributedByToken.Count -gt 0) {
    Out-Line '  Attributed branch breakdown:'
    Out-CountTable $insiderAttributedByToken @('insiderClassificationReason')
}
Out-Line ''
Out-Line ("  Evidence files carrying insiderClassificationReason (any source): {0:N0}" -f `
    $classificationReasonByEvidenceId.Count)
Out-Line '================================================================================'

$report = $lines -join [System.Environment]::NewLine
Write-Output $report

if ($OutFile) {
    [System.IO.File]::WriteAllText($outFull, $report + [System.Environment]::NewLine)
    Write-Verbose "Report written to $outFull"
}
