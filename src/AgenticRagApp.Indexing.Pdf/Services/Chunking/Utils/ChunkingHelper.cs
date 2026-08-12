using System.Text;
using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.Pdf.Utils;

public static class ChunkingHelper
{
    private static readonly Regex TableRowLine = new(@"^\s*\|.*\|\s*$", RegexOptions.Compiled);

    private static readonly char[] SentenceEnders = ['.', '!', '?'];

    // Measured against the real embedding tokenizer (cl100k_base via text-embedding-3-large):
    // body prose 3.10-3.28 chars/token, table markdown 1.88-2.79 - table cells and pipe
    // characters tokenize less efficiently than continuous prose, a >10% divergence, so one
    // blended ratio isn't safe. Each constant here is set at or below the low (worst-case,
    // most-tokens) end of its measured band, never the midpoint: underestimating token count
    // is the dangerous direction (a budget sized against a rosier number silently overruns).
    //
    // CAPACITY PLANNING ONLY. Anything stored on a chunk or enforced as a ceiling goes
    // through TokenCounter (the real cl100k_base tokenizer) instead - see action-plan.md C2.
    // These stay because converting a character budget into an approximate token budget
    // without tokenizing every candidate string is still useful when sizing a split.
    private const double ProseCharsPerToken = 3.1;

    // Re-measured 260812 with the real tokenizer over the whole cached text of the big four,
    // rather than sampled: prose came back 3.10-3.28 (so 3.1 above stands), but TABLE came
    // back 1.88-2.79 - CAO VVT 1.88, CAO GHZ 2.00, both BELOW the 2.20 this constant was set
    // to. At 1.88 actual against 2.20 assumed, a character budget underestimates tokens by
    // ~17%: a table chunk sized to a 512-token ceiling is really ~600 tokens. That is the
    // direction this file already calls the dangerous one, so the constant moves below the
    // measured minimum rather than to its midpoint.
    //
    // Only the big four are measured - the tail is not in the cached page JSON - so this is
    // deliberately set under the lowest observed value rather than at it.
    private const double TableCharsPerToken = 1.8;

    // Ratio estimate. Prefer TokenCounter.Count for any value that is persisted or gated on.
    public static int EstimateTokens(string content, bool isTable) =>
        content.Length == 0 ? 0 : (int)Math.Ceiling(content.Length / (isTable ? TableCharsPerToken : ProseCharsPerToken));

    // Character budget corresponding to a token ceiling, at the worst-case (most tokens per
    // character) ratio for the segment type. Used to size a split before the pieces exist;
    // the pieces themselves are then counted exactly.
    public static int CharBudgetForTokens(int tokenCeiling, bool isTable) =>
        (int)(tokenCeiling * (isTable ? TableCharsPerToken : ProseCharsPerToken));

    public static string SafeKey(string blobName, int index) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{blobName}::{index}"))
            .Replace('+', '-').Replace('/', '_');

    // Splits on sentence-ending punctuation followed by whitespace or end-of-string.
    public static IEnumerable<string> SplitSentences(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(SentenceEnders, text[i]) >= 0 &&
                (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                yield return text[start..(i + 1)].Trim();
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            var rest = text[start..].Trim();
            if (rest.Length > 0) yield return rest;
        }
    }

    // Splits text into ceil(len / ceiling) pieces of roughly equal size, cutting at the
    // nearest paragraph break and falling back to a sentence break, then to a hard cut.
    //
    // This replaces greedy fill-then-remainder (action-plan.md C3), which was a systematic
    // near-duplicate generator at exactly the sizes two-grain cutting exists to serve. Trace
    // a 1,700-character section at ceiling 1,640 with 410 of overlap under the old scheme:
    // chunk 1 takes 1,640, the flush seeds 410 characters of its tail, 60 characters remain,
    // so chunk 2 is ~470 characters of which ~87% is a copy of chunk 1 - and
    // MergeTinyTrailingChunk does NOT fold it away, because the overlap it just added pushed
    // the runt past minTail. Balanced splitting turns that same section into two ~850s.
    //
    // Overlap is deliberately NOT applied here. It is sized against the produced child by
    // the caller, not against the ceiling - sizing it against the ceiling is what made the
    // runt case degenerate.
    public static List<string> SplitBalanced(string text, int ceiling)
    {
        if (ceiling <= 0 || text.Length <= ceiling) return [text];

        var pieces = (int)Math.Ceiling(text.Length / (double)ceiling);
        var target = (int)Math.Ceiling(text.Length / (double)pieces);

        var result = new List<string>();
        var start  = 0;

        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= target)
            {
                var tail = text[start..].Trim();
                if (tail.Length > 0) result.Add(tail);
                break;
            }

