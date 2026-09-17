using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents;

namespace RagApp.UnitTests.Infrastructure.Search;

// What Azure.Search.Documents does with a non-finite vector component, proven against the real
// SDK rather than reasoned about (D199 C1, 2026-09-17). The answer decides whether a NaN or an
// infinity in an embedding is a quiet bad row in the index or a loud failure, and it turned out
// to be the loudest thing in the pipeline: the whole upload batch dies before the service is
// contacted, and with it the run.
//
// Why here and not in Indexing.CU.Tests: that project mocks IIndexDocumentService, so no
// serialiser ever runs in it. The subject IS the SDK's JSON writer, so this needs a real
// SearchClient - built over a local HttpMessageHandler rather than Azure.Core.TestFramework,
// which is not referenced and whose addition would churn packages.lock.json and break the
// locked-mode restore CI depends on.
//
// The payload is a local type, not SearchUploadChunk: the claim is about float[] and the SDK's
// writer, not about our DTO, and Infrastructure deliberately does not reference Indexing.CU.
//
// What this does NOT answer: whether Azure AI Search STORES a right-width all-zero vector, or
// whether such a row is ever returned by a vector query. That is D199 C2 - it needs a live
// service, it is not runnable through this project's pipeline-only workflow, and it is withdrawn
// (D199 §2.2, §9). The all-zero case below proves only that all-zero SERIALISES, which is the
// easy thing to mistake for the question C2 asks.
[TestClass]
public class SearchSerializationTests
{
    private const string DocumentKey = "1";

    // The exact key the uploads below carry, so the canned response is a well-formed answer to
    // the request that was actually made.
    private const string IndexDocumentsResultBody =
        $$"""{"value":[{"key":"{{DocumentKey}}","status":true,"errorMessage":null,"statusCode":200}]}""";

    private sealed class ProbeChunk
    {
        [JsonPropertyName("id")]             public string  Id            { get; set; } = DocumentKey;
        [JsonPropertyName("content_vector")] public float[] ContentVector { get; set; } = [];
    }

    // Counts invocations rather than flagging them. With Retry.MaxRetries = 0 a control is
    // exactly one call, so a later slip in the canned response that made the SDK retry cannot
    // hide behind a boolean that is still "true".
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _invocations;

        public int Invocations => Volatile.Read(ref _invocations);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocations);

            // A real IndexDocumentsResult body, because UploadDocumentsAsync deserialises the
            // response. An empty body would fail the CONTROLS on this test double rather than on
            // the SDK, which would make them prove nothing.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(IndexDocumentsResultBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (SearchClient Client, CountingHandler Handler) BuildClient()
    {
        var handler = new CountingHandler();
        var options = new SearchClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        options.Retry.MaxRetries = 0;

        var client = new SearchClient(
            new Uri("https://search.example.com"), "index", new AzureKeyCredential("not-a-real-key"), options);

        return (client, handler);
    }

    private static ProbeChunk Chunk(params float[] vector) => new() { ContentVector = vector };

    [TestMethod]
    [DataRow(float.NaN,              "NaN")]
    [DataRow(float.PositiveInfinity, "+Infinity")]
    [DataRow(float.NegativeInfinity, "-Infinity")]
    public async Task UploadDocumentsAsync_NonFiniteVectorComponent_FailsBeforeTheServiceSeesIt(
        float component, string label)
    {
        var (client, handler) = BuildClient();

        // The concrete type is Utf8JsonWriter's: System.Text.Json's float converter calls
        // WriteNumberValue, which rejects the special values outright unless
        // JsonNumberHandling.AllowNamedFloatingPointLiterals is set - and the SDK's default
        // serialiser does not set it. Asserted as a type and never on the message text, which is
        // not API and changes between releases.
        //
        // This assertion also carries the claim "the service never saw it", and carries it on its
        // own: Azure.Core wraps anything coming back from the transport in RequestFailedException,
        // which does not derive from ArgumentException - so an EXACT ArgumentException cannot have
        // originated in a response. A separate `ex is not RequestFailedException` check was tried
        // here and the compiler rejected it as provably always-true (CS0184), which is the point
        // stated more precisely than a second assertion could.
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => client.UploadDocumentsAsync(new[] { Chunk(0.1f, component, 0.3f) }));

        // Secondary, and about MECHANISM rather than the claim. On the current SDK the batch is
        // serialised into a buffered Utf8JsonRequestContent before Pipeline.SendAsync, so the
        // writer throws with the transport still untouched. If a future version defers
        // serialisation to RequestContent.WriteTo, the transport IS entered and this fails on a
        // mechanism the test was never really about - the assertion above still holds, and this
        // one should then be deleted rather than worked around.
        Assert.AreEqual(0, handler.Invocations, label);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_FiniteVector_SerialisesAndReachesTheTransport()
    {
        var (client, handler) = BuildClient();

        await client.UploadDocumentsAsync(new[] { Chunk(0.1f, 0.2f, 0.3f) });

        Assert.AreEqual(1, handler.Invocations);
    }

    // The control that stops the negative cases being read as "unusable vectors fail to upload".
    // An all-zero vector is the one that serialises perfectly well and is still useless for
    // retrieval - which is exactly why VectorHealth checks the values and not just the width,
    // and why withholding it (D199 A1) has to be a decision in our code rather than something
    // the SDK does for us.
    [TestMethod]
    public async Task UploadDocumentsAsync_AllZeroVector_SerialisesAndReachesTheTransport()
    {
        var (client, handler) = BuildClient();

        await client.UploadDocumentsAsync(new[] { Chunk(0f, 0f, 0f) });

        Assert.AreEqual(1, handler.Invocations);
    }
}
