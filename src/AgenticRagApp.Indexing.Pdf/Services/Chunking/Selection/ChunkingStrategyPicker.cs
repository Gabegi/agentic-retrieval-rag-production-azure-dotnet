namespace AgenticRagApp.Indexing.Pdf.Services;

// Step 5 of DetermineStrategy: given everything the earlier steps learned, which branch
// chunks this document?
//
// Every branch is EARNED; Fallback is the default. A document earns HeadingBased by
// usable sections, TableAware by table dominance, SingleSection by being small enough to
// genuinely stand as one section (plus at least one heading to anchor it). What earns
// nothing falls to Fallback - which is honest: a large document with no usable structure
// is the "large but unstructured" case, and hiding it under a benign SingleSection label
// would bury exactly the documents that need attention.
//
// Sections are checked before tables because a document with both (the CAOs: hundreds of
// headings, dozens of tables) is heading-shaped; its tables are an atomicity constraint
// for the splitter, not the document's shape.
public static class ChunkingStrategyPicker
{
    // SingleSection needs at least one heading to anchor the section; a small document
    // without even one is indistinguishable from an extraction problem and falls through.
    public const int MinHeadingsForSingleSection = 1;

    public static ChunkingStrategyKind Pick(
        DocumentSizeClass sizeClass, bool hasUsableSections, bool isTableShaped, int headingCount)
    {
        // Content likely lives in images - none of the text signals below can be trusted.
        if (sizeClass == DocumentSizeClass.Picture)
            return ChunkingStrategyKind.Fallback;

        if (hasUsableSections)
            return ChunkingStrategyKind.HeadingBased;

        if (isTableShaped)
            return ChunkingStrategyKind.TableAware;

        // Small = fits in about one returned unit (the same line the parent grain uses),
        // so "the whole document is one section" is actually true of it.
        if (sizeClass == DocumentSizeClass.Small && headingCount >= MinHeadingsForSingleSection)
            return ChunkingStrategyKind.SingleSection;

        return ChunkingStrategyKind.Fallback;
    }
}
