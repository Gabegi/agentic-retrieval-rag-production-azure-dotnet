# Blob Storage Layout — Reports, Artifacts & Snapshots

Everything the pipelines write to blob storage, by container. All date-based paths are
`{yyyy}/{MM}/{dd}` in UTC, taken from the run's `StartedAt`/timestamp — browse to today's
folder to find a given run without already knowing its instance ID.

## Container: `pipeline-reports`

| Path | Written by | Content |
|---|---|---|
| `indexing/{yyyy}/{MM}/{dd}/{instanceId}.json` | PDF/CSV `IndexingOrchestrator` (end of run) | Full run report (`PdfIndexRunReport`/`CsvIndexRunReport`) — docs processed/skipped/new/updated, validation errors/warnings, chunk size distribution, embedding + upload stats, index size snapshot |
| `restore/{yyyy}/{MM}/{dd}/{instanceId}.json` | `RestoreOrchestrator` | `PdfRestoreRunReport` — index-wiped-and-rebuilt-from-snapshot report: which snapshot generation was used, chunks restored, chunks missing a cached vector |
| `queries/{yyyy}/{MM}/{dd}/{HH-mm-ss}.json` | `QueryingFunction` (every `/api/query` call) | `QueryRunReport` — question, answer, retrieved context, model/latency/token telemetry. One file per query |
| `indexing/pdf-extraction/{yyyy}/{MM}/{dd}/{HHmmssfff}-validation-report.json` | `PdfExtractionPipeline` | PDF extraction validation report (errors/warnings, spot-check sample) |
| `indexing/pdf-extraction/{yyyy}/{MM}/{dd}/{HHmmssfff}-file-facts.json` | `PdfExtractionPipeline` | Per-file PDF facts — size, spec version, native metadata (Producer/Creator/Subject/Keywords), estimated cost |
| `indexing/pdf-extraction/{yyyy}/{MM}/{dd}/{HHmmssfff}-diagnostics.json` | `PdfExtractionPipeline` | Per-file extraction diagnostics (only written if non-empty; currently always empty — populated by the removed PdfPig backend, kept as a slot for a future backend) |
| `indexing/pdf-extraction/{yyyy}/{MM}/{dd}/{HHmmssfff}-failure-report.json` | `PdfExtractionPipeline` | Fallback report for a run that failed *before* a validation report existed (e.g. blob listing/cleaning threw) |
| `indexing/csv-extraction/{yyyy}/{MM}/{dd}/{HHmmssfff}-validation-report.json` | `CsvExtractionOrchestrator` | CSV extraction validation report |
| `indexing/extraction-diff/{yyyy}/{MM}/{dd}/{HHmmssfff}-diff.json` | `ExtractionService` (PDF & CSV) | New/updated/deleted document diff for the run |
| `indexing/_last-stats-{source}.json` | `RunReportWriter.SaveLastIndexStatsAsync` | Last known index document count/storage size, keyed by source (`pdf`/`csv`) — single rolling baseline for drift detection, **not** per-run history |

All of the above (except the drift baseline) are written on every run in every environment — see `IRunReportWriter.IsEnabled`.

## Container: `pipeline-artifacts`

| Path | Written by | Content |
|---|---|---|
| `{yyyy}/{MM}/{dd}/{instanceId}/extraction.json` | `ExtractActivity` | Full extracted docs + extraction stats (whole-corpus content, no size cap) |
| `{yyyy}/{MM}/{dd}/{instanceId}/chunking.json` | `ChunkActivity` | Full chunk list + chunking stats |
| `{yyyy}/{MM}/{dd}/{instanceId}/embedding.json` | `EmbedAndUploadActivity` | Chunk metadata (id, doc id, content hash, vector dims) + embedding stats — never the raw vectors |
| `snapshots/{source}/{yyyy}/{MM}/{dd}/{instanceId}/full-index.json` | `SnapshotService.UpdateAsync` | Rolling full-corpus snapshot for that source (`pdf`/`csv`) — every chunk believed live in the Search index, merged run over run. Only the 3 most recent generations are kept (older ones pruned). Read back by `RestoreService` to rebuild the index if it's ever wiped/corrupted |
| `vector-cache/{contentHash}.json` | `VectorCache.SetAsync` | One cached embedding vector per content hash — content-addressed, **not** per-run or dated, since its whole purpose is dedup across runs (same content → cache hit regardless of when it was first embedded). Orphaned entries evicted after each snapshot update |

## Container: `indexing-pipeline` (DI key `pipeline-temp`)

| Path | Written by | Content |
|---|---|---|
| `{yyyy}/{MM}/{dd}/{instanceId}/extracted.json` | `ExtractActivity` | Transient handoff of extracted docs to `ChunkActivity` — deleted once `ChunkActivity` reads it |
| `{yyyy}/{MM}/{dd}/{instanceId}/chunks.json` | `ChunkActivity` | Transient handoff of chunks to `EmbedAndUploadActivity` — deleted once consumed |
| `{yyyy}/{MM}/{dd}/{instanceId}/stale-document-ids.json` | `ExtractActivity` | Stale/removed document IDs, offloaded to blob (rather than Durable Table Storage) to dodge the 64KB row-size limit — deleted once `EmbedAndUploadActivity` consumes it |

These three are pure orchestration payload-passing (only the blob name string travels through Durable state) — nothing here is meant to be read after the run completes; all three are deleted by the end of a successful run.

## Evaluation harness (separate from the app pipelines)

| Path | Written by | Content |
|---|---|---|
| `eval-results/{yyyy-MM-dd}/{executionId}.jsonl` | `EvalResultWriter` (test project) | Append-only JSONL of `EvalRow` scoring results, one blob per test run (date + execution ID) to avoid concurrent-append collisions across parallel test methods. Container name comes from the `STORAGE_CONTAINER` env var used when running the evaluation tests |
