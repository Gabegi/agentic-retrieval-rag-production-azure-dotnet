using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Indexing.DI.Utils;

namespace AgenticRagApp.Indexing.DI.Services;

// One DocumentOutcome from one document's run. Every input document gets exactly one row -
// that is the whole contract of the Documents section, and it is what makes "20 of 51 documents
// are missing from the index" a readable fact rather than an absence.
//
// Replaces ChunkingService.NotChunked, which took a family and a vector source that every
// caller passed null for. They are real parameters here: IdentityResolutionResult carries both
// per document (Families, IdentityVectorSourceOf), so even a row for a document that produced
// nothing can say which family it was resolved into and whether its identity vector was paid
// for this run.
public static class DocumentRowBuilder
{
    // Below this a chunk is too small to answer anything on its own. Reported rather than
    // filtered: the minimum-content rule already removes the residue, so what is left here is
    // genuine text that simply came out short, and the count is a splitter signal.
    private const int ShortChunkTokens = 50;

    public static DocumentOutcome Build(
        PdfExtractionDocument doc,
        DocumentRunFacts?     facts,
        DocumentFamily?       family,
        string?               vectorSource,
        bool                  isInMultiMemberFamily,
        string?               notReachedReason)
    {
        var chunks = facts?.Chunks ?? [];
        var tokens = chunks.Select(c => c.Metadata.TokenCount).OrderBy(t => t).ToList();

        return new DocumentOutcome(
            SourceId: doc.SourceId,
            Title:    doc.Title,

            // No facts at all means the loop never reached this document - the stage threw
            // before its turn.
            Outcome:  facts?.Outcome ?? "not_reached",
            Reason:   facts?.Reason  ?? notReachedReason,

            // Read off the profile, not off a route: on a not_reached row no route was picked,
            // and a route-derived answer would be an invention rather than a measurement.
            FailedExtractionGate:  doc.Profile is { HasExtractableContent: false },
            ResidueChunksDropped:  facts?.ResidueDropped ?? 0,
            TocChunksDropped:      facts?.TocDropped     ?? 0,

            FamilyId:              family?.FamilyId,
            IsInMultiMemberFamily: isInMultiMemberFamily,
            DomainTag:             family?.DomainTag,
            ConfusableWith:        family?.ConfusableWith ?? [],
            IdentityVectorSource:  vectorSource,

            ChunkCount:            chunks.Count,
            HeadingsTotal:         facts?.HeadingsTotal   ?? 0,
            HeadingsLocated:       facts?.HeadingsLocated ?? 0,

            // Sized from the classifier even though the gate no longer uses it, so a row still
            // says how big the document was. DocumentSizeClassifier is report-only now: the
            // gate reads Profile.EstimatedTokens against its own ceiling directly.
            SizeClass:             DocumentSizeClassifier.Classify(doc.Profile).ToString(),

            // Null where no route was picked. Naming one would read as a route that ran and
            // produced nothing, which is a different fact from never having run.
            Strategy:              facts?.Route,

            SectionCount:          chunks.Select(c => c.SectionIndex).Distinct().Count(),
            TokenP50:              Percentile(tokens, 0.50),
            TokenP99:              Percentile(tokens, 0.99),
            ChunksAboveCeiling:    tokens.Count(t => t > ChunkingBudget.TokenCeiling),
            ShortChunks:           tokens.Count(t => t < ShortChunkTokens),
            DegradedChunks:        chunks.Count(c => c.Degraded),

            // How many headings the document declared, whatever the route did with them. On a
            // Recursive row this is how many headings the route discarded - which is what the
            // Content Understanding work is expected to recover.
            HeadingCount:          doc.Headings.Count,

            // A reported signal, not a routing input: tables are an atomicity constraint for
            // the splitter, and TableChecker stopped influencing the route with the two-strategy
            // design.
            IsTableShaped:         TableChecker.IsTableShaped(doc.Tables.Count, doc.Profile),

            // On the recursive route the title is the ONLY prefix, so an empty-title document
            // emits chunks whose embedded text is bare body with zero identity in the vector.
            // DocumentIdentityResolver only drops documents with NEITHER title nor headings, so
            // this case survives selection silently without this flag.
            EmptyTitle:            string.IsNullOrWhiteSpace(doc.Title));
    }

    // Nearest-rank, on the tokenizer counts step 4 stamped. Zero on a document that produced no
    // chunks, which reads correctly next to ChunkCount 0.
    private static int Percentile(IReadOnlyList<int> sorted, double p)
    {
        if (sorted.Count == 0) return 0;

        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}
