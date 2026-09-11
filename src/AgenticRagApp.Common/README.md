# AgenticRagApp.Common

Shared contracts referenced by the other `src/` projects — no logic, no Azure dependencies, no
references to other projects.

```
Models/
  Chunking/     IChunk, TextChunk, IChunkStatsSource + ChunkStatsAdapter, ISnapshotSource — what a chunk exposes to
                reporting and snapshotting without the reporter knowing the concrete chunk type
  Extraction/   ExtractionDocumentBase / ExtractionDocument, ExtractionOutputBase / ExtractionOutput, ExtractionBatch,
                OpenFailureReasonBase — the doc-type-agnostic extraction shape Indexing.CU specialises
  Issues/       PipelineIssue, IssueSeverity, PipelineStage — the validation issue vocabulary reports serialise (as names, not ints)
  Querying/     DocumentReferenceBase
  Reporting/    ValidationEntries
```

Referenced by `AgenticRagApp.Infrastructure`, `AgenticRagApp.Indexing.CU`, `AgenticRagApp.Querying`
and `AgenticRagApp.Observability`. Tests: `src/UnitTests/AgenticRagApp.Common.Tests`.
