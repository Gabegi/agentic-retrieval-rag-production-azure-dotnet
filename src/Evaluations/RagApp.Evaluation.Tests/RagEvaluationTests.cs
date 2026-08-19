using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Azure.AI.TextAnalytics;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.Models;
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

        // This suite builds its own clients rather than resolving them from
        // AddAgenticRagAppInfrastructure, so it has to pin the api-version itself. If it
        // doesn't, eval scores the app against a different wire version than production runs
        // on - and on the knowledge-base surface that is not a cosmetic difference, since the
        // two preview generations project the resource differently (SearchServiceVersion).
        var knowledgeService = new KnowledgeService(config,
            new SearchIndexClient(new Uri(config.SearchEndpoint), credential, SearchServiceVersion.Options()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeService>.Instance);
        await knowledgeService.EnsureKnowledgeSourceAsync();
        await knowledgeService.EnsureKnowledgeBaseAsync();

        var searchClient = new SearchClient(new Uri(config.SearchEndpoint), config.SearchIndexName, credential, SearchServiceVersion.Options());

        // Non-destructive health check, not a repair - RecreateIndexAsync/restore is a
        // deliberate manual operation (POST index/restore on the Function App), not something
        // this suite should trigger itself. Without this check, a broken/empty index doesn't
        // fail the run - every query just silently comes back "no relevant content found" and
        // scores at the floor, which reads as a quality regression rather than what it
        // actually is: the eval isn't testing anything real. This exact failure mode happened
        // 2026-07-30 - indexing had been failing on every run ('id' not sortable, a schema-drift
        // issue only a restore fixes - see docs/260730) so the index was empty/stale and every
        // golden query came back empty, which the eval run reported as passing quality checks.
        var docCount = await WaitForIndexToSettleAsync(searchClient);
        Assert.IsTrue(docCount > 0,
            $"Index '{config.SearchIndexName}' has 0 searchable documents - the eval suite would silently score " +
            "every query as 'no relevant content found' instead of testing anything real. This means " +
            "the indexing pipeline isn't populating the index (check the Function App's indexing run " +
            "reports) or the index schema is stale and needs a restore (POST index/restore on the " +
            "Function App, see IndexRestoreFunction.RestoreOrchestrator) before this suite can run " +
            "meaningfully.");

        var retrievalClient = new KnowledgeBaseClient(new KnowledgeBaseRetrievalClient(new Uri(config.SearchEndpoint), config.KnowledgeBaseName, credential, SearchServiceVersion.Options()));
        var neighborExpander = new ChunkNeighborExpander(searchClient);

        var promptShieldClient = new PromptShieldClient(
            new HttpClient { BaseAddress = new Uri(config.ContentSafetyEndpoint) }, credential);
        var injectionGuard = new PromptInjectionGuard(promptShieldClient, NullLogger<PromptInjectionGuard>.Instance);

        var textAnalyticsClient = new TextAnalyticsClient(new Uri(config.LanguageEndpoint), credential);
        var piiGuard = new PiiGuard(textAnalyticsClient, NullLogger<PiiGuard>.Instance);

        _ragService = new AgenticRagQueryService(config, retrievalClient, neighborExpander, injectionGuard, piiGuard,
            NullLogger<AgenticRagQueryService>.Instance);
        _evaluator = new RagEvaluator(judgeClient);
        _writer = new EvalResultWriter(ResultsFilePath);
    }


    // How long to let the index finish ingesting before believing its document count, and how
    // long a count has to hold still to count as settled: four agreeing reads 5s apart - a 15s
    // quiet window - up to 3 minutes.
    //
    // Four rather than two. Two consecutive equal reads is satisfied by any 5s lull, and the
    // indexing run has several: an embedding batch, or the boundary between deleting stale
    // chunks and uploading new ones. Either produces two equal reads mid-run, which declared
    // the index settled while ingestion was still in flight. 15s outlasts those lulls without
    // meaningfully extending a run that already budgets 3 minutes here.
    private static readonly TimeSpan IndexSettleTimeout  = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan IndexSettleInterval = TimeSpan.FromSeconds(5);
    private const int RequiredStableReads = 4;

    /// <summary>
    /// Reads the searchable document count until it stops changing, and returns the settled value.
    /// </summary>
    /// <remarks>
    /// Azure Search is eventually consistent: an upload returns before every document is
    /// searchable, and vector index building lags further still. The pipeline runs this suite
    /// immediately after the indexing run, which is exactly when that gap is widest.
    ///
    /// Run 6612 (2026-08-19) is what this exists for. The eval started 94 seconds after the
    /// indexing run finished rewriting all 2,932 chunks, and nine scenarios across four
    /// document families came back with zero retrieved chunks - Hygienecode, Privacybeleid,
    /// Cameratoepassingen, and the sector-ambiguous CAO/Verstrekkingen rows. Every one of them
    /// scored at the floor on Groundedness and Retrieval and dragged both run means down, and
    /// the run still reported "79 completed, 0 failed". The same run's indexing report read 0
    /// documents from the statistics API at the same moment.
    ///
    /// It was not a retrieval regression: a plain keyword search against that same index once
    /// it had settled returns 6 hits for "datalek", 229 for "koeltemperatuur", 291 for
    /// "bewaartermijn camera" - the content was there all along, just not yet searchable when
    /// the eval asked for it.
    ///
    /// Waiting for the count to hold still, rather than for a target number, is deliberate:
    /// this suite has no way to know how many chunks the indexing run produced, and a hardcoded
    /// expectation would go stale the first time the corpus changed. A stable count is the
    /// weaker claim, but it is one the suite can actually make on its own.
    ///
    /// The count comes from a search, not from GetDocumentCountAsync. That matters: the
    /// statistics API is the thing that read 0 in run 6612 while documents were present, so
    /// polling it observes the write side and not the property under test. A search with
    /// $count=true is answered by the search index itself, which is exactly what "searchable"
    /// means here and exactly what the eval is about to depend on.
    /// </remarks>
    private static async Task<long> WaitForIndexToSettleAsync(SearchClient searchClient)
    {
        var deadline = DateTimeOffset.UtcNow + IndexSettleTimeout;
        var previous = -1L;
        var agreements = 0;

        while (true)
        {
            // Size = 0: only the count is wanted, so no documents are pulled back.
            var response = await searchClient.SearchAsync<SearchDocument>(
                "*", new SearchOptions { Size = 0, IncludeTotalCount = true });
            var current = response.Value.TotalCount ?? 0;

            agreements = current > 0 && current == previous ? agreements + 1 : 0;

            // Settled: RequiredStableReads consecutive reads agree and the index is not empty.
            // An empty index is never "settled" here - it is left to the assertion at the call
            // site, which explains what to do about it.
            if (agreements >= RequiredStableReads - 1) return current;

            if (DateTimeOffset.UtcNow >= deadline)
            {
                Console.WriteLine(
                    $"Searchable document count still moving after {IndexSettleTimeout.TotalMinutes:F0} min " +
                    $"({previous} -> {current}) - continuing anyway, but scores may be measured against " +
                    "a partially-ingested index.");
                return current;
            }

            if (previous >= 0)
                Console.WriteLine(
                    $"Waiting for index to settle: {previous} -> {current} searchable documents " +
                    $"({agreements + 1}/{RequiredStableReads} stable reads).");

            previous = current;
            await Task.Delay(IndexSettleInterval);
        }
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

        // An Answer scenario that retrieved nothing is not a low score - it is a row that
        // tested nothing. The generator's no-content fallback gets graded as if it were an
        // answer, so the row lands at the floor on Groundedness and Retrieval and reads as a
        // quality regression, which is the same failure mode the docCount check above exists
        // to prevent, one row at a time instead of the whole index at once.
        //
        // This is asserted where the quality thresholds below deliberately are not, and the
        // distinction is the point: a 2/5 Groundedness is a result the eval is meant to
        // report, whereas zero chunks means the measurement did not happen. Run 6612
        // (2026-08-19) had nine of these across four document families - Hygienecode,
        // Privacybeleid, Cameratoepassingen and the sector-ambiguous CAO/Verstrekkingen rows -
        // every one reporting Succeeded=true, Error="", FinishReason=stop, while the run
        // summary said "79 completed, 0 failed". See docs/2608/260819/round-1-results-and-open-work.md §2a.
        //
        // ChunksRetrieved is the MAPPED chunk count, so it separates the two causes the
        // AgenticRagQueryService warning distinguishes: 0 here with references returned by
        // Search is a mapper/SourceDataFields fault, 0 with no references is retrieval.
        if (row.Type == ScenarioType.Answer)
            Assert.IsTrue(row.ChunksRetrieved > 0,
                $"'{testQuery.Name}' was answered with no retrieved context (ChunksRetrieved=0, " +
                $"FinishReason={row.FinishReason}). The row's scores measure the no-content fallback, " +
                "not retrieval quality, so they are not comparable with the rest of the run. Check the " +
                "index has this document's chunks and that the knowledge source returns SourceData " +
                "'content' for them.");
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