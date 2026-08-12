using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Indexing.Pdf;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Querying;
using System.Text;

// Required for PdfCleaner's Windows-1252 mojibake repair (Encoding.GetEncoding(1252)) -
// code pages beyond the built-in set aren't available on .NET Core+ without this.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureLogging((ctx, logging) =>
    {
        var appInsightsConnectionString = ctx.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
        {
            logging.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes           = true;
                options.AddAzureMonitorLogExporter(o => o.ConnectionString = appInsightsConnectionString);
                // Temporary diagnostic, dev-only: prints every exported log record to stdout
                // (visible in the Kudu log stream) so we can confirm the OpenTelemetry pipeline
                // itself is generating/processing records even if the Azure Monitor HTTP export
                // is the part silently failing. Remove once App Insights ingestion is confirmed working.
                if (ctx.HostingEnvironment.IsDevelopment())
                    options.AddConsoleExporter();
            });
        }
    })
    .ConfigureServices((ctx, services) =>
    {
        // Config validation, IndexerConfig, credential, and every Azure SDK client this
        // app talks to (Blob, Search, OpenAI, Document Intelligence) are registered here.
        var config = services.AddAgenticRagAppInfrastructure(ctx.Configuration);

        var appInsightsConnectionString = ctx.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]!;
        var isDevelopment                = ctx.HostingEnvironment.IsDevelopment();

        services.AddOpenTelemetry()
            .UseFunctionsWorkerDefaults()
            .ConfigureResource(r => r.AddService(
                serviceName:    "zenya-indexer",
                serviceVersion: "1.0.0"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource("Microsoft.Extensions.AI")
                    .AddSource(Instrumentation.ActivitySourceName)
                    // Azure SDK clients (Blob, Search, OpenAI, Document Intelligence) emit their
                    // own dependency spans under this source - without it, HTTP calls to those
                    // services (including failures like the 403s) never reach App Insights.
                    .AddSource("Azure.*")
                    .AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnectionString);
                // Temporary diagnostic, dev-only, same reasoning as the logging console exporter
                // above - remove once App Insights ingestion is confirmed working.
                if (isDevelopment)
                    tracing.AddConsoleExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("Microsoft.Extensions.AI")
                    .AddMeter(Instrumentation.MeterName)
                    .AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnectionString);
                if (isDevelopment)
                    metrics.AddConsoleExporter();
            });

        services.AddSingleton<IRunReportWriter>(sp =>
            new RunReportWriter(
                sp.GetRequiredService<IBlobStore>(),
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-reports")));

        // Persistent full-content archive - same "pipeline-reports" container as every other
        // report (run reports, stage diagnostics, snapshots, eval results), always on (not
        // gated by environment or a config flag - see IPipelineArtifactWriter). Only
        // vector-cache/ (VectorCache, wired in AgenticRagApp.Indexing.Pdf/ServiceCollectionExtensions.cs)
        // stays on the separate "pipeline-artifacts" container - it's a content-hash-keyed
        // cache, not a report, and moving it would turn its O(1) lookups into O(n) listings.
        services.AddSingleton<IPipelineArtifactWriter>(sp =>
            new PipelineArtifactWriter(
                sp.GetRequiredService<IBlobStore>(),
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-reports")));

        // Rolling full-corpus snapshot (source-scoped, never merged across doc types) - same
        // "pipeline-reports" container as every other report, found via a
        // _latest-snapshot-{source}.json pointer rather than a per-source path prefix (see
        // SnapshotService). Vector-cache eviction is done by the caller (IndexingFunction), not
        // here - see ISnapshotService's own comment for why.
        services.AddSingleton<ISnapshotService>(sp =>
            new SnapshotService(
                sp.GetRequiredService<IBlobStore>(),
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-reports"),
                sp.GetRequiredService<ILogger<SnapshotService>>()));

        // Index size telemetry + drift-check, source-scoped (see IIndexStatsMonitor) — one
        // instance shared by PDF's and CSV's own UploadService.
        services.AddSingleton<IIndexStatsMonitor, IndexStatsMonitor>();

        // Querying — reads the one shared Search index, doc-type-agnostic. See
        // AgenticRagApp.Querying/ServiceCollectionExtensions.cs for what this wires in.
        services.AddQuerying();

        // PDF indexing pipeline — extraction, chunking, embedding, upload, index. See
        // AgenticRagApp.Indexing.Pdf/ServiceCollectionExtensions.cs for what this wires in.
        services.AddPdfIndexing(config);
    })
    .Build();

await host.RunAsync();
