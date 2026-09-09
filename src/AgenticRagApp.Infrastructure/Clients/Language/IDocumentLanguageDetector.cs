namespace AgenticRagApp.Infrastructure.Clients.Language;

// Detects what language a document is written in - the producer the index's `language` field
// never had.
//
// WHY THIS EXISTS AT ALL. `language` is declared filterable AND facetable on the index
// (IndexService), and its comment described a producer that no longer exists: Document
// Intelligence's AnalyzeResult.Languages. Content Understanding 1.1.0 exposes NO detected
// language on the document response - the only language surfaces in that SDK are the
// analyzer's input Locales config (custom analyzers only) and TranscriptPhrase.Locale
// (audio/video). So the field was not merely unwired, it was structurally unfillable, and a
// facetable field that no producer can ever fill reads as data that exists. Populating it
// touches no schema; removing it would mean an index recreate.
//
// An interface so Indexing.CU depends on a seam rather than on TextAnalyticsClient directly -
// the same layering as IDomainClassifier and IEmbeddingClient, and the same Moq seam for tests.
public interface IDocumentLanguageDetector
{
    // Null means "not detected" - an empty sample, a service failure, or a response the model
    // itself could not attribute to a language. Never throws for service failures: the caller
    // degrades to an unset language, exactly as the domain classifier degrades to untagged
    // documents, because a language guess is not worth failing an extraction run over.
    Task<DocumentLanguage?> DetectAsync(string textSample, CancellationToken ct = default);
}

// Iso6391Name is what the index field carries ("nl", "en") - the two-letter ISO 639-1 code, not
// the spelled-out name, because it is a filter/facet value that queries are written against.
//
// Confidence is carried alongside and reported, but NO THRESHOLD IS APPLIED to it here. This
// codebase does not ship guessed cutoffs (see WordConfidenceSummary and FlagEvaluator's
// sourced-vs-awaiting-calibration split), and there is no published cutoff for language
// detection over 1,200-character samples of Dutch care documents to cite. A run's worth of
// real scores is what a rule would have to be calibrated against.
public sealed record DocumentLanguage(string Iso6391Name, double? Confidence);
