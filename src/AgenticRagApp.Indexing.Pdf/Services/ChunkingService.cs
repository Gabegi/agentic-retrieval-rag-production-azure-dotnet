using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

public class ChunkingService : IChunkingService
{
    private readonly IChunkingStrategy             _strategy;
    private readonly FamilyIdEmbedder              _familyIdEmbedder;
    private readonly ILogger<ChunkingService>      _logger;

    public string Name => _strategy.Name;

    public ChunkingService(IChunkingStrategy strategy, FamilyIdEmbedder familyIdEmbedder, ILogger<ChunkingService> logger)
    {
        _strategy         = strategy;
        _familyIdEmbedder = familyIdEmbedder;
        _logger           = logger;
    }

    // Low-level passthrough — splits raw text into TextChunks.
    public IReadOnlyList<TextChunk> Chunk(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        return _strategy.Chunk(content);
    }

    // Converts ExtractionDocuments into indexed DocumentChunks,
    // computes ChunkingStageMetrics, and emits all chunk telemetry in one place.
    //
    // Async (unlike the old sync ChunkDocuments) because family/domain identity resolution
    // (FamilyIdEmbedder - pre-chunking action items C2/C3) needs an embedding call before
    // splitting can start. Resolved once per document, up front, rather than per chunk -
    // same "resolve once, carry onto every chunk" shape as Title/Breadcrumb below.
    public async Task<(IReadOnlyList<DocumentChunk> Docs, ChunkingStageMetrics Stats)> ChunkDocumentsAsync(
        IReadOnlyList<PdfExtractionDocument> docs, CancellationToken ct = default)
    {
        var families = await _familyIdEmbedder.ResolveAsync(docs, ct);
        var result   = new List<DocumentChunk>();

        foreach (var doc in docs.OrderBy(d => d.SourceId).ThenBy(d => d.Ordinal))
        {
            families.TryGetValue(doc.SourceId, out var family);

            var chunks = Chunk(doc.Content);

            // Real per-page section context: Breadcrumb (hierarchical, from the bookmark
            // outline) is preferred when present; otherwise fall back to the first
            // DI-detected heading on this page. Null when the page has neither - previously
            // this was always null, since nothing ever set TextChunk.Heading.
            //
            // TODO: most of this corpus has no bookmark outline (see docs/260727 run), so
            // Breadcrumb is rarely populated and this falls back to a flat single heading
            // with no parent chain. Once a PdfHeadingBreadCrumbBuilder exists - walking
            // doc.Headings with depth inferred from numbering prefixes ("2.3 ..." -> depth 2),
            // the same stack algorithm as PdfSectionBreadCrumbBuilder - prefer its output here
            // ahead of the flat FirstOrDefault() fallback.
            var heading = doc.Breadcrumb ?? doc.Headings.FirstOrDefault()?.Content;

            // Chunk ordinal is scoped to this document (SourceId + Ordinal), not the run —
            // otherwise the same document gets different chunk IDs depending on which other
            // documents happen to be processed alongside it in a given run.
            for (int docChunkIndex = 0; docChunkIndex < chunks.Count; docChunkIndex++)
            {
                var chunk = chunks[docChunkIndex];

                // Prepend the document title, then the page's heading/breadcrumb, so every
                // chunk — including short continuation pages with no query-term overlap on
                // their own — benefits from both the parent document's identity and its
                // section context in BM25 and vector scoring.
                var body    = heading != null ? $"{heading}\n\n{chunk.Content}" : chunk.Content;
                var content = string.IsNullOrEmpty(doc.Title) ? body : $"{doc.Title}\n\n{body}";

                // content always ends with chunk.Content verbatim (built only by prepending
                // to it above), so slicing off that length recovers exactly the prepended
                // title/heading prefix - estimated at the prose ratio, since a document
                // title/heading is never table markdown regardless of what the chunk itself
                // is. Added on top of chunk.EstimatedTokens (already correctly ratio'd for
                // that chunk's own content) rather than re-estimating the whole thing at one
                // ratio, which would misprice a table-heavy chunk's dominant share.
                var prefix     = content[..(content.Length - chunk.Content.Length)];
                var tokenCount = chunk.EstimatedTokens + ChunkingHelper.EstimateTokens(prefix, isTable: false);

                result.Add(new DocumentChunk
                {
                    Id                    = ChunkingHelper.SafeKey($"{doc.SourceId}::{doc.Ordinal}", docChunkIndex),
                    DocumentId            = doc.SourceId,
                    Title                 = doc.Title,
                    LastModifiedDate      = doc.LastModifiedDate,
                    ZenyaDocumentId       = doc.ZenyaDocumentId,
                    ZenyaVersion          = doc.ZenyaVersion,
                    ZenyaStatus           = doc.ZenyaStatus,
                    ZenyaUrl              = doc.ZenyaUrl,
                    Content               = content,
                    TokenCount            = tokenCount,
                    Heading               = heading,
                    PageNumber            = doc.Ordinal,
                    ChunkIndex            = docChunkIndex,
                    Author                = doc.Author,
                    CreatedAt             = doc.CreatedAt,
                    ModDate               = doc.ModDate,
                    PageCount             = doc.PageCount,
                    Bookmarks             = doc.Bookmarks,
                    Sections              = doc.Sections,
                    Breadcrumb            = doc.Breadcrumb,
                    FamilyId              = family?.FamilyId,
                    DomainTag             = family?.DomainTag,
                    ConfusableWith        = family?.ConfusableWith ?? [],
                    Structure             = new ChunkStructure(
                        Headings:       doc.Headings,
                        Boilerplate:    doc.Boilerplate,
                        Tables:         doc.Tables,
                        Dimensions:     doc.Dimensions,
                        SelectionMarks: doc.SelectionMarks,
                        Figures:        doc.Figures,
                        Lines:          doc.Lines),
                });
            }
        }

        // Input document IDs, not the produced chunks' - a document that produced nothing has
        // no chunk to derive its ID from, so deriving the input set from the output made
        // DocsWithZeroChunks structurally always 0. SourceId is the chunking boundary and
        // repeats across a document's pages, hence Distinct.
        var sourceDocumentIds = docs.Select(d => d.SourceId).Distinct(StringComparer.Ordinal).ToList();

        var stats = ChunkingStageMetrics.Compute(result, Name, sourceDocumentIds);
        EmitChunkMetrics(stats, result);

        _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks ({Strategy})",
            docs.Count, result.Count, stats.Strategy);

