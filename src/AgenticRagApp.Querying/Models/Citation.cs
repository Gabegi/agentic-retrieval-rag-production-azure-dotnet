using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Querying.Models;

public sealed record Citation(
    string  DocumentId,
    string? Title,
    string? QuickCode,
    string? RelativePath,
    // Native PDF metadata (PdfNativeMetadataExtractor) - null for CSV citations. ModDate
    // is the real "is this policy current" signal (content last edited), distinct from
    // any blob re-upload timing.
    int?            Page       = null,
    int?            PageCount  = null,
    DateTimeOffset? CreatedAt  = null,
    DateTimeOffset? ModDate    = null)
    : DocumentReferenceBase(DocumentId, Title, QuickCode, RelativePath, PageCount, CreatedAt, ModDate);
