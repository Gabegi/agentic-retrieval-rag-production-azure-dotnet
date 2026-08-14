using System.Text.RegularExpressions;
using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Combined output of GetHeadingsHelper.GetHeadings: the merged heading list,
// every bare numbered-label word seen among heading-role paragraphs before merge
// outcome was decided, and every paired zero-body heading merge performed (D2). All
// three come from one walk over the same paragraphs, deliberately - a second, separate
// pass over the same data could drift out of sync with the merge logic if one changed
// without the other (the whole point of moving vocabulary discovery to merge time is
// that there's exactly one place doing the walk).
public sealed record HeadingsResult(
    IReadOnlyList<Heading> Headings,
    IReadOnlyDictionary<string, int> NumberedLabelsSeen,
    IReadOnlyList<string> PairedHeadingMerges);

// Paragraphs DI classified as real section structure, not incidental roles.
// Offset/PageNumber come from Spans/BoundingRegions: DocumentParagraph has no
// PageNumber of its own.
internal static partial class GetHeadingsHelper
{
    // A bare numbered label with no title text of its own - "Artikel 9",
    // "Hoofdstuk 4", "Bijlage XII", "Article 9", "Section 3.2". Shape-based
    // (a word plus a number or roman numeral) rather than a language-specific
    // word list: a real standalone heading like "Definities" or "Scope" never
    // matches, since it has no number. See docs/2608/260810 for the corpus
    // evidence this is scoped to (checked against captured headings in both
    // validated documents: matches only the one confirmed legitimate orphan,
    // introduces no new merge candidates).
    // The roman-numeral branch requires TWO OR MORE IVXLCDM letters. A single
    // letter is inherently ambiguous between a roman numeral and an ordinary
    // list letter, and D3's corpus scan (docs/2608/260811/
    // d3-short-label-discovery-findings.md) settled which reading is real here:
    // the branch fired exactly once across all 51 documents, on
    // "Mobiliteitsklasse C" - a mobility class labelled A-E, where only C is
    // also a roman numeral. Its siblings "Mobiliteitsklasse A/B/E" did not
    // match, so one of five identical headings was treated differently purely
    // by letter. Requiring 2+ characters removes that false positive and costs
    // nothing measurable: the scan found zero genuine roman-numeral headings of
    // any length in the corpus, so no real case is lost, while "Bijlage XII" /
    // "Hoofdstuk IV" style labels still match if they ever appear. This is the
    // "tighten only if a real one turns up" condition the previous comment set
    // out, now met.
    // Still a pattern match rather than a validity check - "Bijlage CIVIL"
    // would match - which remains acceptable for the same reason as before:
    // zero such cases in the corpus.
    // Label word captured (group 1) for the vocabulary-discovery signal.
    // internal (not private): GetQualityWarningsHelper.HeadingWarnings shares
    // this regex to identify post-merge orphans by the same shape, rather
    // than duplicating the pattern.
    [GeneratedRegex(@"^(\p{L}+)\s+(?:\d+(?:\.\d+)*|[IVXLCDM]{2,})$")]
    internal static partial Regex BareNumberedLabelWithWord();

    // B4 (pre-chunking-action-items.md) - broader than BareNumberedLabelWithWord above,
    // which only matches a *bare* label with no title merged in. This matches a heading
    // that carries a numbering cross-check at all, title text or not - both numbering
    // shapes confirmed in the corpus: a word+number label at the very start ("Artikel 9
    // Vakantie", "Hoofdstuk IV") and a pure dotted-number prefix ("1.1 Voedselveiligheid...",
    // "10. Producten bereiden" - the exact regex hygienecode-numbering-findings.md already
    // validated reproduces Pass 2's 32% figure). DocumentProfileHelper uses this for B4's
    // per-document numbered-heading share.
    // Roman branch requires 2+ letters for the same reason as
    // BareNumberedLabelWithWord above - the two must agree on what counts as a
    // numeral, or a heading could be "numbered" for B4's share while not being a
    // bare label for the merge, on nothing but a single ambiguous letter.
    // Verified against the corpus: Hygiene Code's numbering is dotted-number
    // throughout, so its 123/385 (31.9%) share is unchanged by this.
    [GeneratedRegex(@"^(?:\p{L}+\s+(?:\d+(?:\.\d+)*|[IVXLCDM]{2,})\b|\d+(?:\.\d+)*\.?)(?=\s|$)")]
    internal static partial Regex NumberedHeadingPrefix();

