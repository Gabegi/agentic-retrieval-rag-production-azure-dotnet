# AgenticRagApp.Indexing.Csv

CSV indexing pipeline: extract → chunk → embed → upload to Azure AI Search.

- `Services/ExtractionService.cs` — reads and validates source CSVs, joins related page records
- `Services/ChunkingService.cs` / `Utils/ChunkingUtils.cs` — splits joined records into search-sized chunks
- `Services/EmbeddingService.cs` — generates embeddings for each chunk
- `Services/UploadService.cs` — uploads embedded chunks to the Search index
- `Models/` — page/join/chunk/validation data contracts

Orchestrated by `AgenticRagApp.FunctionApp`'s `CsvIndexingFunction`. Reports written per run — see the root [ReadMe.md](../../ReadMe.md#blob-storage-layout--reports-artifacts--snapshots) blob storage layout.
