using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

namespace RagApp.UnitTests.Infrastructure;

// Guards the api-version pin the same way SearchServiceVersion's own reasoning demands: the point
// of pinning is that a package bump cannot move the wire protocol without someone reviewing it, and
// a pin nothing asserts is a pin that a "helpful" upgrade quietly rolls forward.
//
// V2025_11_01 is currently the only member of the enum, so this test cannot fail today. It is
// written for the version that adds a second one.
[TestClass]
public class ContentUnderstandingServiceVersionTests
{
    [TestMethod]
    public void PinsApiVersion20251101()
    {
        Assert.AreEqual(
            ContentUnderstandingClientOptions.ServiceVersion.V2025_11_01,
            ContentUnderstandingServiceVersion.Current,
            "The CU api-version pin moved. That is a deliberate, measured change - see " +
            "docs/2608/260819/content-understanding-when-and-how.md:244 - not a side effect of a package update.");
    }

    [TestMethod]
    public void OptionsReturnsAFreshInstanceEachCall()
    {
        // Same reasoning as SearchServiceVersion.Options(): ClientOptions is mutable and Azure SDK
        // clients take ownership of what they are constructed with, so a shared instance would let
        // one client's later mutation leak into another.
        Assert.AreNotSame(ContentUnderstandingServiceVersion.Options(), ContentUnderstandingServiceVersion.Options());
    }
}
