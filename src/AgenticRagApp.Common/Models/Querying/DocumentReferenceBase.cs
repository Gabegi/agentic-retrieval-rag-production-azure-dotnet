namespace AgenticRagApp.Common.Models;

// Every field a query-time reference back to a source document has, regardless of
// whether it's a Citation (answer-level) or a RetrievedChunk (search-hit-level). Page
// is deliberately excluded - Citation's is nullable (a citation may not know a page),
// RetrievedChunk's isn't (a chunk always came from a specific page) - each derived
// record declares its own.
//
// The Zenya fields are gone (2026-08-26, with the whole Zenya-metadata mechanism): no blob
// ever carried them, so every reference reported a traceability gap about a channel that did
// not exist. DocumentId IS the blob name, which is the traceability that actually works.
public abstract record DocumentReferenceBase(
    string  DocumentId,
    string? Title,
    string? QuickCode,
    string? RelativePath,
    // From the index's page_count / created_at / mod_date. PageCount is the number of pages
    // Content Understanding extracted; CreatedAt/ModDate are declared on the index but currently
    // always null (their PdfPig-era producer is gone - see IndexService's header). ModDate would
    // be the real "is this policy current" signal (content last edited), distinct from any blob
    // re-upload timing.
    int?            PageCount = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? ModDate   = null);
