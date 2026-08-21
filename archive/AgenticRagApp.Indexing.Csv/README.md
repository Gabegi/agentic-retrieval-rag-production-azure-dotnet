# AgenticRagApp.Indexing.Csv

CSV indexing pipeline: extract → chunk → embed → upload to Azure AI Search.

- `Services/Extraction/` (`CsvExtractor.cs`, `CsvJoiner.cs`, `DataCleaner.cs`, `PipelineValidator.cs`) — reads and validates source CSVs, joins related page records
- `Services/CsvExtractionService.cs` — extraction pipeline entry point
- `Services/Chunking/ChunkingStrategy1.cs`, `Services/CsvChunkingService.cs` / `Utils/CsvChunkingUtils.cs` — splits joined records into search-sized chunks
- `Services/CsvEmbeddingService.cs` — generates embeddings for each chunk
- `Services/CsvUploadService.cs` — uploads embedded chunks to the Search index
- `CsvServiceCollectionExtensions.cs` — registers this project's services with the DI container
- `Models/` — page/join/chunk/validation data contracts

## Integration

Not yet wired to `AgenticRagApp.FunctionApp` — the pipeline is complete but no Function calls it (see [FunctionApp/README.md](../AgenticRagApp.FunctionApp/README.md)).

## See also

- root [ReadMe.md](../../ReadMe.md#blob-storage-layout--reports-artifacts--snapshots) — blob storage layout for reports written per run
