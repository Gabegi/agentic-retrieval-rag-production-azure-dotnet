namespace AgenticRagApp.Indexing.CU.Models;

// Content Understanding's own whole-document summary - the one FIELD the prebuilt analyzer
// returns (fields.Summary), as opposed to the content elements every other helper maps.
//
// WE ALREADY PAY FOR IT. prebuilt-documentSearch generates it whether or not anything reads it,
// inside the contextualization tokens and the completion-model tokens the run is billed for
// (871k / 2.24M input on run C). Until 2026-09-08 nothing in src touched .Fields at all, so it
// was generated, billed and thrown away on every document of every run.
//
// REPORT AND METADATA ONLY - NOT INDEXED. A whole-document summary as a chunk would compete
// with real chunks for recall, which is the 2026-08-19 decision this does not reopen. So it
// travels the same report-only path as ContentHash / DurationMs / Usage / WordConfidence:
// mapper -> ExtractedFile -> ExtractionOutputBuilder -> the per-document facts report.
//
// Grounding is carried as a COUNT, not as the spans. The field arrives with spans and its own
// Source polygons (run C: confidence 0.774, 7 grounding spans), and those are now parseable -
// see CuGeometryHelper - but no consumer exists, and the decision (user, 2026-09-08) is to
// carry the count until one does. The count is what tells a reader whether the summary was
// grounded in the document at all.
public sealed record DocumentSummary(string Text, double? Confidence, int GroundingSpanCount)
{
    // The per-document facts report is blob-backed and has no row limit, but a 51-document run
    // of unbounded summaries is still a report nobody reads. Capped at a stated number (user
    // decision, 2026-09-08) rather than letting the longest summary in the corpus decide the
    // report's size. The metrics row that DOES have a limit - 64KB, Durable Table Storage -
    // carries only a count and a mean confidence, never this text.
    public const int ReportTextCap = 1000;

    public string TextForReport =>
        Text.Length <= ReportTextCap ? Text : Text[..ReportTextCap] + "…";

    // Reported next to the text so a truncation is visible in the report itself rather than
    // being mistaken for a summary that simply ended there.
    public bool TruncatedInReport => Text.Length > ReportTextCap;
}

// The same value paired with the blob it came from - the fifth of the per-document lifts
// (DocumentContentHash, DocumentExtractDuration, DocumentUsage, DocumentWordConfidence, this).
// Ok rides along for the same reason as on the other four: a failed analysis can still have
// produced a summary before it failed.
public sealed record DocumentSummaryEntry(string BlobName, DocumentSummary Summary, bool Ok);
