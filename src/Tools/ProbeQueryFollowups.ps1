# Multi-turn probe of the deployed POST /api/query (2026-09-24).
#
# WHAT THIS CAN AND CANNOT SHOW
# The API is stateless. QueryRequest is `record QueryRequest(string? Question)` - one field, no
# thread id, no history array (QueryEndpoint.cs). AgenticRagQueryService.AskAsync builds a
# KnowledgeBaseRetrievalRequest whose Messages collection gets exactly one user message, built
# from the question it was handed and nothing else. The response (QueryResponse) carries
# answer/category/sources/telemetry - no conversation id, so even the client cannot stitch calls
# together. Nothing between two calls is shared.
#
# So this script cannot test "does it remember" - the answer is no, by construction. What it
# tests is what that costs a caller, which is the thing OutSystems will actually hit:
#
#   Pass A - the follow-ups sent bare, the way a chat UI would send them ("en daarna?"). Each
#            one is missing its subject, and the knowledge base has to retrieve against the
#            fragment alone.
#   Pass B - the same three follow-ups, self-contained (the subject written back in), the way a
#            caller that carries its own context would send them.
#
# Read it by comparing the two passes row for row:
#   B answers, A refuses or drifts -> the gap IS the missing conversation state. A frontend has
#                                     to rewrite each follow-up before sending it; the fix is on
#                                     the caller's side or in a new history field on the request.
#   A and B both answer alike      -> the retrieval is finding the topic from the fragment
#                                     anyway. Read the sources: they may be answering a
#                                     different question that happens to sound fine.
#   A and B both refuse            -> not a context problem. Something upstream is wrong; check
#                                     sources=0 and go to the App Insights trace.
#
# No judging in here on purpose: every answer is printed in full, because "did it answer the
# question that was asked" is not something this script is in a position to decide.
#
# Run:
#   powershell -ExecutionPolicy Bypass -File src\Tools\ProbeQueryFollowups.ps1 -ApiHost <name>.azurewebsites.net
# (-ApiHost, not -Host: $Host is a PowerShell automatic variable and cannot be a parameter name.)
# The app has no auth on this route (Program.cs registers none, and the pipeline's own smoke
# curl sends no key) - the only gate is the network restriction, so this needs to run from an IP
# in dev_allowed_ips.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ApiHost,

    # Matches the pipeline's own smoke step: agentic retrieval plus synthesis on a cold instance
    # is tens of seconds, not the 20 the /health probe allows (deploy-api.yml).
    [int]    $TimeoutSec = 120,

    # Every call writes pipeline-reports/queries/{yyyy}/{MM}/{dd}/{HH-mm-ss}.json and the writer
    # is always on (RunReportWriter.IsEnabled => true). That path is second-granular, so two
    # calls that land in the same second overwrite each other's report. The pause keeps the 8
    # calls as 8 readable blobs.
    [int]    $PauseSec = 2,

    [string] $OutFile = "$PSScriptRoot\..\..\query-followups.json"
)

$ErrorActionPreference = 'Stop'
$uri = "https://$ApiHost/api/query"

# One topic, then three follow-ups that are unanswerable on their own: each drops the subject and
# leans on the turn before it. Dutch in, Dutch out - the answer instructions are Dutch
# (KnowledgeService.cs), so an English probe tests the wrong thing.
$opener = 'Hoe moet ik mij ziekmelden bij Contoso?'

$followups = @(
    @{ Bare = 'En als het langer duurt dan een week?'
       Full = 'Als mijn ziekmelding bij Contoso langer duurt dan een week, wat moet ik dan doen?' }

    @{ Bare = 'Wie moet ik daarvoor bellen?'
       Full = 'Wie moet ik bellen om mij ziek te melden bij Contoso?' }

    @{ Bare = 'En in het weekend?'
       Full = 'Hoe meld ik mij ziek bij Contoso in het weekend?' }
)

