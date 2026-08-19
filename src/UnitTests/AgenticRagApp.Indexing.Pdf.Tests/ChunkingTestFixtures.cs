using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.Indexing;

// What every cascade test needs, in one place: a block factory, real token counts, and the
// slice invariant.
//
// The invariant is the reason this file exists rather than a private helper per test class.
// docs/2608/260818/chunking-done.md:165 lists it as the first thing left to do - "currently
// reasoned, not executed" - because BlockCascade now narrows its window per section, and a
// piece that addresses the WINDOW instead of the SOURCE is a bug no assertion about text
// content can see. Written once, asserted everywhere pieces are produced.
internal static class ChunkingTestFixtures
{
    public static ContentBlock Block(string text, BlockKind kind = BlockKind.Prose, int start = 0) =>
        new(text, start, kind);

    // A block that is a genuine slice of a larger document, for the cases where Start being
    // non-zero is the point.
    public static ContentBlock BlockIn(string content, int start, int end, BlockKind kind = BlockKind.Prose) =>
        new(content[start..end], start, kind);

    // The real cl100k_base count, the same call the strategies budget with. Ceilings in these
    // tests are derived from it rather than guessed as character counts, so a test cannot pass
    // for the wrong reason when the tokenizer sees text differently than we assumed.
    public static int Tokens(string? text) => TokenEstimator.Estimate(text);

    // Filler prose. Repeated Dutch words tokenize predictably and, crucially, offer sentence
    // and word boundaries - a filler of random characters would fall straight to HardCut and
    // exercise only the bottom rung.
    public static string Prose(int words, string word = "woord") =>
        string.Join(" ", Enumerable.Repeat(word, words)) + ".";

    // Sentences, for the rung above word gaps.
    public static string Sentences(int count, int wordsEach = 6) =>
        string.Join(" ", Enumerable.Range(0, count).Select(i => Prose(wordsEach, "woord" + i)));

    // A document for the two route tests. Only Content, Title, Family and LocatedSections
    // matter to a strategy - it decides WHERE to cut and knows nothing about ids, Zenya
    // metadata or page attribution - so the rest is filled in once here.
    public static PdfExtractionDocument Doc(
        string content,
        string title = "",
        string? domainTag = null,
        IReadOnlyList<LocatedSection>? sections = null,
        string sourceId = "doc1.pdf") =>
        new(SourceId:         sourceId,
            Content:          content,
            PageSpans:        [new PageSpan(1, 0, content.Length, null, IsPictureOnly: false)],
            Title:            title,
            Author:           null,
            CreatedAt:        null,
            ModDate:          null,
            PageCount:        null,
            LastModifiedDate: null,
            ZenyaDocumentId:  null,
            ZenyaVersion:     null,
            ZenyaStatus:      null,
            ZenyaUrl:         null,
            Bookmarks:        [],
            PageBreadcrumbs:  new Dictionary<int, string>(),
            Sections:         [],
            Headings:         [],
            Boilerplate:      [],
            Tables:           [],
            SelectionMarks:   [],
            Figures:          [],
            Lines:            [],
            Profile:          null,
            Language:         null,
            Family:           domainTag is null ? null : new DocumentFamily("fam-1", domainTag, []),
            LocatedSections:  sections);

    // One heading section, in cleaned-content coordinates. Located true with a real source is
    // the ordinary case; a preamble is the same record with source None.
    public static LocatedSection Section(
        int index,
        int start,
        int end,
        string? headingText = "Artikel 1",
        string? headingPath = "Artikel 1",
        string headingSource = ChunkHeadingSource.DiHeading,
        int depth = 1) =>
        new(Index:         index,
            HeadingText:   headingText,
            HeadingPath:   headingPath,
            HeadingSource: headingSource,
            Depth:         depth,
            Start:         start,
            End:           end,
            PageNumber:    1,
            Located:       true);

    //     content.AsSpan(piece.Start, piece.Length).SequenceEqual(piece.Text)
    //
    // Every piece is bounds-checked; only the ones that are pure slices are compared by text.
    // A COMPOSED piece (today only a table continuation fragment, which repeats the header) is
    // the documented exception, and it announces itself by Text.Length != Length - its
    // coordinates still address the rows it carries, which is what page attribution reads.
    public static void AssertSliceInvariant(string content, IEnumerable<ContentPiece> pieces)
    {
        foreach (var piece in pieces)
        {
            Assert.IsTrue(
                piece.Start >= 0 && piece.Length >= 0 && piece.Start + piece.Length <= content.Length,
                "piece [" + piece.Start + ", +" + piece.Length + ") is outside content of length " + content.Length);

            if (piece.Text.Length != piece.Length) continue;

            Assert.AreEqual(content.Substring(piece.Start, piece.Length), piece.Text,
                "piece at " + piece.Start + " is not a slice of the source");
        }
    }

    // Pieces come back in document order and never cover the same character twice. Composed
    // fragments are included: their coordinates are the rows they carry, so they order too.
    public static void AssertAscendingAndDisjoint(IReadOnlyList<ContentPiece> pieces)
    {
        for (var i = 1; i < pieces.Count; i++)
            Assert.IsTrue(
                pieces[i].Start >= pieces[i - 1].Start + pieces[i - 1].Length,
                "piece " + i + " at " + pieces[i].Start + " overlaps piece " + (i - 1) +
                " ending at " + (pieces[i - 1].Start + pieces[i - 1].Length));
    }
}
