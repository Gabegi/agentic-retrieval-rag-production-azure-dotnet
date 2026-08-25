using Azure;
using Azure.AI.ContentUnderstanding;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Configuration;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

// Creates the two pieces of per-resource Content Understanding state the pipeline needs, in the
// one order that works, and reports back what the account actually holds afterwards.
//
// THE DEFINITION OF cap-pdf-layout LIVES HERE. It used to live only in prose (three docs, two of
// them already disagreeing about tableFormat) with the real analyzer created by hand, which meant
// nothing in the repo could be pointed at and called authoritative. Every flag below is one the
// verifier next door asserts - the two must be changed together, and ProvisionedAnalyzerMatches
// the assertions is what the tests pin.
public sealed class ContentUnderstandingProvisioner : IContentUnderstandingProvisioner
{
    private readonly ContentUnderstandingClient _client;
    private readonly IContentUnderstandingVerifier _verifier;
    private readonly IndexerConfig _config;
    private readonly ILogger<ContentUnderstandingProvisioner> _logger;

    public ContentUnderstandingProvisioner(
        ContentUnderstandingClient               client,
        IContentUnderstandingVerifier            verifier,
        IndexerConfig                            config,
        ILogger<ContentUnderstandingProvisioner> logger)
    {
        _client   = client;
        _verifier = verifier;
        _config   = config;
        _logger   = logger;
    }

    public async Task<ProvisioningResult> ProvisionAsync(
        bool replaceAnalyzer = false, CancellationToken ct = default)
    {
        var analyzerId = _config.ContentUnderstandingAnalyzerId;

        // ── STEP 1: account defaults, BEFORE the analyzer ────────────────────────────────
        //
        // Order is not stylistic. Analyzer creation validates the deployments its Models block
        // references against this mapping, so an analyzer-first run fails as "Model deployment
        // not found" - which reads like a missing OpenAI deployment and sends whoever is
        // debugging it to ai_deployments.tf, where they will find the deployment present and
        // correct. Sequencing it here is the whole reason both steps sit behind one call.
        var defaults = await _verifier.EnsureDefaultsAsync(ct);

        // ── STEP 2: the analyzer ─────────────────────────────────────────────────────────
        //
        // One GET answers both questions this needs - does it exist, and is what exists correct -
        // so the decision below reads the verifier's report rather than fetching the analyzer
        // separately.
        var before = await _verifier.VerifyAnalyzerAsync(ct);

        var action = await DecideAndApplyAsync(analyzerId, before, replaceAnalyzer, ct);

        // ── STEP 3: read back the EFFECTIVE state ────────────────────────────────────────
        //
        // Verify rather than echo the request. A PUT that returned 200 says the service accepted
        // the body, not that the analyzer built from it satisfies the pipeline's assertions -
        // and after LeftAsIs there was no request to echo in the first place.
        var verification = await _verifier.VerifyAnalyzerAsync(ct);

        var result = new ProvisioningResult(
            analyzerId, action, verification.Status, defaults, verification);

        if (result.Ok)
            _logger.LogInformation(
                "Content Understanding provisioning complete for '{AnalyzerId}' ({Action}); analyzer is Ready and verifies clean.",
                analyzerId, action);
        else
            _logger.LogError(
                "Content Understanding provisioning finished for '{AnalyzerId}' ({Action}) but the analyzer is not usable: status {Status}, problems: {Problems}",
                analyzerId, action, verification.Status, string.Join(" | ", verification.Problems));

        return result;
    }

    // Idempotence lives here. An analyzer that exists and verifies clean is left alone, so this
    // endpoint is safe to run at the end of every deploy - which is the only way ownership in C#
    // beats "someone remembers to run it once".
    //
    // A DRIFTED analyzer is replaced without being asked, because the alternative is a deploy
    // that reports success against an analyzer known to be wrong. Replacing is safe in a way it
    // would not be for most resources: an analyzer holds no data, and re-creating it costs one
    // LRO rather than a re-analysis of the corpus.
    private async Task<AnalyzerProvisioningAction> DecideAndApplyAsync(
        string analyzerId, AnalyzerVerification before, bool replaceAnalyzer, CancellationToken ct)
    {
        if (!before.Exists)
        {
            _logger.LogInformation("Analyzer '{AnalyzerId}' does not exist; creating.", analyzerId);
            await CreateAsync(analyzerId, allowReplace: false, ct);
            return AnalyzerProvisioningAction.Created;
        }

        if (replaceAnalyzer)
        {
            _logger.LogWarning(
                "Analyzer '{AnalyzerId}' exists and is being replaced because replace=true was requested.",
                analyzerId);
            await CreateAsync(analyzerId, allowReplace: true, ct);
            return AnalyzerProvisioningAction.Replaced;
        }

        if (before.Ok)
        {
            _logger.LogInformation(
                "Analyzer '{AnalyzerId}' already exists and verifies clean; leaving it as is.", analyzerId);
            return AnalyzerProvisioningAction.LeftAsIs;
        }

        _logger.LogWarning(
            "Analyzer '{AnalyzerId}' exists but has drifted ({Problems}); replacing it with the definition in ContentUnderstandingProvisioner.",
            analyzerId, string.Join(" | ", before.Problems));

        await CreateAsync(analyzerId, allowReplace: true, ct);
        return AnalyzerProvisioningAction.Replaced;
    }

