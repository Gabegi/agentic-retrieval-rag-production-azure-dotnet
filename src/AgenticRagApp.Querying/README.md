# AgenticRagApp.Querying

Query-time pipeline: takes a user question, retrieves relevant chunks from the knowledge base, and generates a cited answer.

- `Services/AgenticRagQueryService.cs` — top-level query orchestration
- `Services/ChunkNeighborExpander.cs` — pulls in neighboring chunks for better context
- `Services/KnowledgeBaseReferenceMapper.cs` / `Services/KnowledgeBaseActivitySummary.cs` — maps raw retrieval results into citations/summaries
- `Models/` — `RagQueryResult`, `RetrievedChunk`, `Citation` data contracts

Called by `AgenticRagApp.FunctionApp`'s `QueryingFunction` (`/api/query`). Each call writes a `QueryRunReport` — see the root [ReadMe.md](../../ReadMe.md#blob-storage-layout--reports-artifacts--snapshots) blob storage layout.
