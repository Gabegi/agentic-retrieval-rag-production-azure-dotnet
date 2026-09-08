namespace AgenticRagApp.Infrastructure.Clients.DomainClassification;

// Derives the per-document population tag (sector or doelgroep: GGZ, VVT, LVB, ...) that the
// chunking pipeline stamps onto every chunk as domain_tag. An interface so Indexing.CU
// depends on a seam rather than on IChatClient directly - the same layering as
// IEmbeddingClient, and the same Moq seam for its tests.
public interface IDomainClassifier
{
    // Returns SourceId -> tag for every document the model classified. A null VALUE means
    // "classified: no specific population applies" - a real answer, cached like any other. A
    // MISSING key means classification failed for that document (model error, invalid item in
    // the response) and should be retried on a later run. Never throws for model or parse
    // failures; the caller degrades to untagged documents.
    Task<IReadOnlyDictionary<string, string?>> ClassifyAsync(
        IReadOnlyList<DocumentToClassify> documents, CancellationToken ct = default);
}

// SourceId doubles as the blob filename, which usually carries the population token verbatim
// ("CAO GHZ (Versie 4).pdf"). TextSample is the document's identity text (title + headings),
// capped by the caller - enough signal to classify without paying for full document text.
public sealed record DocumentToClassify(string SourceId, string Title, string TextSample);
