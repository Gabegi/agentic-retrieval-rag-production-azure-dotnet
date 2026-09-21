using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.CU.Services;

public class RestoreService : IRestoreService
{
    // Must match PdfIndexingFunction's Source constant - snapshots for other doc-type
    // pipelines (e.g. "csv") are never restored through this path.
    private const string Source = "pdf";

    private readonly ISnapshotService     _snapshotService;
    private readonly IVectorCache         _vectorCache;
    private readonly IUploadService       _uploadService;
    private readonly IIndexService        _indexService;
    private readonly IndexerConfig        _config;
    private readonly ILogger<RestoreService> _logger;

    // IIndexService is here for one call: reading the live vector field width, for the same reason
    // the indexing pipeline reads it at preflight (D201). A restore publishes cached vectors it
    // did not produce, so "does this match the index" is if anything a sharper question here -
    // a cached vector can outlive the index generation it was made for.
    public RestoreService(
        ISnapshotService        snapshotService,
        IVectorCache            vectorCache,
        IUploadService          uploadService,
        IIndexService           indexService,
        IndexerConfig           config,
        ILogger<RestoreService> logger)
    {
        _snapshotService = snapshotService;
        _vectorCache     = vectorCache;
        _uploadService   = uploadService;
        _indexService    = indexService;
        _config          = config;
        _logger          = logger;
    }

    public async Task<RestoreResult> RestoreFromLatestSnapshotAsync(CancellationToken ct = default)
    {
        var (snapshotChunks, snapshotInstanceId) = await _snapshotService.ReadLatestAsync(Source, ct);

        if (snapshotChunks.Count == 0)
        {
            _logger.LogWarning("No snapshot found for source '{Source}' — nothing to restore.", Source);
            return new RestoreResult(snapshotInstanceId, 0, 0, 0, 0, null, null,
                _config.SearchIndexName, _config.OpenAiEmbeddingModelName, _config.OpenAiEmbeddingDeployment);
        }

        var chunks        = new List<ChunkObject>(snapshotChunks.Count);
        var missingVector = 0;

        foreach (var s in snapshotChunks)
        {
            var vector = (await _vectorCache.TryGetAsync(s.ContentHash, ct))?.Vector;
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

                    PageStart          = s.PageStart,
                    PageEnd            = s.PageEnd,
                    TokenCount         = s.TokenCount,

                    // Structure itself is not snapshotted, so these are restored as the
                    // stamped values they are (has_table included since 2026-09-09).
                    TableCount         = s.TableCount,
                    HasTable           = s.HasTable,
                    FigureCaptions     = s.FigureCaptions,
                    Hyperlinks         = s.Hyperlinks  ?? [],
                    Annotations        = s.Annotations ?? [],

                    // Source-system facts (2026-09-21): restored, never re-read - this side has
                    // no listing of the source container. Null from a pre-2026-09-21 snapshot.
                    SourceDocumentId   = s.SourceDocumentId,
                    SourceVersion      = s.SourceVersion,
                    SourceRevision     = s.SourceRevision,
                    SourceStatus       = s.SourceStatus,
                    SourceActive       = s.SourceActive,
                    SourceTitle        = s.SourceTitle,
                    SourceLanguage     = s.SourceLanguage,
                    QuickCode          = s.QuickCode,
                    FolderPath         = s.FolderPath,
                    FolderName         = s.FolderName,
                    SourceType         = s.SourceType,
                    SourceDocumentType = s.SourceDocumentType,
                    Summary            = s.Summary,
                    CheckDate          = s.CheckDate,
                    AttentionFlags     = s.AttentionFlags ?? [],
                },
            });
        }

        if (missingVector > 0)
            _logger.LogWarning(
                "{Missing} of {Total} restored chunk(s) had no cached vector — uploaded without content_vector, needs re-embedding on next incremental run.",
                missingVector, chunks.Count);

        // No family moves on a restore: the snapshot already carries each chunk's family_id, so
        // the rows go in correct rather than being patched afterwards. A restore rebuilds the
        // index from a point in time; it does not re-run the clustering that produces a move.
        // allowVectorless: the ONLY caller that sets it. A chunk whose vector the cache could not
        // resolve is uploaded without one on purpose - see the missingVector warning above; the
        // row is the restore, and withholding it would mean a restore that restores nothing for
        // those chunks. It exempts an absent vector only: a restored chunk that HAS a vector is
        // judged by VectorHealth.Classify exactly like a freshly embedded one.
        // The live width, for the same reason preflight reads it on the indexing path (D201).
        // Fails the restore rather than falling back to configuration: a restore that cannot read
        // the index cannot publish to it either, and validating against the configured width would
        // reinstate exactly the proxy this replaced.
        var vectorConfig = await _indexService.ReadVectorConfigAsync(ct);
        if (!vectorConfig.FieldPresent || vectorConfig.Dimensions is not > 0)
            throw new InvalidOperationException(
                $"Restore could not read the width of the index's '{vectorConfig.FieldName}' field; " +
                "cached vectors are judged against it, so the restore stops rather than guessing from configuration.");

        var uploadResult = await _uploadService.UploadDocumentsAsync(
            chunks, staleDocumentIds: [], familyMoves: [],
            indexVectorDimensions: vectorConfig.Dimensions.Value,
            allowVectorless: true, ct: ct);

        _logger.LogInformation(
            "Restore from snapshot '{InstanceId}' complete — {Restored} chunk(s) uploaded, {Failed} failed, {Missing} missing vectors, {Withheld} withheld.",
            snapshotInstanceId, uploadResult.DocsUploaded, uploadResult.DocsFailed, missingVector, uploadResult.DocsWithheld);

        return new RestoreResult(
            snapshotInstanceId,
            uploadResult.DocsUploaded,
            uploadResult.DocsFailed,
            missingVector,
            uploadResult.DocsWithheld,
            uploadResult.IndexDocumentCountSnapshot,
            uploadResult.IndexStorageSizeBytesSnapshot,
            _config.SearchIndexName,
            _config.OpenAiEmbeddingModelName,
            _config.OpenAiEmbeddingDeployment);
    }
}
