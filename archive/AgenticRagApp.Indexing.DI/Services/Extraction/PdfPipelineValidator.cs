using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// Quality gate between extraction and the index. Mirrors PipelineValidator.cs's checks
// and pass/fail algorithm, adapted to the PDF models.
//
// The checks fall into three tiers, and the tier decides where a finding lands:
//   HARD GATES  (-> ReconciliationProblems; fail the run):
//     pipeline invariants and corpus level sanity. These exist because the diff step
//     downstream DELETES whatever is missing from a passed run: a bad run that
//     passes doesn't just index garbage, it removes good documents from the index.
//   QUALITY ISSUES (-> Issues; gate only via the aggregate error rate threshold):
//     per page signals (encoding corruption, malformed tables).
//   ADVISORY (-> RedFlags / report fields; never gate):
//     trends and chunking hints (table counts, heading coverage, spot check sample).
//
// Differences from CSV's validator:
//   - No CheckDateExceeded red flag; no attention flags data source for PDFs.
//   - PDF only: table structure checks read from DI's own table data.
//   - Magnitude-shift check is advisory-only here and never gates Passed, unlike CSV's
//     (which can fail the run and be bypassed via overrideMagnitudeCheck): with
//     extraction-skip in place, most runs only touch a handful of changed documents, so
//     a legitimate small-changeset run would otherwise look like a huge swing against
//     the whole-corpus baseline. Still worth surfacing as a warning for a human to look at.
//
// The table flattening heuristic (repeated trigram detection) was deleted: a real
// 3 doc / 30 page run produced 10 warnings of which 8 were ordinary Dutch phrase
// repetition, so roughly 80% noise. It gated nothing, and there is no DI ground truth
// for "a table it chose not to make", so any replacement would be another heuristic.
// Do not reintroduce it without such ground truth.
public class PdfPipelineValidator : IPdfPipelineValidator
{
    private const double MaxAcceptableErrorRatePercent      = 1.0;
    private const double MaxAcceptableMagnitudeShiftPercent = 20.0;
    private const int    SpotCheckSampleSize                = 5;
    private const int    ReplacementCodePoint               = 0xFFFD;

    // Corruption promotion thresholds. A single stray U+FFFD in a 3000 character page is
    // not a corrupt page, but it used to fail one page's worth of the 1% error budget.
    // A page is an Error only once corruption is either absolutely (>= 3 chars) or
    // relatively (> 0.1% of the page) significant; anything below that is a Warning.
    private const int    MinCorruptCharsForError    = 3;
    private const double MaxAcceptableCorruptRatio  = 0.001;

    private static readonly Regex MarkdownHeading =
        new(@"^#{1,6}\s", RegexOptions.Multiline | RegexOptions.Compiled);

    // Shared key comparer for (BlobName, PageNumber) lookups. Azure Blob Storage allows
    // "Report.pdf" and "report.pdf" in the same container, and every lookup in this file
    // treats them as the same document. Using a comparer rather than ToUpperInvariant()
    // per record avoids a string allocation per page in every keyed pass.
    private static readonly BlobPageComparer PageKeyComparer = new();

