# AgenticRagApp.Indexing.CU

The indexing pipeline: **extract → resolve identity → chunk → embed → upload**, plus
restore-from-snapshot. Orchestrated by `AgenticRagApp.FunctionApp`'s `PdfIndexingFunction`
(one Durable activity per stage); this project holds the stages themselves.

"CU" is Azure AI **Content Understanding**, the only extraction backend. The Document
Intelligence path this project replaced is archived under `docs/archive/AgenticRagApp.Indexing.DI/`.

## Stage by stage

| Stage | Entry point | What happens |
|---|---|---|
| Diff | `Services/IndexDiffService.cs` | Lists the `documents` container (id + LastModified only), reads what the index holds, and decides new / updated / removed / skipped. `force=true` marks everything for processing. No paid call yet |
| Extract | `Services/ExtractionService.cs` | Per document: download, hash, `IContentAnalysisClient.AnalyzeAsync` (`prebuilt-documentSearch`), map the typed response with `Services/Extraction/Helpers/CUHelpers/CUHelper.cs`, detect language (`IDocumentLanguageDetector`). Bounded parallelism and a corpus wall-clock limit. No preflight: a bad PDF surfaces as a service error, not a local parse |
| Identity | `Services/Chunking/DocumentIdentity/DocumentIdentityResolver.cs` | Once per document, before cutting: builds the identity text (title + headings) and its hash, embeds only what changed, clusters by cosine similarity into a `family_id`, classifies a `domain_tag` (`IDomainClassifier`, cached per identity hash), detects confusable titles, persists to `IDocumentIdentityStore` |
| Gate + chunk | `Services/ChunkingService.cs` | Anchors headings to offsets (`Utils/HeadingLocator.cs`), gates on declared structure (≥ 2 headings at ≥ 0.1 per 1,000 chars, or ≥ 1 heading under 4,000 tokens) and dispatches to one of two strategies |
| — Route 1 | `Services/Chunking/ChunkingStrategies/DeclaredBoundaryStrategy.cs` | Honour the document's own sections; oversized sections fall into the block cascade |
| — Route 2 | `Services/Chunking/ChunkingStrategies/RecursiveStrategy.cs` | No trustworthy structure: the whole document is the window, cut by the block cascade (tables on rows, key-value runs on pairs, lists on items, prose last) |
| Metadata | `Services/Chunking/ChunkMetadata/ChunkMetadataBuilder.cs` | Turns cuts into index rows: document stamp (title, language, family, domain, validity dates), chunk ids, pages, heading path, embedded prefix, token count, `has_table`, figure captions |
| Report | `Services/Chunking/ChunkingReporting/ChunkingReporter.cs` | Log lines, OpenTelemetry counters and the `chunking-artifact` blob — written from a `finally`, so a stage that throws still leaves a report naming where it died |
| Embed | `Services/EmbeddingService.cs` + `Services/Embedding/VectorCache.cs` | Content-hash cache lookup first (`pipeline-artifacts/vector-cache/`), then batches of 100 through `IEmbeddingClient`; 8,191-token input limit enforced |
| Upload | `Services/UploadService.cs` | Maps to `SearchUploadChunk`, upserts, deletes orphaned chunks of stale documents, applies family moves, records index size/drift |
| Restore | `Services/RestoreService.cs` | Rebuilds the index from the rolling snapshot (`ISnapshotService`), re-embedding only chunks with no cached vector |

Budgets live in one place: `StrategyHelpers/ChunkingBudget.cs` — 512-token ceiling on the
embedded text (prefix included), 128-token body floor. Route names in `StrategyHelpers/RouteNames.cs`
(`DeclaredBoundary`, `Recursive`) are stamped on every chunk as `route_name`.

## Folder map

