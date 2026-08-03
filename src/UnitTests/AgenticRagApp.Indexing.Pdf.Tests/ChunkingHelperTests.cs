using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class ChunkingHelperTests
{
    [TestMethod]
    public void SafeKey_IsUrlSafeBase64_NoPlusOrSlash()
    {
        // Pick inputs whose base64 encoding is known to contain '+' and '/' before replacement.
        var key = ChunkingHelper.SafeKey("blob>>??", 999999);

        Assert.IsFalse(key.Contains('+'));
        Assert.IsFalse(key.Contains('/'));
    }

    [TestMethod]
    public void SafeKey_SameInputs_AreDeterministic()
    {
        var key1 = ChunkingHelper.SafeKey("doc1", 3);
        var key2 = ChunkingHelper.SafeKey("doc1", 3);

        Assert.AreEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_DifferentIndex_ProducesDifferentKey()
    {
        var key1 = ChunkingHelper.SafeKey("doc1", 0);
        var key2 = ChunkingHelper.SafeKey("doc1", 1);

        Assert.AreNotEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_DifferentBlobName_ProducesDifferentKey()
    {
        var key1 = ChunkingHelper.SafeKey("doc1", 0);
        var key2 = ChunkingHelper.SafeKey("doc2", 0);

        Assert.AreNotEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_Decodes_BackToBlobNameAndIndex()
    {
        var key = ChunkingHelper.SafeKey("some::blob/name", 42);

        var restored = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/')));

        Assert.AreEqual("some::blob/name::42", restored);
    }
}
