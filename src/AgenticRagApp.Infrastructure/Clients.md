# Infrastructure Clients

Every wrapper around a raw Azure SDK / HTTP client in `AgenticRagApp.Infrastructure`. All but the
Zenya rows are registered in [`Clients/ServiceCollectionExtensions.cs`](Clients/ServiceCollectionExtensions.cs)
(`AddAgenticRagAppInfrastructure`); the Zenya rows in `Clients/Zenya/ZenyaServiceCollectionExtensions.cs`
(`AddZenyaClient`) and `Clients/Zenya/Sync/ZenyaSyncServiceCollectionExtensions.cs` (`AddZenyaSync`).

| Folder | Interface | Implementation | Wraps | Methods |
|---|---|---|---|---|
| `Clients/Blob/` | `IBlobStore` | `BlobStore` | `BlobContainerClient` | `AssertContainerExistsAsync` · `DownloadBytesAsync` · `OpenReadAsync` · `ExistsAsync` · `UploadAsync` · `DeleteIfExistsAsync` · `ListBlobsAsync` · `DownloadJsonAsync<T>` · `UploadJsonAsync<T>` · `TryReadJsonWithETagAsync<T>` · `SaveJsonWithETagAsync<T>` |
| `Clients/Search/` | `IIndexService` | `IndexService` | `SearchIndexClient` | `EnsureIndexAsync` (get-or-create, never updates) · `RecreateIndexAsync` · `BuildDefinition` (the PDF chunk schema) |
| `Clients/Search/` | `IIndexDocumentService` | `IndexDocumentService` | `SearchClient` | `UpsertDocumentsAsync<T>` · `MergeDocumentFieldsAsync<T>` · `GetCurrentlyIndexedDocsIdsNDatesAsync` · `GetChunkIdsForDocumentsAsync` · `DeleteChunksByIdAsync` · `GetStatisticsAsync` |
| `Clients/Search/` | `IKnowledgeService` | `KnowledgeService` | `SearchIndexClient` | `EnsureKnowledgeSourceAsync` · `EnsureKnowledgeBaseAsync` (retrieval + answer instructions) · `DeleteKnowledgeSourceAsync` · `DeleteKnowledgeBaseAsync` |
| `Clients/Search/` | `IIndexRebuildService` | `IndexRebuildService` | `IIndexService` + `IKnowledgeService` | `RecreateEmptyAsync` — the one teardown/rebuild order both orchestrators use |
| `Clients/Search/` | `ICurrentIndexNameProvider` | `CurrentIndexNameProvider` | `IBlobStore` (pointer blob) | `GetCurrentIndexNameAsync` · `SetCurrentIndexNameAsync` |
| `Clients/Search/` | `ICurrentSearchClientProvider` | `CurrentSearchClientProvider` | `ICurrentIndexNameProvider` | `GetClientAsync` |
| `Clients/Search/` | — | `IndexSchemaComparer` (static) | two `SearchIndex` definitions | `Compare(expected, live)` → field-level drift list, read into the run analysis |
| `Clients/Search/` | — | `SearchServiceVersion` (static) | — | `Current` (`2025-11-01-preview`) · `Options()` — every Search client pins this |
| `Clients/KnowledgeRetrieval/` | `IKnowledgeRetrievalClient` | `KnowledgeBaseClient` | `KnowledgeBaseRetrievalClient` | `RetrieveAsync` |
| `Clients/Embedding/` | `IEmbeddingClient` | `EmbeddingClient` | `IEmbeddingGenerator<string, Embedding<float>>` | `EmbedWithRetryAsync` → vectors, retries, input tokens |
| `Clients/ContentUnderstanding/` | `IContentAnalysisClient` | `ContentAnalysisClient` | `ContentUnderstandingClient` | `AnalyzeAsync(bytes)` — `prebuilt-documentSearch`, inline data |
| `Clients/ContentUnderstanding/` | `IHostedService` | `ContentUnderstandingDefaultsSetup` | `ContentUnderstandingClient` | startup: read the account-wide default model→deployment mapping, write only if missing/wrong; result in `ContentUnderstandingDefaultsState` |
| `Clients/ContentSafety/` | `IPromptShieldClient` | `PromptShieldClient` | typed `HttpClient` (REST `text:shieldPrompt`, `2024-09-01`) | `DetectAttackAsync(userPrompt, documents)` |
| `Clients/Language/` | `IDocumentLanguageDetector` | `DocumentLanguageDetector` | `TextAnalyticsClient` | `DetectAsync(textSample)` — degrade-never-throw |
| `Clients/DomainClassification/` | `IDomainClassifier` | `DomainClassifier` | `IChatClient` | `ClassifyAsync(...)` — batches of 25, canonical tags GGZ/GHZ/VVT/VGZ/LVB/MVB, empty map on failure |
| `Clients/DocumentIdentity/` | `IDocumentIdentityStore` | `DocumentIdentityStore` | `BlobContainerClient` (`pipeline-artifacts/document-identity/`) | `GetAllAsync` · `SetAsync` · `EvictOrphanedAsync` |
| `Clients/Zenya/` | `IZenyaClient` | `ZenyaClient` | named `HttpClient` `"zenya"` (v5 REST, `x-api-version: 5`) | `GetCurrentUserAsync` · `EnsureAuthenticatedAsync` · `ListDocumentsAsync` · `GetDocumentAsync` · `DownloadAsync` · `GetContentsAsync` |
| `Clients/Zenya/` | `IZenyaTokenProvider` | `ZenyaClientAssertionTokenProvider` (live) / `ZenyaClientSecretTokenProvider` | `TokenCredential` / secret + `HttpClient` | `GetTokenAsync` — cached, refreshed 1 min before Zenya's 600 s expiry |
| `Clients/Zenya/Sync/` | `IZenyaDocumentStore` | `BlobZenyaDocumentStore` | `BlobContainerClient` (`zenya-documents`) | `EnsureReadyAsync` · `ListAsync` · `UploadAsync` · `DeleteAsync` |
| `Clients/Zenya/Sync/` | — | `ZenyaSyncService` | `IZenyaClient` + `IZenyaDocumentStore` | `RunAsync` → `ZenyaSyncResult` (listed/new/changed/unchanged/removed/failed…); dry run by default |

Also in `Clients/Zenya/`: `ZenyaOptions` (`ZENYA_BASE_URL`, `ZENYA_CLIENT_ID`, one of
`ZENYA_ENTRA_SCOPE` / `ZENYA_CLIENT_SECRET`), `ZenyaRetryHandler` (429 / Retry-After),
`ZenyaExceptions`, `Models/ZenyaModels.cs`, and `Sync/ZenyaBlobLayout.cs` (the `pdf/`, `docs/`
prefixes and `zenya_*` metadata contract) + `Sync/ZenyaSyncOptions.cs` (`STORAGE_ACCOUNT_URL`,
`STORAGE_CONTAINER`, `ZENYA_SYNC_DRY_RUN`).

See [README.md](README.md) for the project layout this fits into.
