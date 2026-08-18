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

        var chunks        = new List<ChunkObject>(snapshotChunks.Count);
        var missingVector = 0;

        foreach (var s in snapshotChunks)
        {
            var vector = await _vectorCache.TryGetAsync(s.ContentHash, ct);
            if (vector is null) missingVector++;

            // Every field the index holds is restored, because the snapshot now records every
            // field the index holds. The vector is the sole exception, and it is resolved from
            // the cache by ContentHash above rather than re-embedded.
            //
            // HeadingSource and HeadingLocated are restored as a PAIR. Letting either fall back
            // to a type default would make a restored row's heading provenance an artefact of
            // which type wrote it rather than a fact about the chunk.
            chunks.Add(new ChunkObject
            {
                Content        = s.Content,
                HeadingText    = s.HeadingText,
                HeadingPath    = s.HeadingPath,
                HeadingDepth   = s.HeadingDepth,
                HeadingSource  = s.HeadingSource ?? ChunkHeadingSource.None,
                HeadingLocated = s.HeadingLocated,
                SectionIndex   = s.SectionIndex,
                ChildIndex     = s.ChildIndex,
                ParentText     = s.ParentText,
                IsOverlap      = s.IsOverlap,
                ContentVector  = vector,

                // Ids, document facts and page attribution are stamped metadata, so they go on
                // the metadata even on this path - the chunk's own accessors read through to it.
                Metadata = new ChunkMetadata
                {
                    Id                 = s.Id,
                    DocumentId         = s.DocumentId,
                    SectionId          = s.SectionId,
                    Grain              = s.Grain,

                    Title              = s.Title,
                    Language           = s.Language,
                    Population         = s.Population,

                    // Restored, never recomputed. Prefix is what ContentHash is derived from,
                    // so rebuilding it here from title and heading path would risk a hash that
                    // disagrees with the one the vector was just resolved by; validity is parsed
                    // from a title this side no longer re-parses.
                    Prefix             = s.Prefix,
                    ValidFrom          = s.ValidFrom,
                    ValidTo            = s.ValidTo,
                    Version            = s.Version,

                    FamilyId           = s.FamilyId,
                    DomainTag          = s.DomainTag,
                    ConfusableWith     = s.ConfusableWith,

                    LastModifiedDate   = s.LastModifiedDate,
                    CreatedAt          = s.CreatedAt,
                    ModDate            = s.ModDate,
                    PageCount          = s.PageCount,

                    ZenyaDocumentId    = s.ZenyaDocumentId,
                    ZenyaVersion       = s.ZenyaVersion,
                    ZenyaStatus        = s.ZenyaStatus,
                    ZenyaUrl           = s.ZenyaUrl,

                    PageStart          = s.PageStart,
                    PageEnd            = s.PageEnd,
                    PageExtractionFlag = s.PageExtractionFlag,
                    TokenCount         = s.TokenCount,

                    // Structure itself is not snapshotted, so these two are restored as the
                    // stamped values they are. has_table needs no restoring - it recomputes off
                    // Content, which came back above.
                    TableCount         = s.TableCount,
                    FigureCaptions     = s.FigureCaptions,
                },
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
