using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// A PDF extraction backend (Document Intelligence) implements this. Unlike
// ICsvExtractor's two file-shaped calls, a PDF has one file per document, so a single
// Extract call must return both pages and this file's own parsed metadata —
// re-parsing the same PDF twice per file would double the backend's cost for nothing.
// Async because DocumentIntelligenceExtractor does real network I/O (the analyze call,
// plus its retry backoff).
public interface IPdfExtractor
{
    string Name { get; } // "DocumentIntelligence"

    Task<PdfExtractionResult> ExtractPDFAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default);
}
