using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;
using AgenticRagApp.Infrastructure.Clients.Language;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Indexing.CU.Utils;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.CU;

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
    public static IServiceCollection AddIndexing(this IServiceCollection services, IndexerConfig config)
    {
        // Two-axis chunking (docs/2608/260812/chunking_flow_summary.md).
        //
        // Axis 2 - the leaf splitter, chosen per block inside a section.

        // Axis 1 - routing. HeadingSectionGate decides each document's route from its declared
        // structure (a static read, nothing to register); ChunkingService dispatches that route
        // onto one of two real strategies in Services/Chunking/ChunkingStrategies.
        services.AddSingleton<DeclaredBoundaryStrategy>();
        services.AddSingleton<RecursiveStrategy>();

        // Step 4 - what a cut becomes once it is cut. Stateless, like everything else here: it
        // reads a document and writes onto the chunks it is handed, and keeps nothing between
        // calls.
        services.AddSingleton<ChunkMetadataBuilder>();

        // Step 5 - everything the stage says about itself: the log lines, the OpenTelemetry
        // counters and the chunking-artifact blob. Registered rather than newed up because it
        // writes through IPipelineArtifactWriter, which the host owns.
        services.AddSingleton<ChunkingReporter>();

        services.AddSingleton<IChunkingService, ChunkingService>();

        // IDocumentIdentityStore, the corpus-wide family/domain identity store this depends on,
        // is registered by AddAgenticRagAppInfrastructure() - it is a storage client, so it
        // lives with the other ones rather than here.
        services.AddSingleton<DocumentIdentityResolver>();

        // Extraction runs on Content Understanding, and only on Content Understanding - there is
        // no second backend to fall back to since the Document Intelligence path was removed.
        //
        // Fail fast, at registration, rather than at the first analyze call: a missing endpoint
        // is a deployment mistake, and the alternative is a run that lists the corpus, diffs it,
        // and only then discovers it cannot extract anything. Infrastructure registers
        // IContentAnalysisClient under this same condition, so this throw is also what
        // guarantees the resolve below succeeds.
        if (string.IsNullOrWhiteSpace(config.ContentUnderstandingEndpoint))
            throw new InvalidOperationException(
                "CONTENT_UNDERSTANDING_ENDPOINT is not configured. Document extraction runs on Content " +
                "Understanding and has no fallback backend.");

        // One-time setup at host startup: verify the account-wide default model->deployment
        // mapping the prebuilt analyzer resolves against, and write it only if missing/wrong.
        // Registered here rather than by AddAgenticRagAppInfrastructure because only the
        // indexing side needs the mapping - the class itself lives in Infrastructure with the
        // raw client it uses.
        services.AddHostedService<ContentUnderstandingDefaultsSetup>();

        // The pre-extraction diff (container listing + index state + comparison), split out of
        // ExtractionService so the decision logic is testable on its own.
        services.AddSingleton<IIndexDiffService>(sp => new IndexDiffService(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredService<IBlobStore>(),
            sp.GetRequiredService<IIndexDocumentService>(),
            sp.GetRequiredService<ILogger<IndexDiffService>>()));

        // Everything the extraction stage says about itself: counters, log lines and the report
        // blobs. Registered rather than newed up for the same reason ChunkingReporter is - it
        // writes through IRunReportWriter, which the host owns.
        services.AddSingleton<ExtractionReporter>();

        // The two containers are different and both positional: "documents" is what the per-file
        // extraction downloads from, "pipeline-temp" is where the run-state baseline lives.
        services.AddSingleton<IExtractionService>(sp => new ExtractionService(
            sp.GetRequiredService<IIndexDiffService>(),
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredService<IContentAnalysisClient>(),
            sp.GetRequiredKeyedService<BlobContainerClient>("pipeline-temp"),
            sp.GetRequiredService<IBlobStore>(),
            sp.GetRequiredService<ExtractionReporter>(),
            sp.GetRequiredService<ILogger<ExtractionService>>(),
            cuDefaultsState: sp.GetRequiredService<ContentUnderstandingDefaultsState>(),
            // The index's `language` producer (2026-09-08). Registered by
            // AddAgenticRagAppInfrastructure over the TextAnalyticsClient the query side
            // already uses; CU itself reports no language - see IDocumentLanguageDetector.
            languageDetector: sp.GetRequiredService<IDocumentLanguageDetector>()));
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
