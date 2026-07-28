using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The PDF extraction pipeline (download, run Document Intelligence, clean, validate).
// Kept as an interface purely so ExtractionService's unit tests can mock it - not for
// swapping to another document type at runtime; this pipeline is PDF-only.
public interface IExtractionOrchestrator
{
    string Source { get; }  // "pdf"

    // Extracts only the given source entries - ExtractionService has already listed what's in
    // blob storage (LastModified, ContentLength, Zenya metadata - see PdfBlobInfo) and
    // diffed it against the index to determine these are the only ones that are new or
    // changed. Passing the entries themselves (not just the ids) means the orchestrator never
    // needs to list the container a second time. Ids outside this set produce no
    // ExtractionDocuments.
    Task<PdfExtractionOutput> ExtractDocumentsAsync(IReadOnlyDictionary<string, PdfBlobInfo> sourceIdsToProcess, CancellationToken ct = default);
}
