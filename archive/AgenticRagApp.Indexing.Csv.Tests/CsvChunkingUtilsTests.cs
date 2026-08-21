using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Csv.Utils;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class ChunkingUtilsTests
{
    [TestMethod]
    public void SafeKey_IsUrlSafeBase64_NoPlusOrSlash()
    {
        var key = CsvChunkingUtils.SafeKey("blob>>??", 999999);

        Assert.IsFalse(key.Contains('+'));
        Assert.IsFalse(key.Contains('/'));
    }

    [TestMethod]
    public void SafeKey_SameInputs_AreDeterministic()
    {
        var key1 = CsvChunkingUtils.SafeKey("doc1", 3);
        var key2 = CsvChunkingUtils.SafeKey("doc1", 3);

        Assert.AreEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_DifferentIndex_ProducesDifferentKey()
    {
        var key1 = CsvChunkingUtils.SafeKey("doc1", 0);
        var key2 = CsvChunkingUtils.SafeKey("doc1", 1);

        Assert.AreNotEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_DifferentBlobName_ProducesDifferentKey()
    {
        var key1 = CsvChunkingUtils.SafeKey("doc1", 0);
        var key2 = CsvChunkingUtils.SafeKey("doc2", 0);

        Assert.AreNotEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_Decodes_BackToBlobNameAndIndex()
    {
        var key = CsvChunkingUtils.SafeKey("some::blob/name", 42);

        var restored = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/')));

        Assert.AreEqual("some::blob/name::42", restored);
    }
}
