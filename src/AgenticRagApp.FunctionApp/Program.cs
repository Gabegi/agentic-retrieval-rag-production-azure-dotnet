using Azure.Communication.Email;
using Azure.Identity;
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
using AgenticRagApp.Functions.ReportEmail;
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

        // Persistent full-content archive - separate container from pipeline-reports above,
        // always on (not gated by environment or a config flag - see IPipelineArtifactWriter).
        services.AddSingleton<IPipelineArtifactWriter>(sp =>
            new PipelineArtifactWriter(
                sp.GetRequiredService<IBlobStore>(),
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-artifacts")));

        // Rolling full-corpus snapshot (source-scoped, never merged across doc types) - same
        // "pipeline-artifacts" container, under its own snapshots/{source}/ path prefix.
        // Vector-cache eviction is done by the caller (IndexingFunction), not here - see
        // ISnapshotService's own comment for why.
        services.AddSingleton<ISnapshotService>(sp =>
            new SnapshotService(
                sp.GetRequiredService<IBlobStore>(),
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-artifacts"),
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

        // ── Per-run report email ─────────────────────────────────────────────
        // One indexing/restore run -> one email. See ReportEmail/ and
        // docs/2608/260807/pipeline-run-email-report.md.
        var reportEmailOptions = new ReportEmailOptions();
        Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(
            ctx.Configuration.GetSection(ReportEmailOptions.SectionName), reportEmailOptions);
        services.AddSingleton(reportEmailOptions);

        services.AddSingleton<RunEmailRenderer>();
        services.AddSingleton<RunAnalysisAgent>();

        services.AddSingleton(sp => new RunReportAssembler(
            sp.GetRequiredService<IBlobStore>(),
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-reports"),
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(config.StorageContainer),
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("eval-results"),
            reportEmailOptions,
            sp.GetRequiredService<ILogger<RunReportAssembler>>()));

        // Registered even when the feature is disabled or unconfigured: the Functions host
        // resolves a function's constructor dependencies before the function body runs, so a
        // missing registration would fail the invocation rather than letting the body no-op
        // with the informational log it's written to emit.
        services.AddSingleton<IReportEmailSender>(sp =>
        {
            if (string.IsNullOrWhiteSpace(reportEmailOptions.AcsEndpoint))
                return new NoOpReportEmailSender(sp.GetRequiredService<ILogger<NoOpReportEmailSender>>());

            return new AcsEmailSender(
                new EmailClient(new Uri(reportEmailOptions.AcsEndpoint), new DefaultAzureCredential()),
                reportEmailOptions,
                sp.GetRequiredService<ILogger<AcsEmailSender>>());
        });

        // No registration for SendReportEmailActivity itself here, deliberately - a
        // services.AddSingleton<SendReportEmailActivity>(...) was tried first and had no
        // effect in production (2026-08-07): the isolated-worker Functions host activates
        // [Function] classes via ActivatorUtilities.CreateInstance(scopedProvider, type), which
        // resolves each CONSTRUCTOR PARAMETER from the container directly and never consults a
        // registration for the class type itself. The actual fix is on the class - its
        // constructor now takes BlobServiceClient (registered above, in
        // AddAgenticRagAppInfrastructure) and derives the "pipeline-reports" container itself,
        // instead of asking for an unkeyed BlobContainerClient that nothing here ever supplies.
        // See SendReportEmailActivity's constructor comment for the full explanation.
    })
    .Build();

await host.RunAsync();
