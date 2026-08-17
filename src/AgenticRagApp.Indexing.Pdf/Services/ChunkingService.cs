using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Turns extracted documents into indexed chunks: selects a strategy per document, then maps
// what it produced onto DocumentChunk (ids, document metadata, page attribution, the embedded
// prefix) and emits the stage telemetry.
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

    private readonly IDocumentChunkingStrategy   _declaredBoundary;
    private readonly IDocumentChunkingStrategy   _recursive;
    private readonly DocumentIdentityResolver    _identityResolver;
    private readonly IPipelineArtifactWriter     _artifactWriter;
    private readonly ILogger<ChunkingService>    _logger;

    public string Name => "TwoAxisChunking";

    public ChunkingService(
        DeclaredBoundaryStrategy declaredBoundary,
        RecursiveStrategy        recursive,
        DocumentIdentityResolver identityResolver,
        IPipelineArtifactWriter  artifactWriter,
        ILogger<ChunkingService> logger)
    {
        _declaredBoundary = declaredBoundary;
        _recursive        = recursive;
        _identityResolver = identityResolver;
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
    public async Task<(IReadOnlyList<DocumentChunk> Docs, ChunkingStageMetrics Stats)> ChunkDocumentsAsync(
        IReadOnlyList<PdfExtractionDocument> docs,
        string?                              instanceId = null,
        DateTimeOffset?                      startedAt  = null,
        CancellationToken                    ct         = default)
    {
        var runStartedAt = startedAt ?? DateTimeOffset.UtcNow;
        var result       = new List<DocumentChunk>();

        // Populated as the stage progresses rather than assembled at the end, so whatever is
        // known when an exception fires is already in hand.
        var outcomes                       = new List<DocumentOutcome>();
        IdentityResolutionDiagnostics? identity = null;
        HeadingLocationSummary? headingSummary  = null;
        ChunkingStageMetrics?   stats           = null;
        var    failedAtStage = "identity-resolution";
        string? error        = null;

        try
        {
            // 1. Identity: family, domain tag, confusables - needs an embedding call, so it
            //    runs once for the whole batch before anything is cut.
            var resolved = await _identityResolver.ResolveDocumentIdentityAsync(docs, ct);
            identity     = resolved.Diagnostics;
            failedAtStage = "heading-section-gate";

            // 2. Read the declared structure and gate on it: every document's route decided
            //    BEFORE any chunking starts - see ReadHeadingsAndSections. Not overlapped with
            //    chunking: the gate reads counts and profile fields, microseconds against
            //    chunking's seconds.
            var routed = ReadHeadingsAndSections(docs, resolved);

            // One routing line per run instead of a warning per picture document - the
            // per-document detail is on the report rows (SizeClass, Strategy,
            // FailedExtractionGate).
            _logger.LogInformation(
                "Route: {DeclaredBoundary} declared-boundary, {Recursive} recursive " +
                "(of which {Picture} picture/CU candidates)",
                routed.Count(r => r.Gate.Route == ChunkingRoute.DeclaredBoundary),
                routed.Count(r => r.Gate.Route == ChunkingRoute.Recursive),
                routed.Count(r => r.Gate.SizeClass == DocumentSizeClass.Picture));

            failedAtStage = "chunking";

            // 3. Chunking: the routed strategy cuts each document - see ChunkAll.
            var chunked = ChunkAll(routed, ct);

            // 4. Every document becomes a report row; its kept units become indexed rows.
            var (headingsTotal, headingsFound, pairsMerged) =
                CollectOutcomes(chunked, identity, docs, result, outcomes);

            // Failed documents got their rows above; the stage itself still fails, so a
            // partial result is never silently indexed - but only after every other document
            // was chunked and reported.
            ThrowIfAnyDocumentFailed(chunked);

            // The standing evidence for locating headings by string match rather than rewriting
            // PdfCleaner to emit an offset map. That call was made against 1,273/1,273 exact
            // matches with an escalation threshold fixed in advance at >2%, so the rate has to be
            // reported every run, not measured once and assumed to hold.
            if (headingsTotal > 0)
            {
                var failureRate = 1 - (headingsFound / (double)headingsTotal);
                var exceeds     = failureRate > HeadingEscalationThreshold;

                headingSummary = new HeadingLocationSummary(
                    headingsTotal, headingsFound, failureRate, exceeds, pairsMerged);

                _logger.Log(exceeds ? LogLevel.Warning : LogLevel.Information,
                    "Heading location: {Found}/{Total} ({Rate:P2} unlocated), {Merged} paired zero-body heading(s) merged",
                    headingsFound, headingsTotal, failureRate, pairsMerged);
            }

            failedAtStage = "metrics";

            var sourceDocumentIds = docs.Select(d => d.SourceId).Distinct(StringComparer.Ordinal).ToList();

            stats = ChunkingStageMetrics.Compute(result, Name, sourceDocumentIds);
            EmitChunkMetrics(stats, result);

            _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks ({Strategy})",
                docs.Count, result.Count, stats.Strategy);

            failedAtStage = null;
            return (result, stats);
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
                        Chunks:          result),
                    ct);
            }
            catch (Exception reportEx)
            {
                _logger.LogError(reportEx, "Failed to write the chunking run report");
            }
        }
    }

    // Step 2. One routed document per input, in deterministic order, each pairing the document
    // with the identity resolved in step 1 and the gate's verdict on its declared structure.
    //
    // A separate pass from chunking on purpose: the verdicts feed the run report as well as the
    // chunker, so a run that dies mid-chunking can still say what every document's route was.
    private static IReadOnlyList<RoutedDocument> ReadHeadingsAndSections(
        IReadOnlyList<PdfExtractionDocument> docs, IdentityResolutionResult resolved) =>
        docs.OrderBy(d => d.SourceId, StringComparer.Ordinal)
            .Select(doc => new RoutedDocument(
                doc,
                resolved.Families.GetValueOrDefault(doc.SourceId),
                resolved.IdentityVectorSourceOf.GetValueOrDefault(doc.SourceId),
                HeadingSectionGate.Read(doc)))
            .ToList();

    private sealed record ChunkedDocument(RoutedDocument Routed, ChunkingOutcome Outcome, Exception? Error);

    // Step 3. Documents are independent and both strategies are CPU-bound and stateless, so they
    // chunk in parallel; AsOrdered keeps the output - and therefore every id and report
    // row - deterministic. A per-document failure is captured rather than thrown, so one bad
    // document cannot hide the fate of the rest; the caller records it and fails the stage
    // once every document has been processed.
    private List<ChunkedDocument> ChunkAll(IReadOnlyList<RoutedDocument> routed, CancellationToken ct) =>
        routed.AsParallel().AsOrdered().WithCancellation(ct)
             .Select(doc =>
             {
                 try
                 {
                     // The gate named the route; this switch maps it onto its implementation in
                     // Services/Chunking/ChunkingStrategies. Two arms, both real algorithms -
                     // the four-arm version dispatched onto four classes that all delegated to
                     // one shared cascade. Exhaustive on purpose: an unhandled value throws
                     // rather than silently cutting the document the wrong way.
                     var strategy = doc.Gate.Route switch
                     {
                         ChunkingRoute.DeclaredBoundary => _declaredBoundary,
                         ChunkingRoute.Recursive        => _recursive,
                         _ => throw new ArgumentOutOfRangeException(
                                  nameof(routed), doc.Gate.Route, "unhandled chunking route"),
                     };

                     var outcome = strategy.Chunk(doc.Doc, doc.Family?.DomainTag);

                     return new ChunkedDocument(doc, outcome, null);
                 }
                 catch (OperationCanceledException)
                 {
                     throw;
                 }
                 catch (Exception ex)
                 {
                     return new ChunkedDocument(doc, ChunkingOutcome.Empty, ex);
                 }
             })
             .ToList();

    // Step 4: each chunked document becomes indexed rows (result) and exactly one report row
    // (outcomes), applying the minimum-content rule. Returns the heading-location totals the
    // escalation check needs.
    private (int HeadingsTotal, int HeadingsLocated, int PairsMerged) CollectOutcomes(
        IReadOnlyList<ChunkedDocument>       chunked,
        IdentityResolutionDiagnostics        identity,
        IReadOnlyList<PdfExtractionDocument> docs,
        List<DocumentChunk>                  result,
        List<DocumentOutcome>                outcomes)
    {
        var headingsTotal = 0;
        var headingsFound = 0;
        var pairsMerged   = 0;

        foreach (var (routed, outcome, error) in chunked)
        {
            var (doc, family, vectorSource, gate) = routed;

            if (error is not null)
            {
                outcomes.Add(NotChunked(doc, family, vectorSource, "failed",
                    $"the {gate.Route} strategy threw: {error.Message}", gate));
                continue;
            }

            headingsTotal += outcome.HeadingsTotal;
            headingsFound += outcome.HeadingsLocated;
            pairsMerged   += outcome.PairedHeadingsMerged;

            // The minimum-content rule that replaced the extraction gate's document-level
            // drop: a unit whose body carries almost no letters or digits is vector residue
            // (the corpus's literal "£ £" and bare "#" chunks), not content. Measured on
            // letters/digits rather than raw length because residue can be padded with
            // symbols and whitespace, while a genuinely short answer ("body") is all letters.
            var kept    = outcome.Units.Where(u => !IsResidue(u.Content)).ToList();
            var dropped = outcome.Units.Count - kept.Count;

            // 4. Metadata: each kept unit becomes an indexed row. ToChunk below is this step
            //    today; ChunkMetadataBuilder is where it moves, organised as the four metadata
            //    scopes - see docs/2608/260818/chunking-service-refactor.md step 4.
            foreach (var unit in kept)
                result.Add(ToChunk(doc, unit, family));

            outcomes.Add(new DocumentOutcome(
                SourceId:              doc.SourceId,
                Title:                 doc.Title,
                Outcome:               kept.Count > 0 ? "chunked" : "zero_chunks",
                Reason:                kept.Count > 0
                                           ? null
                                           : dropped > 0
                                               ? $"every unit the strategy emitted ({dropped}) was vector residue below the minimum-content rule"
                                               : "the strategy ran but emitted no units - empty or whitespace after cleaning",
                FailedExtractionGate:  gate.SizeClass == DocumentSizeClass.Picture,
                ResidueChunksDropped:  dropped,
                FamilyId:              family?.FamilyId,
                IsInMultiMemberFamily: IsMultiMember(identity, family?.FamilyId),
                DomainTag:             family?.DomainTag,
                ConfusableWith:        family?.ConfusableWith ?? [],
                IdentityVectorSource:  vectorSource,
                ChunkCount:            kept.Count,
                HeadingsTotal:         outcome.HeadingsTotal,
                HeadingsLocated:       outcome.HeadingsLocated,
                SizeClass:             gate.SizeClass.ToString(),
                Strategy:              gate.Route.ToString()));
        }

        // Documents the resolver dropped for having nothing to embed never reach the loop
        // above with an identity, but they are still inputs and still need a row.
        foreach (var sourceId in identity.SkippedEmptyIdentity)
            if (!outcomes.Any(o => o.SourceId == sourceId))
                outcomes.Add(NotChunked(
                    docs.First(d => d.SourceId == sourceId), null, null, "identity_skipped",
                    "no title and no headings, so there was nothing to embed or cluster on"));

        return (headingsTotal, headingsFound, pairsMerged);
    }

    private static void ThrowIfAnyDocumentFailed(IReadOnlyList<ChunkedDocument> chunked)
    {
        var failed = chunked.Where(c => c.Error is not null).ToList();
        if (failed.Count == 0) return;

        throw new AggregateException(
            $"{failed.Count} document(s) failed chunking - see their rows in the run report: " +
            string.Join(", ", failed.Select(f => f.Routed.Doc.SourceId)),
            failed.Select(f => f.Error!));
    }

    // Report name kept as the pre-existing artifact's, so the blob a reader already knows how
    // to find is the one that now carries the whole stage rather than just the chunk list.
    private const string ChunkingReportName = "chunking-artifact";

    private static bool IsMultiMember(IdentityResolutionDiagnostics? identity, string? familyId) =>
        familyId is not null && identity is not null &&
        identity.Families.Any(f => f.FamilyId == familyId);

    // The floor for the minimum-content rule. Set from the corpus's known residue chunks
    // ("£ £" scores 0, a bare "#" scores 0, a checkbox row "1 2 3" scores 3) while the
    // shortest genuine sections comfortably clear it. Counted on the unit's BODY, before the
    // title/heading prefix is prepended - the prefix would make any residue look substantial.
    private const int MinChunkAlphanumericChars = 4;

    private static bool IsResidue(string content) =>
        content.Count(char.IsLetterOrDigit) < MinChunkAlphanumericChars;

    private static DocumentOutcome NotChunked(
        PdfExtractionDocument doc, DocumentFamily? family, string? vectorSource,
        string outcome, string? reason, SectionGateVerdict? gate = null) =>
        new(SourceId:              doc.SourceId,
            Title:                 doc.Title,
            Outcome:               outcome,
            Reason:                reason,
            // From the gate when it ran; otherwise derived from the profile so rows written
            // before the gate (identity_skipped, not_reached) stay truthful.
            FailedExtractionGate:  gate is null
                                       ? doc.Profile is { HasExtractableContent: false }
                                       : gate.SizeClass == DocumentSizeClass.Picture,
            ResidueChunksDropped:  0,
            FamilyId:              family?.FamilyId,
            IsInMultiMemberFamily: false,
            DomainTag:             family?.DomainTag,
            ConfusableWith:        family?.ConfusableWith ?? [],
            IdentityVectorSource:  vectorSource,
            ChunkCount:            0,
            HeadingsTotal:         0,
            HeadingsLocated:       0,
            SizeClass:             gate?.SizeClass.ToString(),
            Strategy:              gate?.Route.ToString());

    private static DocumentChunk ToChunk(PdfExtractionDocument doc, ChunkUnit unit, DocumentFamily? family)
    {
        var (pageStart, pageEnd, pictureOnly) = ResolvePages(doc.PageSpans, unit.Start, unit.Length);

        // The embedded prefix: document title, sector tag, then the heading chain.
        //
        // The sector tag is here rather than added later on purpose. The dangerous failure in
        // this corpus is a well-formed, on-topic, WRONG-SECTOR answer - the three CAOs give
        // different vakantietoeslag figures for the same question - and no similarity score can
        // flag that. The domain_tag filter is the deterministic fix, but putting the tag in the
        // embedded text pushes the signal into the vector too. It has to be in from the first
        // build, because this text IS what gets embedded: adding it afterwards changes every
        // vector and forces a full re-embed.
        // Same rule SectionCascadeStrategy budgeted against - see ChunkingHelper.TitleLine.
        var titleLine = ChunkingHelper.TitleLine(doc.Title, family?.DomainTag);

        var prefixParts = new[] { titleLine, unit.HeadingPath }
            .Where(p => !string.IsNullOrWhiteSpace(p));

        var prefix  = string.Join("\n\n", prefixParts);
        var content = prefix.Length > 0 ? $"{prefix}\n\n{unit.Content}" : unit.Content;

        return new DocumentChunk
        {
            // Carries no page number: the id is scoped to the document and the unit's position
            // within it, so inserting a page no longer shifts every subsequent id - and an id
            // change is a delete-plus-insert in the index, not an update.
            Id                 = ChunkingHelper.SafeKey($"{doc.SourceId}::s{unit.SectionIndex}", unit.ChildIndex),
            DocumentId         = doc.SourceId,

            // Synthesized rather than pointing at a parent row: parent text is materialized
            // onto each child instead of indexed separately, so there is no row to point at.
            // It is a grouping key - de-duplicating children of one section, or fetching the
            // rest of it - and nothing needs to exist for it to identify.
            SectionId          = ChunkingHelper.SafeKey($"{doc.SourceId}::s{unit.SectionIndex}", -1),
            SectionIndex       = unit.SectionIndex,
            ChildIndex         = unit.ChildIndex,
            Grain              = unit.Grain,
            ParentText         = unit.ParentText,

            Title              = doc.Title,
            Content            = content,
            TokenCount         = TokenCounter.Count(content),

            HeadingText        = unit.HeadingText,
            HeadingPath        = unit.HeadingPath,
            HeadingDepth       = unit.HeadingDepth,
            HeadingSource      = unit.HeadingSource,
            HeadingLocated     = unit.HeadingLocated,
            IsOverlap          = unit.IsOverlap,

            PageStart          = pageStart,
            PageEnd            = pageEnd,
            PageExtractionFlag = pictureOnly,
            Language           = doc.Language,

            LastModifiedDate   = doc.LastModifiedDate,
            CreatedAt          = doc.CreatedAt,
            ModDate            = doc.ModDate,
            PageCount          = doc.PageCount,
            ZenyaDocumentId    = doc.ZenyaDocumentId,
            ZenyaVersion       = doc.ZenyaVersion,
            ZenyaStatus        = doc.ZenyaStatus,
            ZenyaUrl           = doc.ZenyaUrl,
            Author             = doc.Author,
            Breadcrumb         = doc.PageBreadcrumbs.GetValueOrDefault(pageStart),

            FamilyId           = family?.FamilyId,
            DomainTag          = family?.DomainTag,
            ConfusableWith     = family?.ConfusableWith ?? [],

            // Filtered to the pages this chunk covers, so the cost scales with the chunk
            // rather than the document. Lines is excluded on measured cost (57% of the whole
            // extraction payload) and Sections/Bookmarks on principle - see ChunkStructure.
            Structure          = new ChunkStructure(
                Headings:       OnPages(doc.Headings,       h => h.PageNumber, pageStart, pageEnd),
                Boilerplate:    OnPages(doc.Boilerplate,    h => h.PageNumber, pageStart, pageEnd),
                Tables:         OnPages(doc.Tables,         t => t.PageNumber, pageStart, pageEnd),
                Dimensions:     doc.PageSpans.FirstOrDefault(s => s.PageNumber == pageStart)?.Dimensions,
                SelectionMarks: OnPages(doc.SelectionMarks, s => s.PageNumber, pageStart, pageEnd),
                Figures:        OnPages(doc.Figures,        f => f.PageNumber, pageStart, pageEnd)),
        };
    }

    private static IReadOnlyList<T> OnPages<T>(
        IReadOnlyList<T> items, Func<T, int> pageOf, int start, int end) =>
        items.Where(i => pageOf(i) >= start && pageOf(i) <= end).ToList();

    // Which pages a chunk covers. A chunk that starts inside page 4 and runs into page 5
    // reports (4, 5) - the reason page_start/page_end replaced a single page_number.
    private static (int Start, int End, bool PictureOnly) ResolvePages(
        IReadOnlyList<PageSpan> spans, int chunkStart, int chunkLength)
    {
        if (spans.Count == 0) return (0, 0, false);

        var chunkEnd = chunkStart + chunkLength;
        var covered  = spans
            .Where(s => s.Offset < chunkEnd && s.Offset + s.Length >= chunkStart)
            .ToList();

        if (covered.Count == 0) return (spans[0].PageNumber, spans[0].PageNumber, spans[0].IsPictureOnly);

        return (covered[0].PageNumber, covered[^1].PageNumber, covered.Any(s => s.IsPictureOnly));
    }

    private static void EmitChunkMetrics(ChunkingStageMetrics stats, IReadOnlyList<DocumentChunk> chunks)
    {
        var strategyTag = new KeyValuePair<string, object?>("strategy", stats.Strategy);

        Instrumentation.ChunksExtracted.Record(stats.ChunksProduced, strategyTag);

        // Per-chunk histogram — preserves the real distribution in App Insights, not just the
        // aggregates already in ChunkingStageMetrics.
        foreach (var chunk in chunks)
            Instrumentation.ChunkSizeChars.Record(chunk.Content.Length, strategyTag);

        Instrumentation.ChunkSizeBand.Add(stats.BandUnder100,  strategyTag, new("band", "under_100"));
        Instrumentation.ChunkSizeBand.Add(stats.Band100To500,  strategyTag, new("band", "100_to_500"));
        Instrumentation.ChunkSizeBand.Add(stats.Band500To1500, strategyTag, new("band", "500_to_1500"));
        Instrumentation.ChunkSizeBand.Add(stats.Band1500Plus,  strategyTag, new("band", "1500_plus"));

        Instrumentation.DuplicateChunks.Add(stats.DuplicateChunks,   strategyTag);
        Instrumentation.CoherentChunks.Add(stats.CoherentChunks,     strategyTag);
        Instrumentation.HeadingsDetected.Add(stats.HeadingsDetected, strategyTag);

        if (stats.DocsWithZeroChunks > 0)
            Instrumentation.DocsWithZeroChunks.Add(stats.DocsWithZeroChunks, strategyTag);
    }
}
