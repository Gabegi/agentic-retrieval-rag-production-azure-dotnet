# Knowing what an indexing run is doing

Progress reporting for `StartIndexing` and `StartRestore`. Two mechanisms, one for
runs in flight and one for runs that have finished.

Code: `IndexingFunctions/IndexingStatusFunction.cs` (the status endpoint),
`IndexingFunctions/PdfIndexingFunction.cs` and `IndexingFunctions/IndexRestoreFunction.cs`
(the orchestrators that write the status), `Models/IndexingProgress.cs`.

## Why this exists

`StartIndexing` returns Durable's standard check-status response, and until now the
orchestrator never called `SetCustomStatus`. That meant the only thing the status URI
could tell you was `Running` → `Completed`/`Failed`. A run that had been going for
twenty minutes looked exactly like a run that had been going for twenty seconds, and
the only way to find out which stage it was in — or whether it had quietly died — was
to open App Insights and read the trace.

The two mechanisms below answer the two different questions that came up while doing
that:

| Question | Use |
|---|---|
| Is it still going? Which stage? How long has it been there? | `GET /api/index/status` |
| What did the run that just finished produce? Was it healthy? | The `INDEXING RUN FINISHED` log line |

## During a run — `GET /api/index/status`

Auth level `Function`, so it needs the same function key as the other endpoints.

```
GET /api/index/status                      # newest indexing run
GET /api/index/status?instanceId=<id>      # a specific run, including restores
```

```json
{
  "instanceId": "8f3c1b2a4d5e4f6a8b9c0d1e2f3a4b5c",
  "orchestration": "IndexingOrchestrator",
  "runtimeStatus": "Running",
  "stage": "embedding+uploading",
  "startedAt": "2026-08-06T09:12:04+00:00",
  "finishedAt": null,
  "elapsed": "00:06:31",
  "docsExtracted": 142,
  "chunksProduced": 3891,
  "docsUploaded": null,
  "error": null
}
```

With no `instanceId`, it finds the most recent `IndexingOrchestrator` run — you do not
need to have kept the ID that `StartIndexing` handed back. Pass `instanceId` to pin an
older run, or to look at a restore.

**Reading the response**

- `runtimeStatus` is Durable's own status (`Pending`, `Running`, `Completed`, `Failed`,
  `Terminated`, `Suspended`). `stage` is ours.
- `stage` for indexing: `starting` → `extracting` → `chunking` → `embedding+uploading`
  → `completed` / `failed`. For restore: `starting` → `recreating-index` →
  `restoring-from-snapshot` → `completed` / `failed`. `starting` is the window between
  scheduling and the orchestrator's first `SetCustomStatus`; it is not an error.
- A count is `null` until the stage that measures it has finished. **Null means "not
  measured yet", not "measured zero"** — the same distinction `PdfIndexRunReport`
  draws. `docsUploaded: null` on a run in `embedding+uploading` means the upload hasn't
  reported yet, not that nothing uploaded.
- `elapsed` runs against the wall clock while the run is live, and against
  `finishedAt` once it is terminal.
- Terminal runs keep their counts, so a `completed` run still shows what it produced.
- `error` carries Durable's failure message on a failed run. The full detail is in the
  run report blob and in App Insights.

**404** means either no indexing run in the last 14 days, or no orchestration with
that instance ID. The message says which.

### The limitation worth knowing about

Resolution is **stage-level only**. Status is written at orchestrator stage
boundaries, and extraction is a single Durable activity that fans out internally
(see the comment on `ExtractActivity`). So a run sits on `extracting` for as long as
extraction takes — which is the longest stage — with no "37 of 142 documents"
underneath it.

This is a real gap, not an oversight: activities have no `SetCustomStatus`, so
sub-stage progress needs a separate channel out of the activity (a blob or table
heartbeat that the status endpoint reads). That was scoped out deliberately. If you
find yourself wanting it, that is the shape of the fix.

### If you'd rather not use the endpoint

The same payload rides along on Durable's own `statusQueryGetUri` — the one in
`StartIndexing`'s response — under `customStatus`. The endpoint exists because it
finds the latest run for you, computes `elapsed`, and returns a flat shape; the raw
URI works fine if you already have it open.

## After a run — the summary log line

One line per run, emitted from `SaveIndexReportActivity`:

```
INDEXING RUN FINISHED — instance=… success=True duration=412.4s force=False
docs=142 chunks=3891 uploaded=3891 failed=0 redFlags=0 error=
```

`Information` on success, **`Error` on failure** — so an App Insights alert rule can
key off severity rather than parsing `success=` out of the message text. `redFlags` is
the union of the extraction and embed stages' own red-flag lists, the pre-computed
"something is wrong here" signals.

In App Insights:

```kql
traces
| where message startswith "INDEXING RUN FINISHED"
| order by timestamp desc
```

Two deliberate choices here:

- It is emitted **before** the `_reportWriter.IsEnabled` guard. The run finished either
  way, and whether you hear about it shouldn't depend on report writing being switched
  on.
- It lives in the activity, not the orchestrator body. Orchestrator code replays, so
  logging there would repeat the line several times per run.

## What this does not do

It does not push. Nothing contacts you when a run finishes — you poll the endpoint or
you query the log. In particular, **a hung or dead run produces no signal at all**, so
"nothing has happened" is never proof that nothing went wrong; check
`runtimeStatus` and `elapsed`.

A separate design (`docs/2608/260806/pipeline-run-email-report.md`) covers emailing a
full report after each run. That is post-run analysis — what was produced, whether it
looks healthy, what changed since last time — and it is complementary to this: it
cannot tell you anything about a run that is still going, or one that never reached
its final activity.

## See also

- [README.md](README.md) — the rest of the Function App
- [../../docs/report-schema.md](../../docs/report-schema.md) — what each stage metric means
- [../../docs/indexing-pipeline-split.md](../../docs/indexing-pipeline-split.md) — the pipeline's stage boundaries
