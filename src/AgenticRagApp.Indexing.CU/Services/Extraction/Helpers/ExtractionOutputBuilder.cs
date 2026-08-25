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
// The per-blob facts (LastModified, Zenya metadata) come in as the same
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
                var zenya     = entry?.Zenya ?? ZenyaMetadata.Empty;
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
                    // report now mean by "pages" - the native page count it replaces could be
                    // null and could disagree with what the service returned.
                    PageCount:        f.PageSpans?.Count,
                    LastModifiedDate: entry?.LastModified,
                    ZenyaDocumentId:  zenya.DocumentId,
                    ZenyaVersion:     zenya.Version,
                    ZenyaStatus:      zenya.Status,
                    ZenyaUrl:         zenya.Url,
                    Bookmarks:        [],
                    PageBreadcrumbs:  new Dictionary<int, string>(),
                    Sections:         structure?.Sections       ?? [],
                    Headings:         structure?.Headings       ?? [],
                    Boilerplate:      structure?.Boilerplate    ?? [],
                    Tables:           structure?.Tables         ?? [],
                    SelectionMarks:   structure?.SelectionMarks ?? [],
                    Figures:          structure?.Figures        ?? [],
                    Lines:            structure?.Lines          ?? [],
                    Profile:          f.Profile,
                    Language:         f.Language);
            })];

    // Maps the extracted files into the source-agnostic PdfExtractionOutput returned to the
    // caller.
    //
    // Several fields are null or zero here and it is worth being precise about which is which:
    // null means "no equivalent concept" (StaleDocCount - no Zenya attention flag;
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

        var okBlobNames = files.Where(f => f.Ok).Select(f => f.BlobName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var traceabilityGapCount = okBlobNames.Count(b => ZenyaFor(entries, b).DocumentId is null);
        var missingVersionCount  = okBlobNames.Count(b => ZenyaFor(entries, b).Version is null);

        var contentHashes = BuildContentHashes(files);

        var redFlags = new List<string>();
        if (traceabilityGapCount > 0)
            redFlags.Add(
                $"{traceabilityGapCount} document(s) have no zenya_document_id blob metadata set â€” " +
                "citations built from these will show a traceability gap (Citation.TraceabilityGap).");
        redFlags.AddRange(DuplicateContentRedFlag(contentHashes));

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
            MissingVersionCount    = missingVersionCount,
            MissingDepartmentCount = null,
            TraceabilityGapCount   = traceabilityGapCount,
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
                                        d.Content.Length > 300 ? d.Content[..300] + "â€¦" : d.Content))],
            ContentHashes          = contentHashes,
        };
    }

    private static ZenyaMetadata ZenyaFor(IReadOnlyDictionary<string, PdfBlobInfo> entries, string blobName) =>
        entries.GetValueOrDefault(blobName)?.Zenya ?? ZenyaMetadata.Empty;

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
    // reach the run report and the run email, a log line reaches whoever goes looking.
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
