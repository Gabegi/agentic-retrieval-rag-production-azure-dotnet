using Azure.Search.Documents.Indexes.Models;

namespace AgenticRagApp.Infrastructure.Clients.Search;

// The vector side of the LIVE index definition, as the service reports it (2026-09-15) - read
// back at report time by SaveIndexReportActivity and carried on the run report as VectorConfig.
//
// Why read it back rather than restate what the code declares: BuildVectorSearch constructs
// `new HnswAlgorithmConfiguration("hnsw-config")` with no parameters, so the metric, m,
// efConstruction and efSearch the index runs with are whatever the service defaulted them to
// (cosine / 4 / 400 / 500 per the service documentation) - a fact about the service, never
// written down anywhere in this repo. And EnsureIndexAsync is get-or-create: a configuration
// change after the index exists (dimensions, embedding model) never reaches it, so "what the
// code says" and "what the index is" can disagree indefinitely. This record is the second half of
// that comparison; the Configured* fields are the first, stamped at read time so the run
// analysis can flag the drift without another lookup.
//
// Metric / M / EfConstruction / EfSearch are null when the service returns no hnswParameters
// block - "not reported", not a value. Nothing here changes the index.
public sealed record IndexVectorConfig(
    string          IndexName,
    string          FieldName,
    bool            FieldPresent,
    // The vector field's width on the index vs IndexerConfig.OpenAiEmbeddingDimensions.
    int?            Dimensions,
    int             ConfiguredDimensions,
    // Algorithm configuration behind the field's profile: kind (Hnsw / ExhaustiveKnn), the
    // similarity metric HNSW compares with (cosine / euclidean / dotProduct / hamming), and the
    // HNSW graph parameters.
    string?         Algorithm,
    string?         Metric,
    int?            M,
    int?            EfConstruction,
    int?            EfSearch,
    // Compression configuration on the profile (scalar / binary quantization), or null = none.
    string?         Compression,
    // The vectorizer that embeds QUERIES at search time, and the model/deployment it uses - vs
    // the model the documents were embedded with (IndexerConfig.OpenAiEmbeddingModelName).
    string?         Vectorizer,
    string?         VectorizerModel,
    string?         VectorizerDeployment,
    string          ConfiguredModelName,
    DateTimeOffset  ReadAtUtc)
{
    public static IndexVectorConfig From(
        SearchIndex index, string fieldName, int configuredDimensions, string configuredModelName, DateTimeOffset readAtUtc)
    {
        var field       = index.Fields.FirstOrDefault(f => f.Name == fieldName);
        var vectorSearch = index.VectorSearch;
        var profile     = vectorSearch?.Profiles.FirstOrDefault(p => p.Name == field?.VectorSearchProfileName);
        var algorithm   = vectorSearch?.Algorithms.FirstOrDefault(a => a.Name == profile?.AlgorithmConfigurationName);
        var compression = profile?.CompressionName is { } compressionName
            ? vectorSearch?.Compressions.FirstOrDefault(c => c.CompressionName == compressionName)
            : null;
        var vectorizer  = profile?.VectorizerName is { } vectorizerName
            ? vectorSearch?.Vectorizers.FirstOrDefault(v => v.VectorizerName == vectorizerName)
            : null;

        // Both algorithm kinds carry a metric; only HNSW carries graph parameters.
        string? metric = null; int? m = null, efConstruction = null, efSearch = null;
        switch (algorithm)
        {
            case HnswAlgorithmConfiguration hnsw:
                metric         = hnsw.Parameters?.Metric?.ToString();
                m              = hnsw.Parameters?.M;
                efConstruction = hnsw.Parameters?.EfConstruction;
                efSearch       = hnsw.Parameters?.EfSearch;
                break;
            case ExhaustiveKnnAlgorithmConfiguration knn:
                metric = knn.Parameters?.Metric?.ToString();
                break;
        }

        var openAi = vectorizer as AzureOpenAIVectorizer;

        return new IndexVectorConfig(
            IndexName:            index.Name,
            FieldName:            fieldName,
            FieldPresent:         field is not null,
            Dimensions:           field?.VectorSearchDimensions,
            ConfiguredDimensions: configuredDimensions,
            Algorithm:            algorithm?.GetType().Name.Replace("AlgorithmConfiguration", ""),
            Metric:               metric,
            M:                    m,
            EfConstruction:       efConstruction,
            EfSearch:             efSearch,
            Compression:          compression?.GetType().Name.Replace("Compression", ""),
            Vectorizer:           vectorizer?.GetType().Name.Replace("Vectorizer", ""),
            VectorizerModel:      openAi?.Parameters?.ModelName?.ToString(),
            VectorizerDeployment: openAi?.Parameters?.DeploymentName,
            ConfiguredModelName:  configuredModelName,
            ReadAtUtc:            readAtUtc);
    }
}
