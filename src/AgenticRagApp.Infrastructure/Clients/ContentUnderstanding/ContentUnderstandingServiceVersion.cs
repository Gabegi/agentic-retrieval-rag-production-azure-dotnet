using Azure.AI.ContentUnderstanding;
using Azure.Core;
using Azure.Core.Pipeline;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

// The Content Understanding REST api-version this app talks to, plus the one pipeline policy
// every CU client needs. Same reasoning as SearchServiceVersion (Clients/Search/): pinned rather
// than left to whatever the referenced package's latest happens to be, so a routine package bump
// cannot silently change the wire protocol with nothing to review.
//
// V2025_11_01 is currently the only member of the enum, so the pin costs nothing today. That is
// exactly why it is worth writing now: the first version that adds a member is the one that would
// otherwise move the default underneath us, and by then nobody is looking. 2026-06-01-preview
// carries agentic workflow, document metadata, signatures and the sync Read/Layout endpoints -
// none of which this pipeline needs, all of which are preview.
// See docs/2608/260819/content-understanding-when-and-how.md:244.
public static class ContentUnderstandingServiceVersion
{
    public const ContentUnderstandingClientOptions.ServiceVersion Current =
        ContentUnderstandingClientOptions.ServiceVersion.V2025_11_01;

    // Fresh instance per client, like SearchServiceVersion.Options(): ClientOptions is mutable and
    // Azure SDK clients take ownership of the options they are constructed with, so sharing one
    // would let a later mutation leak between clients.
    //
    // The utf16 policy rides along here rather than at each call site because forgetting it is
    // silent - offsets simply drift on any document carrying a non-BMP character, and nothing
    // fails. Attaching it to the options means every client built through this method has it.
    public static ContentUnderstandingClientOptions Options()
    {
        var options = new ContentUnderstandingClientOptions(Current);
        options.AddPolicy(new Utf16StringEncodingPolicy(), HttpPipelinePosition.PerCall);
        return options;
    }
}
