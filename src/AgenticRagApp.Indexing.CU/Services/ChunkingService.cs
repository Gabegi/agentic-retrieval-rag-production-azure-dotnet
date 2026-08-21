using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Indexing.CU.Utils;

namespace AgenticRagApp.Indexing.CU.Services;

// Turns extracted documents into indexed chunks, in five steps:
//
//   1. resolve document identity  (DocumentIdentityResolver - family, domain tag, confusables)
//   2. read headings and sections, and gate on them  (the gate below -> one of two routes)
//   3. chunk  (DeclaredBoundaryStrategy | RecursiveStrategy)
//   4. metadata  (ChunkMetadataBuilder)
//   5. report  (ChunkingReporter, one call, from the finally)
//
// The split of responsibility is deliberate. A strategy decides WHERE to cut and knows nothing
// about ids, Zenya metadata or embedding prefixes; ChunkMetadataBuilder decides how a cut
// becomes an indexed row and knows nothing about headings or ceilings; and this class decides
// which of the two routes a document takes and nothing else.
//
// Step 5 is the same division applied to reporting. This method used to carry twelve locals
// declared before its own try block, three aggregation blocks and a row-assembly finally, all of
// it there to be read by the report at the end. It now carries one ChunkingRunState, tells it
// what happened as it goes, and makes one call - so what is left here is the algorithm.
public class ChunkingService : IChunkingService
{
    private readonly IDocumentChunkingStrategy   _declaredBoundaryStrategy;
    private readonly IDocumentChunkingStrategy   _recursiveStrategy;
    private readonly DocumentIdentityResolver    _identityResolver;
    private readonly ChunkMetadataBuilder        _metadataBuilder;
    private readonly ChunkingReporter            _reporter;

    public string Name => "TwoAxisChunking";

    public ChunkingService(
        DeclaredBoundaryStrategy declaredBoundary,
        RecursiveStrategy        recursive,
        DocumentIdentityResolver identityResolver,
        ChunkMetadataBuilder     metadataBuilder,
        ChunkingReporter         reporter)
    {
        _declaredBoundaryStrategy = declaredBoundary;
        _recursiveStrategy        = recursive;
        _identityResolver = identityResolver;
        _metadataBuilder  = metadataBuilder;
        _reporter         = reporter;
    }

