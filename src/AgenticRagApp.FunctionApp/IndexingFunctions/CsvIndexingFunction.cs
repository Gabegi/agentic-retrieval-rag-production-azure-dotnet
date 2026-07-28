// CSV indexing skeleton — commented out. CSV has no active Durable flow today; this is
// a shape-only reference for whenever activating it becomes a deliberate decision, not
// live code. Uncomment together with re-adding the AgenticRagApp.Indexing.Csv
// ProjectReference in AgenticRagApp.FunctionApp.csproj.
/*
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Csv.Services;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Functions;

public class CsvIndexingFunction
{
    // Scopes the rolling snapshot and drift baseline to this doc-type - PDF and CSV must
    // never share or merge either one.
    private const string Source = "csv";

    private readonly IExtractionService           _extractionService;
    private readonly IChunkingService             _chunkingService;
    private readonly IEmbeddingService            _embeddingService;
    private readonly IUploadService               _uploadService;
    private readonly BlobContainerClient          _pipelineContainer;
    private readonly IBlobStore                   _blobStore;
    private readonly IRunReportWriter             _reportWriter;
    private readonly IPipelineArtifactWriter      _artifactWriter;
    private readonly ISnapshotService             _snapshotService;
    private readonly ILogger<CsvIndexingFunction> _logger;

    public CsvIndexingFunction(
        IExtractionService              extractionService,
        IChunkingService                chunkingService,
        IEmbeddingService               embeddingService,
        IUploadService                  uploadService,
        [FromKeyedServices("pipeline-temp")] BlobContainerClient pipelineContainer,
        IBlobStore                      blobStore,
        IRunReportWriter                reportWriter,
        IPipelineArtifactWriter         artifactWriter,
        ISnapshotService                snapshotService,
        ILogger<CsvIndexingFunction>    logger)
    {
        _extractionService = extractionService;
        _chunkingService    = chunkingService;
        _embeddingService   = embeddingService;
        _uploadService       = uploadService;
        _pipelineContainer  = pipelineContainer;
        _blobStore          = blobStore;
        _reportWriter       = reportWriter;
        _artifactWriter     = artifactWriter;
        _snapshotService    = snapshotService;
        _logger             = logger;
    }

    public async Task RunOrchestrator(TaskOrchestrationContext context)
    {
        var startedAt = context.CurrentUtcDateTime;
        var input     = context.GetInput<IndexRequest>()!;
        var docsBlob   = $"{context.InstanceId}/extracted.json";
        var chunksBlob = $"{context.InstanceId}/chunks.json";

        ExtractionResults?     extractResults = null;
        ChunkingResults?       chunkResults   = null;
        EmbedUploadingResults? embedResults   = null;
        bool    success = false;
        string? error   = null;

        try
        {
            extractResults = await context.CallActivityAsync<ExtractionResults>("CsvExtractActivity",        new ExtractRequest(input.ForceReindex, docsBlob, context.InstanceId, startedAt));
            chunkResults   = await context.CallActivityAsync<ChunkingResults>("CsvChunkActivity",               new ChunkRequest(docsBlob, chunksBlob, context.InstanceId, startedAt));
            embedResults   = await context.CallActivityAsync<EmbedUploadingResults>("CsvEmbedAndUploadActivity", new EmbedUploadRequest(chunksBlob, extractResults.StaleDocumentIds, context.InstanceId, startedAt));
            success      = true;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }

        await context.CallActivityAsync("SaveCsvIndexReportActivity",
            BuildReport(context, startedAt, input, extractResults, chunkResults, embedResults, success, error));

        if (!success)
            throw new InvalidOperationException(error ?? "Indexing pipeline failed");
    }

    public async Task<ExtractionResults> ExtractActivity(ExtractRequest req, FunctionContext context)
    {
        var (docs, stats) = await _extractionService.ExtractAsync(req.ForceReindex, ct: context.CancellationToken);
        await WriteBlobAsync(req.OutputBlob, docs, context.CancellationToken);

        await _artifactWriter.WriteArtifactAsync(
            $"{req.StartedAt:yyyy/MM/dd}/{req.InstanceId}/extraction.json", new { Docs = docs, Stats = stats }, context.CancellationToken);

        _logger.LogInformation("Extracted {Count} docs → {Blob}", docs.Count, req.OutputBlob);
        return stats;
    }

    public async Task<ChunkingResults> ChunkActivity(ChunkRequest req, FunctionContext context)
    {
        var docs             = await ReadBlobAsync<List<ExtractionDocument>>(req.InputBlob, context.CancellationToken);
        var (chunks, stats) = _chunkingService.ChunkDocuments(docs);
        await DeleteBlobAsync(req.InputBlob, context.CancellationToken);
        await WriteBlobAsync(req.OutputBlob, chunks, context.CancellationToken);

        await _artifactWriter.WriteArtifactAsync(
            $"{req.StartedAt:yyyy/MM/dd}/{req.InstanceId}/chunking.json", new { Chunks = chunks, Stats = stats }, context.CancellationToken);

        _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks → {Blob}", docs.Count, chunks.Count, req.OutputBlob);
        return stats;
    }

    public async Task<EmbedUploadingResults> EmbedAndUploadActivity(EmbedUploadRequest req, FunctionContext context)
    {
        var chunks = await ReadBlobAsync<List<ProtocolDocument>>(req.ChunksBlob, context.CancellationToken);

        var embeddingResult = await _embeddingService.EmbedDocumentsAsync(chunks, context.CancellationToken);
        var embeddedDocs    = embeddingResult.Documents.ToList();

        var uploadResult = await _uploadService.UploadDocumentsAsync(
            embeddedDocs, req.StaleDocumentIds, context.CancellationToken);

        // TODO before activating: rolling full-corpus snapshot (source-scoped, see
        // PdfIndexingFunction's own comment) needs ProtocolDocument to implement
        // ISnapshotSource first — deferred to the end-of-refactor Models pass, not done
        // here as a side effect of this scaffold.

        await DeleteBlobAsync(req.ChunksBlob, context.CancellationToken);

        return new EmbedUploadingResults(
            DocsUploaded:                  uploadResult.DocsUploaded,
            DocsFailed:                    uploadResult.DocsFailed,
            ChunksRemoved:                 uploadResult.ChunksRemoved,
            ChunksTruncated:               embeddingResult.ChunksTruncated,
            EmbeddingRetries:              embeddingResult.EmbeddingRetries,
            VectorDimErrors:               embeddingResult.VectorDimErrors,
            VectorCacheHits:               0,
            TotalEmbeddingDurationMs:      0,
            IndexDocumentCountSnapshot:    uploadResult.IndexDocumentCountSnapshot,
            IndexStorageSizeBytesSnapshot: uploadResult.IndexStorageSizeBytesSnapshot,
            RedFlags:                      uploadResult.RedFlags);
    }

    public async Task SaveIndexReportActivity(CsvIndexRunReport report, FunctionContext context)
    {
        if (!_reportWriter.IsEnabled) return;

        await _reportWriter.WriteReportAsync(
            $"indexing/{report.StartedAt:yyyy/MM/dd}/{report.StartedAt:HH-mm-ss}.json", report, context.CancellationToken);
    }

    // TODO before activating: fill in the field mapping once CSV's ExtractionResults/
    // ChunkingResults shapes are finalized — this is a placeholder matching
    // PdfIndexingFunction.BuildReport's structure, not yet verified against CSV's actual stats.
    private static CsvIndexRunReport BuildReport(
        TaskOrchestrationContext context,
        DateTimeOffset           startedAt,
        IndexRequest             input,
        ExtractionResults?       ext,
        ChunkingResults?         chunk,
        EmbedUploadingResults?   embed,
        bool                     success,
        string?                  error) => new()
        {
            InstanceId              = context.InstanceId,
            StartedAt               = startedAt,
            FinishedAt              = context.CurrentUtcDateTime,
            ForceReindex            = input.ForceReindex,
            Success                 = success,
            ErrorMessage            = error,
            DocsToProcess           = ext?.DocsToProcess          ?? 0,
            DocsSkipped             = ext?.DocsSkipped             ?? 0,
            DocsNew                 = ext?.DocsNew                 ?? 0,
            DocsUpdated             = ext?.DocsUpdated             ?? 0,
            DocsDeleted             = ext?.DocsDeleted             ?? 0,
            ChunksRemoved           = embed?.ChunksRemoved         ?? 0,
            ValidationErrors        = ext?.ValidationErrors        ?? 0,
            ValidationWarnings      = ext?.ValidationWarnings      ?? 0,
            ReconciliationProblems  = ext?.ReconciliationProblems  ?? 0,
            StaleDocCount           = ext?.StaleDocCount           ?? 0,
            MojibakeRepairedPages   = ext?.MojibakeRepairedPages   ?? 0,
            DetectedTableCount      = ext?.DetectedTableCount      ?? 0,
            DocsWithoutHeadings     = ext?.DocsWithoutHeadings     ?? 0,
            MissingTitleCount       = ext?.MissingTitleCount       ?? 0,
            MissingVersionCount     = ext?.MissingVersionCount     ?? 0,
            MissingDepartmentCount  = ext?.MissingDepartmentCount  ?? 0,
            ChunksProduced          = chunk?.ChunksProduced        ?? 0,
            DocsWithZeroChunks      = chunk?.DocsWithZeroChunks    ?? 0,
            DuplicateChunks         = chunk?.DuplicateChunks       ?? 0,
            MinChunkSizeChars       = chunk?.MinChunkSizeChars     ?? 0,
            MaxChunkSizeChars       = chunk?.MaxChunkSizeChars     ?? 0,
            AvgChunkSizeChars       = chunk?.AvgChunkSizeChars     ?? 0,
            P95ChunkSizeChars       = chunk?.P95ChunkSizeChars     ?? 0,
            BandUnder100            = chunk?.BandUnder100          ?? 0,
            Band100To500            = chunk?.Band100To500          ?? 0,
            Band500To1500           = chunk?.Band500To1500         ?? 0,
            Band1500Plus            = chunk?.Band1500Plus          ?? 0,
            CoherentChunks          = chunk?.CoherentChunks        ?? 0,
            HeadingsDetected        = chunk?.HeadingsDetected      ?? 0,
            ChunksTruncated         = embed?.ChunksTruncated       ?? 0,
            VectorCacheHits         = embed?.VectorCacheHits       ?? 0,
            EmbeddingRetries        = embed?.EmbeddingRetries      ?? 0,
            VectorDimErrors         = embed?.VectorDimErrors       ?? 0,
            TotalEmbeddingDurationMs = embed?.TotalEmbeddingDurationMs ?? 0,
            DocsUploaded                  = embed?.DocsUploaded                  ?? 0,
            DocsFailed                    = embed?.DocsFailed                    ?? 0,
            IndexDocumentCountSnapshot    = embed?.IndexDocumentCountSnapshot,
            IndexStorageSizeBytesSnapshot = embed?.IndexStorageSizeBytesSnapshot,
            Issues                  = ext?.Issues         ?? [],
            RedFlags                = [.. ext?.RedFlags ?? [], .. embed?.RedFlags ?? []],
            SpotCheckSample         = ext?.SpotCheckSample ?? [],
        };

    private async Task WriteBlobAsync<T>(string blobPath, T data, CancellationToken ct)
    {
        await _blobStore.EnsureContainerExistsAsync(_pipelineContainer, ct);
        await _blobStore.UploadJsonAsync(_pipelineContainer, blobPath, data, ct);
    }

    private Task<T> ReadBlobAsync<T>(string blobPath, CancellationToken ct) =>
        _blobStore.DownloadJsonAsync<T>(_pipelineContainer, blobPath, ct);

    private Task DeleteBlobAsync(string blobPath, CancellationToken ct) =>
        _blobStore.DeleteIfExistsAsync(_pipelineContainer, blobPath, ct);
}
*/