    // WaitUntil.Completed, not Started. Analyzer creation is a long-running operation: a
    // fire-and-forget PUT reports success for an analyzer still in Creating, and the first
    // analyze call then races it and fails on an analyzer that is about to be perfectly fine.
    private async Task CreateAsync(string analyzerId, bool allowReplace, CancellationToken ct)
    {
        await _client.CreateAnalyzerAsync(
            WaitUntil.Completed, analyzerId, BuildAnalyzer(), allowReplace, ct);
    }

    // ── THE DEFINITION ───────────────────────────────────────────────────────────────────
    //
    // Every value here is asserted by ContentUnderstandingVerifier.VerifyAnalyzerAsync. Change
    // one without the other and provisioning will happily create an analyzer that its own
    // verification step then rejects - which the round-trip test exists to catch.
    // internal (not private): unit tested directly, without a client.
    internal ContentAnalyzer BuildAnalyzer() => new()
    {
        // Not prebuilt-layout (cannot describe figures, and cannot be a custom-analyzer base -
        // baseAnalyzerId takes only prebuilt-document/-image/-audio/-video) and not
        // prebuilt-documentSearch (service-side chunking and a summary that compete with the
        // whole chunking layer). See docs/2608/260821/cu-analyzer-choice.md.
        BaseAnalyzerId = "prebuilt-document",

        Description =
            "Contoso PDF extraction: DI-shaped layout (paragraphs, sections, tables, pages, spans) " +
            "plus figure descriptions, minus chunking and summarization. Owned by " +
            "ContentUnderstandingProvisioner - do not edit in the portal.",

        // geography keeps processing inside the EU data boundary, matching the westeurope
        // deployment. Global would let the service route the analysis elsewhere.
        ProcessingLocation = ProcessingLocation.Geography,

        Config = new ContentAnalyzerConfig
        {
            // Each of the four below is silent when off: the analyze call still succeeds, still
            // bills for every page, and returns the relevant collection empty.
            EnableOcr    = true,
            EnableLayout = true,

            // The master switch. Off, the three above produce nothing regardless of their own
            // values - paragraphs, sections, tables and figures all come back empty.
            ShouldReturnDetails = true,

            // The flag the entire CU migration exists for: 608 of the corpus's 617 figures are
            // dropped without it.
            EnableFigureDescription = true,
            EnableFigureAnalysis    = true,

            // Markdown, not Html. The pipeline asked for Html while PdfCleaner converted it to
            // GFM pipe tables itself; PdfCleaner is gone and nothing converts now, so HTML
            // <table> markup would be embedded and searched verbatim. The accepted cost is that
            // markdown tables cannot express rowspan/colspan - the typed DocumentTable.Cells
            // still carries the spans for any consumer that needs the true grid.
            TableFormat = TableFormat.Markdown,

            // Defaults to FrontMatter, which prefixes the markdown with a "---" YAML block of
            // annotation metadata. Not document text, but it would be indexed as document text,
            // and it would sit at offset 0 where the document's own title belongs.
            AnnotationFormat = AnnotationFormat.None,
        },

        // Keys are ROLE names, values are MODEL names. The account defaults written in step 1 are
        // the other direction - MODEL name -> deployment name. Putting a DEPLOYMENT name here is
        // the trap worth repeating: it is accepted at provisioning time and fails at analyze
        // time. See IndexerConfig.ContentUnderstandingCompletionModel.
        Models = { ["completion"] = _config.ContentUnderstandingCompletionModel },
    };
}