    // Async because family/domain identity resolution needs an embedding call before splitting
    // can start. Resolved once per document, up front, rather than per chunk.
    //
    // instanceId/startedAt are here only to name this run's report blob (the activity owns
    // both) - the same reason ExtractionService.ExtractAsync takes an instanceId. Null is
    // allowed for callers outside an orchestration, matching StageReportPath.
    //
    // The report is written from a finally, so a run that throws part way through still
    // produces one, marked with the stage it died in. That is the whole point: the failure
    // modes worth diagnosing (a wrong-dimension vector, duplicate SourceIds) throw out of
    // identity resolution, and before this the activity's artifact was written only on the
    // success path - so the runs most in need of a report were exactly the ones without one.
    public async Task<(IReadOnlyList<ChunkObject> Docs, ChunkingStageMetrics Stats, IReadOnlyList<FamilyMove> FamilyMoves)> ChunkDocumentsAsync(
        IReadOnlyList<PdfExtractionDocument> docs,
        string?                              instanceId = null,
        DateTimeOffset?                      startedAt  = null,
        CancellationToken                    ct         = default)
    {
        var allChunks = new List<ChunkObject>();

        // Holds the list by reference, so the report carries whatever was accumulated when an
        // exception fires rather than only what a completed run returned.
        var state = new ChunkingRunState(
            docs, allChunks, instanceId, startedAt ?? DateTimeOffset.UtcNow);

        try
        {
            // 1. Identity: family, domain tag, confusables - needs an embedding call, so it
            //    runs once for the whole batch before anything is cut.
            var resolved = await _identityResolver.ResolveDocumentIdentityAsync(docs, ct);

            state.IdentityResolved(resolved);
            state.Stage = "heading-section-gate";

            // Steps 2-4 are per document: the gate reads one document's signals, a strategy
            // cuts that document, and its chunks get their metadata before the next document
            // starts. Nothing carries between iterations except `allChunks`.
            foreach (var extracted in docs)
            {
                // Attach what step 1 resolved. One dictionary lookup - Families is keyed by
                // SourceId - and from here on the family rides on the document itself, so
                // neither the strategy nor the metadata builder needs it handed over
                // separately. Null when the resolver produced no family for this document.
                var doc = extracted with
                {
                    Family = resolved.Families.GetValueOrDefault(extracted.SourceId),
                };

                // One document's failure is captured and the loop continues; the stage still
                // fails, once, after every other document has been chunked and reported - see
                // ThrowIfAnyDocumentFailed below. A partial result is never silently indexed,
                // and one bad document never costs the corpus its report.
                //
                // Cancellation is not a document failure: it means the whole run is being torn
                // down, so it propagates rather than being recorded against whichever document
                // happened to be in hand.
                try
                {
                    // 2. Read the declared structure and gate on it. The formula is not repeated
                    //    here on purpose - DocContainsHeadingOrLessThan4kTokens below IS the
                    //    routing decision, and this is its only caller.
                    //
                    //    The branch picks a STRATEGY rather than calling one, so step 3 and step
                    //    4 are each written once. It also means the route name step 4 stamps is
                    //    the strategy's own Name, so a chunk's route_name cannot disagree with
                    //    the class that actually cut it.
                    IDocumentChunkingStrategy strategy;

                    if (DocContainsHeadingOrLessThan4kTokens(doc))
                    {
                        // 2b. Anchor the declared headings, and count how many could be placed.
                        //
                        //     This runs HERE rather than inside the strategy for one reason: the
                        //     counters have to survive a document that goes on to produce no
                        //     chunks. A document whose every heading failed to locate is exactly
                        //     the case the >2% escalation exists to catch, and it is also the
                        //     likeliest document to emit nothing - so counters recovered from the
                        //     chunks would drop it.
                        //
                        //     Locate is the whole read: it orders headings by raw DI offset,
                        //     finds each one's real position in the cleaned text, and pairs
                        //     consecutive hits into contiguous sections, preamble and zero-body
                        //     merges included. Raw offsets ORDER headings and never slice -
                        //     cleaning drifts length by a measured 1.066-1.202x, so a raw offset
                        //     cuts wrong, and further wrong the deeper into the document it is.
                        state.Stage = "heading-location";

                        var located = HeadingLocator.Locate(
                            doc.Content, doc.Headings, doc.PageSpans, doc.Sections);

                        state.HeadingsLocatedFor(doc, located);

                        doc = doc with { LocatedSections = located.Sections };

                        strategy = _declaredBoundaryStrategy;
                    }

                    else
                    {
                        // No anchoring on this route, and therefore nothing recorded against the
                        // heading counters. The recursive route deliberately does not use
                        // whatever headings the document has, so reporting them as unlocated
                        // would fill the failure metric with headings that never failed - they
                        // were not attempted.
                        strategy = _recursiveStrategy;
                    }

                    // 3. Chunking: the routed strategy cuts this document. It returns the cuts
                    //    and nothing else - WHERE to cut is all it decides.
                    state.Stage = "chunking";

                    var chunks = await strategy.ChunkDocumentAsync(doc, ct);

                    // 3b. The minimum-content rule. A cut whose body carries almost no letters or
                    //     digits is vector residue, not content - this corpus produced a literal
                    //     "£ £" cut and a bare "#" one. Indexed, they occupy a row and can come
                    //     back as a match for a query they mean nothing about.
                    //
                    //     Dropped here rather than inside the strategy: deciding WHERE to cut and
                    //     deciding whether a cut is worth indexing are different judgements, and
                    //     the strategy only makes the first.
                    //
                    //     Measured on Content, which at this point is the BARE BODY - the prefix
                    //     is a separate field and is not joined on until EmbeddingText composes
                    //     it. That ordering is the whole point: a prefix is dozens of
                    //     alphanumeric characters, so residue measured after it looks substantial.
                    //     The heading-only rule rides along here rather than in its own pass:
                    //     it is the same judgement on the same string ("is this cut worth
                    //     indexing"), so its drops are residue and are counted as residue.
                    var kept = chunks
                        .Where(c => !IsResidue(c.Content))
                        .Where(c => !DropHeadingOnlyChunks || !IsHeadingOnly(c))
                        .ToList();

                    // 3b-ii. Navigation, not content. A table of contents indexed as a chunk
                    //        carries the vocabulary of every section it lists and answers none
                    //        of them - it matches broadly and satisfies nothing.
                    //
                    //        Counted apart from residue rather than folded into it. The two
                    //        answer different questions ("is this document shedding junk cuts"
                    //        versus "did we catch its front matter"), and a merged counter
                    //        cannot tell a dropped TOC from a dropped "£ £".
                    var beforeToc = kept.Count;
                    kept = kept.Where(c => !TocFilter.IsTableOfContents(c)).ToList();
                    var tocDropped = beforeToc - kept.Count;

                    // 3c. The fall-through tripwire. HardCut is the ladder's terminator - fixed
                    //     windows, mid-word by construction - and HardCutter's own contract says
                    //     arrivals mean the extraction lost its separators, not that the prose is
                    //     unusual. The 260818 run measured 0 HardCut chunks in 2,997, so this is
                    //     a regression detector with a real baseline: a document suddenly falling
                    //     through this often means its extraction broke, and indexing its
                    //     mid-word fragments would hide that. Thrown here so the standard
                    //     per-document failure path captures it (outcome "failed", stage fails
                    //     after every document is processed) rather than silently indexing junk.
                    var hardCuts = kept.Count(c => c.BoundaryLevel == BoundaryLevel.HardCut);
                    if (hardCuts >= MinHardCutsToFail && hardCuts > kept.Count * MaxHardCutShare)
                        throw new InvalidOperationException(
                            $"{hardCuts} of {kept.Count} chunks are HardCut fall-through - " +
                            "the extraction likely lost its separators for this document " +
                            $"(threshold {MaxHardCutShare:P0}, baseline 0 per 260818 run).");

                    // 4. Metadata: ONE call for the whole document, not one per chunk. What is a
                    //    property of the DOCUMENT is read once inside and copied onto every chunk
                    //    of it - two chunks of one file disagreeing about its family_id is not
                    //    something anything downstream could detect.
                    //
                    //    Only the survivors: nothing is stamped onto rows already discarded.
                    state.Stage = "chunk-metadata";

                    _metadataBuilder.AddMetadata(kept, doc, strategy.Name);

                    allChunks.AddRange(kept);

                    state.Chunked(doc, kept, chunks.Count, strategy.Name, tocDropped);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    state.DocumentFailed(doc, ex);
                }
            }

            state.Stage = "metrics";

            var sourceDocumentIds = docs.Select(d => d.SourceId).Distinct(StringComparer.Ordinal).ToList();

            // ResidueChunksDropped is stamped here rather than inside Compute: the dropped chunks
            // are not in allChunks by this point, so the count only exists on the state.
            var stats             = ChunkingStageMetrics.Compute(allChunks, Name, sourceDocumentIds)
                                    with { ResidueChunksDropped = state.ResidueDropped,
                                           TocChunksDropped     = state.TocDropped };

            state.Stats = stats;


            // After metrics on purpose: a run that lost documents is the one whose report most
            // needs its numbers, and this throws.
            state.ThrowIfAnyDocumentFailed();

            state.Stage = null;

            // FamilyMoves leaves with the chunks rather than being dropped here, because the only
            // thing that can act on it is the upload stage: a re-homed document's own bytes did
            // not change, so extraction skipped it, nothing in this list belongs to it, and its
            // indexed rows still carry the family_id it had before another document's arrival
            // moved it. UploadService patches those rows - see PatchMovedFamiliesAsync.
            return (allChunks, stats, resolved.FamilyMoves);
        }
        catch (Exception ex)
        {
            state.Threw(ex);
            throw;
        }
        finally
        {
            // 5. Report: one call, and the only reporting in this method. Everything it needs is
            //    on the state, including whatever was accumulated before a throw. It never
            //    rethrows - a reporting failure must not mask the stage's own outcome.
            await _reporter.WriteAsync(state, ct);
        }
    }

