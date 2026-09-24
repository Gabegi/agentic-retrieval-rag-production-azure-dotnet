using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.Indexing;

// Step 9 of the 260818 fix plan. Pinned here from 260818 while ChunkingService.DropHeadingOnlyChunks
// was held at false (so the "35 -> 0" salary-chunk check could not be satisfied by this rule
// instead of TableCaptionSplitter, since removed 2026-09-09); LIVE since 2026-09-23 (D224 A5).
// These tests pin the SHAPE test alone; ChunkingServiceTests pin when the service acts on it -
// only when the section kept another chunk carrying the heading.
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
    public void TheRuleIsLive()
    {
        // Inverted 2026-09-23 (D224 A5). Until then this pinned the flag OFF, so that nobody could
        // flip it without reading why ("step 9 must not precede step 6", last-run-fixes.md). The
        // ordering constraint went with TableCaptionSplitter on 2026-09-09; the rule was then
        // measured on run 260922/2 and switched on with the section-sibling guard. This pin now
        // says the opposite: switching it OFF again is a decision, not an accident.
        var field = typeof(ChunkingService).GetField(
            "DropHeadingOnlyChunks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.IsNotNull(field, "DropHeadingOnlyChunks was renamed or removed.");
        Assert.AreEqual(true, field!.GetValue(null),
            "The heading-only rule is live since D224 A5; read that section before turning it off.");
    }
}