    // Upper bound on a merge-candidate term's length (e.g. "opleiding",
    // "onbetaald verlof voor bijzondere gebeurtenissen"). Picked from the
    // corpus scan: every confirmed real term was well under this, every
    // confirmed body-prose continuation was over it or ended in punctuation.
    private const int MaxTermLength = 60;

    public static HeadingsResult GetHeadings(AnalyzeResult result)
    {
        var paragraphs = result.Paragraphs ?? [];
        var headings = new List<Heading>();
        var labelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pairedMerges = new List<string>();

        for (var i = 0; i < paragraphs.Count; i++)
        {
            var p = paragraphs[i];
            if (p.Role != ParagraphRole.Title && p.Role != ParagraphRole.SectionHeading) continue;

            var content = (p.Content ?? "").Trim();

            var labelMatch = content.Length > 0 ? BareNumberedLabelWithWord().Match(content) : Match.Empty;
            if (labelMatch.Success)
                labelCounts[labelMatch.Groups[1].Value] = labelCounts.GetValueOrDefault(labelMatch.Groups[1].Value) + 1;

            // D2 (pre-chunking-action-items.md) - paired zero-body headings (Hygiene Code:
            // a topic/question heading immediately followed by an "Acties..." imperative
            // heading, with no body paragraph between them - docs/2608/260810/heading-
            // validation-findings.md). Left unmerged, a naive heading-only splitter emits a
            // near-empty chunk for the first heading of every such pair.
            //
            // Gated on !labelMatch.Success: two bare numbered labels in a row ("Artikel 8",
            // "Artikel 9") are legitimately separate short articles, not a pair to merge - see
            // BareLabelFollowedByAnotherHeading_NeitherMerges. Only a heading that ISN'T
            // itself a bare numbered label (a real topic/question heading, not a TOC-style
            // article marker) is treated as a merge candidate. Beyond that gate, no further
            // vocabulary/shape filter is needed: ordinary document structure always has body
            // text between two real headings, so "the very next paragraph is itself
            // heading-role" is already the rare, specific signal on its own.
            //
            // Chains through more than a single pair (three-plus headings in a row with zero
            // body) by continuing to look one paragraph ahead of the merge in progress.
            //
            // Every merge is recorded in PairedHeadingMerges (surfaced as a warning by
            // GetQualityWarningsHelper.HeadingWarnings) precisely because this rule is
            // structural rather than vocabulary-scoped and hasn't been checked against the
            // live corpus yet - see DocumentIdentityResolver's threshold comments for the same
            // "flag for calibration" reasoning. A run that shows this firing somewhere that
            // isn't a real zero-body pair (e.g. two independently meaningful headings that
            // simply happen to be adjacent) is exactly the signal to narrow this rule further.
            if (!labelMatch.Success && i + 1 < paragraphs.Count &&
                (paragraphs[i + 1].Role == ParagraphRole.Title || paragraphs[i + 1].Role == ParagraphRole.SectionHeading))
            {
                var mergedContent = content;
                var lastMerged    = i;

                while (lastMerged + 1 < paragraphs.Count &&
                       (paragraphs[lastMerged + 1].Role == ParagraphRole.Title || paragraphs[lastMerged + 1].Role == ParagraphRole.SectionHeading))
                {
                    lastMerged++;
                    mergedContent = $"{mergedContent}\n{(paragraphs[lastMerged].Content ?? "").Trim()}";
                }

                var pairMerged = DiGeometryHelpers.ToHeading(p) with { Content = mergedContent };
                headings.Add(pairMerged with { Depth = ComputeDepth(p.Role, pairMerged.Offset, result.Content) });
                pairedMerges.Add(content.Length > 40 ? content[..40] + "…" : content);

                i = lastMerged; // consume every paragraph folded into this merge
                continue;
            }

            // DI sometimes splits a bare numbered label and its short
            // definition term (e.g. "Artikel 9" / "opleiding") into two
            // paragraphs, tagging only the first as a heading. Left unmerged,
            // the term line is silently dropped - it was never a heading-role
            // paragraph, so GetHeadings never sees it. Merge when the next
            // paragraph looks like a short label rather than the start of the
            // article's body prose.
            //
            // The merged Heading's Offset/PageNumber still come from p alone,
            // not from a span covering both paragraphs - fine for today's only
            // consumer (heading text/position for chunk boundaries), but a
            // future consumer that slices raw text by heading offset would
            // under-cover the merged heading by the length of the term line.
            if (labelMatch.Success && i + 1 < paragraphs.Count)
            {
                var next = paragraphs[i + 1];
                var nextContent = (next.Content ?? "").Trim();
                var looksLikeTerm = next.Role != ParagraphRole.Title && next.Role != ParagraphRole.SectionHeading
                                     && nextContent.Length > 0 && nextContent.Length <= MaxTermLength
                                     && !nextContent.EndsWith('.') && !nextContent.EndsWith(':')
                                     && !nextContent.EndsWith('?') && !nextContent.EndsWith(';') && !nextContent.EndsWith('!');

                if (looksLikeTerm)
                {
                    var merged = DiGeometryHelpers.ToHeading(p) with { Content = $"{content} {nextContent}" };
                    headings.Add(merged with { Depth = ComputeDepth(p.Role, merged.Offset, result.Content) });
                    i++; // consume the term paragraph so it isn't reprocessed
                    continue;
                }
            }

            var built = DiGeometryHelpers.ToHeading(p);
            headings.Add(built with { Depth = ComputeDepth(p.Role, built.Offset, result.Content) });
        }

        return new HeadingsResult(headings, labelCounts, pairedMerges);
    }

