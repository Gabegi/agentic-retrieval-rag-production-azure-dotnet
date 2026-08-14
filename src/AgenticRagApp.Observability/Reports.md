# Blob Storage Layout — Reports, Artifacts & Snapshots

Everything the pipelines write to blob storage, by container. Every report — run reports, stage
diagnostics, per-run content archives, corpus snapshots, and eval results — shares one container
(`pipeline-reports`) and one naming scheme, built by `ReportPath.Build`:

```
{yyyy}/{MM}/{dd}/{yyyyMMddTHHmmssfff}Z-{report-name}[-{id}].json
```

`{yyyy}/{MM}/{dd}` is UTC, taken from the report's own timestamp — browse to today's folder to
find a given run without already knowing its instance ID. `{id}` is the orchestration instance ID
(or the pipeline build ID for eval results) and is omitted entirely (no trailing `-{id}`) when the
caller has none — see `CsvExtractionOrchestrator`'s dormant-pipeline note below.

The one thing deliberately *not* in this container is `vector-cache/{contentHash}.json` (see the
`pipeline-artifacts` section) — it's a content-addressed cache with no report semantics, looked up
directly by hash, and folding it into the naming scheme would turn its O(1) lookups into O(n)
listings.

## Container: `pipeline-reports`

| Report name | Path | Written by | Content |
|---|---|---|---|
| `index-run` | `.../{ts}-index-run-{instanceId}.json` | `IndexingOrchestrator` → `SaveIndexReportActivity` (end of run) | Full run report (`PdfIndexRunReport`) — docs processed/skipped/new/updated, validation errors/warnings, chunk size distribution + samples, embedding + upload stats, index size snapshot |
| `restore-run` | `.../{ts}-restore-run-{instanceId}.json` | `RestoreOrchestrator` → `SaveRestoreReportActivity` | `PdfRestoreRunReport` — index-wiped-and-rebuilt-from-snapshot report: which snapshot generation was used, chunks restored, chunks missing a cached vector |
| `pdf-validation` | `.../{ts}-pdf-validation-{instanceId}.json` | `PdfExtractionPipeline` | PDF extraction validation report (errors/warnings, spot-check sample, per-transform cleaning counts) |
| `pdf-file-facts` | `.../{ts}-pdf-file-facts-{instanceId}.json` | `PdfExtractionPipeline` | Per-file PDF facts — size, spec version, native metadata (Producer/Creator/Subject/Keywords), estimated cost |
| `pdf-failure` | `.../{ts}-pdf-failure-{instanceId}.json` | `PdfExtractionPipeline` | Fallback report for a run that failed *before* a validation report existed (e.g. blob listing/cleaning threw) |
| `pdf-extraction-diff` | `.../{ts}-pdf-extraction-diff-{instanceId}.json` | `ExtractionService` | New/updated/deleted document diff for the run, including the document IDs |
| `csv-validation` | `.../{ts}-csv-validation.json` | `CsvExtractionOrchestrator` | CSV extraction validation report. No `{id}` — CSV is dormant and no orchestration supplies an instance ID |
| `csv-extraction-diff` | `.../{ts}-csv-extraction-diff.json` | `CsvExtractionService` | Same as `pdf-extraction-diff`, for CSV. Also id-less while CSV is dormant |
| `extraction-artifact` | `.../{ts}-extraction-artifact-{instanceId}.json` | `PdfIndexingFunction.ExtractActivity` | Full extracted docs + extraction stats (whole-corpus content, no size cap) |
| `chunking-artifact` | `.../{ts}-chunking-artifact-{instanceId}.json` | `ChunkingService` | The whole chunking stage (`ChunkingRunReport`): one row per input document with its outcome and reason (chunked / no_strategy / zero_chunks / identity_skipped / not_reached), resolved identity per document (family, domain tag, confusable-with, whether its vector was embedded or reused), identity-resolution diagnostics (comparison-set size, exclusions, near-miss pairs, confusable word pairs, thresholds), heading-location rate, plus the full chunk list + stats. **Written even when the stage throws** — from a `finally`, carrying `Success: false` and the stage it died in — which is why the service writes it rather than the activity |
| `embedding-artifact` | `.../{ts}-embedding-artifact-{instanceId}.json` | `PdfIndexingFunction.EmbedAndUploadActivity` | Chunk metadata (id, doc id, content hash, vector dims) + embedding stats — never the raw vectors |
| `snapshot-{source}` | `.../{ts}-snapshot-{source}-{instanceId}.json` | `SnapshotService.UpdateAsync` | Rolling full-corpus snapshot for that source (`pdf`/`csv`) — every chunk believed live in the Search index, merged run over run. Only the 3 most recent generations are kept (older ones pruned). Read back by `RestoreService` to rebuild the index if it's ever wiped/corrupted |
| `eval-results` / `eval-summary` / `eval-trx` | `.../{ts}-eval-{results\|summary\|trx}-{buildId}.{jsonl\|md\|trx}` | `.pipelines/templates/eval-publish-results.yml` (not app code) | Eval suite output: raw JSONL scoring rows, a generated markdown summary, and the MSTest `.trx` |

