<#
.SYNOPSIS
    Spec 165 - measure cmpscan-v2 CANDIDATE rules against the archived raw exhibit bodies. Read-only,
    deterministic, no AI, no network, no production change (PowerShell 5.1- and pwsh 7-compatible).

.DESCRIPTION
    Spec 162 Phase B established candidate CONCEPTS (acquisition/perimeter, discrete tax) from regexes over
    the second reader's CURATED comparabilityItems strings. That says nothing about how a PHRASE behaves on
    a raw filing body. This script runs each precommitted candidate over the 298 archived full exhibit texts
    and reports hit rate, label-referenced precision/recall/F1 and any-break precision, so the eventual
    production cmpscan-v2 spec adds only rules with evidence.

    NOTHING HERE IS PRODUCTION. `EarningsComparabilityScan` (cmpscan-v1) is untouched; its phrase table is
    hashed into the AI-ON scoring fingerprint, so editing it moves pins. The v1 baseline computed below is a
    MEASUREMENT MIRROR of that table, not a second production definition - see $V1CapTriggeringPhrases.

    INPUTS
      -ExhibitsDir   the archived FULL exhibit texts, one BOM-less UTF-8 file per filing named
                     "{lowercased-ticker}-{accession}.txt" (Radar.CalibrationAudit's ExhibitArchiver
                     naming). READ-ONLY: this script never writes into the exhibit tree.
      -ManifestPath  docs/162-exhibit-manifest.csv. EVERY exhibit's raw bytes are SHA-256'd and compared to
                     the row's fullTextSha256 BEFORE it is read as text; a mismatch throws naming the file,
                     the expected and the actual hash. A measurement over an unverified body is worthless.
      -LabelsPath    docs/162-calibration-labels-full.jsonl - the labeled population (the reference cohort)
                     and the source of the ANY-BREAK reference (label.comparisonClean = false).
      -MappingPath   the per-ITEM concept mapping produced by categorize-comparability.ps1 with
                     -Cohort all-labeled. It MUST cover the labeled population: passing the spec-162
                     directional-only mapping would read every no-signal filing as concept-negative and
                     fabricate false positives, so that case is DETECTED and fails loudly (see below).

    MANIFEST ADMISSION RULE (stated because silence here would be a lie about the denominators): a manifest
    row is ADMITTED iff its outcome is exactly 'success' AND its fullTextSha256 is non-empty. Any other row
    carries no verifiable archived body; such rows are EXCLUDED from every denominator and their count (with
    accessions) is REPORTED, never silently skipped. An admitted row whose exhibit file is missing is a
    FAILURE, not an exclusion.

    MATCHING SEMANTICS - cmpscan-v1's own, so a promoted candidate behaves in production exactly as measured:
    the body has every run of whitespace collapsed to a single space (leading/trailing dropped), and a PRIMARY
    candidate matches by case-insensitive VERBATIM SUBSTRING containment. (.NET regex \s and char.IsWhiteSpace
    cover the same code points, so the regex collapse below equals EarningsComparabilityScan's char loop.)

    SELF-CONTAINMENT: Get-Wilson / Get-Prop are COPIED from analyze-labels.ps1 rather than dot-sourced,
    exactly as analyze-shadow-read.ps1 copies them. Each of these scripts is copied ALONE into a test sandbox
    (and is run standalone by a maintainer against absolute paths), so a dot-sourced sibling would break that
    seam. Do not "fix" this into a shared module.

    DETERMINISM - of CONTENT, and only per-host of BYTES. No timestamps, no Get-Date, no randomness, no
    hashtable-enumeration-order dependence (every key set is sorted ORDINALLY via Sort-Ordinal, never with the
    culture-aware comparer), and every number is formatted through InvariantCulture. So the same inputs yield
    the same lines, in the same order, carrying the same values, on any host and under any locale.
    BYTE-identity holds only when comparing artifacts produced by the SAME host: the report is joined with
    [Environment]::NewLine and both -OutFile and -OutCsv are written through the host's own emitters, so
    Windows PowerShell 5.1 writes CRLF with a UTF-8 BOM while pwsh 7 writes the platform newline BOM-less.
    (Quoting is NOT a difference - measured on both: pwsh 7's Export-Csv -UseQuotes defaults to Always, so
    5.1 and 7 both quote every field.) Do not diff 5.1 output against pwsh 7 output byte-for-byte - diff the
    content, or regenerate both sides on one host.

.PARAMETER ExhibitsDir
    Directory holding the archived full exhibit texts.

.PARAMETER ManifestPath
    Path to 162-exhibit-manifest.csv (fullTextSha256 is the verification source).

.PARAMETER LabelsPath
    Path to 162-calibration-labels-full.jsonl.

.PARAMETER MappingPath
    Path to the all-labeled comparability item mapping CSV.

.PARAMETER OutCsv
    Optional path for the long-form candidate x accession hit matrix.

.PARAMETER OutFile
    Optional path for the markdown summary (identical to stdout).

.EXAMPLE
    powershell -File scripts/calibration-audit/measure-cmpscan-candidates.ps1 `
        -ExhibitsDir C:\repos\radar\data\calibration-audit\exhibits-full `
        -ManifestPath docs/162-exhibit-manifest.csv `
        -LabelsPath docs/162-calibration-labels-full.jsonl `
        -MappingPath docs/165-comparability-item-mapping-all235.csv `
        -OutCsv docs/165-cmpscan-candidate-hits.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExhibitsDir,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$LabelsPath,
    [Parameter(Mandatory = $true)][string]$MappingPath,
    [string]$OutCsv,
    [string]$OutFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# =========================================================================================================
# THE FROZEN PRIMARY CANDIDATE TABLE.
#
# PRECOMMITTED BY SPEC 165 BEFORE ANY MEASUREMENT RAN. Literal, case-insensitive substring matches over the
# whitespace-collapsed body - cmpscan-v1's own semantics. Editing this list (adding, removing, reordering or
# rewording an entry) AFTER seeing results is TUNING, and any number produced by a tuned list is not
# evidence for a production rule. If a new candidate is wanted, it is a new spec with a new precommitment.
#
# Order is table order and is the reported/CSV order.
# =========================================================================================================
$PrimaryCandidates = @(
    [pscustomobject]@{ Id = 'acq-01'; Literal = 'acquisition';          Concept = 'acquisition-divestiture-perimeter'; Rationale = 'The broadest perimeter word; expected to over-match ("acquisition of customers", "talent acquisition") - measuring exactly how much is the point.' }
    [pscustomobject]@{ Id = 'acq-02'; Literal = 'acquisitions';         Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Plural form; separates programme/pipeline language from a single completed deal.' }
    [pscustomobject]@{ Id = 'acq-03'; Literal = 'completed acquisition'; Concept = 'acquisition-divestiture-perimeter'; Rationale = 'A completed deal is what actually breaks a year-over-year comparison; narrower than the bare word.' }
    [pscustomobject]@{ Id = 'acq-04'; Literal = 'recent acquisition';   Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Recency wording usually accompanies a perimeter change inside the compared periods.' }
    [pscustomobject]@{ Id = 'acq-05'; Literal = 'pro forma';            Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Pro-forma presentation is the standard tell that reported periods are not comparable as reported.' }
    [pscustomobject]@{ Id = 'acq-06'; Literal = 'deconsolidation';      Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Technical term with essentially one meaning; expected precise, expected rare.' }
    [pscustomobject]@{ Id = 'acq-07'; Literal = 'divestiture';          Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Perimeter reduction; note cmpscan-v1 ALREADY caps on this phrase - measured here for overlap, not novelty.' }
    [pscustomobject]@{ Id = 'acq-08'; Literal = 'divestitures';         Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Plural form, which v1 does not carry verbatim (v1 has the singular only).' }
    [pscustomobject]@{ Id = 'acq-09'; Literal = 'held for sale';        Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Accounting classification announcing a pending perimeter change before it closes.' }
    [pscustomobject]@{ Id = 'acq-10'; Literal = 'same-store';           Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Its presence implies management itself is normalising away a perimeter change.' }
    [pscustomobject]@{ Id = 'acq-11'; Literal = 'same store';           Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Unhyphenated variant; both spellings occur in filings.' }
    [pscustomobject]@{ Id = 'tax-01'; Literal = 'discrete tax';         Concept = 'discrete-tax';                      Rationale = 'The explicit name for a one-off tax item distorting the effective rate.' }
    [pscustomobject]@{ Id = 'tax-02'; Literal = 'tax benefit';          Concept = 'discrete-tax';                      Rationale = 'Common but ambiguous - also matches routine stock-compensation and deferred-tax prose; the noise test.' }
    [pscustomobject]@{ Id = 'tax-03'; Literal = 'valuation allowance';  Concept = 'discrete-tax';                      Rationale = 'A release/establishment swings net income without operating change; technical and fairly unambiguous.' }
    [pscustomobject]@{ Id = 'tax-04'; Literal = 'uncertain tax position'; Concept = 'discrete-tax';                    Rationale = 'Reserve releases are a classic discrete tax item; formal phrasing, expected rare and precise.' }
)

# =========================================================================================================
# EXPLORATORY rows - regex variants. DESCRIPTIVE ONLY.
#
# These are NOT eligible for the promotion rule. The exclusion is STRUCTURAL, not documentary: the promotion
# evaluation iterates $primaryResults, which is built from $PrimaryCandidates alone, and asserts every row it
# sees carries RowKind 'primary' before evaluating anything. An exploratory row can never reach it.
# =========================================================================================================
$ExploratoryCandidates = @(
    [pscustomobject]@{ Id = 'x-01'; Pattern = '\bacquisitions?\b';    Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Word-boundary-anchored singular/plural: excludes "acquisitional" and in-word matches.' }
    [pscustomobject]@{ Id = 'x-02'; Pattern = '\bdivestitures?\b';    Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Word-boundary-anchored singular/plural divestiture.' }
    [pscustomobject]@{ Id = 'x-03'; Pattern = '\bpro forma\b';        Concept = 'acquisition-divestiture-perimeter'; Rationale = 'Anchored pro-forma, excluding hyphenated compounds inside longer tokens.' }
    [pscustomobject]@{ Id = 'x-04'; Pattern = '\bdiscrete tax\b';     Concept = 'discrete-tax';                      Rationale = 'Anchored discrete-tax form.' }
    [pscustomobject]@{ Id = 'x-05'; Pattern = '\bvaluation allowance\b'; Concept = 'discrete-tax';                   Rationale = 'Anchored valuation-allowance form.' }
)

# =========================================================================================================
# cmpscan-v1 baseline - MEASUREMENT MIRROR of the 15 cap-triggering phrases in
# src/Radar.Infrastructure/Filings/EarningsComparabilityScan.cs (THE source of truth; that file is untouched
# by this spec because its table is hashed into the AI-ON fingerprint). This copy exists only so the baseline
# can be measured from a script; it is not a second production definition and nothing reads it at runtime.
# The 4 diagnostic-only phrases are deliberately NOT here: they never cap, so they are not the baseline.
# =========================================================================================================
$V1CapTriggeringPhrases = @(
    'discontinued operations',
    'divestiture',
    'divested',
    'impairment',
    'litigation settlement',
    'legal settlement',
    'one-time',
    'one time',
    'non-recurring',
    'nonrecurring',
    'gain on sale',
    'loss on sale',
    'securities loss',
    'securities losses',
    'bad debt recovery'
)

$FpFnListCap = 15
$ContextRadius = 80

# --- helpers ---------------------------------------------------------------------------------------------

function Get-Prop {
    # StrictMode-safe optional-property read (copied from analyze-labels.ps1; see SELF-CONTAINMENT above).
    param($Object, [string]$Name, $Default = $null)
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) {
        return $Object.PSObject.Properties[$Name].Value
    }
    return $Default
}

function Get-Wilson {
    # Wilson score interval, 95% (z = 1.959963984540054). Returns $null when n = 0.
    # Copied from analyze-labels.ps1 verbatim (see SELF-CONTAINMENT above).
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

function Format-Fixed {
    # InvariantCulture fixed-point: a machine with a comma decimal separator must not produce a different file.
    param([double]$Value, [int]$Digits = 3)
    return $Value.ToString('F' + $Digits, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Format-Ratio {
    # "s/n = 0.812 (Wilson 95%: 0.701-0.888)" - point estimate plus interval plus the explicit N.
    param([int]$Successes, [int]$N)
    if ($N -le 0) { return 'n/a (n=0)' }
    $w = Get-Wilson -Successes $Successes -N $N
    return ('{0}/{1} = {2} (Wilson 95%: {3}-{4})' -f $Successes, $N, (Format-Fixed $w.P), (Format-Fixed $w.Lower), (Format-Fixed $w.Upper))
}

function Sort-Ordinal {
    # ORDINAL string sort. Sort-Object uses the culture-aware comparer, which can order strings containing
    # '-' differently by machine locale; every ordering in this script is ordinal so the artifacts' CONTENT
    # and row order are host- and locale-independent. (Byte-identity across hosts is a separate matter, and
    # does NOT hold - see DETERMINISM in the help above.)
    param([string[]]$Values)
    $copy = [string[]]@($Values)
    if ($copy.Length -gt 1) { [array]::Sort($copy, [System.StringComparer]::Ordinal) }
    return $copy
}

function Get-Sha256Hex {
    param([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($Bytes)
    } finally {
        $sha.Dispose()
    }
    return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
}

function Get-CollapsedBody {
    # EarningsComparabilityScan.NormalizeWhitespace, expressed as a regex: every run of whitespace becomes a
    # single space and leading/trailing whitespace is dropped. .NET's \s == char.IsWhiteSpace over the same
    # code points ([\f\n\r\t\v\x85\p{Z}]), so the two agree character for character.
    param([string]$Text)
    return ([System.Text.RegularExpressions.Regex]::Replace($Text, '\s+', ' ')).Trim()
}

function Get-MatchContext {
    # +/- $ContextRadius characters around a match in the ALREADY-collapsed body (so it is always one line).
    param([string]$Body, [int]$Index, [int]$Length)
    $start = [math]::Max(0, $Index - $ContextRadius)
    $end = [math]::Min($Body.Length, $Index + $Length + $ContextRadius)
    $snippet = $Body.Substring($start, $end - $start)
    $prefix = ''
    if ($start -gt 0) { $prefix = '...' }
    $suffix = ''
    if ($end -lt $Body.Length) { $suffix = '...' }
    return ($prefix + $snippet + $suffix)
}

function Read-Utf8Text {
    # Byte-level read + explicit UTF-8 decode. NEVER Get-Content for exhibit bodies: Windows PowerShell 5.1
    # decodes a BOM-less UTF-8 file as ANSI and silently corrupts every non-ASCII character - which would
    # also break the hash-verified equivalence between the bytes measured and the bytes archived.
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

function Import-CsvUtf8 {
    # Import-Csv with an explicit UTF-8 read. The 162 artifacts were written by Windows PowerShell 5.1's
    # Export-Csv -Encoding UTF8 (which emits a BOM) while C#-written artifacts are BOM-less; both hosts'
    # UTF-8 readers detect and strip a BOM, so this one call handles both. Import-Csv (not a hand-rolled
    # line split) because it is the only reader that handles a quoted field containing a newline.
    param([string]$Path, [string[]]$RequiredColumns)
    if (-not (Test-Path -LiteralPath $Path)) { throw "No CSV at '$Path'." }
    $rows = @(Import-Csv -LiteralPath $Path -Encoding UTF8)
    if ($rows.Count -gt 0) {
        foreach ($column in $RequiredColumns) {
            if (-not $rows[0].PSObject.Properties[$column]) {
                throw ("CSV '{0}' has no '{1}' column (columns: {2}). Expected columns: {3}." -f `
                    $Path, $column, (($rows[0].PSObject.Properties.Name) -join ', '), ($RequiredColumns -join ', '))
            }
        }
    }
    return $rows
}

