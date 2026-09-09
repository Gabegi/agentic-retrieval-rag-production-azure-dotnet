using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Turns a run's worth of ExtractedFiles into the output the chunking stage consumes: the
// documents themselves, the counts, the red flags and the billed usage.
//
// Pure functions over data, with no dependency on how that data was produced - no blob client, no
// analyzer, no parallelism. That is what makes the whole shape of this stage's output testable
// from hand-built ExtractedFiles, which is what ExtractionOutputBuilderTests does.
//
// The per-blob facts (LastModified) come in as the same
// IReadOnlyDictionary<string, PdfBlobInfo> the run was handed. The extraction loop used to copy
// those two fields into two side dictionaries as it went; the copies carried nothing the input
// did not already have.
internal static class ExtractionOutputBuilder
{
    // Cap on PdfExtractionOutput.Issues to stay safely under Durable Table Storage's 64KB
    // row-size limit.
    private const int MaxReturnedIssues = 100;

    // Assembles one PdfExtractionDocument per successfully extracted document.
    //
    // Ordering is not cosmetic: ChunkingHelper.SafeKey derives chunk ids from SourceId plus the
    // chunk's index within the document, so an unstable order would reshuffle every chunk id
    // from one run to the next. OrderBy(BlobName, Ordinal) over a bag whose order is
    // nondeterministic is what makes it stable - and it is a total order here, because blob
    // names are unique within a container.
    internal static List<PdfExtractionDocument> BuildDocuments(
        IReadOnlyList<ExtractedFile>             files,
        IReadOnlyDictionary<string, PdfBlobInfo> entries) =>
        [.. files
            .Where(f => f.Ok)
            .OrderBy(f => f.BlobName, StringComparer.Ordinal)
            .Select(f =>
            {
                var entry     = entries.GetValueOrDefault(f.BlobName);
                var structure = f.Structure;

                return new PdfExtractionDocument(
                    SourceId:         f.BlobName,
                    Content:          f.Content ?? "",
                    PageSpans:        f.PageSpans ?? [],
                    Title:            f.Title ?? "",
                    // Native PDF metadata is gone with the PdfPig preflight - there is no
                    // Author/CreationDate/ModDate to read any more, and no bookmark outline to
                    // build page breadcrumbs from. Null and empty are honest here: absent, not
                    // "known to be none".
                    Author:           null,
                    CreatedAt:        null,
                    ModDate:          null,
                    // The count of pages actually extracted, which is what the profile and every
                    // report now mean by "pages". DISTINCT page numbers, not span entries: a
                    // PageSpan is one of CU's verbatim ranges and a page can carry several.
                    PageCount:        f.PageSpans?.Select(s => s.PageNumber).Distinct().Count(),
                    LastModifiedDate: entry?.LastModified,
                    PageBreadcrumbs:  new Dictionary<int, string>(),
                    Sections:         structure?.Sections       ?? [],
                    Headings:         structure?.Headings       ?? [],
                    Boilerplate:      structure?.Boilerplate    ?? [],
                    Tables:           structure?.Tables         ?? [],
                    SelectionMarks:   structure?.SelectionMarks ?? [],
                    Figures:          structure?.Figures        ?? [],
                    Lines:            structure?.Lines          ?? [],
                    Annotations:      structure?.Annotations    ?? [],
                    Hyperlinks:       structure?.Hyperlinks     ?? [],
                    Profile:          f.Profile,
                    Language:         f.Language);
            })];

