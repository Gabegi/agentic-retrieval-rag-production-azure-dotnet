namespace AgenticRagApp.Indexing.Pdf.Models;

// Everything read off a PdfDocument via PdfPig, independent of Document Intelligence:
// the native Info-dictionary (Title/Author/CreationDate/ModDate/Producer/Creator/Subject/
// Keywords), the outline/bookmark tree (Bookmarks), whether the file carries any
// encryption/permission restrictions (IsEncrypted - distinct from PdfDocumentValidator's
// Encrypted failure reason, which is for files that couldn't be opened at all; a file can
// open fine and still report IsEncrypted=true, e.g. owner-password-only permission
// restrictions), its AcroForm fields if any (FormFields, including each field's entered/
// selected value - safe here since this corpus's PDFs are guideline documents, not
// per-patient records), its embedded file attachments if any (EmbeddedFiles), and its
// XMP metadata packet if present (Xmp) - all read by
// PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose.
//
// Producer/Creator/Subject/Keywords are diagnostics-only today (see
// PdfNativeMetadataExtractor's Producer-missing warning) - not carried into
// ExtractionDocument/DocumentChunk, same as FileSizeBytes/PdfSpecVersion aren't (see
// ExtractionDocument's own comment on that).
public record DocMetadata(
    string? Title, string? Author, DateTimeOffset? CreatedAt, DateTimeOffset? ModDate,
    string? Producer, string? Creator, string? Subject, string? Keywords,
    int PageCount, IReadOnlyList<Bookmark>? Bookmarks,
    bool IsEncrypted, IReadOnlyList<AcroFormField>? FormFields,
    IReadOnlyList<EmbeddedFileInfo>? EmbeddedFiles, XmpFacts? Xmp);
