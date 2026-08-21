using System.Text.Json;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Indexing.CU.Utils;

namespace RagApp.UnitTests.Indexing;

// The corpus measurements, pinned as a checkable record instead of code comments.
//
// Everything the chunking design rests on was decided by measuring four documents once, in
// August 2026, and then written down in prose: 1,273 headings, every one located, none with a
// null offset, none out of order. Ten code comments cite those numbers
// (chunking-done.md §5 item 6, §8 item 4, §17 item 4). The CSVs here are the durable record of
// that measurement, and these tests keep the record internally consistent.
//
// BE HONEST ABOUT THE FAILURE MODE. The §1 tests read static fixtures, so the only thing that
// can fail them is an edit to the fixtures - they do NOT guard PdfCleaner or GetHeadingsHelper,
// whose raw input (~132 MB of page JSON) is not checked in and cannot be. A regression in the
// live extraction path shows up in the per-run chunking artifact and the run-log counters
// (HardCut tripwire, boundary_level, heading counts), not here. What §1 buys is narrower: the
// numbers the comments cite stay traceable to data that provably has the claimed properties,
// and nobody can quietly edit the record to match a regression without this file noticing.
//
// The §9.2 tests are different in kind: they run REAL code (HeadingTextNormalizer.Flatten,
// GetHeadingsHelper's label regex) over real corpus pairs, and a change to either fails them.
// Flatten's output feeds Prefix -> EmbeddingText -> ContentHash, so a change there silently
// re-embeds the corpus - that is a genuine guard, not a record.
//
// NOT pinned anywhere - the 1,273/1,273 LOCATED rate. That measurement needs the extraction
// pipeline rather than a fixture, stays open until the stage runs end to end (§17 item 1), and
// this file is the place it belongs when it can be taken.
[TestClass]
public class CorpusRegressionTests
{
    private static string CorpusPath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "CorpusData", file);

    private sealed record CorpusHeading(string SourceId, int PageNumber, int Ordinal, int? Offset, string Content);

    // The CSVs are quoted-field exports; Content can itself contain a comma, so this splits on
    // quotes rather than on commas.
    private static IReadOnlyList<CorpusHeading> ReadHeadings(string file)
    {
        var rows = new List<CorpusHeading>();

        foreach (var line in File.ReadLines(CorpusPath(file)).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = SplitQuoted(line);
            if (fields.Count < 6) continue;

            rows.Add(new CorpusHeading(
                SourceId:   fields[0],
                PageNumber: int.Parse(fields[1]),
                Ordinal:    int.Parse(fields[2]),
                Offset:     string.IsNullOrEmpty(fields[4]) ? null : int.Parse(fields[4]),
                Content:    fields[5]));
        }

        return rows;
    }

    private static List<string> SplitQuoted(string line)
    {
        var fields  = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                // "" inside a quoted field is one literal quote.
                if (inQuote && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else inQuote = !inQuote;
            }
            else if (c == ',' && !inQuote)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }

    // ── §1: the offsets are sound ────────────────────────────────────────────

    [TestMethod]
    [DataRow("cao-headings.csv", 321, "CAO GGZ")]
    [DataRow("hyg-headings.csv", 385, "hygienecode")]
    public void TheRecordStillHoldsTheHeadingCountEveryDecisionWasTakenAgainst(
        string file, int expected, string document)
    {
        var headings = ReadHeadings(file);

        Assert.AreEqual(expected, headings.Count,
            $"{document}'s recorded heading count moved - someone edited the fixture. It is one " +
            "of the four documents summing to the 1,273 that HeadingLocator, ChunkingReporter " +
            "and ChunkingRunReport all cite; if the fixture was re-exported from a new run, " +
            "update the constants here and every comment citing the old figure.");
    }

    [TestMethod]
    [DataRow("cao-headings.csv")]
    [DataRow("hyg-headings.csv")]
    public void NoHeadingArrivesWithoutAnOffset(string file)
    {
        // 0 of 1,273 measured. A non-zero count here is an extraction anomaly, not routine input -
        // it means a heading's paragraph carried no spans, and the section boundary it opens then
        // rests on arrival order (HeadingLocator.OrderByOffset's carry).
        var without = ReadHeadings(file).Where(h => h.Offset is null).ToList();

        Assert.AreEqual(0, without.Count,
            $"headings with no offset: {string.Join(", ", without.Take(5).Select(h => h.Content))}");
    }

    [TestMethod]
    [DataRow("cao-headings.csv")]
    [DataRow("hyg-headings.csv")]
    public void OffsetsAscendStrictlyInPageOrder(string file)
    {
        // This is the property the whole "Heading.Offset can order headings" finding rests on:
        // DI assembles content in paragraph order, GetHeadings walks it forward once, and both
        // merge branches keep the FIRST paragraph's offset. So a regression or a tie means one of
        // those three stopped being true.
        var headings = ReadHeadings(file).OrderBy(h => h.PageNumber).ThenBy(h => h.Ordinal).ToList();

        for (int i = 1; i < headings.Count; i++)
        {
            Assert.IsTrue(headings[i].Offset > headings[i - 1].Offset,
                $"offset {headings[i].Offset} at ordinal {headings[i].Ordinal} " +
                $"does not exceed {headings[i - 1].Offset} - page and offset disagree about order " +
                $"at '{headings[i].Content}'");
        }
    }

    [TestMethod]
    [DataRow("cao-headings.csv")]
    [DataRow("hyg-headings.csv")]
    public void NoTwoHeadingsShareAnOffset(string file)
    {
        var duplicates = ReadHeadings(file)
            .GroupBy(h => h.Offset)
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.AreEqual(0, duplicates.Count,
            $"duplicate offsets at: {string.Join(", ", duplicates.Take(5).Select(g => g.Key))}");
    }

    // ── §9.2: the heading text shape that feeds ContentHash ──────────────────

    private sealed record TwoLinePair(int Page, string Label, string Term);

    private static IReadOnlyList<TwoLinePair> ReadTwoLinePairs() =>
        JsonSerializer.Deserialize<List<TwoLinePair>>(
            File.ReadAllText(CorpusPath("cao-two-line-pairs.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [TestMethod]
    public void EveryRealTwoLineHeading_FlattensToASingleSpacedLine()
    {
        // These are the actual label+term pairs extraction merges in the corpus. Flatten's output
        // is what reaches heading_text, heading_path and the embedded prefix - and therefore
        // ContentHash - so a change here re-embeds every document that has one of these.
        var pairs = ReadTwoLinePairs();
        Assert.IsTrue(pairs.Count > 0, "the fixture itself is the measurement; an empty one proves nothing");

        foreach (var pair in pairs)
        {
            var merged    = $"{pair.Label}\n{pair.Term}";
            var flattened = HeadingTextNormalizer.Flatten(merged);

            Assert.AreEqual($"{pair.Label} {pair.Term}", flattened);
            Assert.IsFalse(flattened!.Contains('\n'), "a newline renders as a line break mid-citation");
            Assert.IsFalse(flattened.Contains('\r'));
        }
    }

    [TestMethod]
    public void EveryRealPairsLabel_IsRecognisedAsABareNumberedLabel()
    {
        // §9.1: the merge in HeadingLocator consults this same regex so it cannot re-merge the
        // bare-label runs extraction deliberately kept apart. If the regex stops matching a real
        // label, that gate silently reopens.
        foreach (var pair in ReadTwoLinePairs())
        {
            Assert.IsTrue(GetHeadingsHelper.BareNumberedLabelWithWord().IsMatch(pair.Label),
                $"'{pair.Label}' is a real bare label in the corpus and no longer matches");
        }
    }

    [TestMethod]
    public void FlattenIsIdempotent_SoAReflattenedHeadingHashesTheSame()
    {
        foreach (var pair in ReadTwoLinePairs())
        {
            var once  = HeadingTextNormalizer.Flatten($"{pair.Label}\n{pair.Term}");
            var twice = HeadingTextNormalizer.Flatten(once);

            Assert.AreEqual(once, twice);
        }
    }
}
