using Azure.Core;
using Azure.Core.Pipeline;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

// Forces stringEncoding=utf16 onto every Content Understanding analyze request.
//
// Why a policy and not a parameter: the typed AnalyzeBinaryAsync overload - the one that returns
// Operation<AnalysisResult> rather than Operation<BinaryData> - has no stringEncoding parameter.
// Its bare string parameter is contentType. stringEncoding exists only on the protocol overload,
// and taking that route would mean hand-rolling BinaryData deserialization and losing the typed
// LRO for one query parameter.
//
// Why it matters: CU defaults spans to 'codePoint', which counts Unicode scalar values. .NET
// strings are UTF-16 code units, and every Offset in Indexing.CU's Models/Extraction/Structure/
// (Heading, TableInfo, FigureInfo, LineInfo, SectionSpan) is an anchor into the markdown string
// that Substring and HeadingLocator index directly. The two agree exactly until a document
// contains a character outside the BMP, at which point every anchor past it drifts by one per
// surrogate pair - silently, with no error and no failed document.
//
// This is verifiable rather than hopeful: AnalysisResult.StringEncoding echoes back what the
// service actually applied, and PdfContentUnderstandingAnalyzer's validation asserts it. If that
// assertion ever fires, read the note on the path match below first.
internal sealed class Utf16StringEncodingPolicy : HttpPipelineSynchronousPolicy
{
    // 'codePoint' (the default), 'utf16' and 'utf8' are the accepted values as of api-version
    // 2025-11-01 - see the stringEncoding parameter on the protocol AnalyzeBinaryAsync overload.
    private const string Utf16 = "utf16";

    public override void OnSendingRequest(HttpMessage message)
    {
        var uri = message.Request.Uri;

        // Substring match, not a segment-end match, and deliberately so: this must catch
        // :analyzeBatch as well as :analyze. A batch submission is how a document over CU's
        // 300-page async cap gets sliced, and a sliced document is precisely where a drifting
        // offset would be hardest to spot.
        //
        // Note what this does NOT match: the analyzer PUT (/analyzers/{id}) and the LRO
        // status/result URL, neither of which carries a colon in its path. The former is correct -
        // stringEncoding is meaningless on a control-plane write. The latter is an assumption:
        // it holds only if CU resolves span offsets at submit time. If AnalysisResult.StringEncoding
        // ever comes back 'codePoint' despite this policy, the result fetch is the first place to
        // look, and the fix is to widen this match - not to change the literal above.
        if (!uri.Path.Contains(":analyze", StringComparison.Ordinal)) return;

        // Idempotent: PerCall already means this runs once per logical request rather than once
        // per retry, so this guards against a future SDK version starting to send the parameter
        // itself, not against our own retries.
        if (uri.Query.Contains("stringEncoding", StringComparison.OrdinalIgnoreCase)) return;

        uri.AppendQuery("stringEncoding", Utf16);
    }
}
