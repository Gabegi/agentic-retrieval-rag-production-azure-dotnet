# AgenticRagApp.Indexing.Pdf

PDF indexing pipeline: extract → chunk → embed → upload to Azure AI Search, with restore-from-snapshot support.

- `Services/ExtractionService.cs` — extracts text from PDFs via Document Intelligence
- `Services/ChunkingService.cs` — splits extracted documents into search-sized chunks
- `Services/EmbeddingService.cs` — generates embeddings for each chunk
- `Services/UploadService.cs` — uploads embedded chunks to the Search index
- `Services/RestoreService.cs` — rebuilds the index from the rolling snapshot (see root README's "Recovery steps")
- `Models/` — extraction/chunk/upload data contracts

Orchestrated by `AgenticRagApp.FunctionApp`'s `PdfIndexingFunction`. Reports written per run — see the root [ReadMe.md](../../ReadMe.md#blob-storage-layout--reports-artifacts--snapshots) blob storage layout.
