using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// One strategy per document: the route HeadingSectionGate chose.
//
// Takes the whole document rather than a string - a boundary-aware strategy needs the headings,
// the section tree and the page map, none of which fit through Chunk(string).
//
// Returns the cuts only: Content, Start/Length, ordinals and heading fields. Ids, page
// attribution and document metadata are step 4's business (ChunkMetadataBuilder), so a strategy
// never decides what an indexed row looks like.
//
// Awaitable because a strategy will eventually await - Content Understanding enrichment on the
// recursive route is the expected first caller.
//
// ValueTask rather than Task because today BOTH implementations are pure string work that
// completes synchronously: ValueTask carries that result without allocating a Task object per
// document, and still turns into a real awaited operation the day CU lands, with no signature
// change and no caller change. The usual ValueTask caveat applies - await it exactly once,
// never store it - which is what the per-document loop in ChunkingService does anyway.
public interface IDocumentChunkingStrategy
{
    string Name { get; }

    ValueTask<IReadOnlyList<ChunkObject>> ChunkDocumentAsync(
        PdfExtractionDocument doc, CancellationToken ct = default);
}
