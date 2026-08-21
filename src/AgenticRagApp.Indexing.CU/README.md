# AgenticRagApp.Indexing.CU

PDF indexing pipeline: extract → chunk → embed → upload to Azure AI Search, with restore-from-snapshot support.

- `Services/Extraction/PdfExtractionPipeline.cs` (+ `PdfExtractor.cs`, `PdfCleaner.cs`, `PdfDocumentValidator.cs`, `PdfSectionBreadCrumbBuilder.cs`, `PdfExtraction/DocumentIntelligenceHelpers/*`) — extracts text and structure from PDFs via Document Intelligence
- `Services/ExtractionService.cs` — extraction pipeline entry point
- `Services/Chunking/PdfChunkingStrategy2.cs` (active), `PdfChunkingStrategy1.cs` (superseded), `Utils/ChunkingHelper.cs`, `Services/ChunkingService.cs` — splits extracted documents into search-sized chunks
- `Services/EmbeddingService.cs` — generates embeddings for each chunk
- `Services/Embedding/VectorCache.cs` — per-chunk embedding cache, keyed by content hash, in the `pipeline-artifacts` container
- `Services/UploadService.cs` — uploads embedded chunks to the Search index
- `Services/RestoreService.cs` — rebuilds the index from the rolling snapshot (see root README's "Recovery steps")
- `ServiceCollectionExtensions.cs` — registers this project's services with the DI container
- `Models/` — extraction/chunk/upload data contracts

## Integration

Orchestrated by `AgenticRagApp.FunctionApp`'s `PdfIndexingFunction`.

## See also

- [Services/Extraction/normalization.md](Services/Extraction/normalization.md) — text normalization pipeline for retrieval (PdfCleaner cleanup order and why)
- root [ReadMe.md](../../ReadMe.md#blob-storage-layout--reports-artifacts--snapshots) — blob storage layout for reports written per run
- [docs/2608/260803/embedding-memory-issues.md](../../docs/2608/260803/embedding-memory-issues.md) — known scale/memory risks at large corpus sizes (chunking, vector cache, artifact writers); written against `PdfChunkingStrategy1`, since superseded by `PdfChunkingStrategy2`, but the memory-shape findings still apply
