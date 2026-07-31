<#
.SYNOPSIS
    Spec 162 - reproducible category coding of the free-text comparabilityItems in the study labels.

.DESCRIPTION
    The blinded labels record comparabilityItems as free text (with amounts). The findings doc's
    categorized frequency table is REGEX-CODED EXPLORATORY ANALYSIS, not analyzer output: this script IS
    the committed taxonomy. It reads the labels JSONL, joins to the worksheet to select directional rows,
    applies the category regexes below to every item, and writes a per-item mapping CSV
    (accession, item, categories) plus per-category filing counts (a filing counts once per category).

    A filing/item can match multiple categories; items matching none are emitted as 'uncategorized'.
    Deterministic: same inputs => same output, any machine.

.EXAMPLE
    powershell -File scripts/calibration-audit/categorize-comparability.ps1 `
        -LabelsPath docs/162-calibration-labels-full.jsonl `
        -WorksheetPath docs/162-study-worksheet.csv `
        -OutFile docs/162-comparability-item-mapping.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LabelsPath,
    [Parameter(Mandatory = $true)][string]$WorksheetPath,
    [string]$OutFile
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# THE taxonomy. Case-insensitive regexes; edit here and re-run to re-code.
$Categories = [ordered]@{
    'acquisition-divestiture-perimeter' = 'acqui|divest|perimeter|deconsolidat|held.for.sale|carve|spin|merger|buy.?in|purchase of'
    'one-time-tax'                      = 'tax (benefit|release|swing|item|valuation|position|rate)|discrete tax|deferred tax'
    'impairment-restructuring'          = 'impair|restructur|severance|closure|wind.down|abandon'
    'asset-sale-gains-losses'           = 'gain on|loss on|sale of (building|assets|property|licen|intangible)|disposition'
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
$directionalLabels = 0
$filingsWithItems = 0

foreach ($line in Get-Content -LiteralPath $LabelsPath -Encoding UTF8) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $label = ConvertFrom-Json -InputObject $line
    if (-not $directional.ContainsKey($label.accession)) { continue }
    $directionalLabels++
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

Write-Output ("directional labels: {0}; with >=1 comparability item: {1}; items coded: {2}" -f `
    $directionalLabels, $filingsWithItems, $rows.Count)
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