            var cut = FindBreak(text, start, Math.Min(start + target, text.Length - 1));
            var piece = text[start..cut].Trim();
            if (piece.Length > 0) result.Add(piece);
            start = cut;
        }

        return result.Count > 0 ? result : [text];
    }

    // Nearest structural break at or before "ideal", searching back no further than a
    // quarter of the piece - beyond that the "balanced" property is worth more than the
    // break quality, so a hard cut at ideal is preferable to a badly lopsided piece.
    private static int FindBreak(string text, int start, int ideal)
    {
        var floor = start + (ideal - start) * 3 / 4;

        var para = text.LastIndexOf("\n\n", ideal, ideal - floor + 1, StringComparison.Ordinal);
        if (para > start) return para + 2;

        for (var i = ideal; i > floor; i--)
            if (Array.IndexOf(SentenceEnders, text[i]) >= 0 &&
                (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
                return i + 1;

        return ideal;
    }

    // Emits whatever's in current as a chunk, then seeds the next chunk with a short
    // sentence-aligned tail of it (TakeOverlap) so content near the boundary isn't only
    // ever on one side of the split.
    public static void Flush(List<string> chunks, StringBuilder current, int overlapSize)
    {
        if (current.Length == 0) return;

        var text = current.ToString().Trim();
        current.Clear();
        if (text.Length == 0) return;

        chunks.Add(text);

        var overlap = TakeOverlap(text, overlapSize);
        if (overlap.Length > 0)
            current.Append(overlap);
    }

    public static void MergeTinyTrailingChunk(List<string> chunks, int minTail)
    {
        if (chunks.Count < 2 || chunks[^1].Length >= minTail) return;

        chunks[^2] = $"{chunks[^2]}\n\n{chunks[^1]}";
        chunks.RemoveAt(chunks.Count - 1);
    }

    // Sentence-aligned tail of the last overlapSize characters of a just-flushed chunk -
    // starts after the first sentence end found in that window, so the overlap begins
    // mid-thought as rarely as possible. Falls back to the raw tail if no sentence
    // boundary exists in the window at all.
    public static string TakeOverlap(string text, int overlapSize)
    {
        if (text.Length <= overlapSize) return string.Empty;

        var tail    = text[^overlapSize..];
        var splitAt = tail.IndexOfAny(SentenceEnders);
        return splitAt >= 0 && splitAt + 1 < tail.Length
            ? tail[(splitAt + 1)..].TrimStart()
            : tail;
    }

    // A paragraph that alone exceeds maxSize is split on sentence boundaries and repacked
    // greedily. Pieces from here re-enter the caller's normal paragraph-packing loop, so
    // they can still merge with neighboring paragraphs if they end up small.
    public static IEnumerable<string> SplitIfOversized(string paragraph, int maxSize)
    {
        if (paragraph.Length <= maxSize)
        {
            yield return paragraph;
            yield break;
        }

        var sb = new StringBuilder();
        foreach (var sentence in SplitSentences(paragraph))
        {
            if (sb.Length > 0 && sb.Length + sentence.Length + 1 > maxSize)
            {
                yield return sb.ToString().Trim();
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(sentence);
        }
        if (sb.Length > 0)
            yield return sb.ToString().Trim();
    }

    // Splits content into alternating table/prose runs. A block only counts as a table if
    // it's at least 2 consecutive lines matching the markdown table-row shape - a lone line
    // that happens to contain "|" is left as ordinary prose, not treated as a table.
    public static List<(bool IsTable, string Text)> SplitIntoBlocks(string content)
    {
        var raw = new List<(bool IsTable, List<string> Lines)>();

        foreach (var line in content.Split('\n'))
        {
            var isTableLine = TableRowLine.IsMatch(line);
            if (raw.Count > 0 && raw[^1].IsTable == isTableLine)
                raw[^1].Lines.Add(line);
            else
                raw.Add((isTableLine, [line]));
        }

        // Demote a lone matching line back to prose - a real table needs at least 2 lines
        // (header + a separator or data row).
        for (int i = 0; i < raw.Count; i++)
            if (raw[i].IsTable && raw[i].Lines.Count < 2)
                raw[i] = (false, raw[i].Lines);

        // Re-merge any prose runs that are now adjacent after that demotion.
        var blocks = new List<(bool IsTable, string Text)>();
        foreach (var (isTable, lines) in raw)
        {
            var text = string.Join("\n", lines);
            if (blocks.Count > 0 && !blocks[^1].IsTable && !isTable)
                blocks[^1] = (false, blocks[^1].Text + "\n" + text);
            else
                blocks.Add((isTable, text));
        }

        return blocks;
    }

    // A table that fits in one chunk is returned whole. Otherwise it's split row-by-row,
    // repeating the header (and separator row, if present) at the top of every continuation
    // chunk. A single data row that alone exceeds maxChars is kept intact rather than
    // hard-split - cutting mid-row would corrupt column alignment, worse than one oversized chunk.
    public static List<string> ChunkTable(string tableBlock, int maxChars)
    {
        if (tableBlock.Length <= maxChars)
            return [tableBlock];

        var lines       = tableBlock.Split('\n');
        var headerCount = lines.Length > 1 && LooksLikeSeparatorRow(lines[1]) ? 2 : 1;
        var header      = lines[..headerCount];
        var dataRows    = lines[headerCount..];

        var chunks     = new List<string>();
        var current    = new List<string>(header);
        var currentLen = string.Join("\n", header).Length;

        foreach (var row in dataRows)
        {
            if (current.Count > headerCount && currentLen + 1 + row.Length > maxChars)
            {
                chunks.Add(string.Join("\n", current));
                current    = [.. header, row];
                currentLen = string.Join("\n", header).Length + 1 + row.Length;
            }
            else
            {
                current.Add(row);
                currentLen += 1 + row.Length;
            }
        }

        if (current.Count > headerCount)
            chunks.Add(string.Join("\n", current));

        return chunks;
    }

    // A GFM separator row looks like "|---|:---:|---:|" - every cell between pipes contains
    // only dashes, colons, and whitespace.
    private static bool LooksLikeSeparatorRow(string line)
    {
        var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
        return cells.Length > 0 && cells.All(cell => cell.Trim().Length > 0 && cell.Trim().All(c => c is '-' or ':'));
    }
}
