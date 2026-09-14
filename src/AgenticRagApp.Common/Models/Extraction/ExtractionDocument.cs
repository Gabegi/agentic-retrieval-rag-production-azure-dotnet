namespace AgenticRagApp.Common.Models;

// Extractor-agnostic document shape. No production code constructs it any more: the CU pipeline's
// PdfExtractionDocument deliberately does not derive from ExtractionDocumentBase (a document has no
// ordinal), and the CSV pipeline that used this record is archived. ExtractionOutput is its only
// consumer.
// SourceId is the chunking boundary — the chunker never blends chunks across different SourceIds.
// Metadata uses Dictionary (not IReadOnlyDictionary) so Durable/STJ can deserialize it reliably.
public sealed record ExtractionDocument(
    string SourceId,                        // grouping/chunking boundary — a document id or blob name
    int    Ordinal,                         // page number or row index — used for ordering only
    string Content,
    Dictionary<string, string> Metadata
) : ExtractionDocumentBase(SourceId, Ordinal, Content);
