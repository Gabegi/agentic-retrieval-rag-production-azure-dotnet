using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Querying.Models;

public sealed record Citation(
    string  DocumentId,
    string? Title,
    string? QuickCode,
    string? RelativePath,
    // PageCount / CreatedAt / ModDate come off the index fields of the same name - see
    // DocumentReferenceBase for what each currently carries (CreatedAt/ModDate: always null).
    int?            Page       = null,
    int?            PageCount  = null,
    DateTimeOffset? CreatedAt  = null,
    DateTimeOffset? ModDate    = null)
    : DocumentReferenceBase(DocumentId, Title, QuickCode, RelativePath, PageCount, CreatedAt, ModDate);