    // DI renders a SectionHeading paragraph as ATX markdown ("## Text") directly inline in
    // the whole-document Content string, with the "#" run positioned immediately before the
    // paragraph's own span offset - confirmed against real corpus data (docs/2608/260810/
    // validation/hygienecode-pages.json): "## 1. Inleiding", "### 1.1 Voedselveiligheids...",
    // "#### Veilig eten en drinken" line up with the SectionHeading paragraphs at those
    // offsets, run length tracking DI's own layout-based nesting. That run length is DI's
    // only depth signal - there's no separate structured level field anywhere in the response.
    //
    // Title is rendered as setext ("Text\n===") in raw Content, not ATX (GetPagesHelper only
    // normalizes that to "# " in the cleaned *per-page* output, not in the raw Content this
    // reads) - forced to depth 1 directly rather than scanning for a marker DI never wrote.
    //
    // Not every SectionHeading paragraph gets a "#" run either - a bare numbered TOC entry
    // ("1.", "2.") can carry the role without DI rendering it as ATX at all (also confirmed
    // against the same corpus data). Falls back to depth 1 whenever no run is found immediately
    // before the offset, or the offset is missing/out of range - never throws over a shape a
    // heading-depth guess doesn't strictly need to understand.
    private static int ComputeDepth(ParagraphRole? role, int? offset, string documentContent)
    {
        if (role == ParagraphRole.Title) return 1;
        if (offset is not { } pos || pos <= 0 || pos > documentContent.Length) return 1;

        // Skip the single space DI always emits between the "#" run and the heading text
        // ("## Text", never "##Text") before counting the run itself.
        if (documentContent[pos - 1] == ' ') pos--;

        var start = pos;
        while (start > 0 && documentContent[start - 1] == '#') start--;

        var depth = pos - start;
        return depth is >= 1 and <= 6 ? depth : 1;
    }
}
