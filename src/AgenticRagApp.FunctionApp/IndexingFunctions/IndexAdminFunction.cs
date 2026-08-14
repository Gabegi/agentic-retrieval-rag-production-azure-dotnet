using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Observability;

namespace AgenticRagApp.Functions;

// Manually-triggered index and knowledge-base maintenance - operator tooling, not part of
// any orchestration. The indexing pipeline lives in PdfIndexingFunction; snapshot-based
// recovery in IndexRestoreFunction.
public class IndexAdminFunction
{
    private readonly IIndexRebuildService _indexRebuildService;
    private readonly IKnowledgeService    _knowledgeService;
    private readonly IndexerConfig        _config;
    private readonly ILogger<IndexAdminFunction> _logger;

    public IndexAdminFunction(
        IIndexRebuildService indexRebuildService,
        IKnowledgeService    knowledgeService,
        IndexerConfig        config,
        ILogger<IndexAdminFunction> logger)
    {
        _indexRebuildService = indexRebuildService;
        _knowledgeService    = knowledgeService;
        _config              = config;
        _logger              = logger;
    }

    // Drops the index and rebuilds it EMPTY on the current schema, then rebuilds the
    // knowledge source and base on top of it. Nothing is repopulated - run StartIndexing
    // afterwards.
    //
    // This exists because EnsureIndexAsync is deliberately get-or-create: it never updates an
    // existing index, so a schema change cannot reach a live index through the normal indexing
    // run at all. The only other path that recreates is RestoreOrchestrator, and that
    // immediately repopulates from the rolling snapshot - useless after a field rename, since
    // the snapshot is in the previous shape.
    //
    // Destructive and irreversible: every indexed chunk is gone until a reindex completes, and
    // if the snapshot predates the current schema there is no restore path either. The caller
    // must therefore name the index in ?confirm=, which is checked against the configured name
    // - a function key proves you may call this, not that you meant to call it on THIS index.
    [Function("FullIndexRecreation")]
    public async Task<HttpResponseData> RunFullIndexRecreation(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "index/full-recreation")] HttpRequestData req,
        FunctionContext context)
    {
        var confirm = req.Query["confirm"];

        if (!string.Equals(confirm, _config.SearchIndexName, StringComparison.Ordinal))
        {
            _logger.LogWarning("FullIndexRecreation refused - confirm='{Confirm}' does not name the configured index", confirm);

            var refused = req.CreateResponse(HttpStatusCode.BadRequest);
            await refused.WriteStringAsync(
                $"Refused. This DELETES every indexed chunk in '{_config.SearchIndexName}' and rebuilds it empty. " +
                $"Re-send with ?confirm={_config.SearchIndexName} if that is what you want, " +
                "then run POST /api/index?force=true to repopulate it.");
            return refused;
        }

        _logger.LogWarning(
            "FullIndexRecreation triggered - index '{Name}' will be dropped and rebuilt empty on the current schema",
            _config.SearchIndexName);

        try
        {
            // Exactly what RecreateIndexActivity does - the difference between the two paths
            // is only what happens afterwards (this one repopulates nothing).
            await _indexRebuildService.RecreateEmptyAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "full-index-recreation"));
            _logger.LogError(ex, "FullIndexRecreation failed");

            var failed = req.CreateResponse(HttpStatusCode.InternalServerError);
            await failed.WriteStringAsync($"Recreate failed: {ex.Message}");
            return failed;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync(
            $"Index '{_config.SearchIndexName}' recreated empty on the current schema, knowledge source and base rebuilt. " +
            "It holds no documents until POST /api/index?force=true completes.");
        return response;
    }

    [Function("SetupKnowledgeBase")]
    public async Task<HttpResponseData> RunSetupKnowledgeBase(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "setup-knowledge-base")] HttpRequestData req,
        FunctionContext context)
    {
        _logger.LogInformation("SetupKnowledgeBase triggered");
        await _knowledgeService.EnsureKnowledgeSourceAsync(context.CancellationToken);
        await _knowledgeService.EnsureKnowledgeBaseAsync(context.CancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync("Knowledge source and knowledge base created or updated");
        return response;
    }
}
