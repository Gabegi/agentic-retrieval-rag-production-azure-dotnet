namespace AgenticRagApp.Indexing.Pdf.Models;

// Best-effort read of the PDF's XMP metadata packet (Catalog -> /Metadata stream),
// as read by PdfNativeMetadataExtractor.GetXmpMetadata - a separate, newer metadata
// mechanism from the legacy Info dictionary (DocMetadata's own Title/Author/etc.),
// which some export tools populate instead of, or in addition to, the legacy fields.
// PdfPig only hands back the raw decoded stream bytes (XmpMetadata.MetadataStreamToken.Data) -
// it does not parse the RDF/XML itself, so this covers only the handful of common
// Dublin Core / XMP Basic / PDF-schema fields most tools actually write, not the full
// XMP spec (custom schemas, etc.). Creator/Subject do carry every value from their
// rdf:Seq/Bag container (joined), not just the first.
// A non-null XmpFacts with every field null means the PDF has no /Metadata stream, or
// the stream held no rdf:Description - both zero-facts, not a failure (see
// GetXmpMetadata). The whole XmpFacts is null only when reading/parsing the packet
// itself threw, which logs a warning and is the one case the caller can't act on.
public sealed record XmpFacts(
    string? Title,
    string? Creator,
    string? Subject,
    string? Producer,
    DateTimeOffset? CreateDate,
    DateTimeOffset? ModifyDate);
