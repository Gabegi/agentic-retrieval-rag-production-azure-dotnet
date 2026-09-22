using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Utils;

namespace AgenticRagApp.Indexing.CU.Services;

// The semantic head a CUT diagram fragment carries into its embedding: the figure's caption, or
// failing that the first 40 tokens of its generated description.
//
// Why (D214 §2.6, decided 2026-09-22): fragment 2..n of a cut flowchart is
// `{"from":"F","to":"Q","label":"Nee"},…` or `B --> C` - no head of its own. Its prefix carries
// the document title and heading path, which every sibling fragment in the section shares, so
// the prefix cannot tell the fragments apart and the body embeds to noise. Measured on run
// 260921/1: of the 74 blocks large enough to be cut, 9 have a caption, 57 a description, 17
// neither (their fence matches no figure - see below). Caption alone would have built all of
// this for 9 blocks; the capped description takes it to 57.
//
// HOW THE FIGURE IS FOUND. CU reports no span for the fence, and the figure's own span covers
// only the `![…](figures/…)` embed (D211 decision 6). What the fence body and FigureInfo.Payload
// share is the payload text itself, so the match is exact equality of the trimmed strings -
// 636 of 718 fences on run 260921/1. The other 82 differ from their payload by a few escaped
// characters and resolve to nothing here: absent, counted (D214 §2.8), not approximated.
//
// ONE RULE, TWO CALLERS. BlockCascade prices the result against the ceiling before the cutter
// runs; ChunkMetadataBuilder writes the same call's output into the prefix. A second resolver
// in either place is a ceiling that does not hold - the pattern PrefixBuilder documents.
//
// WHOLE BLOCKS GET NOTHING. Only a fragment lacks a head; a diagram that fit whole carries its
// own opener and closer, and the description already rides beside it as the embed's alt text.
// The callers enforce that: the cascade passes the cost only to the cut path (DiagramCutter),
// the metadata step resolves only for BoundaryLevel.DiagramElement chunks.
public static class DiagramContext
{
    // A chosen cap, recorded as such - not derived from a measurement (D214 §2.6). What IS
    // measured: description p50 is 237 characters (D183 §2), so most descriptions are cut, and
    // the longest caption in the corpus (195 characters) sits under it.
    public const int DescriptionTokenCap = 40;

    // fenceText is any string whose FIRST complete fence is the diagram - the Diagram block's
    // own Text in the cascade, the fence's slice of doc.Content in the metadata step. The body
    // is compared to each figure's Payload after trimming both.
    public static string? Resolve(string fenceText, IReadOnlyList<FigureInfo>? figures)
    {
        if (figures is null || figures.Count == 0 || string.IsNullOrEmpty(fenceText)) return null;

        var fences = DiagramMarkup.Fences(fenceText);
        if (fences.Count == 0) return null;

        var (bodyStart, bodyEnd) = DiagramMarkup.Body(fenceText, fences[0]);
        var body = fenceText[bodyStart..bodyEnd].Trim();
        if (body.Length == 0) return null;

        var figure = figures.FirstOrDefault(f =>
            f.Payload is not null && string.Equals(f.Payload.Trim(), body, StringComparison.Ordinal));

        if (figure is null) return null;

        if (!string.IsNullOrWhiteSpace(figure.Caption))     return figure.Caption.Trim();
        if (!string.IsNullOrWhiteSpace(figure.Description)) return TokenCounter.Truncate(figure.Description.Trim(), DescriptionTokenCap);

        return null;
    }

    // The metadata step's entry: which fence of the document did this chunk come out of? The
    // first fragment starts AT the fence's opener, every later one starts inside it, so the
    // fence containing chunkStart is the one. fences is DiagramMarkup.Fences(content), computed
    // once per document by the caller rather than once per chunk.
    public static string? ForChunk(
        string content, IReadOnlyList<(int Start, int End)> fences, int chunkStart, IReadOnlyList<FigureInfo>? figures)
    {
        foreach (var fence in fences)
            if (fence.Start <= chunkStart && chunkStart < fence.End)
                return Resolve(content[fence.Start..fence.End], figures);

        return null;
    }
}