Plus a handful of fixed-name pointer blobs at the container **root** (not date-folder-scoped —
there's exactly one current value, not history):

| Path | Written by | Content |
|---|---|---|
| `_latest-snapshot-{source}.json` | `SnapshotService.UpdateAsync` | Up to 3 most recent snapshot paths + instance IDs for that source, newest first — how `ReadLatestAsync`/pruning find snapshots without a per-source prefix to list |
| `_latest-eval-results.json` | `.pipelines/templates/eval-publish-results.yml` | `{Path, RanAt}` of the most recent `eval-results` blob — points at the newest eval baseline without having to list the container. No app code reads it since the run report email was removed |
| `indexing/_last-stats-{source}.json` | `RunReportWriter.SaveLastIndexStatsAsync` | Last known index document count/storage size, keyed by source (`pdf`/`csv`) — single rolling baseline for drift detection, **not** per-run history |

All report writes above (except the drift baseline) happen on every run in **every** environment —
`IRunReportWriter.IsEnabled` is unconditionally `true`.

Query reports are the one exception still on their own path, not yet folded into this scheme:
`queries/{yyyy}/{MM}/{dd}/{HH-mm-ss}.json` (`QueryingFunction`, one file per `/api/query` call,
containing question/answer/context/telemetry).

Reports written before this container/naming consolidation stay at their old paths (`runs/`,
`indexing/pdf-extraction/`, the old `pipeline-artifacts`/`eval-results` containers, etc.) — nothing
migrates them.

## Container: `pipeline-artifacts`

| Path | Written by | Content |
|---|---|---|
| `vector-cache/{contentHash}.json` | `VectorCache.SetAsync` | One cached embedding vector per content hash — content-addressed, **not** per-run or dated, since its whole purpose is dedup across runs (same content → cache hit regardless of when it was first embedded). Orphaned entries evicted after each snapshot update |

## Container: `indexing-pipeline` (DI key `pipeline-temp`)

| Path | Written by | Content |
|---|---|---|
| `{yyyy}/{MM}/{dd}/{instanceId}/extracted.json` | `ExtractActivity` | Transient handoff of extracted docs to `ChunkActivity` — deleted once `ChunkActivity` reads it |
| `{yyyy}/{MM}/{dd}/{instanceId}/chunks.json` | `ChunkActivity` | Transient handoff of chunks to `EmbedAndUploadActivity` — deleted once consumed |
| `{yyyy}/{MM}/{dd}/{instanceId}/stale-document-ids.json` | `ExtractActivity` | Stale/removed document IDs, offloaded to blob (rather than Durable Table Storage) to dodge the 64KB row-size limit — deleted once `EmbedAndUploadActivity` consumes it |

These three are pure orchestration payload-passing (only the blob name string travels through Durable state) — nothing here is meant to be read after the run completes; all three are deleted by the end of a successful run.