    // Maps the extracted files into the source-agnostic PdfExtractionOutput returned to the
    // caller.
    //
    // Several fields are null or zero here and it is worth being precise about which is which:
    // null means "no equivalent concept" (StaleDocCount - no source attention flag;
    // MissingDepartmentCount - no folder concept), while zero means "the thing that counted this
    // is gone". ReconciliationProblems and MojibakeRepairedPages are the second kind: they were
    // produced by PdfPipelineValidator and PdfCleaner, both deleted, and they will report real
    // numbers again when the validation layer returns (see ExtractionService's validation seam).
    internal static PdfExtractionOutput BuildExtractionOutput(
        IReadOnlyList<ExtractedFile>             files,
        IReadOnlyDictionary<string, PdfBlobInfo> entries)
    {
        var documents = BuildDocuments(files, entries);

        var errorIssues   = files.Where(f => f.Error is not null).Select(f => f.Error!).ToList();
        var warningIssues = files.SelectMany(f => f.Warnings).ToList();

        var contentHashes = BuildContentHashes(files);

        var redFlags = new List<string>(DuplicateContentRedFlag(contentHashes));

        return new PdfExtractionOutput(documents)
        {
            ValidationErrors       = errorIssues.Count,
            ValidationWarnings     = warningIssues.Count,
            ReconciliationProblems = 0,
            StaleDocCount          = null,
            MojibakeRepairedPages  = 0,
            DetectedTableCount     = documents.Sum(d => d.Tables.Count),
            DocsWithoutHeadings    = documents.Count(d => d.Headings.Count == 0),
            MissingTitleCount      = documents.Count(d => string.IsNullOrWhiteSpace(d.Title)),
            // Null = "no equivalent concept" since the Zenya metadata removal (2026-08-26):
            // version, traceability and the inactive lifecycle all came from blob metadata
            // keys nothing ever set.
            MissingVersionCount    = null,
            MissingDepartmentCount = null,
            TraceabilityGapCount   = null,
            Issues                 = [.. errorIssues.Concat(warningIssues).Take(MaxReturnedIssues)],
            RedFlags               = redFlags,
            // Summed from the service's own usage, per document. Null rather than 0 when nothing
            // reported usage at all, so "no data" and "billed nothing" stay distinguishable.
            BilledPagesStandard    = files.Any(f => f.Usage is not null)
                                     ? files.Sum(f => (long)(f.Usage?.DocumentPagesStandard ?? 0)) : null,
            BilledContextualizationTokens = files.Any(f => f.Usage is not null)
                                     ? files.Sum(f => (long)(f.Usage?.ContextualizationTokens ?? 0)) : null,
            // A cheap, deterministic sample for the run report: the first few documents by name,
            // with their opening text. Replaces the validator-built random sample, which is gone
            // with PdfPipelineValidator - deterministic rather than seeded-random so two runs
            // over the same corpus produce comparable reports.
            SpotCheckSample        = [.. documents.Take(3).Select(d => new SpotCheckEntry(
                                        d.SourceId, d.Title,
                                        d.Content.Length > 300 ? d.Content[..300] + "…" : d.Content))],
            ContentHashes          = contentHashes,
            Durations              = BuildDurations(files),
            Usages                 = BuildUsages(files),
            WordConfidences        = BuildWordConfidences(files),
            Summaries              = BuildSummaries(files),
            Languages              = BuildLanguages(files),
            BilledTokensByModel    = BuildTokensByModel(files),
        };
    }

    // Per-document wall clock, lifted off ExtractedFile the same way BuildContentHashes lifts
    // the hash. Files with no duration are the ones a test built directly rather than the run
    // loop timing them; absent rather than reported as zero. Same blob-name ordering, same
    // reason: reports that diff cleanly.
    internal static List<DocumentExtractDuration> BuildDurations(IReadOnlyList<ExtractedFile> files) =>
        [.. files
            .Where(f => f.DurationMs is not null)
            .OrderBy(f => f.BlobName, StringComparer.Ordinal)
            .Select(f => new DocumentExtractDuration(f.BlobName, f.DurationMs!.Value, f.Ok))];

    // Per-document billed usage, lifted the same way. Files whose analysis reported no usage
    // are absent rather than present-with-nulls - "the service said nothing" is not a row.
    internal static List<DocumentUsage> BuildUsages(IReadOnlyList<ExtractedFile> files) =>
        [.. files
            .Where(f => f.Usage is not null)
            .OrderBy(f => f.BlobName, StringComparer.Ordinal)
            .Select(f => new DocumentUsage(
                f.BlobName, f.Usage!.DocumentPagesStandard, f.Usage.ContextualizationTokens,
                f.Usage.TokensByModel, f.Ok))];

    // Per-document read quality, lifted the same way. Files whose response reported no word
    // confidences are absent rather than present-with-nulls - "the service measured nothing" is
    // not a row, exactly as with usage.
    internal static List<DocumentWordConfidence> BuildWordConfidences(IReadOnlyList<ExtractedFile> files) =>
        [.. files
            .Where(f => f.WordConfidence is not null)
            .OrderBy(f => f.BlobName, StringComparer.Ordinal)
            .Select(f => new DocumentWordConfidence(f.BlobName, f.WordConfidence!, f.Ok))];

