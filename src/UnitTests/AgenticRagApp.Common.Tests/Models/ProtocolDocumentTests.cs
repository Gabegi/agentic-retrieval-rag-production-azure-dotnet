using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.Common;

[TestClass]
public class ProtocolDocumentTests
{
    private static ChunkStatsSource WithContent(string content, string? summary = null) =>
        new() { Content = content, Summary = summary };

    [TestMethod]
    public void TokenEstimate_CountsWhitespaceSeparatedWords()
    {
        var doc = WithContent("one two three");

        Assert.AreEqual(3, doc.TokenEstimate);
    }

    [TestMethod]
    public void TokenEstimate_EmptyContent_IsZero()
    {
        var doc = WithContent("");

        Assert.AreEqual(0, doc.TokenEstimate);
    }

    [TestMethod]
    public void TokenEstimate_IgnoresRepeatedSpaces()
    {
        var doc = WithContent("one  two   three");

        Assert.AreEqual(3, doc.TokenEstimate);
    }

    [TestMethod]
    public void IsEmpty_BlankContent_IsTrue()
    {
        Assert.IsTrue(WithContent("   ").IsEmpty);
        Assert.IsTrue(WithContent("").IsEmpty);
    }

    [TestMethod]
    public void IsEmpty_NonBlankContent_IsFalse()
    {
        Assert.IsFalse(WithContent("text").IsEmpty);
    }

    [TestMethod]
    public void IsOversized_Over1024Tokens_IsTrue()
    {
        var doc = WithContent(string.Join(' ', Enumerable.Repeat("w", 1025)));

        Assert.IsTrue(doc.IsOversized);
    }

    [TestMethod]
    public void IsOversized_Exactly1024Tokens_IsFalse()
    {
        var doc = WithContent(string.Join(' ', Enumerable.Repeat("w", 1024)));

        Assert.IsFalse(doc.IsOversized);
    }

    [TestMethod]
    public void IsUndersized_Under20Tokens_IsTrue()
    {
        var doc = WithContent(string.Join(' ', Enumerable.Repeat("w", 19)));

        Assert.IsTrue(doc.IsUndersized);
    }

    [TestMethod]
    public void IsUndersized_Exactly20Tokens_IsFalse()
    {
        var doc = WithContent(string.Join(' ', Enumerable.Repeat("w", 20)));

        Assert.IsFalse(doc.IsUndersized);
    }

    [TestMethod]
    public void StartsClean_UppercaseFirstChar_IsTrue()
    {
        Assert.IsTrue(WithContent("Hello.").StartsClean);
    }

    [TestMethod]
    public void StartsClean_DigitFirstChar_IsTrue()
    {
        Assert.IsTrue(WithContent("1. Intro").StartsClean);
    }

    [TestMethod]
    public void StartsClean_LowercaseFirstChar_IsFalse()
    {
        Assert.IsFalse(WithContent("hello.").StartsClean);
    }

    [TestMethod]
    public void StartsClean_EmptyContent_IsFalse()
    {
        Assert.IsFalse(WithContent("").StartsClean);
    }

    [TestMethod]
    [DataRow("Ends here.")]
    [DataRow("Really?")]
    [DataRow("Wow!")]
    [DataRow("See section:")]
    [DataRow("(parenthetical)")]
    [DataRow("She said \"done\"")]
    [DataRow("It's fine'")]
    public void EndsClean_RecognizedTerminator_IsTrue(string content)
    {
        Assert.IsTrue(WithContent(content).EndsClean);
    }

    [TestMethod]
    public void EndsClean_NoTerminator_IsFalse()
    {
        Assert.IsFalse(WithContent("mid sentence").EndsClean);
    }

    [TestMethod]
    public void EndsClean_EmptyContent_IsFalse()
    {
        Assert.IsFalse(WithContent("").EndsClean);
    }

    [TestMethod]
    public void IsCoherent_CleanStartAndEnd_IsTrue()
    {
        Assert.IsTrue(WithContent("Hello world.").IsCoherent);
    }

    [TestMethod]
    public void IsCoherent_DirtyStart_IsFalse()
    {
        Assert.IsFalse(WithContent("hello world.").IsCoherent);
    }

    [TestMethod]
    public void IsCoherent_DirtyEnd_IsFalse()
    {
        Assert.IsFalse(WithContent("Hello world").IsCoherent);
    }

    [TestMethod]
    public void EmbeddingText_NoSummary_IsContentOnly()
    {
        var doc = WithContent("Body text", summary: null);

        Assert.AreEqual("Body text", doc.EmbeddingText);
    }

    [TestMethod]
    public void EmbeddingText_WhitespaceSummary_IsContentOnly()
    {
        var doc = WithContent("Body text", summary: "   ");

        Assert.AreEqual("Body text", doc.EmbeddingText);
    }

    [TestMethod]
    public void EmbeddingText_WithSummary_PrependsSummary()
    {
        var doc = WithContent("Body text", summary: "A summary");

        Assert.AreEqual("A summary\n\nBody text", doc.EmbeddingText);
    }
}
