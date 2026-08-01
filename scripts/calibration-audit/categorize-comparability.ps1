<#
.SYNOPSIS
    Spec 162 - reproducible category coding of the free-text comparabilityItems in the study labels.

.DESCRIPTION
    The blinded labels record comparabilityItems as free text (with amounts). The findings doc's
    categorized frequency table is REGEX-CODED EXPLORATORY ANALYSIS, not analyzer output: this script IS
    the committed taxonomy. It reads the labels JSONL, selects the requested COHORT, applies the category
    regexes below to every item, and writes a per-item mapping CSV (accession, ticker, item, categories)
    plus per-category filing counts (a filing counts once per category).

    COHORTS (spec 165):
      - directional  (default) - only filings whose worksheet row is `DirectionalSignalProduced`. This is
                     the spec-162 cohort: the default reproduces docs/162-comparability-item-mapping.csv
                     byte-for-byte, so the committed artifact and its 145-filing numbers never move.
      - all-labeled  - EVERY label line in the JSONL (directional + no-signal). Required whenever the
                     mapping is used as a concept REFERENCE over the labeled population: with the
                     directional cohort every no-signal filing would silently read as concept-negative
                     and manufacture false positives (spec 165).

    A filing/item can match multiple categories; items matching none are emitted as 'uncategorized'.
    Deterministic: same inputs => same output, any machine. A duplicate accession inside the selected
    cohort is a protocol violation (the labels are one effective label per accession) and FAILS loudly
    rather than double-counting its items.

.EXAMPLE
    powershell -File scripts/calibration-audit/categorize-comparability.ps1 `
        -LabelsPath docs/162-calibration-labels-full.jsonl `
        -WorksheetPath docs/162-study-worksheet.csv `
        -OutFile docs/162-comparability-item-mapping.csv

.EXAMPLE
    powershell -File scripts/calibration-audit/categorize-comparability.ps1 `
        -LabelsPath docs/162-calibration-labels-full.jsonl `
        -WorksheetPath docs/162-study-worksheet.csv `
        -Cohort all-labeled `
        -OutFile docs/165-comparability-item-mapping-all235.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LabelsPath,
    [Parameter(Mandatory = $true)][string]$WorksheetPath,
    [ValidateSet('directional', 'all-labeled')][string]$Cohort = 'directional',
    [string]$OutFile
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# THE taxonomy. Case-insensitive regexes; edit here and re-run to re-code.
# Review-narrowed (2026-07-31): 'asset-sale-gains-losses' previously matched bare 'gain on|loss on',
# sweeping in debt-extinguishment gains, business-combination remeasurements and project losses - it now
# requires an actual sale/disposal/disposition/divestiture context. 'discrete-tax' previously matched
# 'tax rate', sweeping ordinary effective-rate changes into a category the findings interpret as
# discrete/one-time tax evidence - 'rate' is out, and the category is renamed to say what it matches.
$Categories = [ordered]@{
    'acquisition-divestiture-perimeter' = 'acqui|divest|perimeter|deconsolidat|held.for.sale|carve|spin|merger|buy.?in|purchase of'
    'discrete-tax'                      = 'discrete tax|tax discrete|one.?time tax|tax (benefit|release|swing|item|position)|uncertain tax|valuation allowance|deferred tax'
    'impairment-restructuring'          = 'impair|restructur|severance|closure|wind.down|abandon'
    'asset-sale-gains-losses'           = '(gain|loss)\w* on [\w\s,.$-]*(sale|sales|disposal|disposition|divestiture)|sale of (building|assets|property|licen|intangible)|(gain|loss)\w* on (a )?(sale|disposal|disposition)|property disposition'
    'fx-currency'                       = 'currency|FX|foreign exchange|translation'
    'insurance-weather-litigation'      = 'insurance|storm|hurricane|weather|fire|litigation|legal settle'
    'accounting-change-recast'          = 'recast|reclassif|restate|accounting change|revised.*definition|adopt'
    'lifo-inventory'                    = 'LIFO|inventory (reserve|step|adjust)'
}

$worksheet = Import-Csv -LiteralPath $WorksheetPath
$directional = @{}
foreach ($row in $worksheet) {
    if ($row.outcome -eq 'DirectionalSignalProduced') { $directional[$row.accession] = $true }
}

$rows = New-Object System.Collections.Generic.List[object]
$filingsPerCategory = @{}
foreach ($k in $Categories.Keys) { $filingsPerCategory[$k] = @{} }
$cohortLabels = 0
$filingsWithItems = 0
$seenAccessions = @{}

foreach ($line in Get-Content -LiteralPath $LabelsPath -Encoding UTF8) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $label = ConvertFrom-Json -InputObject $line
    if ($Cohort -eq 'directional' -and -not $directional.ContainsKey($label.accession)) { continue }
    if ($seenAccessions.ContainsKey($label.accession)) {
        throw "Duplicate accession '$($label.accession)' in the '$Cohort' cohort of '$LabelsPath'. The mapping is one row per comparability ITEM of ONE effective label per accession; a second label line (a retry) would double-count its items. Resolve the effective label before coding."
    }
    $seenAccessions[$label.accession] = $true
    $cohortLabels++
    $items = @($label.label.comparabilityItems)
    if ($items.Count -gt 0) { $filingsWithItems++ }
    foreach ($item in $items) {
        $matched = @()
        foreach ($k in $Categories.Keys) {
            if ($item -imatch $Categories[$k]) {
                $matched += $k
                $filingsPerCategory[$k][$label.accession] = $true
            }
        }
        if ($matched.Count -eq 0) { $matched = @('uncategorized') }
        $rows.Add([pscustomobject]@{
            accession  = $label.accession
            ticker     = $label.ticker
            item       = $item
            categories = ($matched -join ';')
        }) | Out-Null
    }
}

Write-Output ("cohort: {0} ({1} filings)" -f $Cohort, $cohortLabels)
Write-Output ("{0} labels: {1}; with >=1 comparability item: {2}; items coded: {3}" -f `
    $Cohort, $cohortLabels, $filingsWithItems, $rows.Count)
Write-Output ''
Write-Output 'filings per category (a filing counts once per category):'
foreach ($k in $Categories.Keys) {
    Write-Output ("  {0}`t{1}" -f $filingsPerCategory[$k].Keys.Count, $k)
}
$uncat = @($rows | Where-Object { $_.categories -eq 'uncategorized' })
Write-Output ("  {0}`tuncategorized items (across {1} filings)" -f $uncat.Count, (@($uncat | ForEach-Object { $_.accession } | Sort-Object -Unique).Count))

if ($OutFile) {
    $rows | Export-Csv -LiteralPath $OutFile -NoTypeInformation -Encoding UTF8
    Write-Output ''
    Write-Output ("mapping written: {0} ({1} rows)" -f $OutFile, $rows.Count)
}