# AgenticRagApp.Observability

Cross-cutting reporting, snapshotting, and telemetry used by both indexing pipelines and querying.

- `Instrumentation.cs` — shared telemetry setup
- `ReportsWriters/RunReportWriter.cs` — writes per-run reports (indexing, restore, query) to blob storage
- `ReportsWriters/PipelineArtifactWriter.cs` — writes intermediate pipeline artifacts (extraction/chunking/embedding output)
- `ReportsWriters/IndexStatsMonitor.cs` — tracks index document count/size drift between runs
- `Snapshots/SnapshotService.cs` — maintains the rolling full-corpus snapshot used to rebuild the index if it's ever wiped/corrupted
- `Models/` — report and snapshot data contracts (`PdfIndexRunReport`, `CsvIndexRunReport`, `QueryRunReport`, `PdfRestoreRunReport`, etc.)

See [Reports.md](Reports.md) for exactly what gets written where.
