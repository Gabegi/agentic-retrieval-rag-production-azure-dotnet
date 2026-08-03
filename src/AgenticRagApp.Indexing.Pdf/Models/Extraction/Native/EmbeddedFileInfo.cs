namespace AgenticRagApp.Indexing.Pdf.Models;

// One file attachment embedded in the PDF, as read by
// PdfNativeMetadataExtractor.TryGetEmbeddedFileNames (pdf.Advanced.TryGetEmbeddedFiles).
// Name/FileSpecification only - never PdfPig's own EmbeddedFile.Memory/Bytes/Stream
// (the actual attachment content), which has no reason to live in a metadata report.
public sealed record EmbeddedFileInfo(string Name, string FileSpecification);
