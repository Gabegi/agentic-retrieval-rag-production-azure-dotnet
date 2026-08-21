using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Indexing.DI.Services;

namespace AgenticRagApp.Indexing.DI.Utils;

// Splits a located section at the table captions Document Intelligence did not call headings.
//
// The problem this exists for, measured on CAO GHZ (Versie 4) in the 260818 run: DI detects ONE
// heading ("Salarisschaal functiegroep 45") for a page carrying NINE salary tables. The other
// eight captions - "Salarisschaal functiegroep 30", "50", "15*", "35", ... - arrive as ordinary
// paragraph lines, so HeadingLocator never sees them and the section runs across all nine
// tables. SectionChunkBuilder then stamps EVERY child of that section with the one heading, and
// a chunk holding functiegroep 50's pay scale goes into the index labelled, embedded and cited
// as functiegroep 45. Sections 65, 70 and 74 each repeat it for a different effective date;
// 35 chunks corpus-wide.
//
// Wrong attribution on salary data is worse than a missing chunk: the reader has no signal that
// the label and the numbers disagree. So the caption becomes a real boundary and owns its table.
//
// Deliberately conservative. Every rule below narrows to the shape actually measured, because
// the failure mode of being too eager is the opposite of the one being fixed - a prose sentence
// promoted to a heading fragments a section that was correct. A line qualifies only when all of
// these hold:
//
//   1. it is followed, after blank lines only, by a table row;
//   2. it stands alone as its own paragraph (blank line above, or section body start);
//   3. it is short - a caption, not a paragraph;
//   4. it does not read as a sentence or a lead-in ("De schalen zijn als volgt:").
//
// Rule 4 is what keeps "Onderstaande tabel geldt per 1 juli:" a paragraph while
// "Salarisschaal functiegroep 50" becomes a boundary.
public static class TableCaptionSplitter
{
    // A caption is a label. Measured against the corpus's real captions, which top out well
    // under this: the longest "Salarisschaal functiegroep NN*" is 30 characters.
    private const int MaxCaptionLength = 120;

    // Trailing punctuation that marks prose rather than a label. A colon is the important one:
    // it is how a lead-in sentence introduces the table below it.
    private static readonly char[] SentenceEnders = ['.', ':', ';', '!', '?', ','];

    // Sections in, sections out - split where captions were found, renumbered so Index stays
    // the contiguous position it is everywhere else. A section with no captions is returned
    // unchanged (same instance), so the common document costs one scan and no allocation.
    public static IReadOnlyList<LocatedSection> Split(
        string content, IReadOnlyList<LocatedSection> sections)
    {
        if (string.IsNullOrEmpty(content) || sections.Count == 0) return sections;

        var result = new List<LocatedSection>(sections.Count);
        var index  = 0;
        var split  = false;

        foreach (var section in sections)
        {
            var captions = CaptionStarts(content, section);

            if (captions.Count == 0)
            {
                result.Add(section with { Index = index++ });
                continue;
            }

            split = true;

            // The head keeps the original heading: it is the part of the section that really
            // does belong to the DI heading, and it runs to the first caption. A head whose
            // body is nothing but its own heading line is NOT emitted - it would index as a
            // chunk that lexically owns the heading while containing no data, the exact
            // wrong-attribution shape this class exists to remove. Its range is folded into
            // the first caption section instead (the heading line rides along as noise
            // inside a correctly-labelled chunk), so the sections still tile.
            var headEmitted = false;
            if (captions[0] > section.Start &&
                HasContentBeyondHeading(content[section.Start..captions[0]], section.HeadingText))
            {
                result.Add(section with { Index = index++, End = captions[0] });
                headEmitted = true;
            }

            for (var i = 0; i < captions.Count; i++)
            {
                var start = i == 0 && !headEmitted ? section.Start : captions[i];
                var end   = i + 1 < captions.Count ? captions[i + 1] : section.End;
                var text  = CaptionText(content, captions[i]);

                result.Add(new LocatedSection(
                    Index:       index++,
                    HeadingText: text,

                    // Hung under the DI heading rather than replacing it: the caption is a
                    // level below the section that contains it, and the parent is what says
                    // which salary table set (which effective date) this one belongs to.
                    HeadingPath:   Append(section.HeadingPath, text),
                    HeadingSource: ChunkHeadingSource.TableCaption,

                    // One level below its parent. Depth is not used for boundaries (see
                    // HeadingChainBuilder on why containment is preferred), but a caption that
                    // claimed its parent's depth would misreport the shape of the document.
                    Depth:      section.Depth + 1,
                    Start:      start,
                    End:        end,
                    PageNumber: section.PageNumber,
                    Located:    true));
            }
        }

        return split ? result : sections;
    }

