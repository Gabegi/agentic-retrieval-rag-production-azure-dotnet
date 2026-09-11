# AgenticRagApp.Infrastructure

Every Azure SDK (and HTTP) client the app talks to, each behind a thin wrapper, plus the
strongly-typed configuration and the DI registration that hands them to the other projects.
Rule of the project: no caller outside it ever holds a raw SDK client.

```
Configuration/IndexerConfig.cs           the settings object; [Required] keys named after the app-setting
Clients/ServiceCollectionExtensions.cs   AddAgenticRagAppInfrastructure(services, configuration) → IndexerConfig
Clients/
  Blob/                  IBlobStore over BlobContainerClient (bytes, streams, JSON, ETag read/write, listing)
  Search/                index lifecycle (IIndexService: the PDF chunk schema, get-or-create), documents
                         (IIndexDocumentService), knowledge source + base (IKnowledgeService: retrieval and
                         answer instructions live here), IIndexRebuildService (teardown/rebuild order),
                         IndexSchemaComparer (code schema vs live schema), SearchServiceVersion (pinned api-version)
  KnowledgeRetrieval/    IKnowledgeRetrievalClient over KnowledgeBaseRetrievalClient (the query-time call)
  Embedding/             IEmbeddingClient over IEmbeddingGenerator (retry, token accounting)
  ContentUnderstanding/  IContentAnalysisClient (prebuilt-documentSearch analyze) + ContentUnderstandingDefaultsSetup
                         (hosted service: verifies/writes the account-wide model→deployment mapping at startup)
  ContentSafety/         IPromptShieldClient — Prompt Shields REST (no SDK wrapper exists)
  Language/              IDocumentLanguageDetector over TextAnalyticsClient (index `language` field)
  DomainClassification/  IDomainClassifier — LLM classifier for domain_tag over the registered IChatClient
  DocumentIdentity/      IDocumentIdentityStore — one blob per document under pipeline-artifacts/document-identity/
  Zenya/                 IZenyaClient (v5 REST, hand-rolled), token providers (client_assertion / client_secret),
                         retry handler, and Sync/ (ZenyaSyncService + blob store + layout) — see Clients/Zenya/README.md
```

Full client/method table: [Clients.md](Clients.md).

## Registration

`AddAgenticRagAppInfrastructure` checks two host keys (`APPLICATIONINSIGHTS_CONNECTION_STRING`,
`AzureWebJobsStorage:accountName`), assembles and validates `IndexerConfig`, then registers as
singletons: `DefaultAzureCredential`, `BlobServiceClient`, a keyed `BlobContainerClient`
(`"pipeline-temp"` → the `indexing-pipeline` container on the Functions storage account),
`AzureOpenAIClient` + `IEmbeddingGenerator` + `IChatClient` (with OpenTelemetry),
`SearchClient` / `SearchIndexClient` / `KnowledgeBaseRetrievalClient` (all pinned to
`SearchServiceVersion.Current`), `ContentUnderstandingClient` (only when
`CONTENT_UNDERSTANDING_ENDPOINT` is set), the Prompt Shields typed `HttpClient`,
`TextAnalyticsClient`, and every wrapper above. It returns the `IndexerConfig` so the host can
branch on it.

The Zenya client and sync are registered separately (`AddZenyaClient`, `AddZenyaSync`) because
only the sync hosts need `ZENYA_*` settings; the Function App must start without them.

## Consumers

`AgenticRagApp.Indexing.CU`, `AgenticRagApp.Querying`, `AgenticRagApp.Observability`,
`AgenticRagApp.FunctionApp` and `AgenticRagApp.Tools.ZenyaSync`. This project has no pipeline
logic of its own.

## Tests

`src/UnitTests/AgenticRagApp.Infrastructure.Tests` (MSTest, 22 files) — includes the 57 Zenya
client/sync tests.

## See also

- [Clients.md](Clients.md) — every wrapper, what it wraps, and its methods
- [Clients/Zenya/README.md](Clients/Zenya/README.md) — identities, registrations and values of the Zenya integration
- `docs/2608/260812/knowledgebasefix-action-plan.md` (D092) — why the Search api-version is pinned
- `docs/2608/260825/cu-client-simplification.md` (D138) — why the CU client is 35 lines and names no models