    // CU's whole-document summary per document, lifted the same way. Documents whose response
    // carried no Summary field are absent rather than present-with-an-empty-string, exactly as
    // with usage and word confidence.
    internal static List<DocumentSummaryEntry> BuildSummaries(IReadOnlyList<ExtractedFile> files) =>
        [.. files
            .Where(f => f.Summary is not null)
            .OrderBy(f => f.BlobName, StringComparer.Ordinal)
            .Select(f => new DocumentSummaryEntry(f.BlobName, f.Summary!, f.Ok))];

    // What AI Language said each document is written in, lifted the same way. Documents whose
    // language could not be detected - or was never attempted, which is every failed
    // extraction - are absent rather than present with a blank code.
    internal static List<DocumentLanguageDetection> BuildLanguages(IReadOnlyList<ExtractedFile> files) =>
        [.. files
            .Where(f => !string.IsNullOrWhiteSpace(f.Language))
            .OrderBy(f => f.BlobName, StringComparer.Ordinal)
            .Select(f => new DocumentLanguageDetection(
                f.BlobName, f.Language!, f.LanguageConfidence, f.Ok))];

    // The run's per-model token bill: every document's TokensByModel summed key-by-key, keys
    // verbatim as the service bills them. Ordered for the same diff-cleanly reason as the lists.
    internal static IReadOnlyDictionary<string, long> BuildTokensByModel(IReadOnlyList<ExtractedFile> files)
    {
        var totals = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.Usage is null) continue;
            foreach (var (key, count) in file.Usage.TokensByModel)
                totals[key] = totals.GetValueOrDefault(key) + count;
        }
        return totals;
    }

    // --- Content hashing (measurement only) -----------------------------------

    // A content-hash-keyed cache of extraction results was built and then deliberately removed:
    // keying on the build id (so a code change cannot serve stale extractions) excludes the one
    // case such a cache would pay for - a full reindex after a code change - leaving only "blob
    // touched but bytes unchanged" and duplicate uploads. It also could not dedup duplicates
    // within a single run, since parallel workers hash and miss before either write lands, and
    // it saved only the analyze call, never the download that precedes the hash.
    //
    // So the hash carries evidence instead of assuming the answer: it rides out to the run's
    // reports, and the cache gets built only if Distinct vs Total there justifies it. Producing
    // that evidence is all this does - ExtractionReporter is what writes and logs it.
    //
    // Files with no hash are the ones whose download never produced bytes; they are absent here
    // rather than counted as an extra distinct document.
    //
    // Ordered by blob name for the same reason BuildDocuments is: two runs over the same corpus
    // should produce reports that diff cleanly against each other.
    internal static List<DocumentContentHash> BuildContentHashes(IReadOnlyList<ExtractedFile> files) =>
        [.. files
            .Where(f => f.ContentHash is not null)
            .OrderBy(f => f.BlobName, StringComparer.Ordinal)
            .Select(f => new DocumentContentHash(f.BlobName, f.ContentHash!, f.Ok))];

    // Cap on the groups named in the duplicate red flag. Red flags travel in
    // ExtractionStageMetrics, which has to stay under Durable Table Storage's 64KB row limit -
    // same reason MaxReturnedIssues exists. The per-document facts report carries every hash
    // uncapped, so nothing is actually lost by capping here.
    private const int MaxDuplicateGroupsFlagged = 10;

    // Two blobs with the same hash are the same file uploaded twice - a corpus-hygiene finding
    // that stands on its own, independent of whether anything ever caches on it. Raised as a red
    // flag rather than only logged, because that is what puts it in front of someone: red flags
    // reach the run report and the run analysis, a log line reaches whoever goes looking.
    //
    // Hashes are compared case-insensitively to match ComputeContentHash's hex output being
    // treated as case-insensitive everywhere else it is grouped.
    internal static IReadOnlyList<string> DuplicateContentRedFlag(
        IReadOnlyList<DocumentContentHash> hashes)
    {
        var groups = hashes
            .GroupBy(h => h.Hash, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (groups.Count == 0) return [];

        var listed = string.Join(" | ", groups
            .Take(MaxDuplicateGroupsFlagged)
            .Select(g => string.Join(" == ", g.Select(h => h.BlobName))));

        var more = groups.Count > MaxDuplicateGroupsFlagged
            ? $" (+{groups.Count - MaxDuplicateGroupsFlagged} more group(s) - see the file-facts report)"
            : "";

        return [$"byte_identical_duplicates: {groups.Count} group(s) of documents with identical bytes - {listed}{more}"];
    }
}
