using AgenticRagApp.Indexing.Csv.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

// Merges data (pages) with metadata (index).

// Potential outcomes
    // Matched + Active → AddJoined → the only bucket that continues to DataCleaner. This is your "data + metadata both present" set.
    // NotFound → AddError → tracked, not joined. Whether that then gets written to blob for tracking is a caller decision outside this method — Join() just classifies it.
    // Inactive → AddDataQualityWarning + skip count → same idea, tracked but excluded from joined output.
    // Index record, zero matching pages → AddSkippedIndexRecord → tracked separately.
public class CsvJoiner : ICsvJoiner
{
    private enum MatchStatus { NotFound, Inactive, Matched }

    private readonly record struct PageMatch(string DocumentId, MatchStatus Status, JoinedPageRecord? Joined);

    public JoinResult Join(IReadOnlyList<PageRecord> pages, IReadOnlyList<IndexRecord> index)
    {
        var result = new JoinResult();

        var uniqueIndex = BuildIndexLookup(index, result);

        // OrdinalIgnoreCase: we don't have confirmation that DOCUMENT_ID values are
        // consistently cased across the two source files (see docs/data-questions.md).
        // If they're always consistent this changes nothing; if they're not, it
        // prevents the same silent-mismatch failure mode Trim() already guards
        // against for whitespace in RequireDocumentId.
        var matchedDocIds   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alreadyReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in pages)
        {
            var match = MatchPageWithIndexOnID(page, uniqueIndex);
            SetPageResult(match, result, matchedDocIds, alreadyReported);
        }

        // Iterate uniqueIndex.Values, not the raw index list - a duplicate DOCUMENT_ID
        // that never matches any page would otherwise get added to SkippedIndexRecords
        // once per duplicate row instead of once for the (deduplicated) document.
        foreach (var indexRecord in uniqueIndex.Values)
        {
            if (!matchedDocIds.Contains(indexRecord.DocumentId))
                result.AddToIndexWithoutPages(indexRecord);
        }

        return result;
    }

    // matches pages with index based on Document_id.
    // Page is dropped if no match is found, or if ACTIVE=False
    private static PageMatch MatchPageWithIndexOnID(PageRecord page, Dictionary<string, IndexRecord> uniqueIndex)
    {
        // NotFound = Document ID doesn't match any in the index
        if (!uniqueIndex.TryGetValue(page.DocumentId, out var indexRecord))
            return new PageMatch(page.DocumentId, MatchStatus.NotFound, Joined: null);

        // Inactive = Valid Document ID but Index is set as Inactive
        if (!indexRecord.Active)
            return new PageMatch(page.DocumentId, MatchStatus.Inactive, Joined: null);

        // Matched = Matching Document ID and Active
        return new PageMatch(page.DocumentId, MatchStatus.Matched, ToJoinedRecord(page, indexRecord));
    }

    // Applies a PageMatch to the running JoinResult and bookkeeping sets.
    // Split from MatchPageWithIndexOnID so the lookup logic stays a pure function of (page, index).
    private static void SetPageResult(
        PageMatch match, JoinResult result, HashSet<string> matchedDocIds, HashSet<string> alreadyReported)
    {
        switch (match.Status)
        {
            case MatchStatus.NotFound:
                ApplyNotFound(match, result, alreadyReported);
                break;

            case MatchStatus.Inactive:
                // Mark this index record as "used" regardless of Active,
                // so it won't wrongly show up as unmatched later.
                matchedDocIds.Add(match.DocumentId);
                ApplyInactive(match, result, alreadyReported);
                break;

            case MatchStatus.Matched:
                matchedDocIds.Add(match.DocumentId);
                result.AddJoined(match.Joined!);
                break;
        }
    }

    // Not found -> log an error (once per doc, via alreadyReported). Page is dropped.
    private static void ApplyNotFound(PageMatch match, JoinResult result, HashSet<string> alreadyReported)
    {
        if (alreadyReported.Add(match.DocumentId))
            result.AddToNotFound(new JoinError
            {
                DocumentId = match.DocumentId,
                Message    = $"No index record found for document {match.DocumentId}.",
            });
    }

    // Inactive -> count as skipped, warn once per doc (via alreadyReported). Page is dropped.
    private static void ApplyInactive(PageMatch match, JoinResult result, HashSet<string> alreadyReported)
    {
        result.CountInactivePageSkipped();
        if (alreadyReported.Add(match.DocumentId))
            result.AddToInactive(new JoinError
            {
                DocumentId = match.DocumentId,
                Message    = "Document is marked inactive in the index — pages skipped.",
            });
    }

    private static JoinedPageRecord ToJoinedRecord(PageRecord page, IndexRecord indexRecord) => new()
    {
        DocumentId        = page.DocumentId,
        Title             = page.Title,
        QuickCode         = page.QuickCode,
        FolderPath        = page.FolderPath,
        LastModifiedRaw   = page.LastModifiedRaw,
        PageIndex         = page.PageIndex,
        PageContent       = page.PageContent,
        Language          = page.Language,
        RelativePath      = page.RelativePath,
        DocumentTypeName  = indexRecord.DocumentTypeName,
        Summary           = indexRecord.Summary,
        Version           = indexRecord.Version,
        CheckDateRaw      = indexRecord.CheckDateRaw,
        AttentionFlagsRaw = indexRecord.AttentionFlagsRaw,
    };

    // Builds the DOCUMENT_ID -> IndexRecord lookup Join matches pages against, warning
    // on (and skipping) any duplicate DOCUMENT_ID rather than throwing.
    private static Dictionary<string, IndexRecord> BuildIndexLookup(IReadOnlyList<IndexRecord> index, JoinResult result)
    {
        var indexByDocId  = new Dictionary<string, IndexRecord>(StringComparer.OrdinalIgnoreCase);
        var alreadyWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in index)
        {
            if (!indexByDocId.TryAdd(record.DocumentId, record) && alreadyWarned.Add(record.DocumentId))
            {
                result.AddToDuplicates(new JoinError
                {
                    DocumentId = record.DocumentId,
                    Message    = $"Duplicate DOCUMENT_ID '{record.DocumentId}' in index — kept the first occurrence.",
                });
            }
        }
        return indexByDocId;
    }
}
