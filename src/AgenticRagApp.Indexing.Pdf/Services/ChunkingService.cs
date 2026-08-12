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
    private readonly DocumentStrategySelector _selector;
    private readonly FamilyIdEmbedder         _familyIdEmbedder;
    private readonly ILogger<ChunkingService> _logger;

    public string Name => "TwoAxisChunking";

    public ChunkingService(
        DocumentStrategySelector selector,
        FamilyIdEmbedder familyIdEmbedder,
        ILogger<ChunkingService> logger)
    {
        _selector         = selector;
        _familyIdEmbedder = familyIdEmbedder;
        _logger           = logger;
    }

    // Async because family/domain identity resolution needs an embedding call before splitting
    // can start. Resolved once per document, up front, rather than per chunk.
    public async Task<(IReadOnlyList<DocumentChunk> Docs, ChunkingStageMetrics Stats)> ChunkDocumentsAsync(
        IReadOnlyList<PdfExtractionDocument> docs, CancellationToken ct = default)
    {
        var families = await _familyIdEmbedder.ResolveAsync(docs, ct);
        var result   = new List<DocumentChunk>();

        var headingsTotal = 0;
        var headingsFound = 0;
        var pairsMerged   = 0;

        foreach (var doc in docs.OrderBy(d => d.SourceId, StringComparer.Ordinal))
        {
            var strategy = _selector.Select(doc);
            if (strategy is null) continue;

            families.TryGetValue(doc.SourceId, out var family);

            var outcome = strategy.Chunk(doc);

            headingsTotal += outcome.HeadingsTotal;
            headingsFound += outcome.HeadingsLocated;
            pairsMerged   += outcome.PairedHeadingsMerged;

            foreach (var unit in outcome.Units)
                result.Add(ToChunk(doc, unit, family));
        }

        // The standing evidence for locating headings by string match rather than rewriting
        // PdfCleaner to emit an offset map. That call was made against 1,273/1,273 exact
        // matches with an escalation threshold fixed in advance at >2%, so the rate has to be
        // reported every run, not measured once and assumed to hold.
        if (headingsTotal > 0)
        {
            var failureRate = 1 - (headingsFound / (double)headingsTotal);
            var log = failureRate > 0.02 ? LogLevel.Warning : LogLevel.Information;

            _logger.Log(log,
                "Heading location: {Found}/{Total} ({Rate:P2} unlocated), {Merged} paired zero-body heading(s) merged",
                headingsFound, headingsTotal, failureRate, pairsMerged);
        }

        var sourceDocumentIds = docs.Select(d => d.SourceId).Distinct(StringComparer.Ordinal).ToList();

        var stats = ChunkingStageMetrics.Compute(result, Name, sourceDocumentIds);
        EmitChunkMetrics(stats, result);

        _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks ({Strategy})",
            docs.Count, result.Count, stats.Strategy);

        return (result, stats);
    }

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
        var titleLine = string.IsNullOrEmpty(family?.DomainTag)
            ? doc.Title
            : $"{doc.Title} [{family.DomainTag}]";

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
