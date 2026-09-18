# Reproduces IndexDocumentService.GetCurrentlyIndexedDocsIdsNDatesAsync's first page from the
# Kudu PowerShell console of the indexer Function App (D200 §6g, 2026-09-17), for zero Content
# Understanding cost. Same managed identity, same VNet route to Search, same SEARCH_ENDPOINT and
# SEARCH_INDEX_NAME app settings the app reads - so whatever comes back is what the app sees.
#
# Why here and not App Insights or LogFiles: host.json has no fileLoggingMode (= debugOnly), so
# the read's WARNING never reaches disk, and App Insights ingestion was unavailable on the day.
#
# Run from Kudu -> Debug console -> PowerShell, in D:\home (or $env:HOME):
#   1. Upload this file into $env:HOME\site (drag onto the file list), then
#   2. powershell -ExecutionPolicy Bypass -File "$env:HOME\site\ProbeIndexState.ps1"
# or paste the body line by line.
#
# Reading it (§6e's rule):
#   @odata.count 0, value []             -> the query matches nothing (candidate A). Compare the
#                                            two api-versions: if the stable one returns rows and
#                                            the preview does not, the pin is the defect.
#   count > 0, rows carry last_modified_date -> rows exist; the defect is in the app's parsing of
#                                            them (candidate B). Look at the raw JSON TYPE of
#                                            last_modified_date and document_id.
#   count > 0, last_modified_date null/absent -> the field is not on the rows despite being on the
#                                            upload payload; the read is right to skip them.

$ErrorActionPreference = 'Stop'

$endpoint = $env:SEARCH_ENDPOINT
$index    = $env:SEARCH_INDEX_NAME
"SEARCH_ENDPOINT   = $endpoint"
"SEARCH_INDEX_NAME = $index"
"IDENTITY_ENDPOINT = $($env:IDENTITY_ENDPOINT)"
if (-not $env:IDENTITY_ENDPOINT) { throw "No IDENTITY_ENDPOINT in this console - managed identity is not reachable from here." }

# Token for Azure AI Search from the App Service managed-identity endpoint (same identity the
# app's DefaultAzureCredential resolves to in this sandbox).
$tokenResponse = Invoke-RestMethod -Method Get `
    -Uri "$($env:IDENTITY_ENDPOINT)?resource=https://search.azure.com&api-version=2019-08-01" `
    -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }
$headers = @{ Authorization = "Bearer $($tokenResponse.access_token)"; 'Content-Type' = 'application/json' }

# Exactly the app's first page: search=*, the three selected fields, ordered by id, plus count.
$body = @{
    search  = '*'
    select  = 'id,document_id,last_modified_date'
    orderby = 'id'
    top     = 3
    count   = $true
} | ConvertTo-Json

# The app's pin (SearchServiceVersion.Current) first, then a stable version, so an api-version
# difference shows as a difference between the two blocks and nothing else.
foreach ($apiVersion in @('2025-11-01-preview', '2024-07-01')) {
    ""
    "===== api-version=$apiVersion ====="
    try {
        $r = Invoke-RestMethod -Method Post -Headers $headers -Body $body `
            -Uri "$endpoint/indexes/$index/docs/search?api-version=$apiVersion"
        "@odata.count = $($r.'@odata.count')"
        "rows returned = $($r.value.Count)"
        $r.value | ConvertTo-Json -Depth 5
    }
    catch {
        "REQUEST FAILED: $($_.Exception.Message)"
        if ($_.ErrorDetails) { $_.ErrorDetails.Message }
    }
}

# Also the statistic the removed guard compared against, from the same identity.
""
"===== index statistics ====="
try {
    Invoke-RestMethod -Method Get -Headers $headers `
        -Uri "$endpoint/indexes/$index/stats?api-version=2024-07-01" | ConvertTo-Json
}
catch { "STATS FAILED: $($_.Exception.Message)" }
