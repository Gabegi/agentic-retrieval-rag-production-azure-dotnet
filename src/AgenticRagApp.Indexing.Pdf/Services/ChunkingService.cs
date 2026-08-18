using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Turns extracted documents into indexed chunks, in five steps:
//
//   1. resolve document identity  (DocumentIdentityResolver - family, domain tag, confusables)
//   2. read headings and sections, and gate on them  (HeadingSectionGate -> one of two routes)
//   3. chunk  (DeclaredBoundaryStrategy | RecursiveStrategy)
//   4. metadata  (ToChunk here today; moving to ChunkMetadataBuilder)
//   5. report  (ChunkingRunReport, written from a finally)
//
// The split of responsibility is deliberate. A strategy decides WHERE to cut and knows nothing
// about ids, Zenya metadata or embedding prefixes; this class decides how a cut becomes an
// indexed row and knows nothing about headings or ceilings.
public class ChunkingService : IChunkingService
{
    // Above this share of headings failing to locate, the string-match approach is no longer
    // holding and the run says so out loud - fixed in advance, see the heading-location note
    // in ChunkDocumentsAsync.
    private const double HeadingEscalationThreshold = 0.02;

    // The per-document half of the same rule, fixed in advance alongside it. Looser than the
    // corpus figure on purpose: one document is a small sample, so a single unlocated heading in
    // twenty is noise where the same rate across 1,273 is a broken approach.
    private const double PerDocumentHeadingEscalationThreshold = 0.05;

    private readonly IDocumentChunkingStrategy   _declaredBoundaryStrategy;
    private readonly IDocumentChunkingStrategy   _recursiveStrategy;
    private readonly DocumentIdentityResolver    _identityResolver;
    private readonly ChunkMetadataBuilder        _metadataBuilder;
    private readonly IPipelineArtifactWriter     _artifactWriter;
    private readonly ILogger<ChunkingService>    _logger;

    public string Name => "TwoAxisChunking";

