using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class ChunkingHelperTests
{
    [TestMethod]
    public void EstimateTokens_EmptyContent_IsZero()
    {
        Assert.AreEqual(0, ChunkingHelper.EstimateTokens("", isTable: false));
        Assert.AreEqual(0, ChunkingHelper.EstimateTokens("", isTable: true));
    }

    [TestMethod]
    public void EstimateTokens_Prose_UsesProseRatioAndRoundsUp()
    {
        // 10 chars / 3.1 = 3.226... -> ceil to 4.
        var tokens = ChunkingHelper.EstimateTokens(new string('a', 10), isTable: false);

        Assert.AreEqual(4, tokens);
    }

    [TestMethod]
    public void EstimateTokens_Table_UsesTableRatioAndRoundsUp()
    {
        // 10 chars / 2.2 = 4.545... -> ceil to 5.
        var tokens = ChunkingHelper.EstimateTokens(new string('a', 10), isTable: true);

        Assert.AreEqual(5, tokens);
    }

    [TestMethod]
    public void EstimateTokens_SameContent_TableEstimateIsHigherThanProse()
    {
        // Table markdown tokenizes less efficiently (fewer chars/token), so the same content
        // must never estimate *fewer* tokens under the table ratio than the prose ratio.
        var content = new string('a', 500);

        var proseTokens = ChunkingHelper.EstimateTokens(content, isTable: false);
        var tableTokens = ChunkingHelper.EstimateTokens(content, isTable: true);

        Assert.IsTrue(tableTokens > proseTokens);
    }

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
