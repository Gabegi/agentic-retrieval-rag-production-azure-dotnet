# Infrastructure Clients

Every generic wrapper around a raw Azure SDK client in `AgenticRagApp.Infrastructure`, registered once in [`Clients/ServiceCollectionExtensions.cs`](Clients/ServiceCollectionExtensions.cs) and consumed by the indexing, querying, and observability projects.

| Folder | Interface | Implementation | Wraps | Methods |
|---|---|---|---|---|
| `Clients/Blob/` | `IBlobStore` | `BlobStore` | `BlobContainerClient` | `EnsureContainerExistsAsync`<br>`DownloadBytesAsync`<br>`OpenReadAsync`<br>`ExistsAsync`<br>`UploadAsync`<br>`DeleteIfExistsAsync`<br>`ListBlobsAsync`<br>`DownloadJsonAsync<T>`<br>`UploadJsonAsync<T>`<br>`TryReadJsonWithETagAsync<T>`<br>`SaveJsonWithETagAsync<T>` |
| `Clients/DocumentIntelligence/` | `IDocumentAnalysisClient` | `DocumentAnalysisClient` | `DocumentIntelligenceClient` | `SubmitAnalyzeAsync` |
| `Clients/Embedding/` | `IEmbeddingClient` | `EmbeddingClient` | `IEmbeddingGenerator` | `EmbedWithRetryAsync` |
| `Clients/KnowledgeRetrieval/` | `IKnowledgeRetrievalClient` | `KnowledgeBaseClient` | `KnowledgeBaseRetrievalClient` | `RetrieveAsync` |
| `Clients/Search/` | `IIndexDocumentService` | `IndexDocumentService` | `SearchClient` + `SearchIndexClient` | `UpsertDocumentsAsync<T>`<br>`GetCurrentIndexedDocumentDatesAsync`<br>`GetChunkIdsForDocumentsAsync`<br>`DeleteChunksByIdAsync`<br>`GetStatisticsAsync` |
| `Clients/Search/` | `IIndexService` | `IndexService` | `SearchIndexClient` | `EnsureIndexAsync`<br>`RecreateIndexAsync` |
| `Clients/Search/` | `IKnowledgeService` | `KnowledgeService` | `SearchIndexClient` | `EnsureKnowledgeSourceAsync`<br>`EnsureKnowledgeBaseAsync`<br>`DeleteKnowledgeSourceAsync`<br>`DeleteKnowledgeBaseAsync` |
| `Clients/Search/` | `ICurrentIndexNameProvider` | `CurrentIndexNameProvider` | `IBlobStore` (pointer blob) | `GetCurrentIndexNameAsync`<br>`SetCurrentIndexNameAsync` |
| `Clients/Search/` | `ICurrentSearchClientProvider` | `CurrentSearchClientProvider` | `ICurrentIndexNameProvider` | `GetClientAsync` |

Not a client itself — DI registration for every row above:

| File | Method |
|---|---|
| `Clients/ServiceCollectionExtensions.cs` | `AddAgenticRagAppInfrastructure(services, configuration)` |

See [README.md](README.md) for the project layout this fits into.