    public PdfQualityGateResult Validate(
        IReadOnlyList<PdfExtractionResult> fileResults,
        PdfCleanResult                     cleanResult,
        int?                               spotCheckSeed           = null,
        int?                               previousRunCleanedCount = null)
    {
        // 1. Puts things into 3 buckets:
            // - Records = pages from files that extracted successfully.
            // - Errors = either a whole file that failed (file.Error, counted once) or individual bad pages within an otherwise-successful file (file.PageErrors).
            // - Warnings = non-fatal issues from successful files (file.Warnings).
        var pagesExtraction = SortResultsInto3Buckets(fileResults);

        // 2. dictionary of document structure per blob name (key)
        var (structures, collisionProblems) = BuildStructureLookupByBlobName(fileResults);

        var redFlags = new List<string>();

        // 3. Collect all errors issues from two sources
            // - Extraction
            // - Cleaning
        var issues = GetIssuesFromExtractionNCleaning(pagesExtraction, cleanResult);

        // 3b. Native metadata diagnostics (PdfNativeMetadataExtractor's missing Title/
        // Author/Producer and unparseable CreationDate/ModDate warnings, plus the
        // bookmark count note). File level, so read straight off fileResults rather than
        // pagesExtraction. Previously captured into MetadataDiagnostics but never folded
        // into a report anywhere (see PdfExtractionResult.MetadataDiagnostics's own
        // comment), added here as advisory Warnings, same severity tier as the Parse/
        // Clean warnings above, so a run missing native metadata on several files is
        // actually visible in validation-report.json instead of silently discarded.
        issues.AddRange(GetIssuesFromMetadataDiagnostics(fileResults));

        // 3b-2. ADVISORY: Author/Creator/Subject/Keywords absence (finding #15) has no
        // downstream consequence, unlike Title/Producer above, and is absent on nearly
        // every Word-exported PDF in this corpus - as individual per-field Issues
        // (previously Warnings) it dominated the Issues list and could crowd out real
        // TextQuality errors from the truncated report/log (finding #9). Reported as one
        // aggregate RedFlags line instead: still visible, doesn't compete for that budget.
        // PdfNativeMetadataExtractor reports these via diag.Info (not diag.Warn) for
        // exactly this reason - GetIssuesFromMetadataDiagnostics above only reads
        // .Warnings, so they'd otherwise vanish from every report entirely.
        var docsWithMissingOptionalMetadata = fileResults
            .Where(f => f.MetadataDiagnostics.Info.Any(i => i.Message.StartsWith("No native ")))
            .Select(f => f.BlobName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (docsWithMissingOptionalMetadata > 0)
            redFlags.Add(
                $"{docsWithMissingOptionalMetadata} document(s) missing one or more optional " +
                "Info-dictionary fields (Author/Creator/Subject/Keywords).");

        // 3c. PdfDocumentValidator's own soft warnings (old PDF spec version, near the
        // size/page count limit, suspiciously small file), the same "captured but never
        // folded into a report" gap MetadataDiagnostics had above, now fixed the same way.
        // Only Warnings here: ValidationDiagnostics.Errors mirrors file.Error, which
        // SortResultsInto3Buckets above already counted, so folding it in too would
        // double count the same hard failure.
        issues.AddRange(GetIssuesFromValidationDiagnostics(fileResults));

        // 4. Checks difference between Extraction count and Cleaning count
        var diffExtractionNCleaning = CheckDiffExtractNCleaning(pagesExtraction, cleanResult);
        diffExtractionNCleaning.AddRange(collisionProblems);

        // 5. Magnitude shift vs a previous run, if supplied — advisory only (see class
        // comment above), never contributes to EvaluateGate below.
        var magnitude = CheckMagnitudeShift(cleanResult, previousRunCleanedCount);

        // 6. Per page text quality (U+FFFD, control/unassigned scalars, unpaired surrogates).
        issues.AddRange(TextQualityCheck(cleanResult));

        // 7. Table structure issues, from DI's own table data, not a text pattern guess.
        issues.AddRange(TableStructureQualityCheck(structures));

        // 7b. Errors first, stable otherwise (OrderBy is a stable sort, so within each
        // severity the stage order above is preserved). Both consumers of this list -
        // PdfExtractionPipeline's MaxReturnedIssues/MaxLoggedIssues - just take a flat
        // prefix; without sorting here, an unbounded pile of low-value metadata warnings
        // (e.g. "no native Author") assembled ahead of TextQuality's real corruption
        // Errors could exhaust that budget before a single Error is ever returned or
        // logged, even though ValidationErrors in the run stats correctly says they exist.
        issues = issues.OrderBy(i => i.IsError ? 0 : 1).ToList();

        // 8. ADVISORY: total tables detected this run, trended over time, not gated.
        var detectedTableCount = CountDetectedTables(structures);

        // 9. ADVISORY: documents with zero headings across every page need fallback chunking.
        var docsWithNoPagesWithHeadings = DocsWithNoPagesWithHeading(cleanResult);
        if (docsWithNoPagesWithHeadings.Count > 0)
            redFlags.Add($"{docsWithNoPagesWithHeadings.Count} document(s) have no markdown headings, need fallback chunking.");

        // 10. ADVISORY: deterministic sample for human review.
        var sample = BuildRandomCheckSample(cleanResult, spotCheckSeed);

        // 10b. ADVISORY, dev only: same sampled pages, paired with their pre clean raw
        // content. See CleaningSpotCheckEntry's comment for why this exists alongside 10.
        var cleaningSample = BuildCleaningSpotCheckSample(sample, pagesExtraction.Records);

        // 11. Final pass/fail.
        var passed = EvaluateGate(issues, pagesExtraction.RowsAttempted, diffExtractionNCleaning);

        return new PdfQualityGateResult
        {
            RunAtUtc                         = DateTime.UtcNow,
            PagesExtracted                   = pagesExtraction.Records.Count,
            CleanedRecords                   = cleanResult.Records.Count,
            Issues                           = issues,
            ReconciliationProblems           = diffExtractionNCleaning,
            MagnitudeWarnings                = magnitude,
            RedFlags                         = redFlags,
            SpotCheckSample                  = sample,
            DocumentsNeedingFallbackChunking = docsWithNoPagesWithHeadings,
            MojibakeRepairedPages            = cleanResult.MojibakeRepairedPages,
            DetectedTableCount               = detectedTableCount,
            ControlCharsStripped             = cleanResult.ControlCharsStripped,
            InvisibleCharsStripped           = cleanResult.InvisibleCharsStripped,
            LigaturesExpanded                = cleanResult.LigaturesExpanded,
            HyphenationJoinsRepaired         = cleanResult.HyphenationJoinsRepaired,
            LineWrapsReflowed                = cleanResult.LineWrapsReflowed,
            CleaningSpotCheckSample          = cleaningSample,
            TableConversionFallbacks         = cleanResult.TableConversionFallbacks,
            Passed                           = passed,
        };
    }

    // 1. Folds per file PdfExtractionResult results into the batch level shape the checks
    // operate on. A file level extraction error is recorded once; a file that failed to
    // parse contributes nothing else. Validator private on purpose: nothing but
    // validation bookkeeping needs this exact shape.
    private static ExtractionBatch<PdfPageRecord> SortResultsInto3Buckets(IEnumerable<PdfExtractionResult> fileResults)
    {
        var records  = new List<PdfPageRecord>();
        var errors   = new List<PipelineIssue>();
        var warnings = new List<PipelineIssue>();

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

        return new ExtractionBatch<PdfPageRecord> { Records = records, Errors = errors, Warnings = warnings };
    }

    // 2. Azure Blob Storage allows both "Report.pdf" and "report.pdf" in the same container,
    // but this lookup is case insensitive, so they'd collide. ToDictionary would throw and
    // crash the run on that collision; TryAdd below just logs it as a reconciliation
    // problem instead.
    private static (Dictionary<string, PdfDocumentStructure> Structures, List<string> CollisionProblems) BuildStructureLookupByBlobName(
        IReadOnlyList<PdfExtractionResult> fileResults)
    {
        var structures        = new Dictionary<string, PdfDocumentStructure>(StringComparer.OrdinalIgnoreCase);
        var collisionProblems = new List<string>();

        foreach (var file in fileResults.Where(f => f.Structure != null))
        {
            if (!structures.TryAdd(file.BlobName, file.Structure!))
                collisionProblems.Add(
                    $"Blob name '{file.BlobName}' collides case insensitively with another blob in this run, structure data for one was dropped.");
        }

        return (structures, collisionProblems);
    }

    // 3. Aggregate every error/warning bucket into one place. DocumentId (blob name)
    // identifies the file; RowNumber is a CSV concept and never set for PDFs.
    // Each bucket already holds PipelineIssue values carrying their own Stage/Severity,
    // set where the problem was found. This used to re-stamp every item with a stage
    // guessed from which bucket it arrived in ("Parse:Pages" for anything from extraction,
    // "Clean" for anything from cleaning), which flattened real distinctions - a mojibake
    // repair and a blank page both became "Clean". Concatenating preserves what the
    // producing step actually said.
    private static List<PipelineIssue> GetIssuesFromExtractionNCleaning(
        ExtractionBatch<PdfPageRecord> pagesExtraction,
        PdfCleanResult                  cleanResult) =>
        [.. pagesExtraction.Errors, .. pagesExtraction.Warnings, .. cleanResult.Issues];

    // 3b. Folds each file's MetadataDiagnostics.Warnings (native Title/Author/Producer/
    // CreationDate/ModDate/bookmark count facts, see PdfNativeMetadataExtractor) into the
    // same Issues list the Parse/Clean warnings land in. Always Severity="Warning", never
    // gates the run (EvaluateGate only counts Severity="Error"), purely advisory.
    // The `with` only fills in a DocumentId the diagnostic didn't carry - the blob name is
    // known here but not always at the point the warning was raised. Stage and Severity
    // come from the producing step and are left alone.
    private static List<PipelineIssue> GetIssuesFromMetadataDiagnostics(
        IReadOnlyList<PdfExtractionResult> fileResults) =>
        fileResults
            .SelectMany(f => f.MetadataDiagnostics.Warnings.Select(w => w.DocumentId is null ? w with { DocumentId = f.BlobName } : w))
            .ToList();

    // See 3c. above, always Severity=Warning, never gates the run.
    private static List<PipelineIssue> GetIssuesFromValidationDiagnostics(
        IReadOnlyList<PdfExtractionResult> fileResults) =>
        fileResults
            .SelectMany(f => f.ValidationDiagnostics.Warnings.Select(w => w.DocumentId is null ? w with { DocumentId = f.BlobName } : w))
            .ToList();

    // 4. Every extracted page must land in exactly one Clean bucket, an empty run never
    // passes (the diff step would delete the entire index), and the extractor must not
    // produce duplicate (BlobName, PageNumber) pairs. Duplicates land in reconciliation
    // (not Issues) so no error rate threshold can let them slip through: this is the
    // sole enforcement of that invariant, checked against pagesExtraction so the
    // "extractor" attribution stays honest regardless of what Clean does.
    private static List<string> CheckDiffExtractNCleaning(
        ExtractionBatch<PdfPageRecord> pagesExtraction,
        PdfCleanResult                  cleanResult)
    {
        var reconciliation = new List<string>();

        if (cleanResult.Records.Count + cleanResult.Errors.Count != pagesExtraction.Records.Count)
            reconciliation.Add(
                $"Extract->Clean mismatch: {pagesExtraction.Records.Count} pages extracted, but " +
                $"{cleanResult.Records.Count} cleaned + {cleanResult.Errors.Count} errored.");

        // Only a problem if pages were actually attempted: a steady-state run where the
        // pre-extraction diff correctly found nothing new/updated submits zero pages by
        // design, and that must pass, not be mistaken for "we tried to extract something
        // and silently got nothing back" (the case this check exists to catch, since a
        // pass here is what lets the downstream diff step delete the whole index).
        if (pagesExtraction.RowsAttempted > 0 && cleanResult.Records.Count == 0)
            reconciliation.Add(
                $"Zero cleaned records produced from {pagesExtraction.RowsAttempted} attempted page(s), refusing to pass an empty run.");

        // Grouped case insensitively on BlobName via PageKeyComparer, the same convention
        // as BuildStructureLookupByBlobName's dictionary, so "Report.pdf" and "report.pdf" pages of
        // the same PageNumber collide here too instead of only being caught by the
        // structure lookup collision check.
        reconciliation.AddRange(pagesExtraction.Records
            .GroupBy(r => (r.BlobName, r.PageNumber), PageKeyComparer)
            .Where(g => g.Count() > 1)
            .Select(g => $"Duplicate page from extractor: {g.First().BlobName} / page {g.Key.PageNumber} appears {g.Count()} times"));

        return reconciliation;
    }

    // 5. Magnitude shift vs a previous run, if supplied. Advisory only — see class comment
    // above for why this never gates Passed for PDF, unlike CSV's equivalent check.
    private static List<string> CheckMagnitudeShift(PdfCleanResult cleanResult, int? previousRunCleanedCount)
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

    // 6. Per page text quality signals. Decoded per Unicode scalar rather than per char:
    // CharUnicodeInfo.GetUnicodeCategory(char) reports Surrogate for both halves of a
    // surrogate pair, so a char loop can never see the real category of an astral code
    // point and silently misses unassigned scalars above U+FFFF.
    //
    // Rune.DecodeFromUtf16 is used instead of EnumerateRunes because EnumerateRunes
    // substitutes U+FFFD for ill formed sequences, which would make a genuine replacement
    // character in the source indistinguishable from an unpaired surrogate. The
    // OperationStatus tells them apart, and an unpaired surrogate is itself a corruption
    // signal worth counting rather than ignoring.
    //
    // Single pass per page: all three counters in one walk of the content.
    private static List<PipelineIssue> TextQualityCheck(PdfCleanResult cleanResult)
    {
        var issues = new List<PipelineIssue>();

        foreach (var record in cleanResult.Records)
        {
            int replacementCount = 0, corruptCharCount = 0, unpairedSurrogateCount = 0;

            var remaining = record.PageContent.AsSpan();
            while (!remaining.IsEmpty)
            {
                var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
                remaining = remaining[consumed..];

                if (status != OperationStatus.Done) { unpairedSurrogateCount++; continue; }
                if (rune.Value == ReplacementCodePoint) { replacementCount++; continue; }
                if (rune.Value is '\n' or '\r' or '\t') continue;
                if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned)
                    corruptCharCount++;
            }

            var corruptTotal = replacementCount + corruptCharCount + unpairedSurrogateCount;
            if (corruptTotal == 0) continue;

            // One issue per page even when several problems are present: each Error here
            // counts against the error rate denominator (attempted pages), so a page
            // reported twice would silently double its own weight in that rate.
            var parts = new List<string>(3);
            if (replacementCount > 0)       parts.Add($"{replacementCount} U+FFFD char(s)");
            if (corruptCharCount > 0)       parts.Add($"{corruptCharCount} control/unassigned character(s)");
            if (unpairedSurrogateCount > 0) parts.Add($"{unpairedSurrogateCount} unpaired surrogate(s)");

            // Ratio is against UTF-16 code unit length, close enough to scalar count for a
            // threshold and free to obtain.
            var pageLength = record.PageContent.Length;
            var isError    = corruptTotal >= MinCorruptCharsForError
                          || (pageLength > 0 && (double)corruptTotal / pageLength > MaxAcceptableCorruptRatio);

            issues.Add(new PipelineIssue(
                PipelineStage.TextQuality,
                isError ? IssueSeverity.Error : IssueSeverity.Warning,
                record.BlobName,
                $"Page {record.PageNumber}: {string.Join(", ", parts)}" +
                            (isError ? ", source text is corrupted / likely encoding corruption."
                                     : ", isolated bad character(s), below the corruption threshold.")));
        }

        return issues;
    }

