using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AgenticRagApp.Api.Security;

// Declares the interim bearer scheme in the OpenAPI document (2026-09-24, D238 §2).
//
// Why a transformer and not an attribute: the check is an IEndpointFilter, not ASP.NET Core
// authentication, so the generator has no authentication scheme to discover and emits a document
// with no `securitySchemes` at all. An importer generates exactly what the document says, so
// without this the OutSystems client would never send the header and every call would 401.
//
// Scheme "bearer" with no bearerFormat: the token is an opaque shared secret, not a JWT, and
// claiming `bearerFormat: JWT` would be a lie a consumer might act on. The requirement is
// attached to POST /api/query only, matching where the filter is applied - /health and the
// document itself stay open.
//
// Pinned to OpenAPI 3.0 like the rest of the document (Program.cs): `type: http` +
// `scheme: bearer` is valid in 3.0 and is what the OutSystems 11 importer understands.
public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeName = "bearerAuth";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Components               ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type        = SecuritySchemeType.Http,
            Scheme      = "bearer",
            Description =
                "Shared secret issued per consuming organisation, sent as " +
                "`Authorization: Bearer <token>`. Interim scheme - it authenticates the secret, " +
                "not the caller, and is to be replaced by Entra app-to-app auth.",
        };

        // OpenAPI.NET v2 dropped the `Reference` property on the scheme itself; a requirement
        // now points at the component through a dedicated reference type.
        var reference = new OpenApiSecuritySchemeReference(SchemeName, document);

        // Only the operation the filter guards. A document-level `security` entry would claim
        // /health needs a token too, and the first thing a consumer does is call /health.
        foreach (var operation in document.Paths
                     .Where(path => path.Key.Equals("/api/query", StringComparison.OrdinalIgnoreCase))
                     .Where(path => path.Value.Operations is not null)
                     .SelectMany(path => path.Value.Operations!.Values))
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement { [reference] = [] });
        }

        return Task.CompletedTask;
    }
}