# --- load the manifest -----------------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $ExhibitsDir)) { throw "No exhibits directory at '$ExhibitsDir'." }
if (-not (Test-Path -LiteralPath $LabelsPath)) { throw "No labels file at '$LabelsPath'." }

# @() around every Import-CsvUtf8 call: a function returning an empty/single-element array unrolls it, so a
# bare assignment would yield $null (and .Count would throw under StrictMode) for an empty CSV.
$manifestRows = @(Import-CsvUtf8 -Path $ManifestPath -RequiredColumns @('accession', 'ticker', 'fullTextSha256', 'outcome'))
if ($manifestRows.Count -eq 0) { throw "Exhibit manifest '$ManifestPath' is empty." }

$manifestByAccession = @{}
foreach ($row in $manifestRows) {
    $accession = [string]$row.accession
    if ($manifestByAccession.ContainsKey($accession)) {
        throw "Duplicate accession '$accession' in exhibit manifest '$ManifestPath' - the manifest must be one row per accession."
    }
    $manifestByAccession[$accession] = $row
}
$admitted = New-Object System.Collections.Generic.List[object]
$excludedRows = New-Object System.Collections.Generic.List[string]
foreach ($accession in (Sort-Ordinal -Values @($manifestByAccession.Keys))) {
    $row = $manifestByAccession[$accession]
    $outcome = [string]$row.outcome
    $expectedHash = ([string]$row.fullTextSha256).Trim().ToLowerInvariant()
    if ($outcome -ne 'success') {
        $excludedRows.Add(('{0}: outcome ''{1}'' (not ''success'')' -f $accession, $outcome)) | Out-Null
        continue
    }
    if ([string]::IsNullOrEmpty($expectedHash)) {
        $excludedRows.Add(('{0}: empty fullTextSha256 (no verifiable archived body)' -f $accession)) | Out-Null
        continue
    }
    $admitted.Add((New-Object psobject -Property @{
        Accession    = $accession
        Ticker       = [string]$row.ticker
        ExpectedHash = $expectedHash
    })) | Out-Null
}
if ($admitted.Count -eq 0) { throw "No admitted manifest rows in '$ManifestPath' (every row failed the outcome/hash admission rule)." }

