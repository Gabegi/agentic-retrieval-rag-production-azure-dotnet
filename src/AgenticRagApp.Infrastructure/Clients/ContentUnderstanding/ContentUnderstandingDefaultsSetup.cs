using Azure;
using Azure.AI.ContentUnderstanding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Configuration;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

// One-time setup, at host startup: make sure the account-wide Content Understanding default
// model->deployment mapping matches what this app deploys. The SDK's Sample00 documents this
// mapping as required setup per Foundry resource before any prebuilt analyzer works;
// prebuilt-documentSearch (ContentAnalysisClient) needs gpt-4.1-mini and text-embedding-3-large.
//
// Read first, write only when a required entry is missing or wrong - the mapping is
// account-global state shared with every other consumer of the account, so an unconditional
// write on every start would make this host a recurring writer of someone else's config.
// UpdateDefaults is merge-patch semantics on the service side: entries for models this app
// does not use are left untouched either way.
//
// Runs under the Function App's managed identity, which already holds "Cognitive Services User"
// on the account (infra/content_understanding.tf) - no human role assignment or manual PATCH.
//
// Failure here is logged, not thrown: this host also serves the query side, which must not be
// taken down by a startup blip on an indexing prerequisite. If the mapping really is broken,
// the first analyze call fails and ContentAnalysisClient surfaces the service's own error
// detail in the run report.
public sealed class ContentUnderstandingDefaultsSetup : IHostedService
{
    // The model prebuilt-documentSearch's generative work resolves against. The embedding model
    // name comes from config because the app embeds with it elsewhere; this one exists only for
    // CU. Same constant as ContentAnalysisClient conceptually, but that client no longer carries
    // any mapping - this class is the only place the app states it.
    private const string MiniModelName = "gpt-4.1-mini";

    private readonly ContentUnderstandingClient                 _client;
    private readonly IndexerConfig                              _config;
    private readonly ContentUnderstandingDefaultsState          _state;
    private readonly ILogger<ContentUnderstandingDefaultsSetup> _logger;

    public ContentUnderstandingDefaultsSetup(
        ContentUnderstandingClient client,
        IndexerConfig config,
        ContentUnderstandingDefaultsState state,
        ILogger<ContentUnderstandingDefaultsSetup> logger)
    {
        _client = client;
        _config = config;
        _state  = state;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Keys are MODEL names, values are DEPLOYMENT names (infra/ai_deployments.tf).
        var required = new Dictionary<string, string>
        {
            [MiniModelName]                    = _config.OpenAiMiniDeployment,
            [_config.OpenAiEmbeddingModelName] = _config.OpenAiEmbeddingDeployment,
        };

        try
        {
            IDictionary<string, string>? current = null;
            try
            {
                current = (await _client.GetDefaultsAsync(cancellationToken)).Value.ModelDeployments;
            }
            catch (RequestFailedException ex)
            {
                // A never-configured resource does not answer with an empty mapping - observed
                // live (2026-08-25, run 2a7c7387): the GET fails 400 InvalidRequest with
                // innererror "DefaultsNotSet". An earlier guard here expected 404 and let the
                // 400 escape to the outer catch, which skipped the write - five identical runs
                // stayed broken on that. So: treat ANY read failure as "state unknown" and
                // attempt the write below regardless. It either configures the account, or it
                // fails with the error actually worth reporting - a read failure must never
                // again block the write that would fix it.
                _logger.LogInformation(ex,
                    "Content Understanding defaults read failed ({Status}); attempting to write them.",
                    ex.Status);
            }

            var wrong = required
                .Where(kv => current is null || !current.TryGetValue(kv.Key, out var deployment) || deployment != kv.Value)
                .Select(kv => kv.Key)
                .ToList();

            var mappings = string.Join(", ", required.Select(kv => $"{kv.Key} -> {kv.Value}"));

            if (wrong.Count == 0)
            {
                _state.Summary = $"verified at {DateTimeOffset.UtcNow:HH:mm:ss}Z: already correct ({mappings})";
                _state.Ok      = true;
                _logger.LogInformation(
                    "Content Understanding default model mappings already correct: {Mappings}.", mappings);
                return;
            }

            await _client.UpdateDefaultsAsync(required, cancellationToken);

            _state.Summary =
                $"updated at {DateTimeOffset.UtcNow:HH:mm:ss}Z ({string.Join(", ", wrong)} were missing/wrong): {mappings}";
            _state.Ok = true;
            _logger.LogInformation(
                "Content Understanding default model mappings updated ({Wrong} missing/wrong): {Mappings}.",
                string.Join(", ", wrong), mappings);
        }
        catch (RequestFailedException ex)
        {
            // 400 rather than 200: the cap exists for the Durable row limit, but run 2a7c7387's
            // truncated summary cut off the decisive innererror code - keep room for it.
            var message = ex.Message.Length <= 400 ? ex.Message : ex.Message[..400];
            _state.Summary = $"FAILED at {DateTimeOffset.UtcNow:HH:mm:ss}Z: {ex.Status} {ex.ErrorCode}: {message}";
            _logger.LogError(ex,
                "Could not verify/update the Content Understanding default model mappings ({Status}). " +
                "Analyze calls will fail with the service's own error until this is resolved.",
                ex.Status);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