    // Step 2, the gate: is the document's declared boundary worth honouring?
    //
    //   at least 2 headings AND at least 0.1 headings per 1,000 chars
    //   OR: under 4,000 estimated tokens and at least 1 heading
    //
    // True -> DeclaredBoundaryStrategy. False -> RecursiveStrategy.
    //
    // Not "does the document have headings": one heading in 30k chars is a label, not a
    // structure, and 2 headings on a 400-page document pass a count check while structuring
    // nothing. Density is measured per 1,000 CHARS, not per page - page count is this corpus's
    // weakest size signal. The second clause is the N=1 admission: the document fits one
    // retrieval unit, so its single heading genuinely describes the whole thing.
    //
    // The token count is read off the profile directly rather than through a size class: the
    // class was a four-way vocabulary of which this gate only ever used one value, and the raw
    // threshold says what is actually being tested.
    //
    // Runs once per document. Strategies never re-check it, and being over the token ceiling
    // never changes a route - it only triggers a cut inside one.
    private static bool DocContainsHeadingOrLessThan4kTokens(PdfExtractionDocument doc)
    {
        int headingCount = doc.Headings.Count;

        // Null means extraction never measured this document, so the count decides alone - a
        // missing measurement never punishes a document. It is likewise not "small": null < int
        // is false, so an unmeasured document never takes the single-heading clause.
        double? density = doc.Profile?.HeadingsPerThousandChars;
        bool    isSmall = doc.Profile?.EstimatedTokens < SmallDocumentTokenCeiling;

        return (headingCount >= MinHeadings && (density is null || density >= MinHeadingsPerThousandChars))
            || (isSmall && headingCount >= MinHeadingsWhenSmall);
    }

