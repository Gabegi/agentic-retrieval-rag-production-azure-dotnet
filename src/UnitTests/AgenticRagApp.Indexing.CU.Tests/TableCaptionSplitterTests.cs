using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class TableCaptionSplitterTests
{
    private static LocatedSection Whole(string content, string? heading = "Kop", int depth = 1) =>
        new(Index: 0, HeadingText: heading, HeadingPath: heading,
            HeadingSource: heading is null ? ChunkHeadingSource.None : ChunkHeadingSource.DiHeading,
            Depth: depth, Start: 0, End: content.Length, PageNumber: 1, Located: true);

    private const string Table =
        "| trede | inpas.nr. | € |\n| --- | --- | --- |\n| 0 | 1 | 2175 |\n| 1 | 2 | 2207 |";

    [TestMethod]
    public void CaptionAboveATable_OpensItsOwnSection()
    {
        // The measured CAO GHZ shape: DI marks one heading for a page of salary tables, and
        // every other caption arrives as an ordinary paragraph line. Without this split the
        // functiegroep 50 table is stamped "Salarisschaal functiegroep 45".
        var content = $"Salarisschaal functiegroep 45\n\n{Table}\n\nSalarisschaal functiegroep 50\n\n{Table}";
        var result  = TableCaptionSplitter.Split(content, [Whole(content, "Salarisschaal functiegroep 45")]);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("Salarisschaal functiegroep 45", result[0].HeadingText);
        Assert.AreEqual("Salarisschaal functiegroep 50", result[1].HeadingText);
        Assert.AreEqual(ChunkHeadingSource.TableCaption, result[1].HeadingSource);
    }

    [TestMethod]
    public void TheCaptionStaysWithItsOwnTable()
    {
        // The other half of the same defect: the caption was being cut away from the table it
        // labels, leaving a 29-character chunk saying only "Salarisschaal functiegroep 50" and
        // a table of bare numbers under someone else's heading.
        var content = $"Kop\n\n{Table}\n\nSalarisschaal functiegroep 50\n\n{Table}";
        var result  = TableCaptionSplitter.Split(content, [Whole(content)]);

        var caption = result.Single(s => s.HeadingText == "Salarisschaal functiegroep 50");
        var body    = content[caption.Start..caption.End];

        StringAssert.Contains(body, "Salarisschaal functiegroep 50");
        StringAssert.Contains(body, "| 0 | 1 | 2175 |");
    }

    [TestMethod]
    public void HeadingPath_HangsTheCaptionUnderItsSection()
    {
        // The parent is what says WHICH salary table set - which effective date - this caption
        // belongs to. Replacing the path rather than extending it would lose that.
        var content = $"Kop\n\nSalarisschaal functiegroep 50\n\n{Table}";
        var section = Whole(content) with { HeadingPath = "Hoofdstuk 4 > Artikel 4:15" };
        var result  = TableCaptionSplitter.Split(content, [section]);

        Assert.AreEqual("Hoofdstuk 4 > Artikel 4:15 > Salarisschaal functiegroep 50",
            result.Single(s => s.HeadingSource == ChunkHeadingSource.TableCaption).HeadingPath);
    }

    [TestMethod]
    public void SectionsAreRenumberedContiguously()
    {
        var content = $"Kop\n\nSalarisschaal functiegroep 50\n\n{Table}\n\nSalarisschaal functiegroep 55\n\n{Table}";
        var result  = TableCaptionSplitter.Split(content, [Whole(content)]);

        CollectionAssert.AreEqual(
            Enumerable.Range(0, result.Count).ToArray(),
            result.Select(s => s.Index).ToArray());
    }

    [TestMethod]
    public void SectionsTileTheOriginalRange_WithoutGapsOrOverlap()
    {
        // The slice invariant every consumer downstream depends on: the split sections must
        // still cover exactly what the original covered.
        var content = $"Kop\n\nSalarisschaal functiegroep 50\n\n{Table}\n\nSalarisschaal functiegroep 55\n\n{Table}";
        var result  = TableCaptionSplitter.Split(content, [Whole(content)]);

        Assert.AreEqual(0, result[0].Start);
        Assert.AreEqual(content.Length, result[^1].End);

        for (var i = 1; i < result.Count; i++)
            Assert.AreEqual(result[i - 1].End, result[i].Start, $"gap or overlap before section {i}");
    }

    [TestMethod]
    public void ALeadInSentence_IsNotACaption()
    {
        // "De schalen zijn als volgt:" introduces the table but is prose. Promoting it would
        // fragment a section that was already correct - the failure mode opposite to the one
        // being fixed.
        var content = $"Kop\n\nDe schalen zijn als volgt:\n\n{Table}";
        var result  = TableCaptionSplitter.Split(content, [Whole(content)]);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Kop", result[0].HeadingText);
    }

    [TestMethod]
    public void TheLastLineOfAParagraph_IsNotACaption()
    {
        // No blank line above it, so it is the tail of the paragraph rather than a label of
        // its own. Cutting here would separate a sentence from the text it belongs to.
        var content = $"Kop\n\nEerste regel van de alinea\ntweede regel zonder punt\n{Table}";
        var result  = TableCaptionSplitter.Split(content, [Whole(content)]);

        Assert.AreEqual(1, result.Count);
    }

    [TestMethod]
    public void ALineNotFollowedByATable_IsNotACaption()
    {
        var content = "Kop\n\nEen losse regel zonder tabel\n\nGewone alinea tekst hier.";
        var result  = TableCaptionSplitter.Split(content, [Whole(content)]);

        Assert.AreEqual(1, result.Count);
    }

    [TestMethod]
    public void AVeryLongLine_IsNotACaption()
    {
        var longLine = new string('a', 200);
        var content  = $"Kop\n\n{longLine}\n\n{Table}";
        var result   = TableCaptionSplitter.Split(content, [Whole(content)]);

        Assert.AreEqual(1, result.Count);
    }

    [TestMethod]
    public void TheSectionsOwnHeadingLine_IsNeverSplitOffFromItself()
    {
        // The heading opens the section at Start. Splitting there would leave an empty head
        // and duplicate the heading one level down as a caption.
        var content = $"Salarisschaal functiegroep 45\n\n{Table}";
        var result  = TableCaptionSplitter.Split(content, [Whole(content, "Salarisschaal functiegroep 45")]);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(ChunkHeadingSource.DiHeading, result[0].HeadingSource);
    }

    [TestMethod]
    public void ASectionWithNoCaptions_IsReturnedUnchanged()
    {
        var content  = "Kop\n\nGewone alinea zonder tabellen.";
        var sections = new[] { Whole(content) };

        Assert.AreSame(sections, TableCaptionSplitter.Split(content, sections));
    }

    [TestMethod]
    public void APreambleSection_CanStillOpenOnACaption()
    {
        // A preamble has no heading of its own (HeadingSource None), so its first line is not
        // a heading line and is eligible - a document opening straight onto a captioned table
        // should still get the boundary.
        var content = $"Salarisschaal functiegroep 50\n\n{Table}";
        var section = Whole(content, heading: null);
        var result  = TableCaptionSplitter.Split(content, [section]);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Salarisschaal functiegroep 50", result[0].HeadingText);
        Assert.AreEqual(ChunkHeadingSource.TableCaption, result[0].HeadingSource);
    }

    [TestMethod]
    public void EmptyInput_IsHandled()
    {
        Assert.AreEqual(0, TableCaptionSplitter.Split("", []).Count);
        Assert.AreEqual(0, TableCaptionSplitter.Split("tekst", []).Count);
    }

    [TestMethod]
    public void RealCorpusShape_CaoGhzSalaryAppendix_LabelsEachTableWithItsOwnFunctiegroep()
    {
        // Verbatim layout of section 65 of CAO GHZ (Versie 4), read off the 260818 chunking
        // artifact: one DI heading, then caption/table pairs running down the page. That run
        // produced 17 chunks all stamped "Salarisschaal functiegroep 45", 35 mislabelled
        // corpus-wide. Every table below must now answer to its own functiegroep.
        var content =
            "Salarisschaal functiegroep 45\n\n" +
            "Salarisschaal functiegroep 30\n\n" +
            "| trede | inpas.nr. | € |\n| --- | --- | --- |\n| 2 | 16 | 2992 |\n| 3 | 18 | 3149 |\n\n" +
            "Salarisschaal functiegroep 50\n\n" +
            "| trede | inpas.nr. | € |\n| --- | --- | --- |\n| 0 | 6 | 2326 |\n| 1 | 8 | 2433 |\n\n" +
            "Salarisschaal functiegroep 15*\n\n" +
            "| trede | inpas.nr. | € |\n| --- | --- | --- |\n| 1 | 19 | 3231 |\n| 2 | 21 | 3400 |";

        var result = TableCaptionSplitter.Split(
            content, [Whole(content, "Salarisschaal functiegroep 45")]);

        // No "functiegroep 45" head section: its body would have been nothing but its own
        // heading line, which indexes as a lexical magnet for exactly the query it cannot
        // answer (code-review finding, 260818). It is folded into the first caption section,
        // whose range therefore starts at the section start.
        CollectionAssert.AreEqual(
            new[]
            {
                "Salarisschaal functiegroep 30",
                "Salarisschaal functiegroep 50",
                "Salarisschaal functiegroep 15*",
            },
            result.Select(s => s.HeadingText).ToArray());
        Assert.AreEqual(0, result[0].Start);

        // The numbers that identify each table have to sit under the right heading - this is
        // the whole point of the fix, not the section count.
        StringAssert.Contains(Body(content, result, "Salarisschaal functiegroep 30"), "| 2 | 16 | 2992 |");
        StringAssert.Contains(Body(content, result, "Salarisschaal functiegroep 50"), "| 0 | 6 | 2326 |");
        StringAssert.Contains(Body(content, result, "Salarisschaal functiegroep 15*"), "| 1 | 19 | 3231 |");

        // And must NOT appear under a neighbour's.
        Assert.IsFalse(Body(content, result, "Salarisschaal functiegroep 50").Contains("2992"));
    }

    private static string Body(string content, IReadOnlyList<LocatedSection> sections, string heading)
    {
        var section = sections.Single(s => s.HeadingText == heading);

        return content[section.Start..section.End];
    }

    // ── MergedHeaderLabel: the caption DI folded INTO the table ─────────────────────────────

    [TestMethod]
    public void MergedHeaderRow_YieldsItsLabel()
    {
        // DI renders some captions by repeating the label into every cell of the table's
        // first row. That row is part of the table itself - authoritative over any heading
        // the chunk inherited, and immune to the caption drift of a column-serialized page.
        var text =
            "| Salarisschaal functiegroep 75 | Salarisschaal functiegroep 75 | Salarisschaal functiegroep 75 |\n" +
            "| --- | --- | --- |\n| trede | inpas.nr. | € |\n| 0 | 56 | 6631 |";

        Assert.AreEqual("Salarisschaal functiegroep 75", TableCaptionSplitter.MergedHeaderLabel(text));
    }

    [TestMethod]
    public void OrdinaryTableRows_YieldNoLabel()
    {
        Assert.IsNull(TableCaptionSplitter.MergedHeaderLabel("| trede | inpas.nr. | € |\n| 0 | 1 | 2175 |"));
        Assert.IsNull(TableCaptionSplitter.MergedHeaderLabel("| --- | --- | --- |"));
        Assert.IsNull(TableCaptionSplitter.MergedHeaderLabel("Gewone alinea zonder tabel."));
        Assert.IsNull(TableCaptionSplitter.MergedHeaderLabel(""));
    }

    [TestMethod]
    public void SectionChunkBuilder_RelabelsAChunkByItsMergedHeaderRow()
    {
        // The chunk-level half of the fix: a section spanning several self-labelled tables
        // stamps every child with its one heading, so the override has to happen where the
        // chunks are built, not where the sections are cut.
        var text =
            "| Salarisschaal functiegroep 75 | Salarisschaal functiegroep 75 | Salarisschaal functiegroep 75 |\n" +
            "| --- | --- | --- |\n| 0 | 56 | 6631 |";
        var section = new LocatedSection(
            Index: 65, HeadingText: "Salarisschaal functiegroep 45",
            HeadingPath: "Artikel 4:15 > Salarisschaal functiegroep 45",
            HeadingSource: ChunkHeadingSource.DiHeading,
            Depth: 2, Start: 0, End: text.Length, PageNumber: 1, Located: true);
        var piece = new ContentPiece(
            Text: text, Start: 0, Length: text.Length, BoundaryLevel: BoundaryLevel.None);

        var chunk = AgenticRagApp.Indexing.CU.Services.SectionChunkBuilder.Build(section, [piece]).Single();

        Assert.AreEqual("Salarisschaal functiegroep 75", chunk.HeadingText);
        Assert.AreEqual("Artikel 4:15 > Salarisschaal functiegroep 45 > Salarisschaal functiegroep 75",
            chunk.HeadingPath);
        Assert.AreEqual(ChunkHeadingSource.TableCaption, chunk.HeadingSource);
    }
}
