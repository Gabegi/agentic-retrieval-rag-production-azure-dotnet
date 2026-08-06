# AgenticRagApp.Infrastructure

Thin clients around Azure services, plus the DI registration that wires them up for the rest of the app.

- `Clients/Blob/` — blob read/write (`IBlobStore`)
- `Clients/Search/` — Azure AI Search index + document access (`IndexService`, `IndexDocumentService`, `KnowledgeService`, current index/client providers)
- `Clients/DocumentIntelligence/` — Azure Document Intelligence client, used for PDF extraction
- `Clients/Embedding/` — embedding model client
- `Clients/KnowledgeRetrieval/` — Azure AI Knowledge Base query-time retrieval client
- `Configuration/` — strongly-typed config (`IndexerConfig`)
- `Clients/ServiceCollectionExtensions.cs` — registers all of the above with the DI container

## Integration

Consumed by the indexing, querying, and observability projects — this project has no app logic of its own.

## See also

- [Clients.md](Clients.md) — full table of every client and its methods
