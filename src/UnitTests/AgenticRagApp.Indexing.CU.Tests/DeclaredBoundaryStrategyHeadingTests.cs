using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// Route 1 behaviour around a section's heading line, pinned after D224 A2 was built and rejected
// (2026-09-23): the heading-only first pieces it targeted are the heading-only rule's business
// (A5), and these three tests say what the strategy does and must keep doing on its own.
[TestClass]
public class DeclaredBoundaryStrategyHeadingTests
{
    private const string Heading = "## Artikel 1";

    private static async Task<IReadOnlyList<ChunkObject>> Chunk(string content, int? ceilingHint = null)
    {
        var section = Section(0, 0, content.Length, headingText: "Artikel 1", headingPath: "Artikel 1");
        return await new DeclaredBoundaryStrategy().ChunkDocumentAsync(Doc(content, sections: [section]));
    }

    private static int BodyCeiling(string title = "", string path = "Artikel 1") =>
        ChunkingBudget.TokenCeiling - PrefixBuilder.Cost(PrefixBuilder.Build(title, null, path));

    // A table of the requested number of rows; each row tokenizes predictably.
    private static string Table(int rows) =>
        "<table>\n<tr><th>Kolom</th><th>Waarde</th></tr>\n" +
        string.Join("\n", Enumerable.Range(0, rows).Select(i => $"<tr><td>Rij {i}</td><td>waarde {i} beschrijving</td></tr>")) +
        "\n</table>";

    [TestMethod]
    public async Task ALeadInSentenceBeforeATable_StaysSeparateFromTheTable()
    {
        // The no-cross-kind rule, visibly intact: heading + lead-in sentence pack into one prose
        // piece, the table that follows is its own (296 such sections on run 260922/2).
        const string lead = "Deze tabel geeft de betaaldata per maand.";
        var content = Heading + "\n\n" + lead + "\n\n" + Table(120);

        var chunks = await Chunk(content);

        Assert.AreEqual(Heading + "\n\n" + lead, chunks[0].Content.TrimEnd());
        StringAssert.StartsWith(chunks[1].Content, "<table>");
        Assert.IsFalse(chunks[0].Content.Contains("<table>"));
    }

    [TestMethod]
    public async Task AHeadingOnlySection_IsLeftToTheHeadingOnlyRule()
    {
        // A heading with no body is one piece here; whether it is indexed is the heading-only
        // rule's decision (ChunkingService, A5).
        var chunks = await Chunk(Heading + "\n\n");

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(Heading, chunks[0].Content.Trim());
    }

    [TestMethod]
    public async Task ASectionThatFitsWhole_IsUntouched()
    {
        var content = Heading + "\n\n" + Sentences(5);

        var chunks = await Chunk(content);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(content, chunks[0].Content);
        Assert.AreEqual(BoundaryLevel.None, chunks[0].BoundaryLevel);
    }
}