# --- load the labels (the reference cohort + the ANY-BREAK reference) -------------------------------------

$labelClean = @{}      # accession -> $true (clean) / $false (break) / $null (not recorded)
$labelItemCount = @{}  # accession -> number of comparabilityItems recorded
$labelOrder = New-Object System.Collections.Generic.List[string]
$labelText = Read-Utf8Text -Path (Resolve-Path -LiteralPath $LabelsPath).ProviderPath
foreach ($line in ($labelText -split "`n")) {
    $trimmed = $line.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
    $label = ConvertFrom-Json -InputObject $trimmed
    $accession = [string](Get-Prop $label 'accession' '')
    if ([string]::IsNullOrEmpty($accession)) { throw "A label line in '$LabelsPath' has no accession." }
    if ($labelClean.ContainsKey($accession)) {
        # Every measurement below assumes ONE effective label per accession (spec 165 relies on the study's
        # all-attempt-1 state). A retry would need effective-label resolution first, so fail loudly.
        throw "Duplicate accession '$accession' in '$LabelsPath'. This measurement assumes exactly one effective label per accession; resolve retries (highest protocol.attempt) before measuring."
    }
    $body = Get-Prop $label 'label'
    $labelClean[$accession] = Get-Prop $body 'comparisonClean' $null
    $labelItemCount[$accession] = @(Get-Prop $body 'comparabilityItems' @()).Count
    $labelOrder.Add($accession) | Out-Null
}
if ($labelOrder.Count -eq 0) { throw "Labels file '$LabelsPath' contains no label lines." }