    public ChunkingService(
        DeclaredBoundaryStrategy declaredBoundary,
        RecursiveStrategy        recursive,
        DocumentIdentityResolver identityResolver,
        ChunkMetadataBuilder     metadataBuilder,
        IPipelineArtifactWriter  artifactWriter,
        ILogger<ChunkingService> logger)
    {
        _declaredBoundaryStrategy = declaredBoundary;
        _recursiveStrategy        = recursive;
        _identityResolver = identityResolver;
        _metadataBuilder  = metadataBuilder;
        _artifactWriter   = artifactWriter;
        _logger           = logger;
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
    public async Task<(IReadOnlyList<ChunkObject> Docs, ChunkingStageMetrics Stats)> ChunkDocumentsAsync(
        IReadOnlyList<PdfExtractionDocument> docs,
        string?                              instanceId = null,
        DateTimeOffset?                      startedAt  = null,
        CancellationToken                    ct         = default)
    {
        var runStartedAt = startedAt ?? DateTimeOffset.UtcNow;
        var allChunks    = new List<ChunkObject>();

        // Populated as the stage progresses rather than assembled at the end, so whatever is
        // known when an exception fires is already in hand.
        var outcomes                       = new List<DocumentOutcome>();
        IdentityResolutionDiagnostics? identity = null;
        HeadingLocationSummary? headingSummary  = null;
        ChunkingStageMetrics?   stats           = null;
        var    failedAtStage = "identity-resolution";
        string? error        = null;

        // Heading-location totals, accumulated per document as the loop runs rather than
        // collected from the chunks afterwards. Out here with the rest of the report state so a
        // run that throws part way through still reports what it managed to count.
        var headingsTotal   = 0;
        var headingsFound   = 0;
        var pairsMerged     = 0;
        var headingsNoOffset = 0;

        // Cuts the minimum-content rule discarded, for the same reason and in the same place as
        // the heading counters: a run that dies half way through still says what it dropped.
        var residueDropped  = 0;

        try
        {
            // 1. Identity: family, domain tag, confusables - needs an embedding call, so it
            //    runs once for the whole batch before anything is cut.
            var resolved = await _identityResolver.ResolveDocumentIdentityAsync(docs, ct);
            identity     = resolved.Diagnostics;
            failedAtStage = "heading-section-gate";

            // How many documents landed in each family, counted once rather than re-derived per
            // row. A single-member family is the resolver saying "nothing else looks like this",
            // which is a different statement from a family of five - and it is the multi-member
            // case where a wrong family_id actually costs a retrieval.
            var familySize = resolved.Families.Values
                .GroupBy(f => f.FamilyId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

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

                // 2. Read the declared structure and gate on it. The formula is not repeated
                //    here on purpose - DocContainsHeadingOrLessThan4kTokens below IS the routing
                //    decision, and this is its only caller.
                //
                //    The branch picks a STRATEGY rather than calling one, so step 3 and step 4
                //    are each written once. It also means the route name step 4 stamps is the
                //    strategy's own Name, so a chunk's route_name cannot disagree with the class
                //    that actually cut it.
                IDocumentChunkingStrategy strategy;

                // This document's own anchoring result, so the row written at the end of the
                // iteration can report ITS heading counts rather than only contributing to the
                // run totals. Null on the recursive route - which never anchors - and the row
                // reports 0/0 there, matching the counters: not attempted, not failed.
                HeadingLocationResult? located = null;

                if (DocContainsHeadingOrLessThan4kTokens(doc))
                {
                    // 2b. Anchor the declared headings, and count how many could be placed.
                    //
                    //     This runs HERE rather than inside the strategy for one reason: the
                    //     counters have to survive a document that goes on to produce no chunks.
                    //     A document whose every heading failed to locate is exactly the case the
                    //     >2% escalation exists to catch, and it is also the likeliest document to
                    //     emit nothing - so counters recovered from the chunks would drop it.
                    //
                    //     Locate is the whole read: it orders headings by raw DI offset, finds each
                    //     one's real position in the cleaned text, and pairs consecutive hits into
                    //     contiguous sections, preamble and zero-body merges included. Raw offsets
                    //     ORDER headings and never slice - cleaning drifts length by a measured
                    //     1.066-1.202x, so a raw offset cuts wrong, and further wrong the deeper
                    //     into the document it is.
                    failedAtStage = "heading-location";

                    located = HeadingLocator.Locate(
                        doc.Content, doc.Headings, doc.PageSpans, doc.Sections);

                    headingsTotal    += located.HeadingsTotal;
                    headingsFound    += located.HeadingsLocated;
                    pairsMerged      += located.PairedHeadingsMerged;
                    headingsNoOffset += located.HeadingsWithoutOffset;

                    // A heading whose paragraph carried no DI spans at all, so nothing said
                    // where in the raw content it sits. Locate kept it with its neighbours by
                    // carrying the previous offset forward, which is the best available answer
                    // but still a fallback - the section boundary it opens now rests on arrival
                    // order. Named per document rather than only totalled, because chasing it
                    // upstream needs to know WHICH file did it.
                    if (located.HeadingsWithoutOffset > 0)
                        _logger.LogWarning(
                            "{Count} of {Total} headings in {SourceId} carried no DI offset and were " +
                            "ordered by arrival position instead. Zero of 1,273 headings across the big " +
                            "four did this, so it is an extraction anomaly worth chasing upstream, not a " +
                            "routine input.",
                            located.HeadingsWithoutOffset, located.HeadingsTotal, doc.SourceId);

                    // The OTHER half of the escalation rule, and the half that had no code: the
                    // threshold was fixed in advance at >2% corpus-wide OR >5% on any single
                    // document, and only the corpus-wide branch existed. A corpus that locates
                    // 1,270 of 1,273 headings passes the >2% test comfortably while one small
                    // document sits at 100% unlocated - which is exactly the document whose
                    // chunks are all preamble, and exactly what a run total cannot show.
                    if (located.HeadingsTotal > 0 &&
                        located.FailureRate > PerDocumentHeadingEscalationThreshold)
                        _logger.LogWarning(
                            "Heading location in {SourceId}: {Found}/{Total} ({Rate:P2} unlocated), " +
                            "over the >{Threshold:P0} per-document escalation threshold",
                            doc.SourceId, located.HeadingsLocated, located.HeadingsTotal,
                            located.FailureRate, PerDocumentHeadingEscalationThreshold);

                    doc = doc with { LocatedSections = located.Sections };

                    strategy = _declaredBoundaryStrategy;
                }

                else
                {
                    // No anchoring on this route, and therefore nothing added to the counters.
                    // The recursive route deliberately does not use whatever headings the
                    // document has, so reporting them as unlocated would fill the failure metric
                    // with headings that never failed - they were not attempted.
                    strategy = _recursiveStrategy;
                }

                // 3. Chunking: the routed strategy cuts this document. It returns the cuts and
                //    nothing else - WHERE to cut is all it decides.
                failedAtStage = "chunking";

                var chunks = await strategy.ChunkDocumentAsync(doc, ct);

                // 3b. The minimum-content rule. A cut whose body carries almost no letters or
                //     digits is vector residue, not content - this corpus produced a literal
                //     "£ £" cut and a bare "#" one. Indexed, they occupy a row and can come back
                //     as a match for a query they mean nothing about.
                //
                //     Dropped here rather than inside the strategy: deciding WHERE to cut and
                //     deciding whether a cut is worth indexing are different judgements, and the
                //     strategy only makes the first.
                //
                //     Measured on Content, which at this point is the BARE BODY - the prefix is a
                //     separate field and is not joined on until EmbeddingText composes it. That
                //     ordering is the whole point: a prefix is dozens of alphanumeric characters,
                //     so residue measured after it looks substantial.
                var kept    = chunks.Where(c => !IsResidue(c.Content)).ToList();
                var dropped = chunks.Count - kept.Count;

                if (dropped > 0)
                {
                    residueDropped += dropped;
                    _logger.LogInformation(
                        "Minimum-content rule dropped {Dropped} of {Total} cut(s) as vector residue in {SourceId}",
                        dropped, chunks.Count, doc.SourceId);
                }

                // 4. Metadata: ONE call for the whole document, not one per chunk. What is a
                //    property of the DOCUMENT is read once inside and copied onto every chunk of
                //    it - two chunks of one file disagreeing about its family_id is not
                //    something anything downstream could detect.
                //
                //    Only the survivors: nothing is stamped onto rows already discarded.
                failedAtStage = "chunk-metadata";

                _metadataBuilder.AddMetadata(kept, doc, strategy.Name);

                allChunks.AddRange(kept);

                // 5. The row. Written per document, at the end of its own iteration, so the
                //    report says what happened to THIS document rather than only what the run
                //    totalled - `outcomes` was declared for this and nothing ever added to it,
                //    which left every row in the report a `not_reached` fallback with 0/0
                //    headings, on runs that succeeded.
                //
                //    "zero_chunks" covers both a route that emitted nothing and a document whose
                //    every cut was residue; Reason separates them, because they are different
                //    faults - the first is a routing or anchoring failure, the second is an
                //    extraction one.
                outcomes.Add(new DocumentOutcome(
                    SourceId:              doc.SourceId,
                    Title:                 doc.Title,
                    Outcome:               kept.Count > 0 ? "chunked" : "zero_chunks",
                    Reason:                ZeroChunkReason(kept.Count, chunks.Count, dropped),
                    FailedExtractionGate:  doc.Profile is { HasExtractableContent: false },
                    ResidueChunksDropped:  dropped,
                    FamilyId:              doc.Family?.FamilyId,
                    IsInMultiMemberFamily: doc.Family is not null &&
                                           familySize.GetValueOrDefault(doc.Family.FamilyId) > 1,
                    DomainTag:             doc.Family?.DomainTag,
                    ConfusableWith:        doc.Family?.ConfusableWith ?? [],
                    IdentityVectorSource:  resolved.IdentityVectorSourceOf.GetValueOrDefault(doc.SourceId),
                    ChunkCount:            kept.Count,
                    // The real counts, from this document's own anchoring result. 0/0 on the
                    // recursive route means not attempted - the same distinction LocatedSections
                    // being null carries, and why this is not "0 of N located".
                    HeadingsTotal:         located?.HeadingsTotal   ?? 0,
                    HeadingsLocated:       located?.HeadingsLocated ?? 0,
                    SizeClass:             DocumentSizeClassifier.Classify(doc.Profile).ToString(),
                    Strategy:              strategy.Name));
            }

            // The standing evidence for locating headings by string match rather than rewriting
            // PdfCleaner to emit an offset map. That call was made against 1,273/1,273 exact
            // matches with an escalation threshold fixed IN ADVANCE at >2%, so the rate has to be
            // reported every run - measured once and assumed to hold is exactly what it is not.
            //
            // Zero total means no document took the declared-boundary route this run, so there is
            // nothing to report. Not a 0% failure rate - no attempt.
            if (headingsTotal > 0)
            {
                var failureRate = 1 - (headingsFound / (double)headingsTotal);
                var exceeds     = failureRate > HeadingEscalationThreshold;

                headingSummary = new HeadingLocationSummary(
                    headingsTotal, headingsFound, failureRate, exceeds, pairsMerged,
                    headingsNoOffset);

                _logger.Log(exceeds ? LogLevel.Warning : LogLevel.Information,
                    "Heading location: {Found}/{Total} ({Rate:P2} unlocated), {Merged} paired zero-body heading(s) merged",
                    headingsFound, headingsTotal, failureRate, pairsMerged);
            }

            // The run total, so a corpus that sheds residue across many documents says so once
            // rather than only a line at a time. Non-zero is normal on image-heavy documents;
            // a sharp rise is an extraction change, not a chunking one.
            if (residueDropped > 0)
                _logger.LogInformation(
                    "Minimum-content rule dropped {Dropped} cut(s) as vector residue across the run",
                    residueDropped);


            // TO DO (step 5): move all reporting logic to a folder called ChunkingReporting.
            // ONE call, from here, and no reporting slope anywhere else in this method.
            //
            // Commented out rather than deleted: the reporter class does not exist yet, and this
            // line is the only thing left in the file that names where it goes. It was the sole
            // remaining compile error in this project, so leaving it live meant step 4 could not
            // be built or tested at all.
            //
            //     _reportBuild.BuildREPORT();

            // TO DO we will review if it's any useful

            // Failed documents got their rows above; the stage itself still fails, so a
            // partial result is never silently indexed - but only after every other document
            // was chunked and reported.

            failedAtStage = "metrics";

            var sourceDocumentIds = docs.Select(d => d.SourceId).Distinct(StringComparer.Ordinal).ToList();

            stats = ChunkingStageMetrics.Compute(allChunks, Name, sourceDocumentIds);

            _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks ({Strategy})",
                docs.Count, allChunks.Count, stats.Strategy);

            failedAtStage = null;
            return (allChunks, stats);
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            throw;
        }
        finally
        {
            // Never let a reporting failure mask the stage's own outcome - on the success path
            // it would fail a run that worked, and on the failure path it would replace the
            // real exception with a blob-write error.
            try
            {
                // Documents never reached, because the stage threw before their turn.
                var accountedFor = outcomes.Select(o => o.SourceId).ToHashSet(StringComparer.Ordinal);
                var rows = outcomes
                    .Concat(docs.Where(d => !accountedFor.Contains(d.SourceId))
                                .Select(d => NotChunked(d, null, null, "not_reached",
                                    error is null ? null : $"the chunking stage failed at '{failedAtStage}' before this document was processed")))
                    .OrderBy(o => o.SourceId, StringComparer.Ordinal)
                    .ToList();

                await _artifactWriter.WriteArtifactAsync(
                    StageReportPath.Build(ChunkingReportName, runStartedAt, instanceId),
                    new ChunkingRunReport(
                        InstanceId:      instanceId,
                        StartedAt:       runStartedAt,
                        CompletedAt:     DateTimeOffset.UtcNow,
                        Success:         error is null,
                        FailedAtStage:   error is null ? null : failedAtStage,
                        Error:           error,
                        Documents:       rows,
                        Identity:        identity,
                        HeadingLocation: headingSummary,
                        Stats:           stats,
                        Chunks:          allChunks),
                    ct);
            }
            catch (Exception reportEx)
            {
                _logger.LogError(reportEx, "Failed to write the chunking run report");
            }
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

    // Report name kept as the pre-existing artifact's, so the blob a reader already knows how
    // to find is the one that now carries the whole stage rather than just the chunk list.
    private const string ChunkingReportName = "chunking-artifact";

    // The floor for the minimum-content rule. Set from the corpus's known residue chunks
    // ("£ £" scores 0, a bare "#" scores 0, a checkbox row "1 2 3" scores 3) while the
    // shortest genuine sections comfortably clear it. Counted on the unit's BODY, before the
    // title/heading prefix is prepended - the prefix would make any residue look substantial.
    private const int MinChunkAlphanumericChars = 4;

    // Why a document produced nothing, said in the report rather than left to be inferred from a
    // zero. Null on the ordinary path: a document that produced chunks needs no explanation, and
    // a reason present on a "chunked" row would read as a complaint about it.
    private static string? ZeroChunkReason(int kept, int cut, int dropped) =>
        kept  > 0 ? null
      : cut == 0  ? "the strategy produced no cuts for this document"
      :             $"all {dropped} cut(s) were dropped by the minimum-content rule as vector residue";

    private static bool IsResidue(string content) =>
        content.Count(char.IsLetterOrDigit) < MinChunkAlphanumericChars;

    // A row for a document that produced no chunks. The gate parameter this used to take is
    // gone with SectionGateVerdict: every caller passed null anyway, because these are exactly
    // the rows written BEFORE a route was chosen (identity_skipped, not_reached).
    private static DocumentOutcome NotChunked(
        PdfExtractionDocument doc, DocumentFamily? family, string? vectorSource,
        string outcome, string? reason) =>
        new(SourceId:              doc.SourceId,
            Title:                 doc.Title,
            Outcome:               outcome,
            Reason:                reason,
            // Read off the profile. The gate never ran for these documents, so a route-derived
            // answer would be an invention rather than a measurement.
            FailedExtractionGate:  doc.Profile is { HasExtractableContent: false },
            ResidueChunksDropped:  0,
            FamilyId:              family?.FamilyId,
            IsInMultiMemberFamily: false,
            DomainTag:             family?.DomainTag,
            ConfusableWith:        family?.ConfusableWith ?? [],
            IdentityVectorSource:  vectorSource,
            ChunkCount:            0,
            HeadingsTotal:         0,
            HeadingsLocated:       0,
            // Sized from the same classifier step 4 stamps with, so a not_reached row still says
            // how big the document was. Strategy stays null - no route was picked for it, and
            // naming one would read as a route that ran and produced nothing.
            SizeClass:             DocumentSizeClassifier.Classify(doc.Profile).ToString(),
            Strategy:              null);

}
