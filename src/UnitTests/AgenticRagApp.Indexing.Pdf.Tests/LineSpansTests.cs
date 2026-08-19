using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.Indexing;

// Lines as RANGES. Every line-wise cutter reads these, and the mistake the whole helper set
// exists to avoid - splitting on newlines, which answers what a line SAYS and destroys where
// it SITS - is one keystroke away in each of them.
[TestClass]
public class LineSpansTests
{
    [TestMethod]
    public void EndExcludesTheNewline_SoASliceNeverCarriesATrailingLineBreak()
    {
        const string text = "een\ntwee\ndrie";

        var spans = LineSpans.Read(text);

        Assert.AreEqual(3, spans.Count);
        CollectionAssert.AreEqual(
            new[] { "een", "twee", "drie" },
            spans.Select(s => text[s.Start..s.End]).ToArray());
    }

    [TestMethod]
    public void EveryLineIsATrueSliceAtItsOwnOffset()
    {
        // The property the cutters actually depend on: the span addresses the source, so a
        // piece built from it can be found again.
        const string text = "kop\n\nregel twee\nregel drie";

        foreach (var (start, end) in LineSpans.Read(text))
            Assert.AreEqual(text[start..end], text.Substring(start, end - start));
    }

    [TestMethod]
    public void TrailingCarriageReturnSurvives_BecauseTheSliceMustMatchTheSource()
    {
        // Deliberate, per the class comment: normalising CRLF here would make the slice
        // disagree with the source, so every line test tolerates the stray CR instead.
        const string text = "een\r\ntwee";

        var spans = LineSpans.Read(text);

        Assert.AreEqual("een\r", text[spans[0].Start..spans[0].End]);
        Assert.AreEqual("twee", text[spans[1].Start..spans[1].End]);
    }

    [TestMethod]
    public void LastLineWithoutATrailingNewline_IsStillALine()
    {
        var spans = LineSpans.Read("een\ntwee");

        Assert.AreEqual(2, spans.Count);
        Assert.AreEqual(8, spans[1].End);
    }

    [TestMethod]
    public void TrailingNewline_LeavesAnEmptyFinalLine_WhichNonBlankDrops()
    {
        const string text = "een\n";

        Assert.AreEqual(2, LineSpans.Read(text).Count);
        Assert.AreEqual(1, LineSpans.NonBlank(text).Count);
    }

    [TestMethod]
    public void NonBlankDropsWhitespaceOnlyLines_SoABlankRowIsNeverPackedAsData()
    {
        // A table run can end on a blank line; counted as a row it would make an all-rows
        // table fail its own "every line is a row" test.
        const string text = "| a | b |\n   \n| 1 | 2 |";

        var spans = LineSpans.NonBlank(text);

        Assert.AreEqual(2, spans.Count);
        Assert.AreEqual("| a | b |", text[spans[0].Start..spans[0].End]);
        Assert.AreEqual("| 1 | 2 |", text[spans[1].Start..spans[1].End]);
    }

    [TestMethod]
    public void EmptyText_IsOneEmptyLine_AndNoNonBlankLines()
    {
        Assert.AreEqual(1, LineSpans.Read("").Count);
        Assert.AreEqual(0, LineSpans.NonBlank("").Count);
    }
}
