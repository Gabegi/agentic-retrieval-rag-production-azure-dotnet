using System.Globalization;
using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Quality gate between extraction and the index. Mirrors PipelineValidator.cs's checks
// and pass/fail algorithm, adapted to the PDF models.
//
// The checks fall into three tiers — the tier decides where a finding lands:
//   HARD GATES  (-> ReconciliationProblems; fail the run):
//     pipeline invariants and corpus-level sanity. These exist because the diff step
//     downstream DELETES whatever is missing from a passed run — a bad run that
//     passes doesn't just index garbage, it removes good documents from the index.
//   QUALITY ISSUES (-> Issues; gate only via the aggregate error-rate threshold):
//     per-page signals (encoding corruption, malformed tables).
//   ADVISORY (-> RedFlags / MagnitudeWarnings / report fields; never gate):
//     trends and chunking hints (table counts, heading coverage, spot-check sample).
//     MagnitudeWarnings (run-over-run record-count shift) lives here too — it's noisy
//     against a corpus that's mostly stable extraction-skip runs, so it's surfaced for
//     a human to read, not enforced.
//
// Differences from CSV's validator:
//   - No CheckDateExceeded red flag — no attention-flags data source for PDFs.
//   - PDF-only: table-flattening heuristic (see TableFlatteningCheck) and table
//     structure checks read from DI's own table data.
public class PdfPipelineValidator : IPdfPipelineValidator
{
    private const double MaxAcceptableErrorRatePercent      = 1.0;
    private const double MaxAcceptableMagnitudeShiftPercent = 20.0;
    private const int    SpotCheckSampleSize                = 5;
    private const char   ReplacementChar                    = '�';

    // A trigram must repeat at least this many times on one page before it's flagged as
    // possible table flattening. 2 was the spike's value and false-positives on
    // legitimately repetitive protocol prose ("de cliënt moet ..."); 3 trades a little
    // sensitivity for a lot less noise. Tune against real runs — if the flattening
    // warnings are still mostly wolf-crying at 3, delete the whole check rather than
    // keep raising this.
    private const int MinTrigramRepeats = 3;

    // Pages shorter than this can't meaningfully contain a flattened table; skip them.
    private const int MinWordsForFlatteningCheck = 30;

