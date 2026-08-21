namespace AgenticRagApp.Indexing.CU.Models;

// One non-fatal warning DI attached to the whole-document analysis (e.g. a page that
// partially failed OCR) - distinct from the zero-pages case DIAnalyzeDocumentAsync
// already treats as an outright failure. Wraps Azure's DocumentIntelligenceWarning so
// callers of this project's models don't need a reference to the Azure SDK type.
public sealed record AnalysisWarning(string? Code, string? Message, string? Target);
