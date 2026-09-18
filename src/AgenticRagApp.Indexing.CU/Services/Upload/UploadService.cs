using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.CU.Services;

// Owns the upload half of the indexing pipeline: upserts embedded DocumentChunks
// into Azure AI Search and takes a post-upload index stats/drift snapshot.
// Kept separate from EmbeddingService so the two concerns can evolve independently.
public class UploadService : IUploadService
{
    // Scopes the drift-baseline (IIndexStatsMonitor.RecordAndCheckDriftAsync) to this doc-type.
    // Only "pdf" exists today (the CSV pipeline is archived); the scoping stays so a second
    // source can never compare against this one's baseline.
    private const string Source = "pdf";

    // Per-id Error lines are capped at this many, with a per-verdict summary after. A
    // configuration drift withholds EVERY chunk, and a 3,700-line Error burst buries the one
    // line that says what happened.
    private const int MaxWithheldLogged = 20;

    private readonly IIndexDocumentService      _indexDocumentService;
    private readonly IIndexStatsMonitor         _indexStatsMonitor;
    private readonly ILogger<UploadService>     _logger;

    public UploadService(
        IIndexDocumentService  indexDocumentService,
        IIndexStatsMonitor     indexStatsMonitor,
        ILogger<UploadService> logger)
    {
        _indexDocumentService = indexDocumentService;
        _indexStatsMonitor    = indexStatsMonitor;
        _logger               = logger;
    }

    public async Task<UploadResult> UploadDocumentsAsync(
        IEnumerable<ChunkObject> documents,
        IReadOnlyList<string>    staleDocumentIds,
        IReadOnlyList<FamilyMove> familyMoves,
        int indexVectorDimensions,
        bool allowVectorless = false,
        CancellationToken ct = default)
    {
        var docList = documents.ToList();

        var (publishable, withheld) = PartitionByVectorHealth(docList, allowVectorless, indexVectorDimensions);

        // Log and meter BEFORE the guard, because the guard throws. A total withhold is the
        // loudest thing that can happen here and the run dies on it, so it is exactly the case
        // that must not also be the silent one: with the order reversed, a configuration drift
        // would take the run down having recorded neither an id nor a counter.
        LogWithheld(docList.Count, withheld);
        GuardAgainstTotalWithhold(docList.Count, withheld, indexVectorDimensions);

        // Maps down to the exact field set the Search schema knows about, right here, at
        // the last possible moment before handing off to the generic (doc-type-agnostic)
        // upload path - see SearchUploadChunk's own comment.
        var uploadBatch = publishable.Select(SearchUploadChunk.From).ToList();
        var upsert = await _indexDocumentService.UpsertDocumentsAsync(uploadBatch, ct);
        var (succeeded, refused, batches, batchDurationsMs) = upsert;

        // Per-batch wall-clock onto the histogram (D203 M7). Every batch but the last is the
        // 1,000-document maximum; the last is whatever remained, and tagging it apart is what
        // keeps a 711-document tail from reading as a fast full batch.
        var lastBatchIsFull = uploadBatch.Count % 1000 == 0;
        for (var i = 0; i < batchDurationsMs.Count; i++)
        {
            var isLast = i == batchDurationsMs.Count - 1;
            Instrumentation.SearchUploadBatchMs.Record(batchDurationsMs[i],
                new KeyValuePair<string, object?>("batch", isLast && !lastBatchIsFull ? "tail" : "full"));
        }

        _logger.LogInformation("Upload complete — {Succeeded} succeeded, {Failed} refused by Search, {Withheld} withheld",
            succeeded, refused, withheld.Count);

        // Wired 2026-08-26 (observability plan 3.1) - these three instruments existed from the
        // start but nothing ever recorded them, so dashboards read "no uploads" against runs
        // that uploaded thousands.
        Instrumentation.DocsUpserted.Add(succeeded);
        // Refused only, not refused + withheld: this meter means "Azure AI Search rejected it",
        // and the dashboards built on it read it that way. A withheld chunk never reached Search.
        // It is the report, not the meter, that folds the two (IndexingFunction).
        Instrumentation.UploadFailures.Add(refused);
        Instrumentation.UploadBatchCount.Add(batches);

        // Only now, with replacement content already live, clean up what's actually orphaned:
        // chunk ids that existed for a stale (updated/removed) document but aren't among the
        // ids this run accounted for. Anything we just touched - a failed upsert, or a chunk we
        // WITHHELD - is kept, since in both cases the old content at that id is still the
        // authoritative one.
        //
        // Built from docList, NOT from publishable, and the name says so. Narrowing this set to
        // what was actually uploaded is the single change in this file that would make the
        // pipeline destroy working content: IndexDiffService puts every updated document into
        // staleDocumentIds, so a withheld chunk's id missing from here would delete the previous
        // GOOD row - strictly worse than the dead row the withhold exists to prevent. Pinned by
        // UploadDocumentsAsync_WithheldChunkId_IsStillProtectedFromOrphanDelete.
        //
        // What retaining it assumes: chunk ids are deterministic (ChunkMetadata.Id is derived
        // from document id + section + child index), so the next run that re-embeds this chunk
        // successfully upserts straight over the retained row and the staleness ends. That run
        // only happens if the document comes back through the diff, which is A2's job - and for
        // a withheld chunk that had NO prior row, A2 cannot bring it back either (D199 §3/A2).
        // Until then the retained row serves stale content with the host log as the only signal.
        var chunksRemoved = 0;
        if (staleDocumentIds.Count > 0)
        {
            var orphanProtectedIds = docList.Select(d => d.Id).ToHashSet();
            var existingChunkIds   = await _indexDocumentService.GetChunkIdsForDocumentsAsync(staleDocumentIds, ct);
            var orphanedChunkIds   = existingChunkIds.Where(id => !orphanProtectedIds.Contains(id)).ToList();

            if (orphanedChunkIds.Count > 0)
                chunksRemoved = await _indexDocumentService.DeleteChunksByIdAsync(orphanedChunkIds, ct);

            Instrumentation.ChunksRemoved.Add(chunksRemoved);
            _logger.LogInformation(
                "Stale-chunk cleanup for {DocCount} document(s) — {Removed} orphaned chunk(s) deleted",
                staleDocumentIds.Count, chunksRemoved);
        }

        var familiesPatched = await PatchMovedFamiliesAsync(familyMoves, docList, ct);

        // Stats snapshot taken after upload. Azure Search stats lag live writes by minutes —
        // use for corpus drift checks only, not for "did this run add N chunks" (use succeeded/failed).
        long? indexDocCount = null, indexStorageBytes = null, indexVectorBytes = null;
        var drift = IndexDriftCheck.None;
        try
        {
            var (docCount, storageBytes, vectorBytes) = await _indexDocumentService.GetStatisticsAsync(ct);
            (indexDocCount, indexStorageBytes, indexVectorBytes) = (docCount, storageBytes, vectorBytes);
            drift = await _indexStatsMonitor.RecordAndCheckDriftAsync(Source, docCount, storageBytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Index stats snapshot failed — upload results are unaffected");
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "stats_snapshot"));
        }