    // Offsets, in document coordinates, of every caption line inside this section's BODY.
    //
    // The section's own heading line is skipped: it opens the section at Start and splitting
    // there would produce an empty head and a duplicate of the heading one level down.
    private static List<int> CaptionStarts(string content, LocatedSection section)
    {
        var starts = new List<int>();
        if (section.Length <= 0) return starts;

        var body  = content[section.Start..section.End];
        var lines = LineSpans.Read(body);

        for (var i = 0; i < lines.Count; i++)
        {
            var (start, end) = lines[i];
            var text = body[start..end].Trim();

            if (text.Length == 0) continue;

            // The heading line itself, at the top of its own section.
            if (start == 0 && section.HeadingSource != ChunkHeadingSource.None) continue;

            if (!IsCaptionLine(text)) continue;
            if (!StandsAlone(body, lines, i)) continue;
            if (!TableFollows(body, lines, i)) continue;

            starts.Add(section.Start + start);
        }

        return starts;
    }

    // Rules 3 and 4: short, and not prose.
    private static bool IsCaptionLine(string text) =>
        text.Length <= MaxCaptionLength &&
        !TableDetector.IsRow(text) &&
        !SentenceEnders.Contains(text[^1]);

    // Rule 2. A caption is its own paragraph - a blank line above it, or the start of the body.
    // Without this the last line of a paragraph that happens to precede a table would be
    // promoted, which cuts a sentence away from the text it belongs to.
    private static bool StandsAlone(string body, List<(int Start, int End)> lines, int i)
    {
        if (i == 0) return true;

        var previous = body[lines[i - 1].Start..lines[i - 1].End];

        return string.IsNullOrWhiteSpace(previous);
    }

    // Rule 1. The next line carrying anything is a table row.
    private static bool TableFollows(string body, List<(int Start, int End)> lines, int i)
    {
        for (var j = i + 1; j < lines.Count; j++)
        {
            var next = body[lines[j].Start..lines[j].End];
            if (string.IsNullOrWhiteSpace(next)) continue;

            return TableDetector.IsRow(next);
        }

        return false;
    }

    private static string CaptionText(string content, int start)
    {
        var end = content.IndexOf('\n', start);

        return content[start..(end < 0 ? content.Length : end)].Trim();
    }

    // Some tables carry their caption INSIDE themselves, as a merged header row DI rendered by
    // repeating the label into every cell:
    //
    //   | Salarisschaal functiegroep 75 | Salarisschaal functiegroep 75 | Salarisschaal functiegroep 75 |
    //
    // Where that row exists it is authoritative - it is part of the table itself, immune to
    // the caption-drift a column-serialized page suffers - so SectionChunkBuilder uses it to
    // override an inherited heading. Returns the label, or null when the text does not open
    // with such a row.
    public static string? MergedHeaderLabel(string chunkText)
    {
        if (string.IsNullOrWhiteSpace(chunkText)) return null;

        var firstLine = chunkText.TrimStart().Split('\n')[0].Trim();
        if (!TableDetector.IsRow(firstLine) || TableDetector.IsSeparator(firstLine)) return null;

        var cells = firstLine.Split('|', StringSplitOptions.RemoveEmptyEntries)
                             .Select(c => c.Trim())
                             .Where(c => c.Length > 0)
                             .Distinct(StringComparer.Ordinal)
                             .ToList();

        // One distinct value repeated across 2+ cells, caption-shaped, and carrying at least
        // one letter - a label is a word, not a run of dashes or digits.
        return cells.Count == 1 &&
               firstLine.Count(ch => ch == '|') >= 3 &&
               cells[0].Any(char.IsLetter) &&
               IsCaptionLine(cells[0])
            ? cells[0]
            : null;
    }

    // Whether the head region carries anything the caption sections do not: text beyond the
    // section's own heading line. A pure heading-line head is folded rather than emitted -
    // code-review finding 260818: it cleared the residue filter (27 alphanumerics) and would
    // have indexed as a heading-only lexical magnet for exactly the query it cannot answer.
    private static bool HasContentBeyondHeading(string headRegion, string? headingText)
    {
        var remainder = headRegion.Trim();
        if (remainder.Length == 0) return false;
        if (string.IsNullOrWhiteSpace(headingText)) return true;

        // The heading may span lines in the raw text while HeadingText is space-flattened
        // (HeadingTextNormalizer), so compare flattened-to-flattened.
        var flattened = HeadingTextNormalizer.Flatten(remainder);

        return !string.Equals(flattened, headingText, StringComparison.Ordinal);
    }

    private static string Append(string? path, string caption) =>
        string.IsNullOrWhiteSpace(path) ? caption : $"{path} > {caption}";
}