        return (result, stats);
    }

    private static void EmitChunkMetrics(ChunkingStageMetrics stats, IReadOnlyList<DocumentChunk> chunks)
    {
        var strategyTag = new KeyValuePair<string, object?>("strategy", stats.Strategy);

        Instrumentation.ChunksExtracted.Record(stats.ChunksProduced, strategyTag);

        // Per-chunk histogram — preserves the real distribution in App Insights,
        // not just the aggregates already in ChunkingStageMetrics.
        foreach (var chunk in chunks)
            Instrumentation.ChunkSizeChars.Record(chunk.Content.Length, strategyTag);

        Instrumentation.ChunkSizeBand.Add(stats.BandUnder100,  strategyTag, new("band", "under_100"));
        Instrumentation.ChunkSizeBand.Add(stats.Band100To500,  strategyTag, new("band", "100_to_500"));
        Instrumentation.ChunkSizeBand.Add(stats.Band500To1500, strategyTag, new("band", "500_to_1500"));
        Instrumentation.ChunkSizeBand.Add(stats.Band1500Plus,  strategyTag, new("band", "1500_plus"));

        // All quality counters now carry strategyTag consistently.
        Instrumentation.DuplicateChunks.Add(stats.DuplicateChunks,   strategyTag);
        Instrumentation.CoherentChunks.Add(stats.CoherentChunks,     strategyTag);
        Instrumentation.HeadingsDetected.Add(stats.HeadingsDetected, strategyTag);

        if (stats.DocsWithZeroChunks > 0)
            Instrumentation.DocsWithZeroChunks.Add(stats.DocsWithZeroChunks, strategyTag);
    }
}