    // 7. Table structure issues read directly off DI's own table data. Replaces an
    // earlier heuristic that pattern matched GFM pipe tables: DI renders tables as
    // HTML <table> elements, so that heuristic never matched and was silently a no-op.
    private static List<PipelineIssue> TableStructureQualityCheck(
        IReadOnlyDictionary<string, PdfDocumentStructure> structures)
    {
        var issues = new List<PipelineIssue>();

        foreach (var (blobName, structure) in structures)
        {
            foreach (var table in structure.Tables)
            {
                if (table.RowCount <= 0 || table.ColumnCount <= 0)
                    issues.Add(PipelineIssue.Warning(PipelineStage.TableStructure,
                        blobName,
                        $"Table at offset {table.Offset}: reported {table.RowCount} row(s) x {table.ColumnCount} column(s), malformed."));
                else if (table.Cells.Count == 0)
                    issues.Add(PipelineIssue.Warning(PipelineStage.TableStructure,
                        blobName,
                        $"Table at offset {table.Offset}: {table.RowCount}x{table.ColumnCount} reported but no cell data was extracted."));
            }
        }

        return issues;
    }

    // 8. Total tables detected this run, real count from DI's table detection.
    private static int CountDetectedTables(IReadOnlyDictionary<string, PdfDocumentStructure> structures) =>
        structures.Values.Sum(s => s.Tables.Count);

