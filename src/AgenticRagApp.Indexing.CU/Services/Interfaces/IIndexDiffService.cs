using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// The pre-extraction diff: what is in blob storage, what is already in the Search index, and
// which documents this run therefore has to pay to extract.
//
// Split out of ExtractionService so that "what needs doing" and "do it, then report on it" are
// separately testable - the diff is pure decision-making over two cheap listings and never
// touches the extraction backend, while everything left in ExtractionService is sequencing.
//
// Kept as an interface so ExtractionService's
// unit tests can mock it, not to support a second implementation.
public interface IIndexDiffService
{
    Task<IndexDiff> FindDocsNotInIndexAsync(bool forceReindex, CancellationToken ct = default);
}

// One run's diff decision.
//
// EntriesToProcess carries the full PdfBlobInfo (LastModified/ContentLength/Zenya), not just
// the ids, so the orchestrator never lists the container a second time - see
// ExtractionService's extraction loop.
//
// SourceCount/IndexedCount are the raw sizes of the two sides, carried purely so
// ExtractionService can run its high-new-doc-fraction tripwire without re-reading either
// listing.
public sealed record IndexDiff(
    IReadOnlyDictionary<string, PdfBlobInfo> EntriesToProcess,
    IReadOnlyList<string>                    RemovedSourceIds,
    IReadOnlyList<string>                    ToDeleteChunks,
    int                                      NewCount,
    int                                      Updated,
    int                                      Skipped,
    int                                      Inactive,
    int                                      SourceCount,
    int                                      IndexedCount);
