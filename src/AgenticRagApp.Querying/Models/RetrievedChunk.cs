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
    int? PageCount = null, DateTimeOffset? CreatedAt = null, DateTimeOffset? ModDate = null,
    // The two identity fields the embedded prefix was built from (PrefixBuilder): the heading
    // chain and the sector tag. Null on neighbor-expanded chunks, like the fields above.
    string? HeadingPath = null, string? DomainTag = null)
    : DocumentReferenceBase(DocumentId, Title, QuickCode, RelativePath, ZenyaDocumentId, ZenyaVersion, ZenyaStatus, ZenyaUrl, PageCount, CreatedAt, ModDate)
{
    // Rebuilds the same composition the chunk was EMBEDDED with: "Title [tag]", heading path,
    // body, blank-line separated - PrefixBuilder's exact shape. The index stores the bare body
    // (SearchUploadChunk maps chunk.Content, and the prefix lives only inside the vector), so
    // this is the one place the model's context gets the document identity back.
    //
    // The sector tag matters most: it is what separates CAO VVT from CAO GGZ from CAO GHZ on
    // sector-ambiguous questions, and the old "[Title]" header dropped it - both cao-ambig
    // eval scenarios scored Retrieval 2 against that header (260818 eval).
    public string ToContextText()
    {
        var titleLine = Title is null ? null
            : string.IsNullOrWhiteSpace(DomainTag) ? Title : $"{Title} [{DomainTag}]";

        // No Summary element: it was CSV-only, the mapper never populates it, and PDF and CSV
        // no longer share an index. The record keeps the parameter for the CSV-era callers.
        var parts = new[] { titleLine, HeadingPath, Content }
            .Where(part => !string.IsNullOrWhiteSpace(part));

        return string.Join("\n\n", parts);
    }
}
