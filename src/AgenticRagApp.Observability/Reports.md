# Blob Storage Layout — Reports, Artifacts & Snapshots

Everything the pipelines write to blob storage, by container. Every report — run reports, stage
diagnostics, per-run content archives, corpus snapshots, run analyses and eval results — shares
one container (`pipeline-reports`) and one naming scheme, built by `ReportPath.Build`:

```
{yyyy}/{MM}/{dd}/{yyyyMMddTHHmmssfff}Z-{report-name}[-{id}].json
```

`{yyyy}/{MM}/{dd}` is UTC, taken from the report's own timestamp — browse to today's folder to
find a given run without already knowing its instance ID. `{id}` is the orchestration instance ID
(or the pipeline build ID for eval results) and is omitted (no trailing `-{id}`) when the caller
has none (tests, ad-hoc invocations).

Deliberately *not* in this container: the content-addressed caches under `pipeline-artifacts`
(vector cache, document identity). They are looked up by key, not browsed by date, and folding
them into the naming scheme would turn O(1) lookups into O(n) listings.

## Container: `pipeline-reports`

| Report name | Path | Written by | Content |
|---|---|---|---|
| `index-run` | `…/{ts}-index-run-{instanceId}.json` | `IndexingOrchestrator` → `SaveIndexReportActivity` (`RunReportWriter`) | The run report (`PdfIndexRunReport`): `Run` identity, `Extraction` / `Chunking` / `Embedding` stage metrics (each `null` if the stage never ran), stage durations. Written on failed runs too. Schema: [docs/report-schema.md](../../docs/report-schema.md) |
| `restore-run` | `…/{ts}-restore-run-{instanceId}.json` | `RestoreOrchestrator` → `SaveRestoreReportActivity` | `PdfRestoreRunReport`: which snapshot generation was used, chunks restored / failed, chunks missing a cached vector |
| `run-analysis` | `…/{ts}-run-analysis-{instanceId}.json` | `SaveRunAnalysisActivity` (after either report above) | Assembled `RunSummary` (report + sibling stage reports + delta vs previous run + eval baseline + corpus size), the deterministic flags (`FlagEvaluator`), and the model assessment (`RunAnalysisAgent`). Best-effort: never fails the run |
| `pdf-file-facts` | `…/{ts}-pdf-file-facts-{instanceId}.json` | `ExtractionReporter` | One row per extracted document: title, SHA-256 of the raw bytes, page / heading / table / figure counts, content length, CU usage |
| `pdf-extraction-diff` | `…/{ts}-pdf-extraction-diff-{instanceId}.json` | `ExtractionReporter` | New / updated / removed / skipped document IDs decided by `IndexDiffService` for this run |
| `pdf-failure` | `…/{ts}-pdf-failure-{instanceId}.json` | `ExtractionReporter` | Only when extraction threw before producing output — the exception and what was known at that point |
| `cu-raw-response` | `…/{ts}-cu-raw-response-{instanceId}.json` | `ExtractionReporter` | The raw Content Understanding response body of the run's **first** analysis, kept so the typed shape the mappers rely on can be checked against what the service actually returned |
| `extraction-artifact` | `…/{ts}-extraction-artifact-{instanceId}.json` | `PdfIndexingFunction.ExtractActivity` (`PipelineArtifactWriter`) | Every extracted document in full (markdown, structure, page spans) + extraction stats. Whole-corpus content, no size cap |
| `chunking-artifact` | `…/{ts}-chunking-artifact-{instanceId}.json` | `ChunkingReporter` (from `ChunkingService`'s `finally`) | The whole chunking stage (`ChunkingRunReport`): one row per document with outcome and reason (chunked / no_strategy / zero_chunks / identity_skipped / not_reached), per-document resolved identity and route, identity-resolution diagnostics, heading-location rate, the full chunk list + stats. **Written even when the stage throws**, carrying `Success: false` and the stage it died in |
| `embedding-artifact` | `…/{ts}-embedding-artifact-{instanceId}.json` | `PdfIndexingFunction.EmbedAndUploadActivity` | Chunk metadata (id, document id, content hash, vector dims) + embedding stats — never the raw vectors |
| `snapshot-pdf` | `…/{ts}-snapshot-pdf-{instanceId}.json` | `SnapshotService.UpdateAsync` | Rolling full-corpus snapshot: every chunk believed live in the index, every index field except the vector, merged run over run. Only the 3 most recent generations are kept. Read back by `RestoreService` |
| `eval-results` / `eval-summary` / `eval-trx` | `…/{ts}-eval-{results\|summary\|trx}-{buildId}.{jsonl\|md\|trx}` | `.pipelines/templates/eval-publish-results.yml` (not app code) | Eval suite output: raw JSONL scoring rows (`EvalRow`), the markdown summary from `scripts/eval-summary.jq`, and the MSTest `.trx` |

Fixed-name pointer blobs at the container **root** (one current value, not history):

| Path | Written by | Content |
|---|---|---|
| `_latest-snapshot-pdf.json` | `SnapshotService.UpdateAsync` | Paths + instance IDs of the up-to-3 most recent snapshots, newest first — how `ReadLatestAsync` and pruning find them without listing |
| `_last-run.json` | `SaveRunAnalysisActivity` | Pointer to the previous run's report, so the run analysis can compute a delta without walking date folders |
| `_latest-eval-results.json` | `.pipelines/templates/eval-publish-results.yml` | `{Path, RanAt}` of the newest `eval-results` blob — the eval baseline the run analysis compares against |
| `indexing/_last-stats-pdf.json` | `RunReportWriter.SaveLastIndexStatsAsync` | Last known index document count / storage size — the single rolling drift baseline, **not** per-run history |

All report writes happen on every run in **every** environment — `IRunReportWriter.IsEnabled`
is unconditionally `true`.

Query reports are the one exception still on their own path, not yet folded into this scheme:
`queries/{yyyy}/{MM}/{dd}/{HH-mm-ss}.json` (`QueryingFunction`, one file per `/api/query` call,
containing question / answer / context / telemetry).

Reports written before this consolidation stay at their old paths (`runs/`,
`indexing/pdf-extraction/`, the old `pipeline-artifacts` / `eval-results` containers) — nothing
migrates them. The `eval-results` container in `infra/storage.tf` is kept for that history only.

## Container: `pipeline-artifacts`

Content-addressed state that survives across runs.

| Path | Written by | Content |
|---|---|---|
| `vector-cache/{contentHash}.json` | `VectorCache.SetAsync` (Indexing.CU) | One embedding vector per chunk content hash — dedup across runs regardless of when the content was first embedded. Orphans evicted after each snapshot update (`EvictOrphanedAsync`) |
| `document-identity/{base64(sourceId)}.json` | `DocumentIdentityStore.SetAsync` (Infrastructure) | One `DocumentIdentityRecord` per source document: identity hash, family id, domain tag, confusable-with. Read at the start of every chunking run so unchanged documents are neither re-embedded nor re-classified; orphans evicted with the corpus |

## Container: `indexing-pipeline` (DI key `pipeline-temp`, on the Functions storage account)

Transient handoff between Durable activities — only the blob name travels through Durable
state, dodging the 64 KB row-size limit.

| Path | Written by | Read by |
|---|---|---|
| `{yyyy}/{MM}/{dd}/{instanceId}/extracted.json` | `ExtractActivity` | `ChunkActivity` |
| `{yyyy}/{MM}/{dd}/{instanceId}/chunks.json` | `ChunkActivity` | `EmbedAndUploadActivity` |
| `{yyyy}/{MM}/{dd}/{instanceId}/stale-document-ids.json` | `ExtractActivity` | `EmbedAndUploadActivity` (orphaned-chunk cleanup) |
| `{yyyy}/{MM}/{dd}/{instanceId}/family-moves.json` | `ChunkActivity` | `EmbedAndUploadActivity` (documents whose `family_id` changed because other documents' clustering moved them) |

Nothing here is meant to be read after the run completes.

## Container: `documents` (source) and `zenya-documents` (Zenya sync target)

Not written by the indexing pipeline. `documents` holds the hand-uploaded corpus the indexer
reads. `zenya-documents` is written only by `AgenticRagApp.Tools.ZenyaSync` (`pdf/{document_id}.pdf`,
`docs/{document_id}.{ext}`, `zenya_*` blob metadata — contract in
`Infrastructure/Clients/Zenya/Sync/ZenyaBlobLayout.cs`); the indexer is not yet pointed at it.
