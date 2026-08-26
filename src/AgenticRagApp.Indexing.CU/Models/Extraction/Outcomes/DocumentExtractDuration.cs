namespace AgenticRagApp.Indexing.CU.Models;

// One document's end-to-end extraction wall clock (download + analyze + map), paired with the
// blob it came from.
//
// Carried out of extraction for reporting only, exactly like DocumentContentHash: the run loop
// measures it, ExtractionOutputBuilder lifts it here, and ExtractionReporter writes it into the
// per-document facts report. It exists because run 5f5fac04 (2026-08-26) spent 11m47s of a
// 15m19s run inside extraction and left no per-document timing anywhere - the bottleneck had to
// be reconstructed from Durable's activity duration and page counts. Ok distinguishes a timed
// success from a timed failure; failures cost wall clock too, and a slow failure is its own
// finding.
public sealed record DocumentExtractDuration(string BlobName, long DurationMs, bool Ok);
