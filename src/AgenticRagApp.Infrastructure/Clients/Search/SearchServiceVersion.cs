using Azure.Search.Documents;

namespace AgenticRagApp.Infrastructure.Clients.Search;

// The Azure AI Search REST api-version every client in this app talks to.
//
// Pinned rather than left to the SDK default, which is whatever
// SearchClientOptions.LatestVersion happens to be in the referenced package - so a routine
// bump of Azure.Search.Documents silently changes the wire protocol with no code change and
// nothing to review. That matters more than usual here because the knowledge-base surface is
// still on preview api-versions and has already been renamed once: 11.8.0-beta.1 moved the
// resource path from /agents to /knowledgebases, and the two generations project the same
// resource differently. Reading a knowledge base through the older generation returns it
// stripped of every property the newer one added, which is exactly what made the 2026-08-11
// eval failure look like data loss when nothing had been lost.
// See docs/2608/260812/knowledgebasefix-action-plan.md §1.2.
//
// cor-srch-cap-dev-we-001 was probed on 2026-08-12 and serves 2025-11-01-preview, 2026-04-01
// and 2026-05-01-preview on /knowledgebases (and only 2025-08-01-preview on the retired
// /agents path). This pin is therefore behind what the service supports, deliberately: it is
// the version the app has been running and evaluated against. Moving it forward is a
// deliberate change to be made and measured on its own, not a side effect of a package update.
public static class SearchServiceVersion
{
    public const SearchClientOptions.ServiceVersion Current = SearchClientOptions.ServiceVersion.V2025_11_01_Preview;

    // Fresh instance per client: SearchClientOptions is mutable and Azure SDK clients take
    // ownership of the options they're constructed with, so sharing one across clients would
    // let a later mutation leak between them.
    public static SearchClientOptions Options() => new(Current);
}
