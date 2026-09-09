namespace AgenticRagApp.Indexing.CU.Models;

// What AI Language said one document is written in - the sixth per-document lift, alongside
// DocumentContentHash, DocumentExtractDuration, DocumentUsage, DocumentWordConfidence and
// DocumentSummaryEntry, and lifted for the same reason as those: it is a MEASUREMENT about the
// document, not part of the document.
//
// The value itself travels a second path that this one does not replace:
// PdfExtractionDocument.Language -> DocumentStamp -> ChunkObject.Language -> the index's
// `language` field, which is filterable and facetable. This lift is what puts the same value,
// plus the confidence, into the per-document facts report - so "which document is the English
// one" is answerable from a report rather than by querying the index and hoping.
//
// The CONFIDENCE is reported and nothing reads it. There is no published cutoff for language
// detection over 1,200-character samples to cite, and this codebase does not ship guessed
// thresholds (see WordConfidenceSummary for the same stance on word confidence). Null when the
// detection carried no score.
//
// Ok rides along like it does on the other lifts, even though a failed extraction is never
// language-detected today (there is no text to detect from): the field keeps the shape uniform
// and stays honest if that ever changes.
public sealed record DocumentLanguageDetection(
    string  BlobName,
    string  Iso6391Name,
    double? Confidence,
    bool    Ok);
