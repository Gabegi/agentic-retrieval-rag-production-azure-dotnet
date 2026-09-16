# AgenticRagApp.Api

The App Service host for the query side: an ASP.NET Core minimal API that serves `POST /api/query`
over the same `IRagQueryService` the Functions host uses, writes the same per-query report, and
returns the same JSON. Built for `infra/app_service.tf`'s `con-app-api-cap-<env>-we-001`, which
was provisioned for a split-out query API and had nothing to run (D188 §6). Intended consumer: an
OutSystems frontend outside Azure, via the OpenAPI document. Deployed by `pipeline.yml`'s
`deploy_api` job (standalone copy: `.pipelines/base/deploy-api.yml`); the deploy ends with a
`GET /health` probe, so a host that fails fast on a missing setting fails the stage. Design,
verification and the open items (auth, route in from OutSystems):
`docs/2609/260916/query-api-project.md` (D198).

```
Program.cs                 WebApplication host: AddAgenticRagAppInfrastructure(functionsHost: false),
                           OpenTelemetry (logs/traces/metrics -> App Insights, + ASP.NET Core request
                           instrumentation), IRunReportWriter on pipeline-reports, AddQuerying,
                           ProblemDetails, OpenAPI, health check
Endpoints/QueryEndpoint.cs POST /api/query -> IRagQueryService -> QueryResponse; writes a QueryRunReport
Properties/launchSettings.json  http://localhost:5080, Development
```

## Endpoints

| Endpoint | What it does |
|---|---|
| `POST /api/query` `{ "question": "…" }` | Same contract as the Functions host's `Query` function: `200` with `QueryResponse` (`answer`, `category`, `sources[]`, `telemetry`); `400` `ValidationProblemDetails` when `question` is missing or blank, or the body is not JSON; `500` `ProblemDetails` with a fixed title when the query fails (the exception is logged, not returned) |
| `GET /openapi/v1.json` | OpenAPI 3.0 document of the above, for the OutSystems "consume REST API from specification" import |
| `GET /health` | Liveness (`Healthy`); no dependency probes. For App Service's health check and a first connectivity test from OutSystems |

**Why POST and not GET** - the question is free text that can carry personal data (criterion 5),
and a GET puts it in the URL and therefore in every access log between the client and the app; a
query also costs a knowledge-base retrieval plus synthesis and writes a report, which nothing
(cache, prefetcher, retrying proxy) should repeat on its own. Reasoning in `QueryEndpoint.cs`.

## What is shared with the Functions host

- `AgenticRagApp.Querying/Models/QueryResponse.cs` — the wire shape, JSON names pinned by attribute;
  `QueryingFunction` maps through the same `From`.
- `AgenticRagApp.Querying/Services/QueryRunReportFactory.cs` — blob path + field mapping of the
  per-query report (`pipeline-reports/queries/{yyyy}/{MM}/{dd}/{HH-mm-ss}.json`).
- `AgenticRagApp.Infrastructure`'s `AddAgenticRagAppInfrastructure(configuration, functionsHost: false)`
  — the same clients and `IndexerConfig`, minus the Functions-only `AzureWebJobsStorage` requirement
  and the `pipeline-temp` container nothing on the query side reads.

## Configuration

Environment variables (App Service app settings), the same keys as the Functions host minus the
indexing-only ones — see the table in [RunningLocally.md](../../RunningLocally.md#configuration).
`IndexerConfig` still requires every `[Required]` key (including `OPENAI_EMBEDDING_DEPLOYMENT` and
`STORAGE_ACCOUNT_URL`, which the query path uses for the report container), so the App Service's
`app_settings` carry them all — `infra/app_service.tf`, with a comment per non-obvious key.
`ASPNETCORE_ENVIRONMENT=Development` turns on the console exporters, like `DOTNET_ENVIRONMENT`
does on the Functions host.

No authentication in code. The App Service is private-endpoint-only, so nothing outside the VNet
reaches it today; before it is opened to OutSystems an auth layer has to be chosen (D198 §4).

## Run locally

```
dotnet run --project src/AgenticRagApp.Api
curl -s -X POST http://localhost:5080/api/query -H "Content-Type: application/json" -d '{"question":"Wat is het beleid rond medicatie?"}'
curl -s http://localhost:5080/openapi/v1.json
```

Needs the same `az login` + IP allow-listing as the Functions host ([RunningLocally.md](../../RunningLocally.md)).

## Tests

`src/UnitTests/AgenticRagApp.Api.Tests` (MSTest, `QueryEndpointTests` — the handler, case for case
with `QueryingFunctionTests`). The contract itself is pinned in
`src/UnitTests/AgenticRagApp.Querying.Tests/QueryResponseTests.cs`. The framework half (malformed
JSON → 400, routing, ProblemDetails bodies, the OpenAPI document) is not unit-tested; it was
checked by running the app — D198 §3.
