using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Querying.Models;

public sealed record RetrievedChunk(
    string Id, string DocumentId, int Page, int ChunkIndex,
    string? Title, string? Summary, string Content,
    string? QuickCode = null, string? RelativePath = null,
    string? ZenyaDocumentId = null, string? ZenyaVersion = null,
    string? ZenyaStatus = null, string? ZenyaUrl = null,
    // Native PDF metadata (PdfNativeMetadataExtractor) - null for CSV rows and for
    // neighbor-expanded chunks (ChunkNeighborExpander doesn't select these, since only
    // the original matched chunk per document feeds a Citation - see AgenticRagQueryService).
    int? PageCount = null, DateTimeOffset? CreatedAt = null, DateTimeOffset? ModDate = null)
    : DocumentReferenceBase(DocumentId, Title, QuickCode, RelativePath, ZenyaDocumentId, ZenyaVersion, ZenyaStatus, ZenyaUrl, PageCount, CreatedAt, ModDate)
{
    public string ToContextText()
    {
        var header = Title is null ? null : string.IsNullOrWhiteSpace(Summary) ? Title : $"{Title}\n{Summary}";
        return header is null ? Content : $"[{header}]\n{Content}";
    }
}