    // 9. Document flagged if none of its pages has a markdown heading. Grouped first so
    // Any() short circuits on the first page of a document that has one, instead of
    // running the regex over every page of every document.
    private static List<string> DocsWithNoPagesWithHeading(PdfCleanResult cleanResult) =>
        cleanResult.Records
            .GroupBy(r => r.BlobName, StringComparer.OrdinalIgnoreCase)
            .Where(g => !g.Any(r => MarkdownHeading.IsMatch(r.PageContent)))
            .Select(g => g.First().BlobName)
            .ToList();

    // 10. Sample for human review. Reservoir sampling (Algorithm R): one pass, no
    // intermediate allocation, versus the previous OrderBy(Guid.NewGuid()) which sorted
    // the whole corpus and allocated a Guid per page.
    //
    // The seed is explicit or derived from the cleaned page keys, so rerunning the same
    // input produces the same sample and validation-report.json stays diffable. Any real
    // content change reshuffles it, which is the intended behaviour: the sample should
    // track the run, not be frozen forever.
    // internal (not private): same rationale as EvaluateGate below - the reservoir
    // sampling/seed behaviour is unit tested directly without building a full Validate
    // fixture just to get at randomness.
    internal static List<CleanedPdfPageRecord> BuildRandomCheckSample(PdfCleanResult cleanResult, int? seed)
    {
        var records = cleanResult.Records;
        if (records.Count <= SpotCheckSampleSize) return [.. records];

        var random    = new Random(seed ?? StableSeed(records));
        var reservoir = new List<CleanedPdfPageRecord>(SpotCheckSampleSize);

        for (int i = 0; i < records.Count; i++)
        {
            if (i < SpotCheckSampleSize)
            {
                reservoir.Add(records[i]);
                continue;
            }

            var j = random.Next(i + 1);
            if (j < SpotCheckSampleSize) reservoir[j] = records[i];
        }

        return reservoir;
    }