function Invoke-Query {
    param([string] $Label, [string] $Question)

    $body = @{ question = $Question } | ConvertTo-Json -Compress
    $sw   = [Diagnostics.Stopwatch]::StartNew()

    try {
        $resp = Invoke-RestMethod -Method Post -Uri $uri -Body $body `
                                  -ContentType 'application/json' -TimeoutSec $TimeoutSec
        $sw.Stop()
        $status = 200
    }
    catch {
        $sw.Stop()
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        # A 500 answers problem+json and that body is the thing worth reading, same as the
        # pipeline's "dumped before any assertion" rule.
        $raw = try { $_.ErrorDetails.Message } catch { $_.Exception.Message }
        $resp = $null
    }

    $row = [ordered]@{
        label        = $Label
        question     = $Question
        http         = $status
        wall_ms      = $sw.ElapsedMilliseconds
        answer       = if ($resp) { $resp.answer } else { $raw }
        category     = if ($resp) { $resp.category } else { $null }
        source_count = if ($resp) { @($resp.sources).Count } else { 0 }
        sources      = if ($resp) { @($resp.sources | ForEach-Object { $_.label }) } else { @() }
        latency_ms   = if ($resp) { $resp.telemetry.latency_ms } else { $null }
        in_tokens    = if ($resp) { $resp.telemetry.input_tokens } else { $null }
        out_tokens   = if ($resp) { $resp.telemetry.output_tokens } else { $null }
    }

    Write-Host ""
    Write-Host "--- $Label " -NoNewline -ForegroundColor Cyan
    Write-Host "[HTTP $status, $($row.source_count) sources, $($row.in_tokens) in / $($row.out_tokens) out, $($sw.ElapsedMilliseconds)ms]" -ForegroundColor DarkGray
    Write-Host "Q: $Question"
    Write-Host "A: $($row.answer)"
    if ($row.source_count -gt 0) { $row.sources | ForEach-Object { Write-Host "   * $_" -ForegroundColor DarkGray } }

    Start-Sleep -Seconds $PauseSec
    [pscustomobject]$row
}

$rows = @()

Write-Host "POST $uri" -ForegroundColor Yellow
Write-Host "=== Turn 1: the opener (shared by both passes) ===" -ForegroundColor Yellow
$rows += Invoke-Query -Label 'turn-1-opener' -Question $opener

Write-Host ""
Write-Host "=== Pass A: follow-ups sent bare, as a chat UI would ===" -ForegroundColor Yellow
for ($i = 0; $i -lt $followups.Count; $i++) {
    $rows += Invoke-Query -Label "A-followup-$($i + 1)" -Question $followups[$i].Bare
}

Write-Host ""
Write-Host "=== Pass B: the same follow-ups, self-contained ===" -ForegroundColor Yellow
for ($i = 0; $i -lt $followups.Count; $i++) {
    $rows += Invoke-Query -Label "B-followup-$($i + 1)" -Question $followups[$i].Full
}

# The contract probe the pipeline runs: {} binds question to null and the handler answers 400,
# not 500 - the problem+json shape OutSystems imports (deploy-api.yml, QueryEndpoint.cs).
Write-Host ""
Write-Host "=== Contract probe: empty body ===" -ForegroundColor Yellow
try {
    Invoke-RestMethod -Method Post -Uri $uri -Body '{}' -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    $emptyStatus = 200
}
catch {
    $emptyStatus = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
}
Write-Host "empty {} body -> HTTP $emptyStatus (expected 400)" -ForegroundColor $(if ($emptyStatus -eq 400) { 'Green' } else { 'Red' })

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Yellow
$rows | Format-Table label, http, source_count, in_tokens, out_tokens, wall_ms -AutoSize

[pscustomobject]@{
    uri          = $uri
    run_utc      = (Get-Date).ToUniversalTime().ToString('o')
    empty_body   = $emptyStatus
    rows         = $rows
} | ConvertTo-Json -Depth 6 | Set-Content -Path $OutFile -Encoding utf8

Write-Host "Wrote $OutFile" -ForegroundColor DarkGray
