using System.Globalization;
using System.Net;
using System.Text;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Observability.Reports;

// Renders the run summary as the email body, in the BLUF/inverted-pyramid order specified in
// docs/2608/260807/pipeline-email-report-structure.md:
//
//   1 subject line   2 bottom line (one paragraph)   3 flags   4 metrics
//   5 evidence       5a attachment note              6 assessment   7 provenance
//
// Sections 1-5 and 7 are deterministic and always present; 6 is the only model-generated part
// and its absence degrades nothing else.
//
// Two rules run through every number printed here:
//   - Absolute numbers carry their base: "12 of 847", never "12".
//   - Deltas carry the previous value: "41% (was 63%)", never "-22pp".
public sealed class RunEmailRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string RenderSubject(RunEmailSummary s)
    {
        var when = (s.IndexReport?.Run.StartedAt ?? s.RestoreReport?.StartedAt ?? DateTimeOffset.UtcNow)
            .ToString("yyyy-MM-dd HH:mm", Inv);

        if (s.Kind == RunReportKind.Restore)
            return $"[{s.Verdict}] Index restore {when} — {s.RestoreReport?.ChunksRestored:N0} chunks restored";

        var r = s.IndexReport;
        if (r is { Run.Success: false })
        {
            var stage = r.Extraction is null ? "extraction"
                      : r.Chunking   is null ? "chunking"
                      : r.Embedding  is null ? "embed/upload"
                      : "finalisation";
            return $"[FAIL] Indexing {when} — {stage} failed after {Duration(r.Run.StartedAt, r.Run.FinishedAt)}";
        }

        var flagCount = s.Flags.Count(f => f.Severity >= FlagSeverity.Warning);
        return $"[{s.Verdict}] Indexing {when} — {r?.Extraction?.DocsToProcess ?? 0} docs, "
             + $"{r?.Chunking?.ChunksProduced ?? 0:N0} chunks, {flagCount} flag{(flagCount == 1 ? "" : "s")}";
    }

    public string RenderHtml(RunEmailSummary s, string? attachmentNote)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <html><head><meta charset="utf-8"><style>
            body{font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;font-size:14px;color:#1a1a1a;line-height:1.5;max-width:900px}
            h2{font-size:16px;margin:24px 0 8px;padding-bottom:4px;border-bottom:1px solid #ddd}
            h3{font-size:14px;margin:16px 0 4px}
            table{border-collapse:collapse;width:100%;margin:8px 0}
            th,td{text-align:left;padding:4px 8px;border-bottom:1px solid #eee;vertical-align:top}
            th{background:#f6f6f6;font-weight:600}
            .lead{font-size:15px;background:#f6f8fa;padding:12px;border-left:4px solid #666;margin:12px 0}
            .crit{color:#b3261e;font-weight:600}.warn{color:#a35200;font-weight:600}.watch{color:#41576b}
            .muted{color:#666}.mono{font-family:Consolas,Monaco,monospace;font-size:12px}
            pre{background:#f6f8fa;padding:8px;overflow-x:auto;white-space:pre-wrap;word-break:break-word;font-size:12px;margin:4px 0}
            .na{color:#999;font-style:italic}
            </style></head><body>
            """);

        RenderBottomLine(sb, s);
        RenderFlags(sb, s);

        if (s.Kind == RunReportKind.Restore) RenderRestoreMetrics(sb, s);
        else                                 RenderIndexMetrics(sb, s);

        RenderEvidence(sb, s);
        RenderEvalBaseline(sb, s);
        RenderAssessment(sb, s);
        RenderProvenance(sb, s, attachmentNote);

        sb.Append("</body></html>");
        return sb.ToString();
    }

    // ── 2. Bottom line ───────────────────────────────────────────────────────

    private static void RenderBottomLine(StringBuilder sb, RunEmailSummary s)
    {
        sb.Append("<div class=\"lead\">");

        if (s.Kind == RunReportKind.Restore)
        {
            var r = s.RestoreReport!;
            sb.Append($"Index restore <span class=\"mono\">{E(r.InstanceId)}</span> ")
              .Append(r.Success ? "completed" : "<span class=\"crit\">FAILED</span>")
              .Append($" in {Duration(r.StartedAt, r.FinishedAt)}, restoring {r.ChunksRestored:N0} chunks from snapshot ")
              .Append($"<span class=\"mono\">{E(r.SnapshotInstanceId ?? "(none)")}</span>. ");
            if (r.ChunksFailed > 0)
                sb.Append($"<span class=\"crit\"><b>{r.ChunksFailed:N0} chunks failed to upload</b></span> — the index is incomplete. ");
            if (r.ChunksMissingVector > 0)
                sb.Append($"<b>{r.ChunksMissingVector:N0} chunks were restored without a cached vector</b> and are not yet reachable by vector search. ");
            sb.Append("</div>");
            return;
        }

        var report = s.IndexReport!;
        var run = report.Run;

        sb.Append($"Indexing run <span class=\"mono\">{E(run.InstanceId)}</span> ");

        if (!run.Success)
        {
            // A failed run replaces the corpus/flag/delta slots with the failure itself, and
            // says which stages therefore have no data at all.
            sb.Append($"<span class=\"crit\">failed</span> after {Duration(run.StartedAt, run.FinishedAt)}. ");
            var notRun = new List<string>();
            if (report.Extraction is null) notRun.Add("extraction");
            if (report.Chunking   is null) notRun.Add("chunking");
            if (report.Embedding  is null) notRun.Add("embed/upload");
            if (notRun.Count > 0)
                sb.Append($"<b>{string.Join(", ", notRun)}</b> never ran, so those metrics are absent rather than zero. ");
            if (!string.IsNullOrWhiteSpace(run.ErrorMessage))
                sb.Append($"<br><span class=\"mono\">{E(Truncate(run.ErrorMessage, 400))}</span>");
            sb.Append("</div>");
            return;
        }

        sb.Append($"completed successfully in {Duration(run.StartedAt, run.FinishedAt)}");

        var x = report.Extraction;
        if (x is not null)
        {
            sb.Append($", processing {x.DocsToProcess:N0}");
            if (s.CorpusDocumentCount is > 0) sb.Append($" of {s.CorpusDocumentCount:N0}");
            sb.Append($" documents ({x.DocsNew:N0} new, {x.DocsUpdated:N0} updated, {x.DocsSkipped:N0} unchanged)");
        }
        if (report.Chunking is not null)
            sb.Append($" into {report.Chunking.ChunksProduced:N0} chunks");
        sb.Append(". ");

        var worst = s.Flags.FirstOrDefault(f => f.Severity >= FlagSeverity.Warning);
        sb.Append(worst is null
            ? "No flags raised. "
            : $"<b>{(worst.Severity == FlagSeverity.Critical ? "Critical" : "Warning")}:</b> {E(worst.Meaning)} ({E(worst.Metric)} = {E(worst.Observed)}). ");

        if (s.Previous is null)
            sb.Append("<span class=\"muted\">No previous run on record, so no comparison is shown.</span> ");
        else
            AppendLargestDelta(sb, s);

        if (s.Assessment is not null && !string.IsNullOrWhiteSpace(s.Assessment.Narrative))
            sb.Append($"<br><br>{E(s.Assessment.Narrative)}");

        sb.Append("</div>");
    }

    private static void AppendLargestDelta(StringBuilder sb, RunEmailSummary s)
    {
        var prev = s.Previous!;
        var c    = s.IndexReport?.Chunking;

        if (c is { ChunksProduced: > 0 } && prev.CoherentChunkRatio is > 0)
        {
            var now = c.CoherentChunks / (double)c.ChunksProduced;
            if (Math.Abs(now - prev.CoherentChunkRatio.Value) >= 0.05)
            {
                sb.Append($"Chunk coherence is {now:P0} (was {prev.CoherentChunkRatio:P0}). ");
                return;
            }
        }

        if (c is not null && prev.ChunksProduced is > 0 && c.ChunksProduced != prev.ChunksProduced)
            sb.Append($"Chunks produced: {c.ChunksProduced:N0} (was {prev.ChunksProduced:N0}). ");
    }

    // ── 3. Flags ─────────────────────────────────────────────────────────────

    private static void RenderFlags(StringBuilder sb, RunEmailSummary s)
    {
        if (s.Flags.Count == 0)
        {
            sb.Append("<h2>Flags</h2><p class=\"muted\">None. Every threshold-checked metric is within range.</p>");
            return;
        }

        sb.Append("<h2>Flags</h2><table><tr><th>Severity</th><th>Metric</th><th>Observed</th><th>Expected</th><th>What it means / what to do</th></tr>");
        foreach (var f in s.Flags)
        {
            var (cls, label) = f.Severity switch
            {
                FlagSeverity.Critical => ("crit",  "CRITICAL"),
                FlagSeverity.Warning  => ("warn",  "WARNING"),
                _                     => ("watch", "watch"),
            };
            sb.Append($"<tr><td class=\"{cls}\">{label}</td><td class=\"mono\">{E(f.Metric)}</td>")
              .Append($"<td>{E(f.Observed)}</td><td class=\"muted\">{E(f.Expected)}</td>")
              .Append($"<td>{E(f.Meaning)}<br><span class=\"muted\">{E(f.Action)}</span></td></tr>");
        }
        sb.Append("</table>");
    }

    // ── 4. Metrics ───────────────────────────────────────────────────────────

    private void RenderIndexMetrics(StringBuilder sb, RunEmailSummary s)
    {
        var r = s.IndexReport!;

        sb.Append("<h2>Run</h2><table>");
        Row(sb, "Outcome",       r.Run.Success ? "success" : "FAILED");
        Row(sb, "Instance",      r.Run.InstanceId);
        Row(sb, "Started",       r.Run.StartedAt.ToString("u", Inv));
        Row(sb, "Duration",      Duration(r.Run.StartedAt, r.Run.FinishedAt));
        Row(sb, "ForceReindex",  r.Run.ForceReindex.ToString());
        sb.Append("</table>");

        // Extraction
        sb.Append("<h2>Extraction</h2>");
        if (r.Extraction is null) sb.Append(DidNotRun);
        else
        {
            var x = r.Extraction;
            sb.Append("<table>");
            Row(sb, "Corpus documents", s.CorpusDocumentCount?.ToString("N0", Inv) ?? "(not counted)");
            Row(sb, "To process",  $"{x.DocsToProcess:N0}", s.Previous?.DocsToProcess);
            Row(sb, "New / Updated / Deleted", $"{x.DocsNew:N0} / {x.DocsUpdated:N0} / {x.DocsDeleted:N0}");
            Row(sb, "Skipped (unchanged)", $"{x.DocsSkipped:N0}");
            Row(sb, "Validation errors / warnings", $"{x.ValidationErrors:N0} / {x.ValidationWarnings:N0}");
            Row(sb, "Reconciliation problems", x.ReconciliationProblems.ToString());
            Row(sb, "Mojibake repaired pages", x.MojibakeRepairedPages.ToString());
            Row(sb, "Tables detected", x.DetectedTableCount.ToString());
            Row(sb, "Missing titles", x.MissingTitleCount.ToString());
            Row(sb, "Docs without headings", x.DocsWithoutHeadings.ToString());
            Row(sb, "Traceability gap", x.TraceabilityGapCount?.ToString() ?? "n/a");
            if (s.Validation is { } v)
            {
                Row(sb, "Cleaning — control / invisible chars", $"{v.ControlCharsStripped:N0} / {v.InvisibleCharsStripped:N0}");
                Row(sb, "Cleaning — ligatures / hyphenation joins", $"{v.LigaturesExpanded:N0} / {v.HyphenationJoinsRepaired:N0}");
                Row(sb, "Table conversion fallbacks", $"{v.TableConversionFallbacks} of {v.DetectedTableCount} tables");
            }
            if (s.FileFacts is { } ff)
            {
                Row(sb, "Extraction cost (estimated)", ff.EstimatedCostUsd.ToString("C4", CultureInfo.GetCultureInfo("en-US")));
                Row(sb, "Files without a Producer", $"{ff.FilesWithoutProducer} of {ff.FileCount}");
                Row(sb, "PDF spec versions", string.Join(", ", ff.SpecVersionHistogram.OrderBy(k => k.Key).Select(k => $"{k.Key}×{k.Value}")));
            }
            sb.Append("</table>");
        }

        // Chunking
        sb.Append("<h2>Chunking</h2>");
        if (r.Chunking is null) sb.Append(DidNotRun);
        else
        {
            var c = r.Chunking;
            sb.Append("<table>");
            Row(sb, "Chunks produced", $"{c.ChunksProduced:N0}", s.Previous?.ChunksProduced);
            Row(sb, "Strategy", c.Strategy);
            Row(sb, "Documents with zero chunks", c.DocsWithZeroChunks.ToString());
            Row(sb, "Duplicates", Pct(c.DuplicateChunks, c.ChunksProduced));
            Row(sb, "Coherent", Pct(c.CoherentChunks, c.ChunksProduced));
            Row(sb, "With a heading", Pct(c.HeadingsDetected, c.ChunksProduced));
            Row(sb, "Size — min / avg / p95 / max",
                $"{c.MinChunkSizeChars:N0} / {c.AvgChunkSizeChars:N0} / {c.P95ChunkSizeChars:N0} / {c.MaxChunkSizeChars:N0} chars");
            Row(sb, "Band &lt;100",      Pct(c.BandUnder100,  c.ChunksProduced));
            Row(sb, "Band 100–500",      Pct(c.Band100To500,  c.ChunksProduced));
            Row(sb, "Band 500–1500",     Pct(c.Band500To1500, c.ChunksProduced));
            Row(sb, "Band 1500+",        Pct(c.Band1500Plus,  c.ChunksProduced));
            sb.Append("</table>");
        }

        // Embed + upload
        sb.Append("<h2>Embed &amp; upload</h2>");
        if (r.Embedding is null) sb.Append(DidNotRun);
        else
        {
            var e = r.Embedding;
            sb.Append("<table>");
            Row(sb, "Docs uploaded / failed", $"{e.DocsUploaded:N0} / {e.DocsFailed:N0}", s.Previous?.DocsUploaded);
            Row(sb, "Chunks removed (stale cleanup)", $"{e.ChunksRemoved:N0}");
            Row(sb, "Chunks truncated", e.ChunksTruncated.ToString());
            Row(sb, "Vector cache hits", Pct(e.VectorCacheHits, r.Chunking?.ChunksProduced ?? 0));
            Row(sb, "Cached vectors evicted", e.ChunksEvicted.ToString());
            Row(sb, "Embedding retries", e.EmbeddingRetries.ToString());
            Row(sb, "Vector dimension errors", e.VectorDimErrors.ToString());
            Row(sb, "Embedding duration", $"{e.TotalEmbeddingDurationMs / 1000.0:N1} s");
            Row(sb, "Index documents",
                e.IndexDocumentCountSnapshot?.ToString("N0", Inv) ?? "(stats call failed)",
                (int?)e.PreviousIndexDocumentCount);
            Row(sb, "Index storage",
                e.IndexStorageSizeBytesSnapshot is { } b ? $"{b / 1024.0 / 1024.0:N1} MB" : "(stats call failed)");
            sb.Append("</table>");
        }

        RenderReconciliationLine(sb, s);
    }

    // The single most diagnostic line in the email: any unexplained narrowing between arrows
    // is a bug, and it is visible at a glance.
    private static void RenderReconciliationLine(StringBuilder sb, RunEmailSummary s)
    {
        var r = s.IndexReport!;
        if (r.Extraction is null) return;

        var parts = new List<string>();
        if (s.CorpusDocumentCount is { } corpus) parts.Add($"{corpus:N0} in corpus");
        parts.Add($"{r.Extraction.DocsToProcess:N0} to process");
        if (r.Chunking is { } c)  parts.Add($"{c.ChunksProduced:N0} chunks");
        if (r.Embedding is { } e) parts.Add($"{e.DocsUploaded:N0} uploaded" + (e.DocsFailed > 0 ? $" ({e.DocsFailed} failed)" : ""));

        sb.Append("<h3>Cross-stage reconciliation</h3><pre>")
          .Append(E(string.Join("  →  ", parts)))
          .Append("</pre><p class=\"muted\">Any unexplained narrowing between arrows is a bug.</p>");
    }

    private static void RenderRestoreMetrics(StringBuilder sb, RunEmailSummary s)
    {
        var r = s.RestoreReport!;
        sb.Append("<h2>Restore</h2><table>");
        Row(sb, "Outcome",  r.Success ? "success" : "FAILED");
        Row(sb, "Instance", r.InstanceId);
        Row(sb, "Duration", Duration(r.StartedAt, r.FinishedAt));
        Row(sb, "Snapshot generation", r.SnapshotInstanceId ?? "(none — nothing to restore from)");
        Row(sb, "Chunks restored", $"{r.ChunksRestored:N0}");
        Row(sb, "Chunks failed", r.ChunksFailed.ToString());
        Row(sb, "Chunks missing a vector", r.ChunksMissingVector.ToString());
        Row(sb, "Index", r.SearchIndexName);
        Row(sb, "Embedding model", $"{r.EmbeddingModel} ({r.EmbeddingDeployment})");
        Row(sb, "Index documents", r.IndexDocumentCountSnapshot?.ToString("N0", Inv) ?? "(stats call failed)");
        sb.Append("</table>");
        if (!string.IsNullOrWhiteSpace(r.ErrorMessage))
            sb.Append($"<pre>{E(Truncate(r.ErrorMessage, 800))}</pre>");
    }

    // ── 5. Evidence ──────────────────────────────────────────────────────────

    private static void RenderEvidence(StringBuilder sb, RunEmailSummary s)
    {
        var c = s.IndexReport?.Chunking;
        if (c is null) return;

        sb.Append("<h2>Evidence</h2>");

        if (s.Failure is { } f)
        {
            sb.Append("<h3>Extraction failure</h3><pre>")
              .Append(E($"{f.ExceptionType}: {f.Message}"));
            if (f.StackTraceExcerpt is not null) sb.Append("\n\n").Append(E(f.StackTraceExcerpt));
            sb.Append("</pre>");
        }

        if (c.SmallestChunk is not null || c.LargestChunk is not null)
        {
            sb.Append("<h3>Size extremes</h3>");
            if (c.SmallestChunk is not null) AppendChunk(sb, "Smallest", c.SmallestChunk);
            if (c.LargestChunk  is not null) AppendChunk(sb, "Largest",  c.LargestChunk);
        }

        if (c.SampleChunks.Count > 0)
        {
            sb.Append("<h3>Samples across size bands</h3>");
            foreach (var sample in c.SampleChunks) AppendChunk(sb, "Sample", sample);
        }

        if (c.ZeroChunkDocumentIds.Count > 0)
            sb.Append("<h3>Documents that produced no chunks</h3><pre>")
              .Append(E(string.Join('\n', c.ZeroChunkDocumentIds))).Append("</pre>");

        if (c.DuplicateSamples.Count > 0)
        {
            sb.Append("<h3>Repeated content</h3>");
            foreach (var d in c.DuplicateSamples.Take(3))
                sb.Append($"<p class=\"muted\">×{d.Occurrences}, hash <span class=\"mono\">{E(d.ContentHash[..12])}…</span></p><pre>")
                  .Append(E(d.ContentExcerpt)).Append("</pre>");
        }

        if (s.Diff is { } diff && (diff.RemovedSourceIds.Count > 0 || diff.ProcessedSourceIds.Count > 0))
        {
            sb.Append("<h3>Documents this run touched</h3>");
            if (diff.RemovedSourceIds.Count > 0)
                sb.Append("<p><b>Removed:</b></p><pre>").Append(E(string.Join('\n', diff.RemovedSourceIds))).Append("</pre>");
            if (diff.ProcessedSourceIds.Count > 0)
                sb.Append("<p><b>Processed:</b></p><pre>").Append(E(string.Join('\n', diff.ProcessedSourceIds))).Append("</pre>");
            if (diff.NamesTruncated)
                sb.Append("<p class=\"muted\">List truncated — see the attached summary for the full set.</p>");
        }
    }

    private static void AppendChunk(StringBuilder sb, string label, ChunkSample c)
    {
        // SizeChars is the real length; the excerpt may be clipped. Saying so is the point -
        // otherwise a clipped excerpt reads as a genuinely short chunk.
        sb.Append($"<p class=\"muted\">{label} — <span class=\"mono\">{E(c.DocumentId)}</span> p{c.PageNumber} #{c.ChunkIndex}, ")
          .Append($"{c.SizeChars:N0} chars{(c.Truncated ? ", excerpt clipped" : "")}")
          .Append(c.Heading is null ? "" : $", heading “{E(c.Heading)}”")
          .Append("</p><pre>").Append(E(c.ContentExcerpt)).Append("</pre>");
    }

    private static void RenderEvalBaseline(StringBuilder sb, RunEmailSummary s)
    {
        if (s.EvalBaseline is not { } e) return;

        sb.Append("<h2>Answer quality — pre-run baseline</h2>")
          .Append($"<p class=\"muted\">From eval <span class=\"mono\">{E(e.ExecutionId)}</span> at {e.RanAt:u}. ")
          .Append("This eval ran <b>before</b> this indexing run, so it measures the previous state of the index — not this run's output.</p>")
          .Append("<table>");
        Row(sb, "Scenarios", $"{e.ScenarioCount:N0} ({e.FailedCount} failed)");
        RowIfSet(sb, "Groundedness",  e.MeanGroundedness);
        RowIfSet(sb, "Relevance",     e.MeanRelevance);
        RowIfSet(sb, "Coherence",     e.MeanCoherence);
        RowIfSet(sb, "Equivalence",   e.MeanEquivalence);
        RowIfSet(sb, "Citation match", e.MeanCitationMatch);
        RowIfSet(sb, "Refusal score", e.MeanRefusalScore);
        RowIfSet(sb, "Context tokens", e.MeanContextTokens);
        Row(sb, "Eval cost", e.TotalCostUsd.ToString("C4", CultureInfo.GetCultureInfo("en-US")));
        sb.Append("</table>");
    }

    // ── 6. Assessment ────────────────────────────────────────────────────────

    private static void RenderAssessment(StringBuilder sb, RunEmailSummary s)
    {
        sb.Append("<h2>Assessment &amp; suggestions</h2>");

        if (s.Assessment is not { } a)
        {
            sb.Append("<p class=\"muted\">Assessment unavailable — the analysis call failed. "
                    + "Every metric above is unaffected.</p>");
            return;
        }

        if (a.Suggestions.Count == 0)
            sb.Append("<p>No changes suggested for this run.</p>");

        var i = 1;
        foreach (var sug in a.Suggestions)
        {
            sb.Append($"<h3>{i++}. {E(sug.Suggestion)}</h3><table>");
            Row(sb, "Evidence", sug.Evidence);
            if (!string.IsNullOrWhiteSpace(sug.ExpectedImpact)) Row(sb, "Expected impact", sug.ExpectedImpact);
            if (!string.IsNullOrWhiteSpace(sug.Effort))         Row(sb, "Effort", sug.Effort);
            sb.Append("</table>");
        }

        if (!string.IsNullOrWhiteSpace(a.WhatIsFine))
            sb.Append($"<p><b>Healthy:</b> {E(a.WhatIsFine)}</p>");
    }

    // ── 7. Provenance ────────────────────────────────────────────────────────

    private static void RenderProvenance(StringBuilder sb, RunEmailSummary s, string? attachmentNote)
    {
        sb.Append("<h2>Provenance</h2>");

        if (attachmentNote is not null)
            sb.Append($"<p>{E(attachmentNote)}</p>");

        sb.Append("<table>");
        Row(sb, "Run report", $"pipeline-reports/{s.BlobPath}");
        Row(sb, "Artifacts",  $"pipeline-artifacts/{(s.IndexReport?.Run.StartedAt ?? default):yyyy/MM/dd}/{s.InstanceId}/");
        Row(sb, "Sources found",   s.SourcesFound.Count == 0 ? "(none)" : string.Join("<br>", s.SourcesFound.Select(E)));
        Row(sb, "Sources missing", s.SourcesMissing.Count == 0 ? "(none)" : string.Join(", ", s.SourcesMissing.Select(E)));
        sb.Append("</table>");

        // The data account is private-endpoint-only, so an https:// blob link would not open
        // from a normal browser off-VNet. A copy-pasteable command is honest; a dead link is not.
        sb.Append("<p class=\"muted\">The data storage account is private-endpoint-only, so these paths are not "
                + "browser-clickable from outside the VNet. To fetch one:</p><pre>")
          .Append(E($"az storage blob download --account-name <data-account> --container-name pipeline-reports --name {s.BlobPath} --file run.json --auth-mode login"))
          .Append("</pre>");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private const string DidNotRun =
        "<p class=\"na\">Did not run — this stage was never reached, so it has no measurements. "
        + "This is not the same as measuring zero.</p>";

    private static void Row(StringBuilder sb, string label, string value, int? previous = null)
    {
        sb.Append($"<tr><th>{label}</th><td>{value}");
        if (previous is not null) sb.Append($" <span class=\"muted\">(was {previous:N0})</span>");
        sb.Append("</td></tr>");
    }

    private static void RowIfSet(StringBuilder sb, string label, double? value)
    {
        if (value is null) return; // not scored - never render as 0
        Row(sb, label, value.Value.ToString("N2", Inv));
    }

    private static string Pct(int part, int total) =>
        total == 0 ? "n/a" : $"{part:N0} ({part / (double)total:P0})";

    private static string Duration(DateTimeOffset from, DateTimeOffset to)
    {
        var d = to - from;
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s"
             : d.TotalMinutes >= 1 ? $"{d.Minutes}m {d.Seconds}s"
             : $"{d.TotalSeconds:N1}s";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
}
