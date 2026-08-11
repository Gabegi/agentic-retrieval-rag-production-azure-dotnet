using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Services;

public class RestoreService : IRestoreService
{
    // Must match PdfIndexingFunction's Source constant - snapshots for other doc-type
    // pipelines (e.g. "csv") are never restored through this path.
    private const string Source = "pdf";

    private readonly ISnapshotService     _snapshotService;
    private readonly IVectorCache         _vectorCache;
    private readonly IUploadService       _uploadService;
    private readonly IndexerConfig        _config;
    private readonly ILogger<RestoreService> _logger;

    public RestoreService(
        ISnapshotService        snapshotService,
        IVectorCache            vectorCache,
        IUploadService          uploadService,
        IndexerConfig           config,
        ILogger<RestoreService> logger)
    {
        _snapshotService = snapshotService;
        _vectorCache     = vectorCache;
        _uploadService   = uploadService;
        _config          = config;
        _logger          = logger;
    }

    public async Task<RestoreResult> RestoreFromLatestSnapshotAsync(CancellationToken ct = default)
    {
        var (snapshotChunks, snapshotInstanceId) = await _snapshotService.ReadLatestAsync(Source, ct);

        if (snapshotChunks.Count == 0)
        {
            _logger.LogWarning("No snapshot found for source '{Source}' — nothing to restore.", Source);
            return new RestoreResult(snapshotInstanceId, 0, 0, 0, null, null,
                _config.SearchIndexName, _config.OpenAiEmbeddingModelName, _config.OpenAiEmbeddingDeployment);
        }

        var chunks        = new List<DocumentChunk>(snapshotChunks.Count);
        var missingVector = 0;

        foreach (var s in snapshotChunks)
        {
            var vector = await _vectorCache.TryGetAsync(s.ContentHash, ct);
            if (vector is null) missingVector++;

            // Zenya fields are deliberately not carried here - the snapshot doesn't record
            // them (ISnapshotSource predates them), so a restored chunk goes back in without
            // Zenya traceability until the next incremental run re-touches its document and
            // re-populates them from blob metadata.
            chunks.Add(new DocumentChunk
            {
                Id               = s.Id,
                DocumentId       = s.DocumentId,
                Title            = s.Title,
                LastModifiedDate = s.LastModifiedDate,
                Content          = s.Content,
                Heading          = s.Heading,
                PageNumber       = s.PageNumber,
                ChunkIndex       = s.ChunkIndex,
                ContentVector    = vector,
            });
        }

        if (missingVector > 0)
            _logger.LogWarning(
                "{Missing} of {Total} restored chunk(s) had no cached vector — uploaded without content_vector, needs re-embedding on next incremental run.",
                missingVector, chunks.Count);

        var uploadResult = await _uploadService.UploadDocumentsAsync(chunks, staleDocumentIds: [], ct);

        _logger.LogInformation(
            "Restore from snapshot '{InstanceId}' complete — {Restored} chunk(s) uploaded, {Failed} failed, {Missing} missing vectors.",
            snapshotInstanceId, uploadResult.DocsUploaded, uploadResult.DocsFailed, missingVector);

        return new RestoreResult(
            snapshotInstanceId,
            uploadResult.DocsUploaded,
            uploadResult.DocsFailed,
            missingVector,
            uploadResult.IndexDocumentCountSnapshot,
            uploadResult.IndexStorageSizeBytesSnapshot,
            _config.SearchIndexName,
            _config.OpenAiEmbeddingModelName,
            _config.OpenAiEmbeddingDeployment);
    }
}