    // FNV-1a over the page keys. string.GetHashCode is randomised per process on .NET
    // Core, so it cannot be used for anything that must be stable across runs.
    private static int StableSeed(IReadOnlyList<CleanedPdfPageRecord> records)
    {
        const uint offsetBasis = 2166136261;
        const uint prime       = 16777619;

        var hash = offsetBasis;
        foreach (var record in records)
        {
            foreach (var c in record.BlobName)
            {
                hash = (hash ^ char.ToUpperInvariant(c)) * prime;
            }
            hash = (hash ^ (uint)record.PageNumber) * prime;
        }

        return (int)hash;
    }

    // 10b. Pairs the same sampled pages with their pre clean raw content, keyed on
    // (BlobName, PageNumber) against pagesExtraction.Records, the raw PdfPageRecord list
    // Clean read from. A miss (extractor produced a page Clean has no record for, which
    // reconciliation above would already flag as a hard failure) is skipped rather than
    // guessed at.
    //
    // Scans rawRecords looking for the <= 5 wanted keys rather than indexing the entire
    // corpus into a dictionary to serve five lookups, and stops as soon as all are found.
    private static List<CleaningSpotCheckEntry> BuildCleaningSpotCheckSample(
        IReadOnlyList<CleanedPdfPageRecord> sample, IReadOnlyList<PdfPageRecord> rawRecords)
    {
        if (sample.Count == 0) return [];

        var wanted = new HashSet<(string BlobName, int PageNumber)>(sample.Count, PageKeyComparer);
        foreach (var record in sample) wanted.Add((record.BlobName, record.PageNumber));

        var found = new Dictionary<(string BlobName, int PageNumber), PdfPageRecord>(wanted.Count, PageKeyComparer);
        foreach (var raw in rawRecords)
        {
            var key = (raw.BlobName, raw.PageNumber);
            // TryAdd, not indexer assignment: a duplicate (BlobName, PageNumber) from the
            // extractor is already a hard reconciliation failure above, so this lookup just
            // needs the first raw page for the sample, not to re-detect that problem.
            if (wanted.Contains(key)) found.TryAdd(key, raw);
            if (found.Count == wanted.Count) break;
        }

        var entries = new List<CleaningSpotCheckEntry>(sample.Count);
        foreach (var record in sample)
        {
            if (!found.TryGetValue((record.BlobName, record.PageNumber), out var raw)) continue;

            entries.Add(new CleaningSpotCheckEntry
            {
                BlobName       = record.BlobName,
                PageNumber     = record.PageNumber,
                RawContent     = raw.PageContent,
                CleanedContent = record.PageContent,
            });
        }

        return entries;
    }

