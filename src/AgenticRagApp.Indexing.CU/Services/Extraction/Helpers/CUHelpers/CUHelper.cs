using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// The typed-response structure mapper: one call in, the extraction stage's structure out. CU
// classifies, the helpers map - no regex over the markdown, no "#"-counting. Replaces
// MarkdownStructureMapper (2026-08-26); the plan and its decisions live in
// docs/2608/260826/cuhelper-typed-structure-plan.md, the 2026-09-09 review of the helpers in
// docs/2609/260909/cu-helpers-review.md.
//
// The facade owns one thing: the ORDER of the helpers (page spans first - every other helper
// attributes pages off them). It used to also join chart/mermaid payloads onto their figures
// from two payload-only helpers; those were folded into CuFigureHelper (2026-09-09).
//
// The markdown is returned VERBATIM - no stripping, no rewriting (user decision 2026-08-26,
// "no heuristics anywhere": a PageFurnitureStripper was built here and deleted the same day).
// Every offset addresses the exact string the service returned, which is the string chunking
// cuts - one coordinate system, no transformation between the service and the index.
//
// Map-what's-there-and-warn throughout (decision 2026-08-26): a response missing a typed
// collection degrades that one output and says so in Warnings - it never fails the file and
// never falls back to markdown parsing. The same bar applies to substitutes: a document with
// no Role=Title paragraph has no title (null), not its first heading (fallback removed
// 2026-09-09 - it fired on 12 of 51 documents and produced "Inleiding", "Inhoudsopgave" and a
// copyright line as titles).
internal static class CUHelper
{
    internal sealed record MappedDocument(
        string                   Markdown,
        IReadOnlyList<PageSpan>  PageSpans,
        PdfDocumentStructure     Structure,
        string?                  Title,
        IReadOnlyList<string>    Warnings,
        // How well the service says it read this document - see WordConfidenceSummary. A
        // trailing default because it is a measurement about the mapping, not part of it:
        // nothing downstream of the report reads it, and every existing construction of this
        // record (empty-markdown early return, tests) stays valid without it. Null = the
        // response carried no words, which is not the same as zero confidence.
        WordConfidenceSummary?   WordConfidence = null,
        // CU's generated whole-document summary, off fields.Summary - see DocumentSummary for
        // why it is report-only and why grounding is a count. Trailing default for the same
        // reason as WordConfidence. Null = the response carried no Summary field.
        DocumentSummary?         Summary = null);

    // stringEncoding is AnalysisResult.StringEncoding - the service's echo of what span
    // encoding it actually applied. The SDK's typed Analyze overload hardcodes utf16 on every
    // analyze call (verified against SDK 1.1.0); anything else coming back means offsets may
    // drift on non-BMP characters, which is worth a warning on every document rather than a
    // silent misalignment.
    internal static MappedDocument Map(DocumentContent document, string? stringEncoding)
    {
        var warnings = new List<string>();
        var markdown = document.Markdown ?? "";

        if (!string.Equals(stringEncoding, "utf16", StringComparison.OrdinalIgnoreCase))
            warnings.Add(
                $"CU span encoding is '{stringEncoding ?? "(null)"}', not utf16 - typed offsets may drift " +
                "on non-BMP characters - the SDK's typed Analyze overload should have sent utf16.");

        if (markdown.Length == 0)
        {
            warnings.Add("CU returned empty markdown for this document.");
            return new MappedDocument("", [], PdfDocumentStructure.Empty, null, warnings);
        }

        var unit = document.Unit?.ToString();

        // 1. Page spans first: every other helper's PageNumber attribution reads them.
        var pageSpans = CuPageHelper.BuildPageSpans(document, markdown, unit, warnings);

        // 2. The outline (title, headings, depth, sections) and the furniture paragraphs.
        var (headings, title, sections) = CuOutlineHelper.Build(document, markdown, pageSpans, warnings);
        var boilerplate = CuPageHelper.BuildBoilerplate(document, pageSpans);

        // 3. The rest of the typed structure.
        var structure = new PdfDocumentStructure(
            Headings:       headings,
            Boilerplate:    boilerplate,
            Tables:         CuTableHelper.Build(document, pageSpans, warnings),
            Figures:        CuFigureHelper.Build(document, pageSpans, warnings),
            Sections:       sections,
            Annotations:    CuAnnotationHelper.Build(document, pageSpans),
            Hyperlinks:     CuHyperlinkHelper.Build(document, pageSpans),
            // Page-scoped and rare by nature; mapped so the corpus question ("do we have any,
            // and are the 36 formulas still all euro misreads?") is answered by a report column
            // instead of a hand-count of the raw capture.
            Barcodes:       CuPageHelper.BuildBarcodes(document),
            Formulas:       CuPageHelper.BuildFormulas(document),
            LineCount:      CuPageHelper.CountLines(document));

        // No step 4. A furniture-stripping pass sat here for a few hours on 2026-08-26 and was
        // deleted the same day (user decision, "no heuristics anywhere") - see the class
        // comment. The 335-tiny/77-duplicate chunk baseline that motivated it was measured
        // before this typed-mapping train anyway, so its removal costs nothing that was ever
        // measured on this pipeline.
        return new MappedDocument(
            markdown, pageSpans, structure, title, warnings,
            WordConfidence: CuPageHelper.SummariseWordConfidence(document),
            Summary:        SummaryOf(document));
    }

    // fields.Summary, the prebuilt's own generated summary. The facade owns this rather than a
    // helper because it is the only FIELD on the response - every Cu*Helper maps content
    // elements, and one field does not justify a sixth helper.
    //
    // GetFieldOrDefault is the SDK's own idiom for this (ContentFieldDictionaryExtensions), so
    // a missing Fields dictionary and a missing key are the same null here. Blank text is null
    // too: "the service returned a Summary field carrying nothing" is not a summary, and the
    // report shows it as absent rather than as an empty string.
    private static DocumentSummary? SummaryOf(DocumentContent document)
    {
        var field = document.Fields?.GetFieldOrDefault("Summary");
        var text  = field?.Value?.ToString();

        if (string.IsNullOrWhiteSpace(text)) return null;

        return new DocumentSummary(
            text,
            field!.Confidence,
            // The spans themselves stay behind (user decision 2026-09-08) - see DocumentSummary.
            GroundingSpanCount: field.Spans?.Count ?? 0);
    }
}
