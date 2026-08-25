using Azure;
using Azure.AI.ContentUnderstanding;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Configuration;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

public sealed class ContentUnderstandingVerifier : IContentUnderstandingVerifier
{
    private readonly ContentUnderstandingClient _client;
    private readonly IndexerConfig              _config;
    private readonly ILogger<ContentUnderstandingVerifier> _logger;

    public ContentUnderstandingVerifier(
        ContentUnderstandingClient client, IndexerConfig config,
        ILogger<ContentUnderstandingVerifier> logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    public async Task<AnalyzerVerification> VerifyAnalyzerAsync(CancellationToken ct = default)
    {
        var analyzerId = _config.ContentUnderstandingAnalyzerId;

        ContentAnalyzer analyzer;
        try
        {
            analyzer = (await _client.GetAnalyzerAsync(analyzerId, ct)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Deliberately not created here. This type verifies; the provisioning endpoint
            // creates. Keeping those two apart is the whole point of the split - a verifier that
            // repaired what it found missing could never report drift, only hide it.
            _logger.LogError(
                "Content Understanding analyzer {AnalyzerId} does not exist on this account. It is created by " +
                "POST /api/content-understanding/provision, which owns both CU bootstrap steps and holds the " +
                "definition (ContentUnderstandingProvisioner.BuildAnalyzer). Do not create it from here.",
                analyzerId);

            return new AnalyzerVerification(
                analyzerId, Exists: false, Status: null, BaseAnalyzerId: null,
                Models: new Dictionary<string, string>(),
                Problems: ["Analyzer does not exist. Run POST /api/content-understanding/provision, which owns this object."]);
        }

        var problems = new List<string>();

        // Only Ready can serve an analyze call.
        if (analyzer.Status != ContentAnalyzerStatus.Ready)
            problems.Add($"Status is {analyzer.Status}, expected Ready.");

        if (!string.Equals(analyzer.BaseAnalyzerId, "prebuilt-document", StringComparison.Ordinal))
            problems.Add(
                $"BaseAnalyzerId is '{analyzer.BaseAnalyzerId}', expected 'prebuilt-document'. " +
                "prebuilt-documentSearch would bring service-side chunking and a summary that compete with the " +
                "chunking layer; prebuilt-layout cannot describe figures at all. See docs/2608/260821/cu-analyzer-choice.md.");

        var cfg = analyzer.Config;
        if (cfg is null)
        {
            problems.Add("Analyzer has no config block; every extraction flag below is therefore off.");
        }
        else
        {
            // Each of these is silent when wrong: the analyze call still succeeds, still bills for
            // every page, and simply returns the relevant collection empty. That is why they are
            // checked here rather than left to be noticed downstream as a thin document.
            if (cfg.EnableOcr != true)
                problems.Add("enableOcr is off - paragraphs and words will be empty.");
            if (cfg.EnableLayout != true)
                problems.Add("enableLayout is off - sections, tables and figures will be empty.");
            if (cfg.ShouldReturnDetails != true)
                problems.Add("returnDetails is off - paragraphs, sections, tables and figures will all be empty regardless of the flags above.");
            if (cfg.EnableFigureDescription != true)
                problems.Add("enableFigureDescription is off - this is the flag the whole CU migration exists for (608 of 617 corpus figures are dropped without it).");
            if (cfg.EnableFigureAnalysis != true)
                problems.Add("enableFigureAnalysis is off - chart and diagram content will not be extracted.");
            // Markdown, not Html. The pipeline used to ask for Html because PdfCleaner converted
            // it to GFM pipe tables itself, expanding merged cells into a full grid so no row
            // was ragged. PdfCleaner is gone, and nothing else converts - HTML <table> markup
            // left in the markdown would be embedded and searched verbatim.
            //
            // The cost is real and accepted: markdown tables cannot express rowspan/colspan, so
            // a merged header cell flattens. The typed DocumentTable.Cells still carries the
            // spans if a consumer ever needs the true grid (CuStructureMapper maps them onto
            // TableCellInfo.RowSpan/ColumnSpan).
            if (cfg.TableFormat != TableFormat.Markdown)
                problems.Add($"tableFormat is '{cfg.TableFormat}', expected Markdown - nothing converts HTML tables now that PdfCleaner is gone, so they would reach the index as raw markup.");

            // annotationFormat defaults to frontMatter, which prefixes the markdown with a
            // "---" YAML block of annotation metadata. That block is not document text but
            // would be indexed as document text, and it sits at offset 0 where the document's
            // own title belongs.
            //
            // This is now the ONLY guard. ContentUnderstandingAnalyzer used to reject any
            // response whose markdown started with one, as a backstop for an analyzer that had
            // drifted; that response validation was removed, so a drifted analyzer is caught
            // here or not at all.
            if (cfg.AnnotationFormat is not null && cfg.AnnotationFormat != AnnotationFormat.None)
                problems.Add($"annotationFormat is '{cfg.AnnotationFormat}', expected None - any other value prefixes the markdown with a YAML block that would be indexed as document content.");
        }

        if (analyzer.Models is not { Count: > 0 } || !analyzer.Models.ContainsKey("completion"))
            problems.Add("No models.completion is pinned on the analyzer, so it falls back to the account-wide default mapping.");

        if (problems.Count > 0)
            _logger.LogError(
                "Content Understanding analyzer {AnalyzerId} does not match what the pipeline needs: {Problems}. " +
                "The definition lives in ContentUnderstandingProvisioner.BuildAnalyzer() - " +
                "POST /api/content-understanding/provision repairs drift.",
                analyzerId, string.Join(" | ", problems));

        return new AnalyzerVerification(
            analyzerId,
            Exists:         true,
            Status:         analyzer.Status,
            BaseAnalyzerId: analyzer.BaseAnalyzerId,
            Models:         new Dictionary<string, string>(analyzer.Models),
            Problems:       problems);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDefaultsAsync(CancellationToken ct = default) =>
        new Dictionary<string, string>((await _client.GetDefaultsAsync(ct)).Value.ModelDeployments);

    // Keys are MODEL names, values are deployment names - "Mapping of model names to deployment
    // names", per the SDK. Not role names: ContentAnalyzer.Models is the one that takes roles.
    //
    // The prebuilt-analyzer-* entries are the role aliases CU resolves for any analyzer that does
    // not pin its own model. cap-pdf-layout does pin one (infra/content_understanding.tf), so this
    // mapping is not on the critical path for it - it is here so that a prebuilt used for a spike,
    // or a classifier added later, does not fail on first call for a reason nobody connects back
    // to a missing account default.
    public async Task<IReadOnlyDictionary<string, string>> EnsureDefaultsAsync(CancellationToken ct = default)
    {
        var completionDeployment = _config.OpenAiExtractionDeployment;
        var embeddingDeployment  = _config.OpenAiEmbeddingDeployment;

        var modelDeployments = new Dictionary<string, string>
        {
            [_config.ContentUnderstandingCompletionModel] = completionDeployment,
            [_config.OpenAiEmbeddingModelName]            = embeddingDeployment,
            ["prebuilt-analyzer-completion"]              = completionDeployment,
            ["prebuilt-analyzer-completion-mini"]         = completionDeployment,
            ["prebuilt-analyzer-embedding"]               = embeddingDeployment,
        };

        // Loud because this is account-global shared state on a resource the landing-zone team
        // owns: a merge-PATCH here is visible to every other analyzer and every other consumer of
        // this Foundry account.
        _logger.LogWarning(
            "Writing account-wide Content Understanding default model deployments (shared state): {Mapping}",
            string.Join(", ", modelDeployments.Select(kv => $"{kv.Key}->{kv.Value}")));

        await _client.UpdateDefaultsAsync(modelDeployments, ct);

        return await GetDefaultsAsync(ct);
    }
}