    private static readonly Regex MarkdownHeading =
        new(@"^#{1,6}\s", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex NonWordChars =
        new(@"[^\w\s]", RegexOptions.Compiled);

    public PdfValidationReport Validate(
        IReadOnlyList<PDFExtractionResult>        fileResults,
        PdfCleanResult                             cleanResult,
        int?                                       previousRunCleanedCount = null,
        IReadOnlyList<PdfExtractionDiagnostics>?   diagnostics = null)
    {
        // 1. Puts things into 3 buckets:
            // - Records = pages from files that extracted successfully.
            // - Errors = either a whole file that failed (file.Error, counted once) or individual bad pages within an otherwise-successful file (file.PageErrors).
            // - Warnings = non-fatal issues from successful files (file.Warnings).
        var pagesExtraction = SortResultsInto3Buckets(fileResults);

        // 2. dictionary of document structure per blob name (key)
        var (structures, similarNamingProblems) = GetDocumentStructure(fileResults);

        var redFlags = new List<string>();

        // 3. Collect all errors issues from two sources
            // - Extraction
            // - Cleaning
        var issues = GetIssuesFromExtractionNCleaning(pagesExtraction, cleanResult);

        // 3b. Native-metadata diagnostics (PdfNativeMetadataExtractor's missing-Title/
        // Author/Producer and unparseable-CreationDate/ModDate warnings, plus the
        // bookmark-count note) - file-level, so read straight off fileResults rather than
        // pagesExtraction. Previously captured into MetadataDiagnostics but never folded
        // into a report anywhere (see PDFExtractionResult.MetadataDiagnostics's own
        // comment) - added here as advisory Warnings, same severity tier as the Parse/
        // Clean warnings above, so a run missing native metadata on several files is
        // actually visible in validation-report.json instead of silently discarded.
        issues.AddRange(GetIssuesFromMetadataDiagnostics(fileResults));

        // 3c. PdfDocumentValidator's own soft warnings (old PDF spec version, near the
        // size/page-count limit, suspiciously-small file) - same "captured but never
        // folded into a report" gap MetadataDiagnostics had above, now fixed the same way.
        // Only Warnings here: ValidationDiagnostics.Errors mirrors file.Error, which
        // SortResultsInto3Buckets above already counted - folding it in too would
        // double-count the same hard failure.
        issues.AddRange(GetIssuesFromValidationDiagnostics(fileResults));

        // 4. Checks difference between Extraction count and Cleaning count
        var diffExtractionNCleaning = CheckDiffExtractNCleaning(pagesExtraction, cleanResult);
        diffExtractionNCleaning.AddRange(similarNamingProblems);

        // 5. Checks difference between Cleaning and Previous Run
        var diffCleaningNPreviousRun = CheckDiffCleanNPreviousRun(cleanResult, previousRunCleanedCount);

        // 6. Per-page text quality (U+FFFD, control/unassigned chars).
        issues.AddRange(TextQualityCheck(cleanResult));

        // 7. Tables collapsed into repeated-phrase prose during extraction.
        issues.AddRange(TableFlatteningCheck(cleanResult, structures));

        // 8. Table structure issues, from DI's own table data — not a text-pattern guess.
        issues.AddRange(TableStructureQualityCheck(structures));

        // 9. ADVISORY: total tables detected this run — trended over time, not gated.
        var detectedTableCount = CountDetectedTables(structures);

        // 10. ADVISORY: documents with zero headings across every page need fallback chunking.
        var docsWithNoPagesWithHeadings = DocsWithNoPagesWithHeading(cleanResult);
        if (docsWithNoPagesWithHeadings.Count > 0)
            redFlags.Add($"{docsWithNoPagesWithHeadings.Count} document(s) have no markdown headings — need fallback chunking.");

        // 11. ADVISORY, currently dormant: only fires if a backend populates
        // PdfFileExtraction.Diagnostics again (nothing does since PdfPig was removed).
        // Kept as the report slot for whichever backend picks decoration detection back up.
        if (diagnostics is { Count: > 0 })
        {
            var noDecorationCount = diagnostics.Count(d => !d.DecorationDetectionRan);
            if (noDecorationCount > 0)
                redFlags.Add(
                    $"{noDecorationCount} document(s) got no header/footer stripping — too few pages for decoration detection.");
        }

        // 12. ADVISORY: random sample for human review.
        var sample = BuildRandomCheckSample(cleanResult);

        // 13. Final pass/fail. Error rate is per ATTEMPTED page, so file-level failures
        // (which contribute errors but no pages) still count against the denominator.
        var errorCount     = issues.Count(i => i.Severity == "Error");
        var totalAttempted = pagesExtraction.RowsAttempted;
        var errorRate      = totalAttempted == 0 ? 100.0 : 100.0 * errorCount / totalAttempted;

        // Magnitude shift never gates - diffCleaningNPreviousRun still computed above and
        // surfaced via MagnitudeWarnings, just not folded into Passed.
        var passed = errorRate <= MaxAcceptableErrorRatePercent && diffExtractionNCleaning.Count == 0;

        return new PdfValidationReport
        {
            RunAtUtc                         = DateTime.UtcNow,
            PagesExtracted                   = pagesExtraction.Records.Count,
            CleanedRecords                   = cleanResult.Records.Count,
            Issues                           = issues,
            ReconciliationProblems           = diffExtractionNCleaning,
            MagnitudeWarnings                = diffCleaningNPreviousRun,
            RedFlags                         = redFlags,
            SpotCheckSample                  = sample,
            DocumentsNeedingFallbackChunking = docsWithNoPagesWithHeadings,
            MojibakeRepairedPages            = cleanResult.MojibakeRepairedPages,
            DetectedTableCount               = detectedTableCount,
            Passed                           = passed,
        };
    }

        // 1. Folds per-file PDFExtractionResult results into the batch-level shape the checks
    // operate on. A file-level extraction error is recorded once; a file that failed to
    // parse contributes nothing else. Validator-private on purpose — nothing but
    // validation bookkeeping needs this exact shape.
    private static ExtractionResult<PdfPageRecord> SortResultsInto3Buckets(IEnumerable<PDFExtractionResult> fileResults)
    {
        var records  = new List<PdfPageRecord>();
        var errors   = new List<ExtractionError>();
        var warnings = new List<ExtractionWarning>();

        foreach (var file in fileResults)
        {
            // if failed file has no pages, it's null
            // without this check the Add fails
            if (file.Error != null)
            {
                errors.Add(file.Error);
                continue;
            }

            records.AddRange(file.Pages!); // Ok=true guarantees Pages is populated
            errors.AddRange(file.PageErrors);
            warnings.AddRange(file.Warnings);
        }

        return new ExtractionResult<PdfPageRecord> { Records = records, Errors = errors, Warnings = warnings };
    }



    // 2. Azure Blob Storage allows both "Report.pdf" and "report.pdf" in the same container,
    // but this lookup is case-insensitive, so they'd collide. ToDictionary would throw and
    // crash the run on that collision; TryAdd below just logs it as a reconciliation
    // problem instead.
    private static (Dictionary<string, PdfDocumentStructure> Structures, List<string> CollisionProblems) GetDocumentStructure(
        IReadOnlyList<PDFExtractionResult> fileResults)
    {
        var structures        = new Dictionary<string, PdfDocumentStructure>(StringComparer.OrdinalIgnoreCase);
        var collisionProblems = new List<string>();

        foreach (var file in fileResults.Where(f => f.Structure != null))
        {
            if (!structures.TryAdd(file.BlobName, file.Structure!))
                collisionProblems.Add(
                    $"Blob name '{file.BlobName}' collides case-insensitively with another blob in this run — structure data for one was dropped.");
        }

        return (structures, collisionProblems);
    }

    // 3. Aggregate every error/warning bucket into one place. DocumentId (blob name)
    // identifies the file; RowNumber is a CSV concept and never set for PDFs.
    private static List<ValidationIssue> GetIssuesFromExtractionNCleaning(
        ExtractionResult<PdfPageRecord> pagesExtraction,
        PdfCleanResult                  cleanResult)
    {
        var issues = new List<ValidationIssue>();

        issues.AddRange(pagesExtraction.Errors.Select(e => new ValidationIssue(
            Stage: "Parse:Pages", Severity: "Error", DocumentId: e.DocumentId ?? "", Message: e.Message, Reason: e.Reason)));

        issues.AddRange(pagesExtraction.Warnings.Select(w => new ValidationIssue(
            Stage: "Parse:Pages", Severity: "Warning", DocumentId: w.DocumentId ?? "", Message: w.Message)));

        issues.AddRange(cleanResult.Errors.Select(e => new ValidationIssue(
            Stage: "Clean", Severity: "Error", DocumentId: e.DocumentId ?? "", Message: e.Message)));

        issues.AddRange(cleanResult.Warnings.Select(w => new ValidationIssue(
            Stage: "Clean", Severity: "Warning", DocumentId: w.DocumentId ?? "", Message: w.Message)));

        return issues;
    }

    // 3b. Folds each file's MetadataDiagnostics.Warnings (native Title/Author/Producer/
    // CreationDate/ModDate/bookmark-count facts - see PdfNativeMetadataExtractor) into the
    // same Issues list the Parse/Clean warnings land in. Always Severity="Warning" - never
    // gates the run (errorRate above only counts Severity="Error"), purely advisory.
    private static List<ValidationIssue> GetIssuesFromMetadataDiagnostics(
        IReadOnlyList<PDFExtractionResult> fileResults) =>
        fileResults
            .SelectMany(f => f.MetadataDiagnostics.Warnings.Select(w => new ValidationIssue(
                Stage: "Metadata", Severity: "Warning", DocumentId: w.DocumentId ?? f.BlobName, Message: w.Message)))
            .ToList();

    // See 3c. above - always Severity="Warning", never gates the run.
    private static List<ValidationIssue> GetIssuesFromValidationDiagnostics(
        IReadOnlyList<PDFExtractionResult> fileResults) =>
        fileResults
            .SelectMany(f => f.ValidationDiagnostics.Warnings.Select(w => new ValidationIssue(
                Stage: "Validation", Severity: "Warning", DocumentId: w.DocumentId ?? f.BlobName, Message: w.Message)))
            .ToList();

    // 4. Every extracted page must land in exactly one Clean bucket, an empty run never
    // passes (the diff step would delete the entire index), and the extractor must not
    // produce duplicate (BlobName, PageNumber) pairs. Duplicates land in reconciliation
    // (not Issues) so no error-rate threshold can let them slip through — this is the
    // sole enforcement of that invariant, checked against pagesExtraction so the
    // "extractor" attribution stays honest regardless of what Clean does.
    private static List<string> CheckDiffExtractNCleaning(
        ExtractionResult<PdfPageRecord> pagesExtraction,
        PdfCleanResult                  cleanResult)
    {
        var reconciliation = new List<string>();

        if (cleanResult.Records.Count + cleanResult.Errors.Count != pagesExtraction.Records.Count)
            reconciliation.Add(
                $"Extract->Clean mismatch: {pagesExtraction.Records.Count} pages extracted, but " +
                $"{cleanResult.Records.Count} cleaned + {cleanResult.Errors.Count} errored.");

        if (cleanResult.Records.Count == 0)
            reconciliation.Add("Zero cleaned records produced — refusing to pass an empty run.");

        // Grouped case-insensitively on BlobName - same convention as BuildStructureLookup's
        // dictionary, so "Report.pdf" and "report.pdf" pages of the same PageNumber collide
        // here too instead of only being caught by the structure-lookup collision check.
        reconciliation.AddRange(pagesExtraction.Records
            .GroupBy(r => (BlobName: r.BlobName.ToUpperInvariant(), r.PageNumber))
            .Where(g => g.Count() > 1)
            .Select(g => $"Duplicate page from extractor: {g.First().BlobName} / page {g.Key.PageNumber} appears {g.Count()} times"));

        return reconciliation;
    }

    // 5. Magnitude shift vs a previous run, if supplied. Advisory only (see the tiering
    // comment at the top of this file) - previousRunCleanedCount is itself just "however
    // many records the last run actually processed", which shrinks a lot once extraction-
    // skip means most runs only touch a handful of changed documents. Gating on that
    // would fail nearly every normal run, so this is reported via MagnitudeWarnings for a
    // human to read, never enforced.
    private static List<string> CheckDiffCleanNPreviousRun(PdfCleanResult cleanResult, int? previousRunCleanedCount)
    {
        var magnitude = new List<string>();

        if (previousRunCleanedCount is int previous && previous > 0)
        {
            var deltaPercent = 100.0 * (cleanResult.Records.Count - previous) / previous;
            if (Math.Abs(deltaPercent) > MaxAcceptableMagnitudeShiftPercent)
                magnitude.Add(
                    $"Cleaned count shifted {deltaPercent:+0.0;-0.0}% vs previous run " +
                    $"({previous} -> {cleanResult.Records.Count}) — exceeds {MaxAcceptableMagnitudeShiftPercent}% threshold.");
        }

        return magnitude;
    }

    // 6. Per-page text-quality signals. Single pass per page: both counters in one
    // walk of the content instead of two full Count() scans.
    private static List<ValidationIssue> TextQualityCheck(PdfCleanResult cleanResult)
    {
        var issues = new List<ValidationIssue>();

        foreach (var record in cleanResult.Records)
        {
            int replacementCount = 0, corruptCharCount = 0;

            foreach (var c in record.PageContent)
            {
                if (c == ReplacementChar) { replacementCount++; continue; }
                if (c is '\n' or '\r' or '\t') continue;
                if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned)
                    corruptCharCount++;
            }

            // One issue per page even when both problems are present - each Error here
            // counts against the error-rate denominator (attempted pages), so a page
            // reported twice would silently double its own weight in that rate.
            if (replacementCount > 0 || corruptCharCount > 0)
            {
                var parts = new List<string>();
                if (replacementCount > 0) parts.Add($"{replacementCount} U+FFFD char(s)");
                if (corruptCharCount > 0) parts.Add($"{corruptCharCount} control/unassigned character(s)");

                issues.Add(new ValidationIssue(Stage: "TextQuality", Severity: "Error",
                    DocumentId: record.BlobName,
                    Message:    $"Page {record.PageNumber}: {string.Join(", ", parts)} — source text is corrupted / likely encoding corruption."));
            }
        }

        return issues;
    }

