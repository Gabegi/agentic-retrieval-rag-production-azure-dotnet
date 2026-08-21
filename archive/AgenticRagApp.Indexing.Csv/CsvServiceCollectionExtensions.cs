using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Csv;

// All of CSV's DI registrations live here, self-contained, so the Functions host
// (AgenticRagApp.FunctionApp/Program.cs) only ever needs one line —
// services.AddCsvIndexing() — to wire the whole pipeline in. Not called from
// Program.cs yet: CSV has no active Durable flow today. Assumes the host has already
// called AgenticRagApp.Infrastructure's AddAgenticRagAppInfrastructure() (BlobServiceClient,
// IndexerConfig, SearchClient/SearchIndexClient, the "pipeline-temp" keyed
// BlobContainerClient, IEmbeddingGenerator<string, Embedding<float>>) — see
// AgenticRagApp.Common for the remaining source-agnostic seam types (ExtractionDocument/
// ExtractionOutput/etc.) and IRunReportWriter.
public static class CsvServiceCollectionExtensions
{
    public static IServiceCollection AddCsvIndexing(this IServiceCollection services)
    {
        // CSV extraction pipeline stages - stateless, so singletons are safe.
        services.AddSingleton<Services.ICsvExtractor,     Services.CsvExtractor>();
        services.AddSingleton<Services.ICsvJoiner,        Services.CsvJoiner>();
        services.AddSingleton<Services.IDataCleaner,      Services.DataCleaner>();
        services.AddSingleton<Services.IPipelineValidator, Services.PipelineValidator>();

        services.AddSingleton<Services.ICsvExtractionOrchestrator>(sp => new Services.CsvExtractionOrchestrator(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredKeyedService<BlobContainerClient>("pipeline-temp"),
            sp.GetRequiredService<IBlobStore>(),
            sp.GetRequiredService<IRunReportWriter>(),
            sp.GetRequiredService<Services.ICsvExtractor>(),
            sp.GetRequiredService<Services.ICsvJoiner>(),
            sp.GetRequiredService<Services.IDataCleaner>(),
            sp.GetRequiredService<Services.IPipelineValidator>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.CsvExtractionOrchestrator>>()));

        // Chunking
        services.AddSingleton<Services.ICsvChunkingStrategy, Services.ChunkingStrategy1>();
        services.AddSingleton<Services.ICsvChunkingService,  Services.CsvChunkingService>();

        // Extraction diff / embedding / upload / index pipeline
        services.AddSingleton<Services.ICsvExtractionService,      Services.CsvExtractionService>();
        services.AddSingleton<Services.ICsvEmbeddingService,       Services.CsvEmbeddingService>();
        services.AddSingleton<Services.ICsvUploadService,          Services.CsvUploadService>();
        // IIndexService/IIndexDocumentService are registered once by
        // AgenticRagApp.Infrastructure's AddAgenticRagAppInfrastructure() — shared with
        // PDF, since both write into the same Search index.

        return services;
    }
}
