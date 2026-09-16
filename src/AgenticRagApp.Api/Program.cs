using System.Text.Json.Serialization;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Blobs;
using Microsoft.OpenApi;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using AgenticRagApp.Api.Endpoints;
using AgenticRagApp.Infrastructure;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Querying;

// The App Service host for the query side (infra/app_service.tf, con-app-api-*): the same
// composition as AgenticRagApp.FunctionApp/Program.cs minus everything indexing - no Durable,
// no AddIndexing, no artifact/snapshot writers, no run analysis. What it registers is exactly
// what POST /api/query needs and nothing the Functions host does not also register, so the two
// hosts answer from the same service over the same clients.

var builder = WebApplication.CreateBuilder(args);

// Configuration is environment variables (App Service app settings in Azure), the same keys as
// the Functions host - CreateBuilder would also read appsettings*.json, but there is none, on
// purpose (RunningLocally.md). Config validation, IndexerConfig, the credential and every Azure
// SDK client are registered here. functionsHost: false drops the one Functions-only requirement
// (AzureWebJobsStorage, Durable's account) and the pipeline-temp container nothing on the query
// side reads.
builder.Services.AddAgenticRagAppInfrastructure(builder.Configuration, functionsHost: false);

var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]!;
var isDevelopment                = builder.Environment.IsDevelopment();

// Same three exporters as the Functions host, same sources and meters, plus the ASP.NET Core
// request instrumentation the Functions worker gets from UseFunctionsWorkerDefaults. Console
// exporters in Development only, as over there.
builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeFormattedMessage = true;
    options.IncludeScopes           = true;
    options.AddAzureMonitorLogExporter(o => o.ConnectionString = appInsightsConnectionString);
    if (isDevelopment)
        options.AddConsoleExporter();
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName:    "cap-query-api",
        serviceVersion: "1.0.0"))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddSource("Microsoft.Extensions.AI")
            .AddSource(Instrumentation.ActivitySourceName)
            // Azure SDK clients (Search, the knowledge base, Blob) emit their dependency spans
            // under this source - same reason as in the Functions host.
            .AddSource("Azure.*")
            .AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnectionString);
        if (isDevelopment)
            tracing.AddConsoleExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddMeter("Microsoft.Extensions.AI")
            .AddMeter(Instrumentation.MeterName)
            .AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnectionString);
        if (isDevelopment)
            metrics.AddConsoleExporter();
    });

// Per-query report -> pipeline-reports/queries/..., the same container and path the Functions
// host writes (QueryRunReportFactory), so the reports of both hosts land in one folder.
builder.Services.AddSingleton<IRunReportWriter>(sp =>
    new RunReportWriter(
        sp.GetRequiredService<IBlobStore>(),
        sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-reports")));

// Querying - reads the one shared Search index through the knowledge base. See
// AgenticRagApp.Querying/ServiceCollectionExtensions.cs for what this wires in.
builder.Services.AddQuerying();

// RFC 9457 problem+json bodies for every non-2xx the framework produces (malformed JSON -> 400,
// unknown route -> 404, unhandled exception -> 500); the endpoint's own 400/500 use the same
// shape via TypedResults.
builder.Services.AddProblemDetails();

// ASP.NET Core's Web defaults accept numbers written as JSON strings, and the OpenAPI generator
// documents that faithfully: every integer becomes anyOf [integer, string]. Nothing here sends
// numbers in, and the OutSystems importer maps types off this document, so numbers are numbers.
// The response is unaffected (this governs reading, not writing).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

// OpenAPI document at /openapi/v1.json, for the OutSystems "consume REST API from a specification"
// import. Pinned to 3.0 because that importer takes specs "compliant with the OpenAPI
// specification up to OAS 3.0" (OutSystems 11 docs, Consume one or more REST API methods,
// read 2026-09-16), and .NET 10 emits 3.1 by default.
builder.Services.AddOpenApi(options => options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0);

// Liveness only - no dependency probes, so a Search or Foundry hiccup does not get the instance
// recycled by App Service's health check.
builder.Services.AddHealthChecks();

// No authentication here, deliberately for now: the App Service is private-endpoint-only, and
// the auth layer for an OutSystems caller outside Azure (Easy Auth with Entra, or a bearer
// scheme in code) is an open decision - docs/2609/260916/query-api-project.md (D198) §4.

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapQueryEndpoints();

app.Run();