# --- load the concept reference (per-ITEM mapping, per-FILING reference) ----------------------------------

$mappingRows = @(Import-CsvUtf8 -Path $MappingPath -RequiredColumns @('accession', 'item', 'categories'))
$conceptsByAccession = @{}   # accession -> hashtable of concept -> $true
$itemsByAccessionConcept = @{}  # "accession|concept" -> List[string] of the items that mapped there
$mappingRowsByAccession = @{}
$mappingOutsideLabels = New-Object System.Collections.Generic.List[string]
foreach ($row in $mappingRows) {
    $accession = [string]$row.accession
    if (-not $labelClean.ContainsKey($accession)) {
        if (-not $mappingOutsideLabels.Contains($accession)) { $mappingOutsideLabels.Add($accession) | Out-Null }
        continue
    }
    if (-not $mappingRowsByAccession.ContainsKey($accession)) { $mappingRowsByAccession[$accession] = 0 }
    $mappingRowsByAccession[$accession] = $mappingRowsByAccession[$accession] + 1
    if (-not $conceptsByAccession.ContainsKey($accession)) { $conceptsByAccession[$accession] = @{} }
    foreach ($category in (([string]$row.categories) -split ';')) {
        $concept = $category.Trim()
        if ([string]::IsNullOrEmpty($concept)) { continue }
        $conceptsByAccession[$accession][$concept] = $true
        $key = $accession + '|' + $concept
        if (-not $itemsByAccessionConcept.ContainsKey($key)) {
            $itemsByAccessionConcept[$key] = New-Object System.Collections.Generic.List[string]
        }
        $itemsByAccessionConcept[$key].Add([string]$row.item) | Out-Null
    }
}
if ($mappingOutsideLabels.Count -gt 0) {
    throw ("Mapping '{0}' holds {1} accession(s) that are not in '{2}' (e.g. {3}). The concept reference must be derived from the SAME labeled population it is scored over." -f `
        $MappingPath, $mappingOutsideLabels.Count, $LabelsPath, (@(Sort-Ordinal -Values @($mappingOutsideLabels))[0]))
}

# COHORT-COVERAGE GUARD (spec 165's central correctness risk): the spec-162 mapping covers only the 145
# directional filings. Scored over the 235 labeled filings it would read every no-signal filing as
# concept-negative and manufacture false positives. A labeled filing that RECORDED comparability items but
# has no mapping row proves the mapping was generated over a narrower cohort - fail, naming the remedy.
$uncoveredWithItems = New-Object System.Collections.Generic.List[string]
$itemCountMismatches = New-Object System.Collections.Generic.List[string]
foreach ($accession in (Sort-Ordinal -Values @($labelOrder))) {
    $recorded = $labelItemCount[$accession]
    $mapped = 0
    if ($mappingRowsByAccession.ContainsKey($accession)) { $mapped = $mappingRowsByAccession[$accession] }
    if ($recorded -gt 0 -and $mapped -eq 0) { $uncoveredWithItems.Add($accession) | Out-Null }
    elseif ($recorded -ne $mapped) { $itemCountMismatches.Add(('{0}: {1} labeled item(s), {2} mapping row(s)' -f $accession, $recorded, $mapped)) | Out-Null }
}
if ($uncoveredWithItems.Count -gt 0) {
    throw ("Mapping '{0}' does not cover the labeled population: {1} labeled filing(s) record comparability items but have NO mapping row (e.g. {2}). Regenerate with: categorize-comparability.ps1 -Cohort all-labeled." -f `
        $MappingPath, $uncoveredWithItems.Count, (@($uncoveredWithItems)[0]))
}

# --- scan ------------------------------------------------------------------------------------------------

$primaryIds = @($PrimaryCandidates | ForEach-Object { $_.Id })
$exploratoryIds = @($ExploratoryCandidates | ForEach-Object { $_.Id })

$hitsById = @{}        # candidateId -> hashtable accession -> $true
$contextById = @{}     # "candidateId|accession" -> matched context
foreach ($id in $primaryIds) { $hitsById[$id] = @{} }
foreach ($id in $exploratoryIds) { $hitsById[$id] = @{} }
$v1Hits = @{}
$v1MatchedPhrases = @{}

