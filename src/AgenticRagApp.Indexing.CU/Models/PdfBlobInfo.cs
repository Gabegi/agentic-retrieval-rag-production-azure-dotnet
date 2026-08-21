namespace AgenticRagApp.Indexing.CU.Models;

// One PDF blob's facts from a cheap container listing (name + LastModified + ContentLength +
// Zenya metadata) - no download, no Document Intelligence call. ExtractionService builds the
// full listing to diff against the index; only the entries it decides are new/updated get
// passed on to IExtractionOrchestrator.ExtractDocumentsAsync, which uses this same data
// instead of listing the container a second time.
public record PdfBlobInfo(DateTimeOffset LastModified, long? ContentLength, ZenyaMetadata Zenya);