    // 7. PDF-only heuristic: a trigram repeating MinTrigramRepeats+ times on one page
    // suggests table rows run together with no delimiters left. Skips pages where DI
    // already detected a table — this check's purpose is tables DI MISSED; running it on
    // detected-table pages just false-positives on legitimate repeated cell content.
    // Skips short pages entirely. Warning-only: it gates nothing, so its only cost is
    // report noise — watch it in real runs and delete it if it stays noisy (see
    // MinTrigramRepeats).
    //
    // Reviewed against a real 3-doc/30-page run (2026-07-27): 10 warnings fired, 8 were
    // ordinary Dutch phrase repetition ("op basis van", "in de wijk", "en e mail" etc.),
    // not flattened tables. Only 2 pointed at genuinely repeating templated content (a
    // checkbox list, a glossary), and even those read fine as prose. ~80% noise — this
    // meets the "mostly wolf-crying" bar above. There's no DI ground truth for "a table
    // it chose not to make," so a fix here would just swap this text-pattern guess for a
    // geometry-based one (line-polygon column alignment), still a heuristic. Next time
    // this comes up, delete the check rather than try to make the guess smarter.
    private static List<ValidationIssue> TableFlatteningCheck(
        PdfCleanResult cleanResult, IReadOnlyDictionary<string, PdfDocumentStructure>? structures)
    {
        var issues = new List<ValidationIssue>();

        foreach (var record in cleanResult.Records)
        {
            var hasDetectedTable = structures != null
                && structures.TryGetValue(record.BlobName, out var structure)
                && structure.Tables.Any(t => t.PageNumber == record.PageNumber);
            if (hasDetectedTable) continue;

            var repeated = FindRepeatedTrigrams(record.PageContent);
            if (repeated.Count == 0) continue;

            issues.Add(new ValidationIssue(Stage: "TableFlattening", Severity: "Warning",
                DocumentId: record.BlobName,
                Message:    $"Page {record.PageNumber}: possible flattened table — repeated phrase(s) {string.Join(", ", repeated.Take(3))}."));
        }

        return issues;
    }