$missingExhibits = New-Object System.Collections.Generic.List[string]
foreach ($filing in $admitted) {
    $tickerKey = 'unknown'
    if (-not [string]::IsNullOrWhiteSpace($filing.Ticker)) {
        $candidateKey = $filing.Ticker.Trim().ToLowerInvariant()
        if ($candidateKey.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -lt 0) { $tickerKey = $candidateKey }
    }
    $path = Join-Path $ExhibitsDir ('{0}-{1}.txt' -f $tickerKey, $filing.Accession)
    if (-not (Test-Path -LiteralPath $path)) {
        $missingExhibits.Add($path) | Out-Null
        continue
    }

    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $path).ProviderPath)
    $actualHash = Get-Sha256Hex -Bytes $bytes
    if ($actualHash -ne $filing.ExpectedHash) {
        throw ("Exhibit hash mismatch for '{0}' (accession {1}): manifest fullTextSha256 '{2}', recomputed SHA-256 of the file bytes '{3}'. The archived body is not the body the manifest pinned; refusing to measure it." -f `
            $path, $filing.Accession, $filing.ExpectedHash, $actualHash)
    }

    $collapsed = Get-CollapsedBody -Text ([System.Text.Encoding]::UTF8.GetString($bytes))

    foreach ($candidate in $PrimaryCandidates) {
        $index = $collapsed.IndexOf($candidate.Literal, [System.StringComparison]::OrdinalIgnoreCase)
        if ($index -ge 0) {
            $hitsById[$candidate.Id][$filing.Accession] = $true
            $contextById[$candidate.Id + '|' + $filing.Accession] = Get-MatchContext -Body $collapsed -Index $index -Length $candidate.Literal.Length
        }
    }
    foreach ($candidate in $ExploratoryCandidates) {
        $match = [System.Text.RegularExpressions.Regex]::Match($collapsed, $candidate.Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($match.Success) {
            $hitsById[$candidate.Id][$filing.Accession] = $true
            $contextById[$candidate.Id + '|' + $filing.Accession] = Get-MatchContext -Body $collapsed -Index $match.Index -Length $match.Length
        }
    }
    $matchedV1 = New-Object System.Collections.Generic.List[string]
    foreach ($phrase in $V1CapTriggeringPhrases) {
        if ($collapsed.IndexOf($phrase, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $matchedV1.Add($phrase) | Out-Null }
    }
    if ($matchedV1.Count -gt 0) {
        $v1Hits[$filing.Accession] = $true
        $v1MatchedPhrases[$filing.Accession] = ($matchedV1 -join ';')
    }
}
if ($missingExhibits.Count -gt 0) {
    throw ("{0} admitted manifest row(s) have no exhibit file (first: '{1}'). An admitted row is a filing whose body was archived; a missing file is a broken archive, not an exclusion." -f `
        $missingExhibits.Count, $missingExhibits[0])
}

# Every collection below inherits the ORDINAL accession order of $admitted, so every listing, FP/FN
# enumeration and CSV block is deterministically ordered without a further sort.
$scannedAccessions = @($admitted | ForEach-Object { $_.Accession })
$labeledScanned = @($scannedAccessions | Where-Object { $labelClean.ContainsKey($_) })
$unlabeledScanned = @($scannedAccessions | Where-Object { -not $labelClean.ContainsKey($_) })
$labeledNotScanned = @(Sort-Ordinal -Values @(@($labelOrder) | Where-Object { -not $manifestByAccession.ContainsKey($_) }))
$tickerByAccession = @{}
foreach ($filing in $admitted) { $tickerByAccession[$filing.Accession] = $filing.Ticker }

# --- metrics ---------------------------------------------------------------------------------------------

function Test-HasConcept {
    param([string]$Accession, [string]$Concept)
    if (-not $conceptsByAccession.ContainsKey($Accession)) { return $false }
    return $conceptsByAccession[$Accession].ContainsKey($Concept)
}

function New-CandidateResult {
    param($Candidate, [string]$RowKind, [string]$Rule)
    $hits = $hitsById[$Candidate.Id]
    $allHits = @($scannedAccessions | Where-Object { $hits.ContainsKey($_) })
    $labeledHits = @($labeledScanned | Where-Object { $hits.ContainsKey($_) })

    $tp = 0; $fp = 0; $fn = 0
    $fpList = New-Object System.Collections.Generic.List[object]
    $fnList = New-Object System.Collections.Generic.List[object]
    foreach ($accession in $labeledScanned) {
        $hit = $hits.ContainsKey($accession)
        $has = Test-HasConcept -Accession $accession -Concept $Candidate.Concept
        if ($hit -and $has) { $tp++ }
        elseif ($hit -and -not $has) {
            $fp++
            $context = ''
            if ($contextById.ContainsKey($Candidate.Id + '|' + $accession)) { $context = $contextById[$Candidate.Id + '|' + $accession] }
            $fpList.Add((New-Object psobject -Property @{ Accession = $accession; Context = $context })) | Out-Null
        } elseif (-not $hit -and $has) {
            $fn++
            $key = $accession + '|' + $Candidate.Concept
            $items = @()
            if ($itemsByAccessionConcept.ContainsKey($key)) { $items = @($itemsByAccessionConcept[$key]) }
            $fnList.Add((New-Object psobject -Property @{ Accession = $accession; Items = $items })) | Out-Null
        }
    }

    # ANY-BREAK precision: of the LABELED filings this candidate fires on, how many carry a recorded
    # comparisonClean = false? Filings whose label did not record comparisonClean are excluded from both
    # sides and counted separately - never guessed either way.
    $breakHits = 0; $anyBreakN = 0; $unrecordedClean = 0
    foreach ($accession in $labeledHits) {
        $clean = $labelClean[$accession]
        if ($clean -eq $true) { $anyBreakN++ }
        elseif ($clean -eq $false) { $anyBreakN++; $breakHits++ }
        else { $unrecordedClean++ }
    }

    $v1Overlap = @($allHits | Where-Object { $v1Hits.ContainsKey($_) }).Count
    $novelLabeled = @($labeledHits | Where-Object { -not $v1Hits.ContainsKey($_) }).Count

    $precision = $null; $recall = $null; $f1 = $null
    if (($tp + $fp) -gt 0) { $precision = $tp / [double]($tp + $fp) }
    if (($tp + $fn) -gt 0) { $recall = $tp / [double]($tp + $fn) }
    if ($null -ne $precision -and $null -ne $recall -and ($precision + $recall) -gt 0) {
        $f1 = (2.0 * $precision * $recall) / ($precision + $recall)
    }

    return (New-Object psobject -Property @{
        Id              = $Candidate.Id
        Rule            = $Rule
        Concept         = $Candidate.Concept
        Rationale       = $Candidate.Rationale
        RowKind         = $RowKind
        HitAccessions   = $allHits
        Hits            = $allHits.Count
        HitRate         = $allHits.Count / [double]$scannedAccessions.Count
        LabeledHits     = $labeledHits.Count
        Tp              = $tp
        Fp              = $fp
        Fn              = $fn
        Precision       = $precision
        Recall          = $recall
        F1              = $f1
        FpList          = $fpList
        FnList          = $fnList
        BreakHits       = $breakHits
        AnyBreakN       = $anyBreakN
        UnrecordedClean = $unrecordedClean
        V1Overlap       = $v1Overlap
        NovelLabeled    = $novelLabeled
    })
}

