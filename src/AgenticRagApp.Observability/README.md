# AgenticRagApp.Observability

Cross-cutting reporting, snapshotting, run analysis and telemetry shared by indexing and
querying. Nothing here changes pipeline behaviour; it records it.

```
Instrumentation.cs                OpenTelemetry ActivitySource + Meter and every counter/histogram the app records
ReportsWriters/
  ReportPath.cs                   the ONE naming scheme: {yyyy}/{MM}/{dd}/{yyyyMMddTHHmmssfff}Z-{report-name}[-{id}].{ext}
  RunReportPath.cs                index-run / restore-run paths (RunReportKind, RunReportRef)
  StageReportPath.cs              per-stage diagnostics (file-facts, diff, failure, raw capture)
  RunReportWriter.cs              IRunReportWriter — writes any report JSON; keeps the last index-stats baseline
  PipelineArtifactWriter.cs       IPipelineArtifactWriter — the large per-run artifacts (extraction/chunking/embedding)
  IndexStatsMonitor.cs            IIndexStatsMonitor — index document count/size drift between runs
  Interfaces/
Snapshots/
  SnapshotService.cs              ISnapshotService — rolling full-corpus snapshot per source (pdf), 3 generations kept,
                                  found via a _latest-snapshot-{source}.json pointer; read back by RestoreService
  SnapshotChunk.cs
RunAnalysis/
  RunReportAssembler.cs           builds a RunSummary from the run report + sibling stage reports + previous-run pointer
                                  (_last-run.json) + eval baseline + corpus size; every sibling read is best-effort
  FlagEvaluator.cs                deterministic flags (drift, embedding retry rate, validation errors, extract duration,
                                  CU page spike, …); sourced thresholds fire, uncalibrated ones are suppressed in CalibrationMode
  RunAnalysisAgent.cs             the model assessment over the summary (IChatClient); degrade-never-throw
  RunAnalysisOptions.cs           RunAnalysis__Enabled, RunAnalysis__CalibrationMode
  RunSummary.cs, ReportFlag.cs
Models/
  PdfIndexRunReport, PdfRestoreRunReport, QueryRunReport, RunIdentity,
  ExtractionStageMetrics, ChunkingStageMetrics, EmbedUploadStageMetrics, ChunkSample
  CsvIndexRunReport               dormant — the CSV pipeline is archived; kept only so old reports deserialize
ReportEmail/IReportEmailSender.cs unused seam left from the removed email transport (no implementation, no consumer)
```

The activity that ties run analysis together (`SaveRunAnalysisActivity`) lives in
`AgenticRagApp.FunctionApp/RunAnalysis/` because it is a Durable activity; it writes the
`run-analysis` blob next to the run report it reads. History: the run analysis grew out of a run
report *email* whose transport was removed — `docs/2608/260826/run-analysis-rename.md` (D153).

## Where things are written

[Reports.md](Reports.md) — every blob, by container, with its writer. The `index-run` report's
field-by-field schema is in [docs/report-schema.md](../../docs/report-schema.md) (D170).

## Tests

`src/UnitTests/AgenticRagApp.Observability.Tests` (MSTest, 11 files).