        return new UploadResult(
            // succeeded, never uploadBatch.Count: the identity has two sides, and counting the
            // batch would report a chunk Search refused as uploaded.
            DocsUploaded:                  succeeded,
            DocsFailed:                    refused,
            ChunksRemoved:                 chunksRemoved,
            ChunkFamiliesPatched:          familiesPatched,
            IndexDocumentCountSnapshot:    indexDocCount,
            IndexStorageSizeBytesSnapshot: indexStorageBytes,
            RedFlags:                      drift.RedFlags,
            PreviousIndexDocumentCount:    drift.PreviousDocumentCount,
            PreviousIndexStorageSizeBytes: drift.PreviousStorageSizeBytes)
        {
            IndexVectorIndexSizeBytesSnapshot = indexVectorBytes,
            DocsWithheld                      = withheld.Count,
            DocumentsWithheld                 = withheld.Select(w => w.DocumentId).Distinct().Count(),
            SearchUploadBatches               = batches,
            SearchUploadBatchMaxMs            = batchDurationsMs.Count > 0 ? batchDurationsMs.Max() : null,
            SearchUploadBytes                 = upsert.BytesSent,
        };
    }

    // A withheld chunk: its id, why, and the verdict that decided it. Verdict is null for the one
    // reason Classify cannot produce - an absent vector, which has no verdict at all.
    private readonly record struct WithheldChunk(string Id, string DocumentId, string Reason, VectorVerdict? Verdict);

    // The same predicate that decides what is worth CACHING decides what is worth PUBLISHING
    // (VectorHealth.Classify, 2026-09-17). Until then the embed stage counted a wrong-width or
    // unusable vector as a defect and uploaded it anyway - the cache refused it, so it was
    // re-embedded whenever the document came back, and the document-level diff means it usually
    // never does.
    private (List<ChunkObject> Publishable, List<WithheldChunk> Withheld) PartitionByVectorHealth(
        List<ChunkObject> docList, bool allowVectorless, int indexVectorDimensions)
    {
        var publishable = new List<ChunkObject>(docList.Count);
        var withheld    = new List<WithheldChunk>();

        foreach (var doc in docList)
        {
            // An ABSENT vector is not an unhealthy one, and the two callers want opposite answers.
            // RestoreService rebuilds rows from a snapshot and uploads the ones whose vector it
            // could not resolve from the cache ON PURPOSE (see its missingVector log) - those rows
            // are the restore. On the indexing path a null cannot occur (BatchEmbedder hands back
            // Vector.ToArray(), and a short response array throws before this), and if one ever
            // did it would mean an embed failure, so it is refused rather than silently published.
            //
            // Classify is not consulted and must not be: it throws on null, and a null has no
            // verdict to report. This branch therefore has to come first.
            if (doc.ContentVector is not { } vector)
            {
                if (allowVectorless) publishable.Add(doc);
                else withheld.Add(new WithheldChunk(
                    doc.Id, doc.DocumentId, "vector absent, and vectorless rows are not allowed on this path", null));
                continue;
            }

            // allowVectorless exempts a NULL vector only. A restored row that carries a vector is
            // judged exactly like a freshly embedded one - a wrong-width vector out of the cache
            // is no more publishable for having come from a snapshot.
            var verdict = VectorHealth.Classify(vector, indexVectorDimensions);
            if (verdict is VectorVerdict.Healthy) publishable.Add(doc);
            else withheld.Add(new WithheldChunk(doc.Id, doc.DocumentId, ReasonFor(verdict, vector, indexVectorDimensions), verdict));
        }

        return (publishable, withheld);
    }

    private static string ReasonFor(VectorVerdict verdict, float[] vector, int indexVectorDimensions) =>