```
Services/
  ExtractionService.cs, IndexDiffService.cs, ChunkingService.cs,
  EmbeddingService.cs, UploadService.cs, RestoreService.cs
  Interfaces/                       one interface per service above (+ IDocumentChunkingStrategy, IVectorCache)
  Extraction/
    Helpers/CUHelpers/              CUHelper (facade) + Cu{Page,Outline,Table,Figure,Hyperlink,Annotation,Geometry}Helper
    Helpers/                        ExtractionOutputBuilder, ExtractionVersion, HeadingNumbering
    ExtractionReporting/            ExtractionReporter (pdf-file-facts, pdf-extraction-diff, pdf-failure, cu-raw-response), ExtractionStatsBuilder
  Chunking/
    ChunkingStrategies/             DeclaredBoundaryStrategy, RecursiveStrategy
      StrategyHelpers/              BlockParser/BlockCascade/BlockPacker, the cutters (Table, KeyValue, ListRun, Sentence, LineBreak, WordGap, Hard, Span), PrefixBuilder, TokenEstimator, ChunkingBudget, RouteNames, …
    DocumentIdentity/               DocumentIdentityResolver + Helpers/ (builder, embedder, clusterer, tagger, store writer, diagnostics)
    ChunkMetadata/                  ChunkMetadataBuilder + MetadataHelpers/ (ChunkIdBuilder, DocumentStamp, PageResolver, DocumentValidityParser, StructureFilter)
    ChunkingReporting/              ChunkingReporter, ChunkingRunState, ReportHelpers/
    Selection/                      TableChecker (reported signal, not a route input), TocFilter (drops table-of-contents chunks)
    Utils/                          HeadingLocator, HeadingChainBuilder, HeadingTextNormalizer, TokenCounter, ChunkingHelper
    Models/ContentPiece.cs
  Embedding/VectorCache.cs
Models/
  PdfExtractionDocument, PdfExtractionOutput, ChunkObject, SearchUploadChunk, ChunkingRunReport, DocumentFamily, ZenyaMetadata, …
  Extraction/Structure/             PdfDocumentStructure, Heading, SectionInfo, TableInfo, FigureInfo, PageSpan, … (the typed CU shape the app keeps)
  Extraction/Outcomes/              ExtractedFile, DocumentUsage, DocumentLanguageDetection, DocumentSummary, WordConfidence, …
ServiceCollectionExtensions.cs      AddIndexing(services, config) — everything above, plus ContentUnderstandingDefaultsSetup as a hosted service
```

## Design rules that explain the code

- **No heuristics over the markdown.** CU's typed response is the source of structure; the
  markdown is kept verbatim so every offset (headings, tables, page spans) addresses the exact
  string that gets cut. A missing typed collection degrades one output and is reported in
  `Warnings` — it never triggers a fallback parser. (`CUHelper.cs`, decisions 2026-08-26 / 09-09.)
- **A declared boundary is a fact; a computed one is a hypothesis.** Hence two routes, not a
  spectrum (D113, D116).
- **Strategies cut, `ChunkMetadataBuilder` stamps, `ChunkingService` routes** — none knows the
  others' business.
- **Reports are written from `finally` blocks**, so failed runs are the best-documented ones.
- **Ordinals never enter the chunk hash**, so inserting a section does not re-embed everything
  below it.

## Configuration this project needs

`CONTENT_UNDERSTANDING_ENDPOINT` (throws at registration if absent), `OPENAI_MINI_DEPLOYMENT`
and `OPENAI_EMBEDDING_MODEL_NAME` (verified into the account-wide CU default mapping at startup),
plus everything `IndexerConfig` requires — table in [RunningLocally.md](../../RunningLocally.md#configuration).

## Tests

`src/UnitTests/AgenticRagApp.Indexing.CU.Tests` (MSTest, 45 files). Strategy helpers, identity
helpers and `ExtractionOutputBuilder` are static or pure so they test from hand-built inputs.

## See also

- [Reports.md](../AgenticRagApp.Observability/Reports.md) — every blob this pipeline writes
- `docs/2608/260818/chunking-done.md` (D119) — the append-only ledger of the chunking rewrite
- `docs/2608/260817/chunking-two-strategies.md` (D113), `read-the-boundary-strategy.md` (D116) — why two routes
- `docs/2608/260819/content-understanding-when-and-how.md` (D125), `docs/2608/260825/cu-client-simplification.md` (D138) — why CU and why the prebuilt analyzer
- `docs/2609/260909/cu-helpers-review.md` (D179) — the current shape of the CU helpers
- `docs/2608/260824/cu-markdown-representation-notes.md` (D136), `docs/2609/260908/cu-payload-additions-action-plan.md` (D176) — what CU returns and what is still unused
- `docs/2608/260814/families.md` (D107) — how `family_id` / `domain_tag` / `confusable_with` are produced
- `docs/2608/260812/chunk-payload-cost.md` (D088) — what a chunk may and may not carry (post-OOM rules)
