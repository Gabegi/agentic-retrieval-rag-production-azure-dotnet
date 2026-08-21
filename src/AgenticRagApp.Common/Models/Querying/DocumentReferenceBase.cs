namespace AgenticRagApp.Common.Models;

// Every field a query-time reference back to a source document has, regardless of
// whether it's a Citation (answer-level) or a RetrievedChunk (search-hit-level). Page
// is deliberately excluded - Citation's is nullable (a citation may not know a page),
// RetrievedChunk's isn't (a chunk always came from a specific page) - each derived
// record declares its own.
//
// ZenyaDocumentId/Version/Status/Url: PDF-only, sourced from blob metadata set at upload
// time (see ZenyaMetadata in AgenticRagApp.Indexing.CU) - not guaranteed to be present.
public abstract record DocumentReferenceBase(
    string  DocumentId,
    string? Title,
    string? QuickCode,
    string? RelativePath,
    string? ZenyaDocumentId = null,
    string? ZenyaVersion    = null,
    string? ZenyaStatus     = null,
    string? ZenyaUrl        = null,
    // Native PDF metadata (PdfNativeMetadataExtractor) - null for CSV. ModDate is the
    // real "is this policy current" signal (content last edited), distinct from any blob
    // re-upload timing.
    int?            PageCount = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? ModDate   = null)
{
    // Neither a Zenya document id (PDF's mechanism) nor a relative path (CSV's own,
    // pre-existing mechanism) means this reference can't be traced back to its source -
    // the "meetgat" (measurement gap) the traceability scenario calls for, not something
    // to silently paper over.
    public bool TraceabilityGap => ZenyaDocumentId is null && RelativePath is null;
}
