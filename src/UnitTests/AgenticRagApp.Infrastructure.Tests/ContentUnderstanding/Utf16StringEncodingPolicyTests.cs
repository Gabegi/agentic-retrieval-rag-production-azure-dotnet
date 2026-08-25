using Azure.AI.ContentUnderstanding;
using Azure.Core;
using Azure.Core.Pipeline;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

namespace RagApp.UnitTests.Infrastructure;

// The typed AnalyzeBinaryAsync overload - the one returning Operation<AnalysisResult> rather than
// Operation<BinaryData> - has no stringEncoding parameter, so this policy is the only thing making
// Content Understanding return UTF-16 span offsets instead of its 'codePoint' default.
//
// It is worth testing directly, and worth testing the negative cases hardest, because getting this
// wrong is silent in both directions: too narrow a match and every structural Offset drifts on any
// document containing a non-BMP character, with no error and no failed document; too broad a match
// and the parameter lands on a control-plane request that never asked for it.
[TestClass]
public class Utf16StringEncodingPolicyTests
{
    // A real pipeline is the cheapest way to get a well-formed HttpMessage: RequestUriBuilder's
    // query handling (the thing under test) is what we want exercised, not a stand-in for it.
    private static HttpMessage MessageFor(string uri)
    {
        var pipeline = HttpPipelineBuilder.Build(new ContentUnderstandingClientOptions());
        var message  = pipeline.CreateMessage();
        message.Request.Method = RequestMethod.Post;
        message.Request.Uri.Reset(new Uri(uri));
        return message;
    }

    private static string Apply(string uri, int times = 1)
    {
        var policy  = new Utf16StringEncodingPolicy();
        var message = MessageFor(uri);
        for (var i = 0; i < times; i++) policy.OnSendingRequest(message);
        return message.Request.Uri.ToUri().Query;
    }

    private const string AnalyzeUri =
        "https://acct.cognitiveservices.azure.com/contentunderstanding/analyzers/cap-pdf-layout:analyze?api-version=2025-11-01";

    [TestMethod]
    public void AppendsUtf16ToAnalyzeRequest()
    {
        StringAssert.Contains(Apply(AnalyzeUri), "stringEncoding=utf16");
    }

    [TestMethod]
    public void PreservesExistingQueryParameters()
    {
        // Appending must not clobber api-version - losing that would pin the call to whatever the
        // service defaults to, which is the exact failure ContentUnderstandingServiceVersion exists
        // to prevent.
        StringAssert.Contains(Apply(AnalyzeUri), "api-version=2025-11-01");
    }

    [TestMethod]
    public void AppliesToAnalyzeBatch()
    {
        // Deliberate: a document over CU's 300-page async cap is submitted in slices, and a sliced
        // document is where a drifting offset is hardest to notice. If the substring match is ever
        // tightened to an exact segment, this is the test that should stop it.
        var query = Apply(
            "https://acct.cognitiveservices.azure.com/contentunderstanding/analyzers/cap-pdf-layout:analyzeBatch?api-version=2025-11-01");

        StringAssert.Contains(query, "stringEncoding=utf16");
    }

    [TestMethod]
    public void AppendsOnlyOnceWhenInvokedRepeatedly()
    {
        // PerCall registration already means one invocation per logical request rather than one per
        // retry, so this guards the other case: a future SDK version that starts sending
        // stringEncoding itself, which would otherwise produce a duplicated query parameter.
        var query = Apply(AnalyzeUri, times: 3);
        var occurrences = query.Split("stringEncoding").Length - 1;

        Assert.AreEqual(1, occurrences, $"Expected exactly one stringEncoding parameter, got query '{query}'.");
    }

    [TestMethod]
    public void DoesNotTouchTheAnalyzerCreateRequest()
    {
        // The nearest real false positive, not a hypothetical one: the provisioner's analyzer write
        // goes through the same client and therefore the same pipeline. Its path is
        // /analyzers/{id} with no colon, so the match must miss it - stringEncoding is meaningless
        // on a control-plane write and has no business being sent there.
        var query = Apply(
            "https://acct.cognitiveservices.azure.com/contentunderstanding/analyzers/cap-pdf-layout?api-version=2025-11-01");

        Assert.IsFalse(query.Contains("stringEncoding", StringComparison.OrdinalIgnoreCase),
            $"Analyzer create/get must not carry stringEncoding, but query was '{query}'.");
    }

    [TestMethod]
    public void DoesNotTouchTheDefaultsRequest()
    {
        // The provisioner's other write, same reasoning.
        var query = Apply(
            "https://acct.cognitiveservices.azure.com/contentunderstanding/defaults?api-version=2025-11-01");

        Assert.IsFalse(query.Contains("stringEncoding", StringComparison.OrdinalIgnoreCase),
            $"Defaults update must not carry stringEncoding, but query was '{query}'.");
    }
}