$primaryResults = @($PrimaryCandidates | ForEach-Object { New-CandidateResult -Candidate $_ -RowKind 'primary' -Rule $_.Literal })
$exploratoryResults = @($ExploratoryCandidates | ForEach-Object { New-CandidateResult -Candidate $_ -RowKind 'exploratory' -Rule $_.Pattern })

# v1 baseline: hit rate + ANY-BREAK precision + overlap ONLY. Structurally no concept precision/recall - v1's
# 15 phrases detect impairments, litigation, settlements and asset-sale effects, concepts NEITHER candidate
# reference covers, so scoring it against them would count legitimate hits as false positives.
$v1HitAccessions = @($scannedAccessions | Where-Object { $v1Hits.ContainsKey($_) })
$v1LabeledHits = @($labeledScanned | Where-Object { $v1Hits.ContainsKey($_) })
$v1BreakHits = 0; $v1AnyBreakN = 0; $v1UnrecordedClean = 0
foreach ($accession in $v1LabeledHits) {
    $clean = $labelClean[$accession]
    if ($clean -eq $true) { $v1AnyBreakN++ }
    elseif ($clean -eq $false) { $v1AnyBreakN++; $v1BreakHits++ }
    else { $v1UnrecordedClean++ }
}

# --- concept reference sizes ------------------------------------------------------------------------------

$conceptNames = @()
foreach ($concept in (Sort-Ordinal -Values @($PrimaryCandidates | ForEach-Object { $_.Concept }))) {
    if ($conceptNames -notcontains $concept) { $conceptNames += $concept }
}
$conceptPositives = @{}
foreach ($concept in $conceptNames) {
    $conceptPositives[$concept] = @($labeledScanned | Where-Object { Test-HasConcept -Accession $_ -Concept $concept }).Count
}

# --- render -----------------------------------------------------------------------------------------------

$md = New-Object System.Collections.Generic.List[string]
function Add-Line { param([string]$Text = '') $md.Add($Text) | Out-Null }
function Format-Cell { param([string]$Text) return ($Text -replace '\|', '\|') }
function Format-Optional { param($Value) if ($null -eq $Value) { return 'n/a' } return (Format-Fixed ([double]$Value)) }

Add-Line '# Spec 165 - cmpscan-v2 candidate measurement (raw exhibit bodies)'
Add-Line ''
Add-Line 'Read-only measurement. `EarningsComparabilityScan` (cmpscan-v1) is NOT touched: no production code,'
Add-Line 'no fingerprint input, no pin move. Every number below is descriptive except the precommitted'
Add-Line 'promotion rule at the end.'
Add-Line ''
Add-Line '## Inputs and denominators'
Add-Line ''
Add-Line ('- Manifest rows: {0}; admitted (outcome=success and non-empty fullTextSha256): {1}; excluded: {2}.' -f `
    $manifestRows.Count, $admitted.Count, $excludedRows.Count)
if ($excludedRows.Count -gt 0) {
    foreach ($excluded in $excludedRows) { Add-Line ('  - EXCLUDED {0}' -f $excluded) }
}
Add-Line ('- Every admitted exhibit was hash-verified: raw file bytes SHA-256 == manifest `fullTextSha256` ({0}/{0}).' -f $admitted.Count)
Add-Line ('- Labeled filings (concept + any-break reference): {0} of the {1} scanned. Unlabeled (hit rates ONLY): {2}.' -f `
    $labeledScanned.Count, $scannedAccessions.Count, $unlabeledScanned.Count)