    // 11. The whole gating policy in one place, so the thresholds can be unit tested
    // without building a full Validate fixture.
    //
    // Error rate is per ATTEMPTED page, so file level failures (which contribute errors
    // but no pages) still count against the denominator.
    //
    // attemptedPages == 0 means zero source documents were even submitted for extraction
    // (SortResultsInto3Buckets only ever produces Records/Errors from fileResults, so
    // this is empty exactly when fileResults is) - the normal steady-state case where the
    // pre-extraction diff correctly found nothing new/updated. That must pass (0.0), not
    // force a 100% error rate: "attempted something and got nothing back" is what
    // CheckDiffExtractNCleaning's zero-cleaned-records check exists to catch, gated
    // separately via reconciliationProblems, and only when pages were actually attempted.
    internal static bool EvaluateGate(
        IReadOnlyList<PipelineIssue> issues,
        int                            attemptedPages,
        IReadOnlyList<string>          reconciliationProblems)
    {
        var errorCount = issues.Count(i => i.IsError);
        var errorRate  = attemptedPages == 0 ? 0.0 : 100.0 * errorCount / attemptedPages;

        return errorRate <= MaxAcceptableErrorRatePercent && reconciliationProblems.Count == 0;
    }

    // Case insensitive on BlobName, exact on PageNumber. Tuple keys cannot carry a per
    // field comparer, which is why every keyed pass previously had to allocate an
    // upper invariant copy of the blob name per record.
    private sealed class BlobPageComparer : IEqualityComparer<(string BlobName, int PageNumber)>
    {
        public bool Equals((string BlobName, int PageNumber) x, (string BlobName, int PageNumber) y) =>
            x.PageNumber == y.PageNumber
            && StringComparer.OrdinalIgnoreCase.Equals(x.BlobName, y.BlobName);

        public int GetHashCode((string BlobName, int PageNumber) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.BlobName), obj.PageNumber);
    }
}
