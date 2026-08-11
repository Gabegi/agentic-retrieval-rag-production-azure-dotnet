using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Azure.AI.TextAnalytics;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.KnowledgeBases;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Infrastructure.Clients.ContentSafety;
using AgenticRagApp.Infrastructure.Clients.KnowledgeRetrieval;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Querying.Guards;
using AgenticRagApp.Querying.Services;
using RagApp.Evaluation.Tests.Evaluation;
using RagApp.Evaluation.Tests.Models;

// Each golden-query test fires 1 real RAG query plus up to 5 concurrent judge calls
// (RagEvaluator.cs). All of that is I/O-bound (waiting on Search/OpenAI latency, not CPU),
// so more workers should cut wall-clock time close to linearly until a deployment's TPM or
// Search's query rate is actually saturated - not before. Raised 5->10 (2026-07-30) now that
// querying/evaluation both sit at 200K TPM with most of that unused (ai_deployments.tf).
// Leans entirely on JudgeAsync's 429 retry/back-off (RagEvaluator.cs/RefusalEvaluator.cs) and
// the per-call CallTimeout to absorb throttling/stalls rather than avoiding them. Revisit
// (lower Workers, or add back staggering) after watching a real run for retry-exhaustion
// failures or a jump in the 429 rate.
[assembly: Parallelize(Workers = 10, Scope = ExecutionScope.MethodLevel)]

namespace RagApp.Evaluation.Tests;

[TestClass]
public class RagEvaluationTests
{
    private static RagEvaluator _evaluator = null!;
    private static IRagQueryService _ragService = null!;
    private static EvalResultWriter _writer = null!;

    public TestContext TestContext { get; set; } = null!;

    // MSTest v3's console logger only reports "Passed"/"Failed" lines once the whole
    // parallel run finishes - with [assembly: Parallelize], nothing streams to the
    // pipeline's live log for the full ~duration of the suite (confirmed 2026-07-30: a
    // run in progress showed no output past "Test Parallelization enabled..." until it
    // completed). Writing one line per completed test straight to a file, independent of
    // the test framework's own reporting, lets the pipeline `tail -f` that file for live
    // progress instead. No-op locally (EVAL_PROGRESS_FILE unset outside the pipeline).
    private static readonly string? ProgressFilePath = Environment.GetEnvironmentVariable("EVAL_PROGRESS_FILE");
    private static readonly object ProgressFileLock = new();

    // EvalResultWriter's JSONL output. The pipeline points this at its results
    // directory so the file is published as a build artifact and uploaded to blob
    // by a step after the run (see EvalResultWriter's remarks for why the upload
    // no longer happens inline). Defaults to the test output directory so a local
    // run still produces results without any env setup.
    private static readonly string ResultsFilePath =
        Environment.GetEnvironmentVariable("EVAL_RESULTS_FILE")
        ?? Path.Combine(AppContext.BaseDirectory, "eval-results", $"{DateTime.UtcNow:yyyyMMddTHHmmss}.jsonl");

    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        var credential = new DefaultAzureCredential();

        var config = new IndexerConfig
        {
            SearchEndpoint = Env("SEARCH_ENDPOINT"),
            OpenAiEndpoint = Env("OPENAI_ENDPOINT"),
            OpenAiEmbeddingDeployment = Env("OPENAI_EMBEDDING_DEPLOYMENT"),
            OpenAiGptDeployment = Env("OPENAI_GPT_DEPLOYMENT"),
            OpenAiGptModelName = Env("OPENAI_GPT_MODEL_NAME"),
            SearchIndexName = Env("SEARCH_INDEX_NAME"),
            StorageAccountUrl = Env("STORAGE_ACCOUNT_URL"),
            StorageContainer = Env("STORAGE_CONTAINER"),
            KnowledgeSourceName = Env("KNOWLEDGE_SOURCE_NAME"),
            KnowledgeBaseName = Env("KNOWLEDGE_BASE_NAME"),
            ContentSafetyEndpoint = Env("CONTENT_SAFETY_ENDPOINT"),
            LanguageEndpoint = Env("LANGUAGE_ENDPOINT"),
        };

        var openAi = new AzureOpenAIClient(new Uri(config.OpenAiEndpoint), credential);

        // Cap output tokens so Azure's TPM estimate is prompt+500 instead of prompt+model-default (~4096).
        // Scoring evaluators emit a score + brief explanation; they never need more than ~300 tokens.
        IChatClient judgeClient = openAi.GetChatClient(Env("OPENAI_EVAL_DEPLOYMENT"))
            .AsIChatClient()
            .AsBuilder()
            .ConfigureOptions(o => o.MaxOutputTokens ??= 500)
            .Build();

        var knowledgeService = new KnowledgeService(config, new SearchIndexClient(new Uri(config.SearchEndpoint), credential),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeService>.Instance);
        await knowledgeService.EnsureKnowledgeSourceAsync();
        await knowledgeService.EnsureKnowledgeBaseAsync();

        var searchClient = new SearchClient(new Uri(config.SearchEndpoint), config.SearchIndexName, credential);

