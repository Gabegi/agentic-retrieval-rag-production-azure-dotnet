using Azure.Core;
using Azure.Core.Pipeline;

namespace AgenticRagApp.Infrastructure.Clients.Search;

// How many bytes this app sends to Azure AI Search, counted where they leave (2026-09-18, D203
// §8). The upload was 28-31 s on every force run of the day - 58% of embed_upload after the cache
// work - in four uniform batches, and the one input nobody had measured was the payload: the SDK
// serialises inside UploadDocumentsAsync, so nothing above it can see the size. A pipeline
// policy on the SearchClient can: it sees every request's content before it is sent.
//
// Plain counters, no meter. This project cannot reference Observability, and the meter would go
// to Azure Monitor where nobody on the pipeline can read it (D203 §6c); IndexDocumentService
// reads the counter before and after its batch loop and puts the delta on the report.
//
// Two buckets by request path. `docs/index` (the REST name; the SDK writes it `docs/search.index`)
// is the document push API - upload, merge, delete - and is the only bucket the report carries.
// Everything else (queries, statistics, index definitions) is counted so it can be told apart,
// not because anyone reads it yet.
public sealed class SearchRequestByteCounter
{
    private long _indexDocsBytes, _indexDocsRequests, _indexDocsUnmeasured, _otherBytes, _otherRequests;

    public readonly record struct Snapshot(
        long IndexDocsBytes,
        long IndexDocsRequests,
        // Requests whose content length the SDK could not compute up front. Non-zero means
        // IndexDocsBytes is an undercount for the window, and a reader must treat it as unknown.
        long IndexDocsUnmeasured,
        long OtherBytes,
        long OtherRequests);

    public void Record(string requestPath, long? contentLength)
    {
        if (IsIndexDocsPath(requestPath))
        {
            Interlocked.Increment(ref _indexDocsRequests);
            if (contentLength is { } n) Interlocked.Add(ref _indexDocsBytes, n);
            else                        Interlocked.Increment(ref _indexDocsUnmeasured);
        }
        else
        {
            Interlocked.Increment(ref _otherRequests);
            if (contentLength is { } n) Interlocked.Add(ref _otherBytes, n);
        }
    }

    public Snapshot Read() => new(
        Volatile.Read(ref _indexDocsBytes),
        Volatile.Read(ref _indexDocsRequests),
        Volatile.Read(ref _indexDocsUnmeasured),
        Volatile.Read(ref _otherBytes),
        Volatile.Read(ref _otherRequests));

    // Both spellings the service accepts for the same operation. Case-insensitive because the
    // SDK has changed the casing of path segments across versions before.
    public static bool IsIndexDocsPath(string path) =>
        path.Contains("/docs/index", StringComparison.OrdinalIgnoreCase)
     || path.Contains("/docs/search.index", StringComparison.OrdinalIgnoreCase);
}

// The policy that feeds the counter. PerCall, not PerRetry: a retried batch is the same
// payload sent again, and the question is what the payload weighs, not how often the transport
// had to carry it. Synchronous because it only reads a length; it never touches the body.
public sealed class SearchRequestSizePolicy : HttpPipelineSynchronousPolicy
{
    private readonly SearchRequestByteCounter _counter;

    public SearchRequestSizePolicy(SearchRequestByteCounter counter) => _counter = counter;

    public override void OnSendingRequest(HttpMessage message)
    {
        long? length = message.Request.Content is { } content && content.TryComputeLength(out var n) ? n : null;
        _counter.Record(message.Request.Uri.Path, length);
    }
}
