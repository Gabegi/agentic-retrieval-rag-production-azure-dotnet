using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// One document's full routing context: the doc, its resolved identity, and the strategy
// decision - everything both the chunk pass and the report row need.
public sealed record DocumentPlan(
    PdfExtractionDocument    Doc,
    DocumentFamily?          Family,
    string?                  VectorSource,
    ChunkingStrategyDecision Decision);

// Decides how documents should be chunked, without chunking anything.
//
// SelectStrategies is the chunking stage's selection pass: one plan per document, in
// deterministic order, each pairing the document with its resolved identity and its
// strategy decision. A separate pass from chunking on purpose - the plans feed the run
// report as well as the chunker, so a run that dies mid-chunking can still say what every
// document's route was.
//
// DetermineStrategy runs the selection steps in order and returns everything they learned
// as one decision record. Each step lives as a static class under Selection/ so it can be
// tested on its inputs alone. ChunkingService dispatches on Decision.Strategy and records
// the rest on the report row.
public sealed class ChunkingStrategySelector
{
    public IReadOnlyList<DocumentPlan> SelectStrategies(
        IReadOnlyList<PdfExtractionDocument> docs, IdentityResolutionResult resolved) =>
        docs.OrderBy(d => d.SourceId, StringComparer.Ordinal)
            .Select(doc => new DocumentPlan(
                doc,
                resolved.Families.GetValueOrDefault(doc.SourceId),
                resolved.IdentityVectorSourceOf.GetValueOrDefault(doc.SourceId),
                DetermineStrategy(doc)))
            .ToList();

    public ChunkingStrategyDecision DetermineStrategy(PdfExtractionDocument doc)
    {
        // The token ceiling is not a step here: over-the-ceiling is per section, answered
        // by SectionSplitter at cut time with the section's actual block composition.
        var sizeClass    = DocumentSizeClassifier.Classify(doc.Profile);          // 1. picture / L / M / S
        var parentGrain  = ParentGrainChecker.Determine(sizeClass);               // 2. whole doc vs parent/child
        var hasSections  = SectionChecker.HasUsableSections(doc.Headings.Count, doc.Profile); // 3. usable sections?
        var tableShaped  = TableChecker.IsTableShaped(doc.Tables.Count, doc.Profile);         // 4. mostly table?
        var strategy     = ChunkingStrategyPicker.Pick(                                       // 5. the branch
                               sizeClass, hasSections, tableShaped, doc.Headings.Count);

        // No per-document logging here: the decision lands on the run report row
        // (SizeClass, Strategy, FailedExtractionGate), and ChunkingService logs the
        // per-run routing distribution. A warning per picture document was ~20 expected
        // lines every run - noise, not signal.
        return new ChunkingStrategyDecision(
            sizeClass, parentGrain, hasSections, doc.Headings.Count, strategy);
    }
}