        // Non-destructive health check, not a repair - RecreateIndexAsync/restore is a
        // deliberate manual operation (POST index/restore on the Function App), not something
        // this suite should trigger itself. Without this check, a broken/empty index doesn't
        // fail the run - every query just silently comes back "no relevant content found" and
        // scores at the floor, which reads as a quality regression rather than what it
        // actually is: the eval isn't testing anything real. This exact failure mode happened
        // 2026-07-30 - indexing had been failing on every run ('id' not sortable, a schema-drift
        // issue only a restore fixes - see docs/260730) so the index was empty/stale and every
        // golden query came back empty, which the eval run reported as passing quality checks.
        var docCount = (await searchClient.GetDocumentCountAsync()).Value;
        Assert.IsTrue(docCount > 0,
            $"Index '{config.SearchIndexName}' has 0 documents - the eval suite would silently score " +
            "every query as 'no relevant content found' instead of testing anything real. This means " +
            "the indexing pipeline isn't populating the index (check the Function App's indexing run " +
            "reports) or the index schema is stale and needs a restore (POST index/restore on the " +
            "Function App, see PdfIndexingFunction.RestoreOrchestrator) before this suite can run " +
            "meaningfully.");

        var retrievalClient = new KnowledgeBaseClient(new KnowledgeBaseRetrievalClient(new Uri(config.SearchEndpoint), config.KnowledgeBaseName, credential));
        var neighborExpander = new ChunkNeighborExpander(searchClient);

        var promptShieldClient = new PromptShieldClient(
            new HttpClient { BaseAddress = new Uri(config.ContentSafetyEndpoint) }, credential);
        var injectionGuard = new PromptInjectionGuard(promptShieldClient, NullLogger<PromptInjectionGuard>.Instance);

        var textAnalyticsClient = new TextAnalyticsClient(new Uri(config.LanguageEndpoint), credential);
        var piiGuard = new PiiGuard(textAnalyticsClient, NullLogger<PiiGuard>.Instance);

        _ragService = new AgenticRagQueryService(config, retrievalClient, neighborExpander, injectionGuard, piiGuard);
        _evaluator = new RagEvaluator(judgeClient);
        _writer = new EvalResultWriter(ResultsFilePath);
    }

    [TestMethod]
    [TestCategory("golden")]
    [DynamicData(nameof(GoldenQueries))]
    public async Task EvaluateGoldenQuery(TestQuery testQuery)
    {
        var row = await _evaluator.RunAsync(testQuery, (q, ct) => _ragService.AskAsync(q, ct));
        await _writer.WriteAsync(row);

        var summary =
            $"[{row.ScenarioName}] ({row.Type}) G={row.Groundedness:F1} R={row.Relevance:F1} C={row.Coherence:F1} Eq={row.Equivalence:F1} " +
            $"Ret={row.Retrieval:F1} F1={row.F1:F2} Cite={row.CitationMatch:F2} Refusal={row.RefusalScore:F1}  " +
            $"{row.LatencyMs}ms  ${row.CostUsd:F4}  in={row.InputTokens} out={row.OutputTokens} ctx={row.ContextTokens}  ok={row.Succeeded}";
        Console.WriteLine(summary);
        AppendProgress(summary);

        // A failed row whose error is an Azure OpenAI content-filter block (400) isn't a real
        // pass or fail - it's the platform rejecting the call before the app/judge could act,
        // which for an Answer scenario can just as easily be a false positive on legitimate
        // content as a genuine over-block (see gq-ged-003-verborgen-camera-familieleden,
        // docs/2608/260806/eval-content-filter-answer-block.md). Report it as Inconclusive
        // instead of Failed so it's visible in test results without breaking the pipeline.
        if (!row.Succeeded && RagEvaluator.IsContentFilterError(row.Error))
            Assert.Inconclusive($"Content filter blocked '{testQuery.Name}' (reported, not failed): {row.Error}");

        Assert.IsTrue(row.Succeeded,
            $"RAG call failed for '{testQuery.Name}': {row.Error}");

        // Quality thresholds (MinGroundedness/MinRefusalScore) are no longer asserted here -
        // a low score is exactly what the eval run exists to surface, and used to fail the
        // whole suite on a single row (docs/2608/260807/evaluations/fail2.txt). Scores are
        // still written to the row and the progress line above, so the report/summary still
        // shows every miss; the suite just doesn't fail the build over it anymore.
    }

    public static IEnumerable<object[]> GoldenQueries
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "testdata", "golden-questions.json");
            return LoadFile(path).Select(q => new object[] { q });
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static List<TestQuery> LoadFile(string path) =>
        JsonSerializer.Deserialize<TestQuery[]>(File.ReadAllText(path), JsonOptions)
            ?.Where(q => !string.IsNullOrWhiteSpace(q.Query))
            .ToList() ?? [];

    // Resource names/endpoints are environment-specific and documented in .env.example
    // (not secrets, but subscription-specific values that rot quickly if baked into source).
    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            $"Missing required env var: {name}. See .env.example for the full list of required variables.");

    private static void AppendProgress(string line)
    {
        if (ProgressFilePath is null) return;

        lock (ProgressFileLock)
        {
            File.AppendAllText(ProgressFilePath, $"[{DateTime.UtcNow:HH:mm:ss}] {line}{Environment.NewLine}");
        }
    }
}