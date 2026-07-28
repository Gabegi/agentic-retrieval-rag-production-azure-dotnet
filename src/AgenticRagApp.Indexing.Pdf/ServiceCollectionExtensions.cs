using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf;

// All of PDF's DI registrations live here, self-contained, so the Functions host
// (AgenticRagApp.FunctionApp/Program.cs) only ever needs one line —
// services.AddPdfIndexing() — to wire the whole pipeline in. Assumes the host has
// already called AgenticRagApp.Infrastructure's AddAgenticRagAppInfrastructure()
// (BlobServiceClient, IndexerConfig, SearchClient/SearchIndexClient, the
// "pipeline-temp" keyed BlobContainerClient, IEmbeddingGenerator<string,
// Embedding<float>>) and registered IBlobStore/IRunReportWriter from Observability.
public static class ServiceCollectionExtensions
{
    // Takes the IndexerConfig the host already built via AddAgenticRagAppInfrastructure()
    // so the Document Intelligence conditional registration below doesn't need to
    // resolve a temporary provider mid-registration.
    public static IServiceCollection AddPdfIndexing(this IServiceCollection services, IndexerConfig config)
    {
        services.AddSingleton<IChunkingStrategy, PdfChunkingStrategy2>();
        services.AddSingleton<IChunkingService,  ChunkingService>();

        // PDF extraction backend — only registered when Document Intelligence is
        // configured (Infrastructure only registers the DocumentIntelligenceClient itself
        // under the same condition); PdfExtractionPipeline resolves it explicitly by
        // Name below rather than via GetRequiredService, so a future second backend can't
        // silently change which one gets picked.
        if (!string.IsNullOrWhiteSpace(config.DocumentIntelligenceEndpoint))
        {
            services.AddSingleton<PdfDocumentIntelligenceAnalyzer>();
            services.AddSingleton<IPdfExtractor, PDFExtractor>();
        }
        services.AddSingleton<IPdfCleaner,           PdfCleaner>();
        services.AddSingleton<IPdfPipelineValidator, PdfPipelineValidator>();

        services.AddSingleton<IExtractionOrchestrator>(sp => new PdfExtractionPipeline(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredKeyedService<BlobContainerClient>("pipeline-temp"),
            sp.GetRequiredService<IBlobStore>(),
            sp.GetRequiredService<IRunReportWriter>(),
            sp.GetServices<IPdfExtractor>().Single(e => e.Name == "DocumentIntelligence"),
            sp.GetRequiredService<IPdfCleaner>(),
            sp.GetRequiredService<IPdfPipelineValidator>(),
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<ILogger<PdfExtractionPipeline>>()));

        services.AddSingleton<IExtractionService>(sp => new ExtractionService(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredService<IBlobStore>(),
            sp.GetRequiredService<IExtractionOrchestrator>(),
            sp.GetRequiredService<IIndexDocumentService>(),
            sp.GetRequiredService<IRunReportWriter>(),
            sp.GetRequiredService<ILogger<ExtractionService>>()));
        services.AddSingleton<IEmbeddingService,       EmbeddingService>();
        services.AddSingleton<IUploadService,          UploadService>();
        // IIndexService/IIndexDocumentService are registered once by
        // AgenticRagApp.Infrastructure's AddAgenticRagAppInfrastructure() — shared with
        // CSV, since both write into the same Search index.

        // Content-hash-keyed embedding vector cache — "pipeline-artifacts" container,
        // under its own vector-cache/ path prefix (see VectorCache).
        services.AddSingleton<IVectorCache>(sp =>
            new VectorCache(
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-artifacts")));

        // Index-recovery path — reads Observability's rolling snapshot (registered by the
        // host, see Program.cs) instead of re-extracting from source.
        services.AddSingleton<IRestoreService, RestoreService>();

        return services;
    }
}
