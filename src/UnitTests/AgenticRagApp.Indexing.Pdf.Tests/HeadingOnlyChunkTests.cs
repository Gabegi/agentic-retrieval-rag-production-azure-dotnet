using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.Indexing;

// Step 9 of the 260818 fix plan, pinned but NOT live - ChunkingService.DropHeadingOnlyChunks is
// false until a re-index confirms the 35 mislabelled salary chunks have gone to 0. Those 35 ARE
// heading-only chunks, so enabling this first would make that check pass whether or not
// TableCaptionSplitter actually worked.
[TestClass]
public class HeadingOnlyChunkTests
{
    private static ChunkObject Chunk(string content, string? heading = null) =>
        new() { Content = content, HeadingText = heading };

    [TestMethod]
    public void AChunkThatIsOnlyItsOwnHeading_IsHeadingOnly()
    {
        // The measured shape: "Salarisschaal functiegroep 25" as an entire body, the heading
        // repeated as content with no rows under it.
        Assert.IsTrue(ChunkingService.IsHeadingOnly(
            Chunk("Salarisschaal functiegroep 25", "Salarisschaal functiegroep 25")));
    }

    [TestMethod]
    public void ARenderedMarkdownHeadingWithNoBody_IsHeadingOnly()
    {
        Assert.IsTrue(ChunkingService.IsHeadingOnly(Chunk("#### Artikel 4:15 Salarisschalen")));
        Assert.IsTrue(ChunkingService.IsHeadingOnly(Chunk("#### Artikel 4:15\nZie 4.2")));
    }

    [TestMethod]
    public void AHeadingWithARealBody_IsKept()
    {
        Assert.IsFalse(ChunkingService.IsHeadingOnly(
            Chunk("#### Artikel 4:15\nDe werknemer heeft recht op een vergoeding.")));

        Assert.IsFalse(ChunkingService.IsHeadingOnly(
            Chunk("Artikel 4:15\nNiet van toepassing.", "Artikel 4:15")));
    }

    [TestMethod]
    public void AShortChunkWithNoHeadingLine_IsKept()
    {
        // The rule requires that a heading line was ACTUALLY removed. Without that, every short
        // cut on the recursive route - where HeadingSource is "none" by design - would be
        // measured as if its first line were furniture and dropped.
        Assert.IsFalse(ChunkingService.IsHeadingOnly(Chunk("Bel 112.")));
        Assert.IsFalse(ChunkingService.IsHeadingOnly(Chunk("Bel 112.", "Noodgevallen")));
    }

    [TestMethod]
    public void TheRuleIsNotLiveYet()
    {
        // Pins the ordering itself: this must fail the day someone flips the flag without
        // reading why it is off. See last-run-fixes.md - "step 9 must not precede step 6".
        var field = typeof(ChunkingService).GetField(
            "DropHeadingOnlyChunks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.IsNotNull(field, "DropHeadingOnlyChunks was renamed or removed.");
        Assert.AreEqual(false, field!.GetValue(null),
            "Enable this only after a re-index confirms the 35 mislabelled salary chunks are 0.");
    }
}