if ($labeledNotScanned.Count -gt 0) {
    Add-Line ('- Labeled filings with NO manifest row (excluded from every reference metric): {0} - {1}.' -f `
        $labeledNotScanned.Count, ($labeledNotScanned -join ', '))
}
Add-Line ('- Concept mapping: {0} item rows over {1} labeled filing(s).' -f $mappingRows.Count, $mappingRowsByAccession.Keys.Count)
foreach ($concept in $conceptNames) {
    Add-Line ('  - concept reference `{0}`: {1}/{2} labeled filings positive.' -f $concept, $conceptPositives[$concept], $labeledScanned.Count)
}
$breakCount = @($labeledScanned | Where-Object { $labelClean[$_] -eq $false }).Count
$cleanCount = @($labeledScanned | Where-Object { $labelClean[$_] -eq $true }).Count
$unrecordedCount = $labeledScanned.Count - $breakCount - $cleanCount
Add-Line ('  - ANY-BREAK reference (`label.comparisonClean = false`): {0} break / {1} clean / {2} not recorded.' -f `
    $breakCount, $cleanCount, $unrecordedCount)
if ($itemCountMismatches.Count -gt 0) {
    Add-Line ('- NOTE: {0} labeled filing(s) whose recorded item count differs from their mapping row count (reported, not fatal): {1}.' -f `
        $itemCountMismatches.Count, ((Sort-Ordinal -Values @($itemCountMismatches)) -join '; '))
}
Add-Line ''

Add-Line '## Primary candidates (FROZEN before the run - literal, case-insensitive substring, cmpscan-v1 semantics)'
Add-Line ''
Add-Line 'Precision/recall/F1 are FILING-LEVEL over the labeled cohort, against the concept reference. Wilson'
Add-Line '95% intervals are reported for precision and recall; **F1 is a point estimate with no interval**'
Add-Line '(it is a function of two dependent proportions - a Wilson interval on it would not mean what it looks'
Add-Line 'like). `v1 overlap` counts scanned filings where the candidate AND cmpscan-v1 both fire; `novel'
Add-Line '(labeled)` counts LABELED filings where the candidate fires and cmpscan-v1 does NOT.'
Add-Line ''
Add-Line '| id | literal | concept | hit | hit rate | TP | FP | FN | precision (Wilson 95%) | recall (Wilson 95%) | F1 | any-break precision | v1 overlap | novel (labeled) |'
Add-Line '| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |'
foreach ($result in $primaryResults) {
    Add-Line ('| {0} | `{1}` | {2} | {3}/{4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} |' -f `
        $result.Id, (Format-Cell $result.Rule), $result.Concept, $result.Hits, $scannedAccessions.Count, `
        (Format-Fixed $result.HitRate), $result.Tp, $result.Fp, $result.Fn, `
        (Format-Ratio -Successes $result.Tp -N ($result.Tp + $result.Fp)), `
        (Format-Ratio -Successes $result.Tp -N ($result.Tp + $result.Fn)), `
        (Format-Optional $result.F1), `
        (Format-Ratio -Successes $result.BreakHits -N $result.AnyBreakN), `
        $result.V1Overlap, $result.NovelLabeled)
}
Add-Line ''
Add-Line 'Rationales (recorded with the frozen list, before any result was seen):'
Add-Line ''
foreach ($result in $primaryResults) {
    Add-Line ('- `{0}` ({1}) - {2}' -f $result.Rule, $result.Id, $result.Rationale)
}
Add-Line ''

Add-Line '## cmpscan-v1 baseline (hit rate + ANY-BREAK precision + overlap ONLY)'
Add-Line ''
Add-Line 'v1''s 15 cap-triggering phrases legitimately detect impairments, litigation, settlements and'
Add-Line 'asset-sale effects - concepts NEITHER candidate reference covers. Scoring v1 against the'
Add-Line 'acquisition/tax references would count its legitimate hits as false positives, so **no concept'
Add-Line 'precision or recall is computed for v1** (structurally: the baseline is not a candidate row and'
Add-Line 'never enters the candidate metric function).'
Add-Line ''
Add-Line '| rule | filings hit | hit rate | labeled hits | any-break precision |'
Add-Line '| --- | --- | --- | --- | --- |'
Add-Line ('| cmpscan-v1 (15 cap-triggering phrases) | {0}/{1} | {2} | {3} | {4} |' -f `
    $v1HitAccessions.Count, $scannedAccessions.Count, `
    (Format-Fixed ($v1HitAccessions.Count / [double]$scannedAccessions.Count)), `
    $v1LabeledHits.Count, (Format-Ratio -Successes $v1BreakHits -N $v1AnyBreakN))
if ($v1UnrecordedClean -gt 0) {
    Add-Line ''
    Add-Line ('Labeled v1 hits whose label did not record comparisonClean (excluded from the rate): {0}.' -f $v1UnrecordedClean)
}
Add-Line ''

Add-Line '## False positives and false negatives (examples, not just counts)'
Add-Line ''
Add-Line 'A "false positive" here means the candidate fired on a labeled filing whose concept reference is'
Add-Line 'negative. That may be a genuine over-match OR a label omission - only the example lets a human tell,'
Add-Line ('which is why the context is printed. Listings are capped at {0} per candidate per list, sorted by' -f $FpFnListCap)
Add-Line 'accession (ordinal ascending), with the overflow counted.'
Add-Line ''
foreach ($result in $primaryResults) {
    Add-Line ('### {0} - `{1}` ({2})' -f $result.Id, $result.Rule, $result.Concept)
    Add-Line ''
    if ($result.FpList.Count -eq 0) {
        Add-Line 'No false positives.'
    } else {
        Add-Line ('False positives: {0}.' -f $result.FpList.Count)
        $shown = 0
        foreach ($fp in $result.FpList) {
            if ($shown -ge $FpFnListCap) { break }
            Add-Line ('- {0}: {1}' -f $fp.Accession, (Format-Cell $fp.Context))
            $shown++
        }
        if ($result.FpList.Count -gt $shown) {
            Add-Line ('- ... and {0} more (listing capped at {1}).' -f ($result.FpList.Count - $shown), $FpFnListCap)
        }
    }
    Add-Line ''
    if ($result.FnList.Count -eq 0) {
        Add-Line 'No false negatives.'
    } else {
        Add-Line ('False negatives: {0}.' -f $result.FnList.Count)
        $shown = 0
        foreach ($fn in $result.FnList) {
            if ($shown -ge $FpFnListCap) { break }
            Add-Line ('- {0}: {1}' -f $fn.Accession, (Format-Cell (($fn.Items) -join ' // ')))
            $shown++
        }
        if ($result.FnList.Count -gt $shown) {
            Add-Line ('- ... and {0} more (listing capped at {1}).' -f ($result.FnList.Count - $shown), $FpFnListCap)
        }
    }
    Add-Line ''
}

Add-Line '## EXPLORATORY rows (regex variants) - DESCRIPTIVE ONLY'
Add-Line ''
Add-Line '**These rows are NOT eligible for the promotion rule and no production recommendation may cite'
Add-Line 'them.** They were not frozen with the primary list and exist only to indicate whether a narrower'
Add-Line 'anchored form is worth precommitting in a FUTURE measurement round.'
Add-Line ''
Add-Line '| id | regex | concept | hit | hit rate | TP | FP | FN | precision | recall | F1 | any-break precision | v1 overlap | novel (labeled) |'
Add-Line '| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |'
foreach ($result in $exploratoryResults) {
    Add-Line ('| {0} | `{1}` | {2} | {3}/{4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} |' -f `
        $result.Id, (Format-Cell $result.Rule), $result.Concept, $result.Hits, $scannedAccessions.Count, `
        (Format-Fixed $result.HitRate), $result.Tp, $result.Fp, $result.Fn, `
        (Format-Optional $result.Precision), (Format-Optional $result.Recall), (Format-Optional $result.F1), `
        (Format-Ratio -Successes $result.BreakHits -N $result.AnyBreakN), `
        $result.V1Overlap, $result.NovelLabeled)
}
Add-Line ''

# =========================================================================================================
# THE PRECOMMITTED PROMOTION RULE (frozen in spec 165 before the run). This block is the ONLY thing here
# that grounds a production recommendation.
#
# RECOMMENDED iff, over the labeled reference cohort:
#     concept precision >= 0.80  AND  concept recall >= 0.30  AND  novel (labeled) >= 5
# All three, or NOT RECOMMENDED - full stop.
# =========================================================================================================
$PromotionPrecision = 0.80
$PromotionRecall = 0.30
$PromotionNovel = 5

foreach ($result in $primaryResults) {
    if ($result.RowKind -ne 'primary') {
        throw "Promotion evaluation reached a '$($result.RowKind)' row ('$($result.Id)'). Only the frozen primary literal candidates are eligible; exploratory rows are excluded by construction."
    }
}

Add-Line '## Decisions - the PRECOMMITTED promotion rule, applied verbatim'
Add-Line ''
Add-Line ('Frozen in spec 165 before this measurement ran: a PRIMARY (literal) candidate is RECOMMENDED for a')
Add-Line ('production cmpscan-v2 spec iff **concept precision >= {0} AND concept recall >= {1} AND it fires on' -f `
    (Format-Fixed $PromotionPrecision 2), (Format-Fixed $PromotionRecall 2))
Add-Line ('>= {0} labeled filings where cmpscan-v1 did not** - all three, over the {1}-filing labeled reference.' -f `
    $PromotionNovel, $labeledScanned.Count)
Add-Line 'Candidates failing it are NOT recommended, full stop. Exploratory rows are ineligible.'
Add-Line ''
Add-Line '| id | literal | precision | >= threshold | recall | >= threshold | novel (labeled) | >= threshold | VERDICT |'
Add-Line '| --- | --- | --- | --- | --- | --- | --- | --- | --- |'
$recommended = New-Object System.Collections.Generic.List[string]
foreach ($result in $primaryResults) {
    $precisionOk = ($null -ne $result.Precision -and [double]$result.Precision -ge $PromotionPrecision)
    $recallOk = ($null -ne $result.Recall -and [double]$result.Recall -ge $PromotionRecall)
    $novelOk = ($result.NovelLabeled -ge $PromotionNovel)
    $verdict = 'NOT RECOMMENDED'
    if ($precisionOk -and $recallOk -and $novelOk) {
        $verdict = 'RECOMMENDED'
        $recommended.Add($result.Rule) | Out-Null
    }
    Add-Line ('| {0} | `{1}` | {2} | {3} | {4} | {5} | {6} | {7} | {8} |' -f `
        $result.Id, (Format-Cell $result.Rule), (Format-Optional $result.Precision), `
        $(if ($precisionOk) { 'yes' } else { 'no' }), (Format-Optional $result.Recall), `
        $(if ($recallOk) { 'yes' } else { 'no' }), $result.NovelLabeled, `
        $(if ($novelOk) { 'yes' } else { 'no' }), $verdict)
}
Add-Line ''
if ($recommended.Count -eq 0) {
    Add-Line 'RESULT: **no candidate passes the precommitted rule.** Nothing is recommended for production.'
} else {
    Add-Line ('RESULT: {0} candidate(s) pass the precommitted rule: {1}.' -f $recommended.Count, (($recommended | ForEach-Object { '`' + $_ + '`' }) -join ', '))
}
Add-Line ''

Add-Line '## Standing caveats (they apply to EVERY number above)'
Add-Line ''
Add-Line '1. The concept reference derives from EXPLORATORY ratified labels (spec 162 status), not ground truth.'
Add-Line '2. The taxonomy is REGEX-CODED (`categorize-comparability.ps1`) with a long uncategorized tail; a'
Add-Line '   concept-negative filing may simply be a filing whose item text the taxonomy did not catch.'
Add-Line ('3. {0} of the {1} scanned filings have no labels at all - they contribute HIT RATES ONLY and never' -f `
    $unlabeledScanned.Count, $scannedAccessions.Count)
Add-Line '   enter any precision, recall, F1 or any-break number.'
Add-Line '4. Filings cluster within tickers, so observations are not independent and the Wilson intervals are'
Add-Line '   somewhat narrower than the truth.'
Add-Line '5. Nothing here changes production. A promoted phrase becomes real only via a cmpscan-v2 spec, which'
Add-Line '   bumps `EarningsComparabilityScan.Version` and moves the AI-ON scoring pins.'

$report = $md -join [Environment]::NewLine
if ($OutFile) { Set-Content -LiteralPath $OutFile -Value $report -Encoding UTF8 }
Write-Output $report

# --- hit matrix CSV ---------------------------------------------------------------------------------------
# Long form, ONE ROW PER (candidate, filing) WHERE THE CANDIDATE FIRED. Deterministic order: row kind
# (primary, then exploratory, then baseline), candidate table order, accession ordinal ascending.

if ($OutCsv) {
    $csvRows = New-Object System.Collections.Generic.List[object]
    function Add-CsvRow {
        param([string]$CandidateId, [string]$Rule, [string]$Concept, [string]$RowKind, [string]$Accession, [string]$Matched)
        $ticker = ''
        if ($tickerByAccession.ContainsKey($Accession)) { $ticker = $tickerByAccession[$Accession] }
        $labeled = $labelClean.ContainsKey($Accession)
        $hasConcept = ''
        $anyBreak = ''
        if ($labeled) {
            $hasConcept = [string](Test-HasConcept -Accession $Accession -Concept $Concept)
            if ($null -eq $labelClean[$Accession]) { $anyBreak = 'not-recorded' } else { $anyBreak = [string]($labelClean[$Accession] -eq $false) }
        }
        $csvRows.Add([pscustomobject]@{
            candidateId = $CandidateId
            literal     = $Rule
            concept     = $Concept
            rowKind     = $RowKind
            accession   = $Accession
            ticker      = $ticker
            labeled     = [string]$labeled
            hasConcept  = $hasConcept
            anyBreak    = $anyBreak
            v1Hit       = [string]($v1Hits.ContainsKey($Accession))
            matched     = $Matched
        }) | Out-Null
    }

    # $result.HitAccessions and $v1HitAccessions are already in ordinal accession order (they are filtered
    # projections of the ordinally-sorted $scannedAccessions).
    foreach ($result in $primaryResults) {
        foreach ($accession in $result.HitAccessions) {
            Add-CsvRow -CandidateId $result.Id -Rule $result.Rule -Concept $result.Concept -RowKind 'primary' -Accession $accession -Matched $result.Rule
        }
    }
    foreach ($result in $exploratoryResults) {
        foreach ($accession in $result.HitAccessions) {
            Add-CsvRow -CandidateId $result.Id -Rule $result.Rule -Concept $result.Concept -RowKind 'exploratory' -Accession $accession -Matched $result.Rule
        }
    }
    foreach ($accession in $v1HitAccessions) {
        Add-CsvRow -CandidateId 'cmpscan-v1' -Rule '(15 cap-triggering phrases)' -Concept '' -RowKind 'baseline' -Accession $accession -Matched $v1MatchedPhrases[$accession]
    }

    $csvRows | Export-Csv -LiteralPath $OutCsv -NoTypeInformation -Encoding UTF8
    Write-Output ''
    Write-Output ('hit matrix written: {0} ({1} rows)' -f $OutCsv, $csvRows.Count)
}