    private const int    MinHeadings                 = 2;
    private const double MinHeadingsPerThousandChars = 0.1;
    private const int    MinHeadingsWhenSmall        = 1;

    // The "can the whole document BE the retrieval unit" line, and the only size threshold this
    // gate needs. Reasoned, never measured (chunking-signals-map.md) - it was
    // DocumentSizeClassifier.MediumTokenThreshold, the boundary below which a document was
    // classified Small.
    private const int    SmallDocumentTokenCeiling   = 4_000;

    // The floor for the minimum-content rule. Set from the corpus's known residue chunks
    // ("£ £" scores 0, a bare "#" scores 0, a checkbox row "1 2 3" scores 3) while the
    // shortest genuine sections comfortably clear it. Counted on the unit's BODY, before the
    // title/heading prefix is prepended - the prefix would make any residue look substantial.
    //
    // The rule itself stays here rather than moving to ChunkingReporting with the counting:
    // dropping a cut changes what gets indexed, which is a chunking decision. Only the count of
    // what it dropped is reporting.
    private const int MinChunkAlphanumericChars = 4;

    private static bool IsResidue(string content) =>
        content.Count(char.IsLetterOrDigit) < MinChunkAlphanumericChars;

    // ── The heading-only rule (step 9) ──────────────────────────────────────
    //
    // A cut that is its own heading and nothing else. "Salarisschaal functiegroep 25" as an
    // entire 185-char body, heading repeated as content, no rows under it - 29 chunks (4%) in
    // the 260818 retrieved corpus had a body under 80 chars, and this is what most of them
    // were. Indexed, such a chunk matches the query its heading names and then answers nothing.
    //
    // NOT LIVE YET, and the flag below is why. The 35 mislabelled salary chunks that verify
    // TableCaptionSplitter ARE heading-only chunks: drop them first and the "35 -> 0" check
    // passes whether or not the caption fix actually worked, because the rows it counts were
    // removed by this rule instead of repaired by that one. Ordering stated in
    // last-run-fixes.md as "step 9 must not precede step 6".
    //
    // Flip to true once a re-index has confirmed 35 -> 0. A field rather than a const so the
    // disabled branch is not unreachable code in a zero-warning build.
    private static readonly bool DropHeadingOnlyChunks = false;

    // The floor for what counts as a body UNDER a heading. Higher than
    // MinChunkAlphanumericChars, which is calibrated against "£ £" and a bare "#" and is not
    // moved: this asks a different question on a different string - what is left AFTER the
    // heading line comes off - so it gets its own number rather than stretching that one.
    // "Zie 4.2" (6) is not a section; "Niet van toepassing." (18) is.
    private const int MinBodyAlphanumericChars = 12;

    // Requires that a heading line was ACTUALLY removed before judging what remains. Without
    // that condition the rule would measure the full body of any short chunk, and a genuine
    // one-line section with no heading - which the recursive route produces by design, every
    // chunk on it having HeadingSource "none" - would be dropped as if it were furniture.
    // internal so the rule can be tested while the flag above still holds it out of the run -
    // "built and pinned, not yet landed" is the state this is deliberately in.
    internal static bool IsHeadingOnly(ChunkObject chunk)
    {
        var content   = chunk.Content;
        var firstBreak = content.IndexOf('\n');
        var firstLine  = (firstBreak < 0 ? content : content[..firstBreak]).Trim();

        if (firstLine.Length == 0) return false;

        // Either rendered as markdown ("#### Artikel 4:15 ...") or as the bare heading text,
        // which is how a located heading arrives once HeadingLocator has pulled the boundary
        // back over its own "#" markers.
        var isHeadingLine =
            firstLine[0] == '#'
            || (!string.IsNullOrWhiteSpace(chunk.HeadingText)
                && string.Equals(firstLine, chunk.HeadingText!.Trim(), StringComparison.Ordinal));

        if (!isHeadingLine) return false;

        var rest = firstBreak < 0 ? "" : content[(firstBreak + 1)..];

        return rest.Count(char.IsLetterOrDigit) < MinBodyAlphanumericChars;
    }

    // The fall-through tripwire's thresholds (step 3c). Share, not count: 3 HardCuts in a
    // 400-chunk CAO is noise, 3 in a 10-chunk infographic is the document. The minimum count
    // keeps a two-chunk document from failing on a single unlucky token run.
    private const double MaxHardCutShare  = 0.10;
    private const int    MinHardCutsToFail = 2;
}