    private static List<string> FindRepeatedTrigrams(string text)
    {
        var words = NonWordChars.Replace(text.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < MinWordsForFlatteningCheck) return [];

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i <= words.Length - 3; i++)
        {
            var trigram = $"{words[i]} {words[i + 1]} {words[i + 2]}";
            seen[trigram] = seen.TryGetValue(trigram, out var count) ? count + 1 : 1;
        }

        return seen
            .Where(kv => kv.Value >= MinTrigramRepeats)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"\"{kv.Key}\" ({kv.Value}x)")
            .ToList();
    }

    // 8. Table structure issues read directly off DI's own table data. Replaces an
    // earlier heuristic that pattern-matched GFM pipe tables — DI renders tables as
    // HTML <table> elements, so that heuristic never matched and was silently a no-op.
    private static List<ValidationIssue> TableStructureQualityCheck(
        IReadOnlyDictionary<string, PdfDocumentStructure>? structures)
    {
        var issues = new List<ValidationIssue>();
        if (structures is null) return issues;

        foreach (var (blobName, structure) in structures)
        {
            foreach (var table in structure.Tables)
            {
                if (table.RowCount <= 0 || table.ColumnCount <= 0)
                    issues.Add(new ValidationIssue(Stage: "TableStructure", Severity: "Warning",
                        DocumentId: blobName,
                        Message:    $"Table at offset {table.Offset}: reported {table.RowCount} row(s) x {table.ColumnCount} column(s) — malformed."));
                else if (table.Cells.Count == 0)
                    issues.Add(new ValidationIssue(Stage: "TableStructure", Severity: "Warning",
                        DocumentId: blobName,
                        Message:    $"Table at offset {table.Offset}: {table.RowCount}x{table.ColumnCount} reported but no cell data was extracted."));
            }
        }

        return issues;
    }

    // 9. Total tables detected this run — real count from DI's table detection.
    private static int CountDetectedTables(IReadOnlyDictionary<string, PdfDocumentStructure>? structures) =>
        structures?.Values.Sum(s => s.Tables.Count) ?? 0;

    // 10. Document flagged if none of its pages has a markdown heading.
    private static List<string> DocsWithNoPagesWithHeading(PdfCleanResult cleanResult)
    {
        var docsWithHeadings = cleanResult.Records
            .Where(r => MarkdownHeading.IsMatch(r.PageContent))
            .Select(r => r.BlobName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return cleanResult.Records
            .Select(r => r.BlobName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !docsWithHeadings.Contains(id))
            .ToList();
    }

    // 12. Random sample for human review.
    private static List<CleanedPdfPageRecord> BuildRandomCheckSample(PdfCleanResult cleanResult) =>
        cleanResult.Records.Count <= SpotCheckSampleSize
            ? [.. cleanResult.Records]
            : [.. cleanResult.Records.OrderBy(_ => Guid.NewGuid()).Take(SpotCheckSampleSize)];
}