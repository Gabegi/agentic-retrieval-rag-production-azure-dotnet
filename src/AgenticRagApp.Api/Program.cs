using System.Text.Json.Serialization;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Blobs;
using Microsoft.OpenApi;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using AgenticRagApp.Api.Endpoints;
using AgenticRagApp.Api.Security;
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
// The document also has to DECLARE the bearer scheme, or the OutSystems import produces a
// client that never sends the header and gets a 401 on every call - the generator only emits
// what the specification says. BearerSecuritySchemeTransformer adds the scheme and requires it
// on the query operation; /health and /openapi itself stay unsecured, matching the filter.
builder.Services.AddOpenApi(options =>
{
    options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});

// Liveness only - no dependency probes, so a Search or Foundry hiccup does not get the instance
// recycled by App Service's health check.
builder.Services.AddHealthChecks();

// Inbound auth, interim (2026-09-24, D238 §2): one static shared secret on
// `Authorization: Bearer <token>`, applied to POST /api/query only. It replaces "no
// authentication at all" - which was defensible while the App Service was reachable only from
// inside the network and dev_allowed_ips, and stops being defensible the moment an external
// organisation is let in. It is NOT the answer to D198 §4.4: a shared token authenticates the
// secret, not the caller. Entra app-to-app auth is the plan (D238 §4); this is what holds until
// it lands.
//
// Fail fast rather than default to open. A missing key means the app setting was not deployed,
// and an API that silently serves without auth because its configuration is incomplete is the
// exact failure this change exists to prevent. Terraform owns the value
// (random_password.query_api_key -> the QUERY_API_KEY app setting in app_service.tf); nothing
// reads it from source.
var queryApiKey = builder.Configuration["QUERY_API_KEY"];
if (string.IsNullOrWhiteSpace(queryApiKey))
    throw new InvalidOperationException(
        "QUERY_API_KEY is required. Terraform sets it from random_password.query_api_key " +
        "(infra/app_service.tf); for a local run, set it in the environment.");

builder.Services.AddSingleton(new ApiKeyGuard(queryApiKey));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapQueryEndpoints();

app.Run();
