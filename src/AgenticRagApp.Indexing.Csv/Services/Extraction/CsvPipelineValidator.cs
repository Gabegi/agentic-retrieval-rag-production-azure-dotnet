using System.Globalization;
using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.Csv.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

public class PipelineValidator : IPipelineValidator
{
    private const double MaxAcceptableErrorRatePercent      = 1.0;
    private const double MaxAcceptableMagnitudeShiftPercent = 20.0;
    private const int    SpotCheckSampleSize                = 5;
    private const char   ReplacementChar                    = '�';

    private static readonly Regex MarkdownHeading =
        new(@"^#{1,6}\s", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex MarkdownTableLine =
        new(@"^\s*\|.*\|\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public ValidationReport Validate(
        ExtractionResult<PageRecord>  pagesExtraction,
        ExtractionResult<IndexRecord> indexExtraction,
        JoinResult                    joinResult,
        CleanResult                   cleanResult,
        int?                          previousRunCleanedCount = null)
    {
        var redFlags      = new List<string>();

        // 1. Collect all errors from all 3 previous steps =
            //     pagesExtraction.Errors                    (Error,   Stage=Parse:Pages)
            //   + indexExtraction.Errors                    (Error,   Stage=Parse:Index)
            //   + joinResult.Errors                         (Error,   Stage=Join)
            //   + joinResult.DataQualityWarnings            (Warning, Stage=Join)
            //   + cleanResult.Errors                        (Error,   Stage=Clean)
            //   + cleanResult.Warnings                      (Warning, Stage=Clean)
            //   + joinResult.SkippedIndexRecords            (Warning, Stage=Join — "no pages" docs)
        var issues = CollectIssues(pagesExtraction, indexExtraction, joinResult, cleanResult, redFlags);

        // 2. Check whether Extraction page numbers matches the joint numbers
        var reconciliation = CheckCleanVsJointCount(pagesExtraction, joinResult, cleanResult);

        // 3. Magnitude shift vs a previous run, if supplied.
        var magnitude = CheckMagnitudeShift(cleanResult, previousRunCleanedCount);

        // 4. Domain-specific red flag: documents flagged as overdue for review.
        var staleDocCount = CheckDateExceeded(cleanResult);
        if (staleDocCount > 0)
            redFlags.Add($"{staleDocCount} document(s) flagged check_date_exceeded — guidance may be outdated.");

        // 5. Collect Text (char and language) + table format issues
        issues.AddRange(TextNTableQualityCheck(cleanResult));

        // 5b. Total tables detected this run — trended over time via dashboard, not gated;
        // a drop here (with no ground truth for "expected" count) is a signal worth a look,
        // not proof a table got flattened into prose.
        var detectedTableCount = CountDetectedTables(cleanResult);

        // 6. docsNeedingFallback = zero headings across every single page of that document
        var docsWithNoPagesWithHeadings = DocsWithNoPagesWithHeading(cleanResult);
        if (docsWithNoPagesWithHeadings.Count > 0)
            redFlags.Add($"{docsWithNoPagesWithHeadings.Count} document(s) have no markdown headings — need fallback chunking.");

        // 7. takes a random sample for human review
        var sample = BuildRandomCheckSample(cleanResult);

        // 8. Final pass/ fail check
            // errorCount = count of every Error-severity item across the whole issues list (parse errors, join errors, clean errors, plus the U+FFFD text-quality errors from step 5).
            // Warnings don't count.
        var errorCount     = issues.Count(i => i.Severity == "Error");

        // totalAttempted — the denominator: total rows attempted across both raw CSVs (pagesExtraction.RowsAttempted + indexExtraction.RowsAttempted)
        // i.e. rows attempted before anything got dropped by join/clean.
        var totalAttempted = pagesExtraction.RowsAttempted + indexExtraction.RowsAttempted;

        // errorRate — errorCount as a percentage of totalAttempted
        // (or 100.0 if totalAttempted is 0, since a run with zero attempted rows can't be considered a clean pass).
        var errorRate      = totalAttempted == 0 ? 100.0 : 100.0 * errorCount / totalAttempted;


        // passedExcludingMagnitude — two conditions both have to hold:
        // - errorRate <= 1.0% (MaxAcceptableErrorRatePercent)
        // - reconciliation.Count == 0 (no pipeline-integrity mismatches from step 2 — Parse→Join, Join→Clean, empty run, duplicate keys)
        var passedExcludingMagnitude = errorRate <= MaxAcceptableErrorRatePercent && reconciliation.Count == 0;


        // passed — takes that result and adds a third condition: magnitude.Count == 0 (no >20% swing vs. the previous run, from step 3)
        var passed        = passedExcludingMagnitude && magnitude.Count == 0;


        return new ValidationReport
        {
            RunAtUtc                      = DateTime.UtcNow,
            PagesExtracted                = pagesExtraction.Records.Count,
            IndexRecordsExtracted         = indexExtraction.Records.Count,
            JoinedRecords                 = joinResult.Joined.Count,
            CleanedRecords                = cleanResult.Records.Count,
            Issues                        = issues,
            ReconciliationProblems        = reconciliation,
            MagnitudeWarnings             = magnitude,
            RedFlags                      = redFlags,
            SpotCheckSample               = sample,
            DocumentsNeedingFallbackChunking = docsWithNoPagesWithHeadings,
            SkippedIndexDocuments         = joinResult.SkippedIndexRecords
                .Select(r => $"{r.DocumentTypeName} ({r.DocumentId})")
                .ToList(),
            StaleDocCount                 = staleDocCount,
            MojibakeRepairedPages         = cleanResult.MojibakeRepairedPages,
            DetectedTableCount            = detectedTableCount,
            Passed                        = passed,
            PassedExcludingMagnitude      = passedExcludingMagnitude,
        };
    }



    // 1. Aggregate every error/warning bucket into one place.
            //     pagesExtraction.Errors                    (Error,   Stage=Parse:Pages)
            //   + indexExtraction.Errors                    (Error,   Stage=Parse:Index)
            //   + joinResult.Errors                         (Error,   Stage=Join)
            //   + joinResult.DataQualityWarnings            (Warning, Stage=Join)
            //   + cleanResult.Errors                        (Error,   Stage=Clean)
            //   + cleanResult.Warnings                      (Warning, Stage=Clean)
            //   + joinResult.SkippedIndexRecords            (Warning, Stage=Join — "no pages" docs)
    private static List<ValidationIssue> CollectIssues(
        ExtractionResult<PageRecord>  pagesExtraction,
        ExtractionResult<IndexRecord> indexExtraction,
        JoinResult                    joinResult,
        CleanResult                   cleanResult,
        List<string>                  redFlags)
    {
        var issues = new List<ValidationIssue>();

        //     pagesExtraction.Errors                    (Error,   Stage=Parse:Pages)
        issues.AddRange(pagesExtraction.Errors.Select(e => new ValidationIssue(
            Stage: "Parse:Pages", Severity: "Error", DocumentId: e.DocumentId ?? "", Message: $"Row {e.RowNumber}: {e.Message}")));

        //   + indexExtraction.Errors                    (Error,   Stage=Parse:Index)
        issues.AddRange(indexExtraction.Errors.Select(e => new ValidationIssue(
            Stage: "Parse:Index", Severity: "Error", DocumentId: e.DocumentId ?? "", Message: $"Row {e.RowNumber}: {e.Message}")));

        //   + joinResult.Errors                         (Error,   Stage=Join)
        issues.AddRange(joinResult.Errors.Select(e => new ValidationIssue(
            Stage: "Join", Severity: "Error", DocumentId: e.DocumentId, Message: e.Message)));

        //   + joinResult.DataQualityWarnings            (Warning, Stage=Join)
        issues.AddRange(joinResult.DataQualityWarnings.Select(w => new ValidationIssue(
            Stage: "Join", Severity: "Warning", DocumentId: w.DocumentId, Message: w.Message)));

        //   + cleanResult.Errors                        (Error,   Stage=Clean)
        issues.AddRange(cleanResult.Errors.Select(e => new ValidationIssue(
            Stage: "Clean", Severity: "Error", DocumentId: e.DocumentId ?? "", Message: e.Message)));

        //   + cleanResult.Warnings                      (Warning, Stage=Clean)
        issues.AddRange(cleanResult.Warnings.Select(w => new ValidationIssue(
            Stage: "Clean", Severity: "Warning", DocumentId: w.DocumentId ?? "", Message: w.Message)));

        // 1b. Index documents with no pages never reach the search index — make that visible

        //   + joinResult.SkippedIndexRecords            (Warning, Stage=Join — "no pages" docs)
        issues.AddRange(joinResult.SkippedIndexRecords.Select(r => new ValidationIssue(
            Stage:      "Join",
            Severity:   "Warning",
            DocumentId: r.DocumentId,
            Message:    "Index record has no pages — document will not be indexed.")));
        if (joinResult.SkippedIndexRecords.Count > 0)
            redFlags.Add($"{joinResult.SkippedIndexRecords.Count} index document(s) have no pages and will not be indexed.");

        return issues;
    }

    // 2. Every pages that come after Extraction must come into 3 buckets:
        // Joined = page matched with index
        // Errors (unmatchedPageCount) = page doesn't match with any index
        // InactivePagesSkipped = pages skipped because document is not active
    // pagesExtraction.Records.Count == joinResult.Joined.Count + unmatchedPageCount + joinResult.InactivePagesSkipped
    private static List<string> CheckCleanVsJointCount(
        ExtractionResult<PageRecord> pagesExtraction,
        JoinResult                   joinResult,
        CleanResult                  cleanResult)
    {
        var reconciliation = new List<string>();


        var unmatchedDocIds    = joinResult.Errors.Select(e => e.DocumentId).ToHashSet();

        var unmatchedPageCount = pagesExtraction.Records.Count(p => unmatchedDocIds.Contains(p.DocumentId));
        if (joinResult.Joined.Count + unmatchedPageCount + joinResult.InactivePagesSkipped
                != pagesExtraction.Records.Count)
            reconciliation.Add(
                $"Parse->Join mismatch: {pagesExtraction.Records.Count} pages extracted, but " +
                $"{joinResult.Joined.Count} joined + {unmatchedPageCount} unmatched + " +
                $"{joinResult.InactivePagesSkipped} inactive-skipped.");

        // Join -> Clean: every joined record is processed exactly once.
        if (cleanResult.Records.Count + cleanResult.Errors.Count + cleanResult.DuplicatePagesSkipped
                != joinResult.Joined.Count)
            reconciliation.Add(
                $"Join->Clean mismatch: {joinResult.Joined.Count} joined, but " +
                $"{cleanResult.Records.Count} cleaned + {cleanResult.Errors.Count} errored + " +
                $"{cleanResult.DuplicatePagesSkipped} duplicate-skipped.");

        // Zero cleaned records is never a legitimate outcome, even an export where
        // every document happens to be inactive. Unlike the magnitude-shift check below,
        // this fires with no previous-run baseline required — a first-ever run (or a run
        // right after a lost/corrupt state blob) shouldn't be able to sail through empty.
        // Folded into reconciliation (not magnitude) so overrideMagnitudeCheck can never
        // bypass it — see the magnitude-shift comment below on why an empty run is
        // dangerous downstream (the diff step deletes anything "missing").
        if (cleanResult.Records.Count == 0)
            reconciliation.Add("Zero cleaned records produced — refusing to pass an empty run.");

        // Referential integrity: no duplicate (DocumentId, PageIndex) in the final output.
        // Defense-in-depth, not reachable today — DataCleaner.Clean already dedupes on
        // this exact key before a record ever reaches cleanResult.Records, so this can
        // only fire if that upstream guarantee is ever broken. Kept as a cheap invariant
        // check rather than relied on as live logic.
        var duplicateKeys = cleanResult.Records
            .GroupBy(r => (r.DocumentId, r.PageIndex))
            .Where(g => g.Count() > 1)
            .Select(g => $"Duplicate output key: {g.Key.DocumentId} / page {g.Key.PageIndex} appears {g.Count()} times");
        reconciliation.AddRange(duplicateKeys);

        return reconciliation;
    }

    // 3. Magnitude shift vs a previous run, if supplied.
        // deltaPercent = 100 * (cleanResult.Records.Count - previous) / previous
        // example
            // previous = 1000, current cleaned count = 750:
            // deltaPercent = 100 * (750 - 1000) / 1000 = -25.0
            // threshold = MaxAcceptableMagnitudeShiftPercent

    private static List<string> CheckMagnitudeShift(CleanResult cleanResult, int? previousRunCleanedCount)
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

    // 4. Domain-specific red flag: documents flagged as overdue for review.
    private static int CheckDateExceeded(CleanResult cleanResult) =>
        cleanResult.Records
            .Where(r => r.AttentionFlags.Contains("check_date_exceeded"))
            .Select(r => r.DocumentId)
            .Distinct()
            .Count();

    // 5. Text-quality signals on Zenya's source text.
    private static List<ValidationIssue> TextNTableQualityCheck(CleanResult cleanResult)
    {
        var issues = new List<ValidationIssue>();

        foreach (var record in cleanResult.Records)
        {

            // Counts how many � (U+FFFD, the Unicode "replacement character") appear in that page's cleaned text
            // decoder emits for bytes it can't map to a valid character.
            // Its presence means actual source text was lost during encoding/decoding somewhere upstream
            var replacementCount = record.PageContent.Count(c => c == ReplacementChar);
            if (replacementCount > 0)
                issues.Add(new ValidationIssue(Stage: "TextQuality", Severity: "Error",
                    DocumentId: record.DocumentId,
                    Message:    $"Page {record.PageIndex}: {replacementCount} U+FFFD char(s) — source text is corrupted."));

            // Control characters (outside normal whitespace) and unassigned code points — corruption
            // U+FFFD doesn't catch, because the decoder didn't fail outright, it just produced a
            // character that has no business appearing in prose text.
            var corruptCharCount = record.PageContent.Count(c =>
                c is not ('\n' or '\r' or '\t')
                && CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned);
            if (corruptCharCount > 0)
                issues.Add(new ValidationIssue(Stage: "TextQuality", Severity: "Error",
                    DocumentId: record.DocumentId,
                    Message:    $"Page {record.PageIndex}: {corruptCharCount} control/unassigned character(s) — likely encoding corruption."));

            // Flags any page whose Language field isn't Dutch
            if (!string.IsNullOrEmpty(record.Language) &&
                !record.Language.StartsWith("nl", StringComparison.OrdinalIgnoreCase))
                issues.Add(new ValidationIssue(Stage: "TextQuality", Severity: "Warning",
                    DocumentId: record.DocumentId,
                    Message:    $"Page {record.PageIndex}: language '{record.Language}' — nl.microsoft analyzer will tokenize this poorly."));

            //  any table whose rows have inconsistent column count?
            // a well-formed markdown table has the same number of | delimiters on every row
            //  a broken table renders as garbage, and confuses the LLM
            var tableBlocks = record.PageContent
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Where(block => MarkdownTableLine.IsMatch(block));

            foreach (var block in tableBlocks)
            {
                var pipeCounts = MarkdownTableLine.Matches(block)
                    .Select(m => m.Value.Count(ch => ch == '|'))
                    .ToList();
                if (pipeCounts.Count > 1 && pipeCounts.Distinct().Count() > 1)
                {
                    issues.Add(new ValidationIssue(Stage: "TextQuality", Severity: "Warning",
                        DocumentId: record.DocumentId,
                        Message:    $"Page {record.PageIndex}: markdown table has inconsistent column counts across rows."));
                    break; // one warning per page is enough
                }
            }
        }

        return issues;
    }

    // Total table-like blocks across every cleaned page this run — same block definition
    // TextNTableQualityCheck already uses for the column-consistency check, so both checks
    // agree on what "a table" means.
    private static int CountDetectedTables(CleanResult cleanResult) =>
        cleanResult.Records.Sum(record =>
            record.PageContent
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Count(block => MarkdownTableLine.IsMatch(block)));

    // 6. document flagged if none of its pages has a heading
    // matters because chunking = chunks done per header
    private static List<string> DocsWithNoPagesWithHeading(CleanResult cleanResult)
    {
        // checks all pages that have a heading
        var docsWithHeadings = cleanResult.Records
            .Where(r => MarkdownHeading.IsMatch(r.PageContent))
            .Select(r => r.DocumentId)
            .ToHashSet();

        return cleanResult.Records
            .Select(r => r.DocumentId)
            .Distinct()
            .Where(id => !docsWithHeadings.Contains(id))
            .ToList();
    }

    // 7. takes a random sample for human review
    private static List<CleanedPageRecord> BuildRandomCheckSample(CleanResult cleanResult) =>
        cleanResult.Records.Count <= SpotCheckSampleSize
            ? [.. cleanResult.Records]
            : [.. cleanResult.Records.OrderBy(_ => Guid.NewGuid()).Take(SpotCheckSampleSize)];

}