#pragma warning disable CS8524
        verdict switch
        {
            VectorVerdict.WrongWidth => $"vector is {vector.Length} wide, but the index's vector field is {indexVectorDimensions}",
            VectorVerdict.NonFinite  => "vector carries a NaN or infinity and cannot be serialised — left in the batch it would fail the whole upload",
            VectorVerdict.Empty      => "vector is all-zero and could never match a query",
            VectorVerdict.Healthy    => throw new InvalidOperationException("A healthy vector is not withheld"),
        };
#pragma warning restore CS8524

    // A TOTAL withhold is a configuration fault, not a data-quality event, and it has to throw:
    // otherwise the run reports success with DocsUploaded = 0 while every stale row is retained
    // by the orphan-protection set above - structurally indistinguishable from "nothing to do".
    //
    // Only 100% throws. A ratio would need a threshold, and there is nothing to set one from:
    // VectorDimErrors and EmptyVectors are 0 on every run in the archive. 100% is an identity,
    // not a guess.
    //
    // Scoped per RUN, not per document: both callers pass the whole corpus in one call
    // (IndexingFunction, RestoreService), so this cannot fire for a single bad document.
    private static void GuardAgainstTotalWithhold(int total, List<WithheldChunk> withheld, int indexVectorDimensions)
    {
        if (total == 0 || withheld.Count != total) return;

        // The diagnosis turns on WHICH verdict, because the two send the reader to opposite ends
        // of the pipeline.
        //
        // All-WrongWidth means the model's output does not match the INDEX's vector field. Since
        // D201 that no longer implicates OPENAI_EMBEDDING_DIMENSIONS: vectors are judged against
        // the live index width read at preflight, so the configured value is not part of this
        // verdict at all. It means the deployment changed under a stable index, or the index was
        // rebuilt at a width the deployment does not produce.
        //
        // Anything else is the embedding deployment returning unusable values, and sending that
        // reader to the index schema would waste the outage. Carried as a property on the
        // exception so a reporter branches on it rather than re-deriving it.
        var isDimensionDrift = withheld.All(w => w.Verdict is VectorVerdict.WrongWidth);

        var verdictCounts = withheld
            .GroupBy(w => w.Verdict?.ToString() ?? "NoVector")
            .ToDictionary(g => g.Key, g => g.Count());

        throw new TotalWithholdException(
            total,
            withheld.Select(w => w.DocumentId).Distinct().Count(),
            indexVectorDimensions,
            verdictCounts,
            isDimensionDrift);
    }

    private void LogWithheld(int total, List<WithheldChunk> withheld)
    {
        if (withheld.Count == 0) return;

        // Metered for every withheld chunk, not just the ones the log names: the per-id lines are
        // capped and the run report's defect counts come from the embedder, so on the restore
        // path this counter is the only telemetry that a withhold happened at all.
        foreach (var w in withheld)
            Instrumentation.ChunksWithheld.Add(1, VectorHealth.VerdictTag(w.Verdict));

        foreach (var w in withheld.Take(MaxWithheldLogged))
            _logger.LogError(
                "Withholding {Id} from the index — {Reason}. Any previously indexed row is left in place.",
                w.Id, w.Reason);

        // Per-verdict counts as structured properties rather than a total in a string: this is
        // the line that gets queried, and "how many of which" is the question it has to answer.
        _logger.LogError(
            "Withheld {Withheld} chunk(s) across {Documents} document(s), of {Total} total — wrong width {WrongWidth}, non-finite {NonFinite}, all-zero {Empty}, no vector {NoVector}. First {Listed} listed individually above.",
            withheld.Count,
            // Distinct documents, not just chunks: the per-id lines are capped, so once a run
            // withholds more than the cap a persistently stuck document hides behind whatever
            // else withheld that run. A flat non-zero document count across runs is the signal
            // that something is stuck rather than transient, readable without the ids.
            withheld.Select(w => w.DocumentId).Distinct().Count(),
            total,
            withheld.Count(w => w.Verdict is VectorVerdict.WrongWidth),
            withheld.Count(w => w.Verdict is VectorVerdict.NonFinite),
            withheld.Count(w => w.Verdict is VectorVerdict.Empty),
            withheld.Count(w => w.Verdict is null),
            Math.Min(withheld.Count, MaxWithheldLogged));
    }

    // Patches family_id onto the indexed rows of documents this run re-homed, without touching
    // their content.
    //
    // Why a patch and not a re-index: a family move is caused by OTHER documents changing the
    // clustering, so the moved document's own bytes are unchanged. ExtractionService's diff
    // therefore skips it, it never reaches chunking, and this run holds no ChunkObject for it -
    // while its indexed rows keep a family_id that is now wrong, in the field the knowledge agent
    // filters on. Nothing else in the pipeline can see this: the chunk text is identical, so a
    // content hash matches, and the document-level gate skipped it before chunking ran.
    //
    // The chunk ids come from the INDEX, for the same reason: we have no chunk list of our own
    // for these documents. Same call the stale-chunk cleanup above uses.
    //
    // Runs after the upsert, and after the cleanup, so it patches settled rows. Documents that
    // were uploaded this run are excluded - their rows already carry the new family_id from the
    // projection, and patching them again would be a second write saying the same thing.
    private async Task<int> PatchMovedFamiliesAsync(
        IReadOnlyList<FamilyMove> familyMoves, IReadOnlyList<ChunkObject> uploaded, CancellationToken ct)
    {
        if (familyMoves.Count == 0) return 0;

        var uploadedDocIds = uploaded.Select(d => d.DocumentId).ToHashSet(StringComparer.Ordinal);
        var toPatch        = familyMoves.Where(m => !uploadedDocIds.Contains(m.SourceId)).ToList();

        if (toPatch.Count == 0)
        {
            _logger.LogInformation(
                "Family moves: all {Count} re-homed document(s) were uploaded this run, so their rows already carry the new family_id",
                familyMoves.Count);
            return 0;
        }

        // Queried one document at a time, deliberately. The batched form returns a flat id list
        // with no document_id attached, so pairing an id back to the family it should get would
        // mean parsing it - and ChunkIdBuilder runs sourceId through SafeKey, so the id is not
        // reliably a prefix match on the source id. One query per moved document is exact, and
        // moved documents are a handful per run at most (a whole corpus re-homing is a clustering
        // failure, not a workload).
        var patches = new List<ChunkFamilyPatch>();
        foreach (var move in toPatch)
        {
            var chunkIds = await _indexDocumentService.GetChunkIdsForDocumentsAsync([move.SourceId], ct);
            patches.AddRange(chunkIds.Select(id => new ChunkFamilyPatch(id, move.ToFamilyId)));
        }

        if (patches.Count == 0)
        {
            // The identity store says these documents moved; the index has no rows for them. Not
            // an error - a document resolved but never successfully indexed does this - but it
            // means the two are describing different corpora, which is worth saying out loud.
            _logger.LogWarning(
                "Family moves: {Count} document(s) were re-homed but have no rows in the index to patch",
                toPatch.Count);
            return 0;
        }

        var (patched, patchFailed) = await _indexDocumentService.MergeDocumentFieldsAsync(patches, ct);

        Instrumentation.ChunkFamiliesPatched.Add(patched);
        _logger.Log(patchFailed > 0 ? LogLevel.Warning : LogLevel.Information,
            "Family re-homing — {Patched} chunk row(s) across {Docs} document(s) moved to a new family_id, {Failed} failed",
            patched, toPatch.Count, patchFailed);

        return patched;
    }
}
