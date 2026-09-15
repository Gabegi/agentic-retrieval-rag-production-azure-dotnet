using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.AI.OpenAI;
using Azure.AI.TextAnalytics;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
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

// This attribute does NOT parallelize the golden rows, and never did - RunAllGoldenQueriesAsync
// below is what runs them concurrently (see EvalConcurrency). MethodLevel scope schedules whole
// test METHODS across workers, and the 56 golden rows are data rows of ONE [DynamicData] method,
// so they all stay on a single worker and execute one after another regardless of Workers.
// Measured on run 2026-09-11: 42 of 56 rows in 28 minutes at ~40s each, every gap between
// consecutive progress lines equal to that row's own reported latency (effective concurrency
// ~0.9), and the pipeline step's 30-minute timeout killed the remaining 14 rows.
//
// Kept at 10 because it still applies to what it can schedule - GoldenQuestionsDatasetTests'
// methods, and the 56 assertion-only EvaluateGoldenQuery cases, all of which are instant.
[assembly: Parallelize(Workers = 10, Scope = ExecutionScope.MethodLevel)]

namespace RagApp.Evaluation.Tests;

[TestClass]
public class RagEvaluationTests
{
    private static RagEvaluator _evaluator = null!;
    private static IRagQueryService _ragService = null!;
    private static EvalResultWriter _writer = null!;

    // How many golden rows are in flight at once in RunAllGoldenQueriesAsync.
    //
    // Each one fires 1 real RAG query plus up to 5 concurrent judge calls (RagEvaluator.cs).
    // All of that is I/O-bound (waiting on Search/OpenAI latency, not CPU), so this cuts
    // wall-clock time close to linearly until a deployment's TPM is actually saturated - which
    // is exactly what 10 did.
    //
    // 10 -> 4 (2026-09-14). 10 was chosen on 2026-07-30 against an [assembly: Parallelize] that
    // was not in fact running the rows concurrently (see the attribute's comment); the fan-out
    // in RunAllGoldenQueriesAsync made it real, and the first run at a true 10 lost 17 of 56
    // rows to "Your requests to gpt-5.4 for gpt-4.1-query in westeurope have exceeded rate
    // limit" (429) raised by the knowledge base's own retrieve call. Measured on that run
    // (39 completed rows): 18.9 K tokens per row billed to the query deployment - EvalRow's
    // in+out is KnowledgeBaseActivitySummary's sum over the agentic retrieval's model calls,
    // not just the final answer - at 29.6 s per row, so ~38 K TPM per concurrent row.
    // `querying` is 200 K TPM (ai_deployments.tf): 10 rows demand ~382 K TPM, 1.9x the
    // deployment, and 4 rows demand ~153 K, 77% of it, leaving the remainder for the retries
    // underneath rather than spending the whole budget on first attempts.
    //
    // The cost is wall clock: ~56/4 x 30 s = ~7 min of scoring against the step's 30-minute
    // timeout (pipeline.yml), so there is room. Raising this again is a change to make together
    // with `querying`'s capacity - the gpt-5.4 pool has ~300 K TPM unallocated - not on its own.
    // Throttles are absorbed by RagCallAsync (the app call) and JudgeAsync (the judges) in
    // RagEvaluator.cs, but retrying cannot create quota that was never there.
    private const int EvalConcurrency = 4;

    // Every golden row, scored once by RunAllGoldenQueriesAsync and asserted one row per test
    // method below. Keyed by TestQuery.Name, which GoldenQuestionsDatasetTests.ScenarioNames_AreUnique
    // guarantees is unique.
    private static IReadOnlyDictionary<string, EvalRow> _rows = new Dictionary<string, EvalRow>();

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
        var searchIndexClient = new SearchIndexClient(new Uri(config.SearchEndpoint), credential, SearchServiceVersion.Options());

        // Schema check BEFORE the knowledge-source push, not after. The push validates its
        // field references against the LIVE index, so a stale index makes it throw first -
        // "Target Index with name '...' does not have a retrievable field with name '...'",
        // a raw 400 that names one field and no cause (index run 03437511, 2026-08-26). The
        // drift check below names every difference and says how to fix it, so it has to run
        // while it still can.
        await VerifyIndexSchemaMatchesCodeAsync(config, searchIndexClient);

        var knowledgeService = new KnowledgeService(config, searchIndexClient,
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
        // issue only a restore fixes - see docs/2607/260730) so the index was empty/stale and every
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

        await VerifyKnowledgeBaseAnswersAsync(config);

        // The suite's actual work, run here rather than inside the test method, so it can run
        // EvalConcurrency rows at a time instead of one - see RunAllGoldenQueriesAsync.
        _rows = await RunAllGoldenQueriesAsync(LoadFile(GoldenQueriesPath));
    }

    /// <summary>
    /// Scores every golden row against the app and the judges, <see cref="EvalConcurrency"/> at a
    /// time, and returns the rows keyed by scenario name.
    /// </summary>
    /// <remarks>
    /// This exists because MSTest will not do it. The suite is one [DynamicData] test method, and
    /// MSTest's MethodLevel parallelism schedules methods, not the data rows of a method - so
    /// [assembly: Parallelize(Workers = 10)] left the rows running strictly one after another
    /// (see the comment on that attribute for the measurement). Fanning out here is independent
    /// of the test framework's scheduler, so the concurrency is the one thing it claims to be.
    ///
    /// Rows are written to the results file and the progress file as each one lands, not in a
    /// batch at the end, so a run that is cancelled or times out still leaves every row it
    /// finished - the pipeline's summary/upload steps run on succeededOrFailed() precisely to
    /// salvage those.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, EvalRow>> RunAllGoldenQueriesAsync(
        IReadOnlyList<TestQuery> queries)
    {
        Console.WriteLine($"Scoring {queries.Count} golden rows, {EvalConcurrency} at a time...");

        var rows = new ConcurrentDictionary<string, EvalRow>();
        var gate = new SemaphoreSlim(EvalConcurrency, EvalConcurrency);
        var completed = 0;

        await Task.WhenAll(queries.Select(async query =>
        {
            await gate.WaitAsync();
            try
            {
                // RunAsync never throws - every failure comes back as a row with Succeeded=false
                // and Error set (its catch-all). That matters more here than it did in the test
                // method: an exception escaping this loop would fail ClassInitialize and take
                // every row's result with it, not just this one.
                var row = await _evaluator.RunAsync(query, (q, ct) => _ragService.AskAsync(q, ct));
                rows[query.Name] = row;

                try
                {
                    await _writer.WriteAsync(row);
                }
                catch (Exception ex)
                {
                    // Persistence must never cost a scored row. A run on 2026-08-06 reported 79
                    // quality regressions when the only thing that had failed was the write
                    // (EvalResultWriter's remarks); the write is a local append now, but it
                    // happens inside ClassInitialize, where throwing would fail the whole class.
                    Console.WriteLine(
                        $"[eval] WARNING: could not persist '{query.Name}' to the results file: {ex.Message}");
                }

                // Progress lines interleave now that rows overlap, so each carries its own
                // position in the run - the pipeline tails this file as the suite's live log.
                var summary = $"({Interlocked.Increment(ref completed)}/{queries.Count}) {Describe(row)}";
                Console.WriteLine(summary);
                AppendProgress(summary);
            }
            finally
            {
                gate.Release();
            }
        }));

        return rows;
    }

    private static string Describe(EvalRow row) =>
        $"[{row.ScenarioName}] ({row.Type}) G={row.Groundedness:F1} R={row.Relevance:F1} C={row.Coherence:F1} Eq={row.Equivalence:F1} " +
        $"Ret={row.Retrieval:F1} F1={row.F1:F2} Cite={row.CitationMatch:F2} MRR={row.ReciprocalRank:F2} " +
        // R@5 < R@50 on a row means the document was retrieved but ranked out of reach; equal and
        // low means it was never retrieved. k is how many references there were to rank at all.
        $"R@5={row.RecallAt5:F2} R@50={row.RecallAt50:F2} k={row.ReferencesRetrieved} Refusal={row.RefusalScore:F1}  " +
        // The agentic signal, on the live line rather than only in the JSONL: cap/subq
        // says whether this question was even given the chance to benefit from planning
        // (subq < need means it was not), and docs says how many documents the answer
        // ended up standing on.
        $"cap={row.Capability} subq={row.SubQueryCount}/{row.MinSubQueries} docs={row.DistinctDocumentsCited}  " +
        $"{row.LatencyMs}ms  ${row.CostUsd:F4}  in={row.InputTokens} out={row.OutputTokens} ctx={row.ContextTokens}  ok={row.Succeeded}";

    // The knowledge source and base are pushed from THIS build on every run (the two
    // CreateOrUpdate calls above), so the suite always scores the definitions the code
    // declares - instructions, searchFields, sourceDataFields included. The index is the
    // opposite: EnsureIndexAsync is get-or-create, so a deployed schema change reaches an
    // existing index only through a recreate, and until then the suite would be scoring the
    // app against a shape the code no longer declares, with nothing anywhere saying so.
    // Every symptom of that looks like a quality regression: fields the query path reads come
    // back null, citations lose their provenance, retrieval degrades. Comparing the two
    // definitions is the only thing that names the real cause.
    //
    // Fails the whole class rather than warning - a run scored against a stale schema is
    // worse than no run, because its numbers get published and compared against previous ones.
    private static async Task VerifyIndexSchemaMatchesCodeAsync(IndexerConfig config, SearchIndexClient client)
    {
        var expected = new IndexService(config, client,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IndexService>.Instance).BuildDefinition();

        SearchIndex live;
        try
        {
            live = await client.GetIndexAsync(config.SearchIndexName);
        }
        catch (RequestFailedException ex) when (ex.Status is 403 or 401)
        {
            // Reading an index DEFINITION is a control-plane call, which "Search Index Data
            // Reader" - the only Search role Terraform grants this identity (Rbac.md) - does
            // not cover. It works in the pipeline because that service principal is far more
            // privileged; a developer running the suite locally may well get a 403 here. That
            // is not a reason to fail their run over a check the pipeline still performs.
            Console.WriteLine(
                $"[eval] WARNING: no permission to read the definition of '{config.SearchIndexName}' " +
                $"({ex.Status}) - skipping the schema-drift check. The pipeline run performs it.");
            return;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            Assert.Fail(
                $"Index '{config.SearchIndexName}' does not exist. Nothing has created it: run " +
                "POST /api/index?force=true&recreate=true on the Function App, or wait for the " +
                "17:00 scheduled rebuild.");
            return;
        }

        var drift = IndexSchemaComparer.Compare(expected, live);
        Assert.IsTrue(drift.Count == 0,
            $"Index '{config.SearchIndexName}' does not match the schema this build declares, so the " +
            "suite would score the app against an index shape the code no longer describes:" +
            Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", drift) +
            Environment.NewLine +
            "A schema change only reaches an existing index through a recreate (IndexService is " +
            "get-or-create by design). Run POST /api/index?force=true&recreate=true on the Function " +
            "App and re-run this suite once it completes, or wait for the 17:00 scheduled rebuild.");
    }

    // Closes the window the pipeline's own knowledge-base gate cannot see. That gate runs
    // BEFORE this suite starts, so it checks the PREVIOUS definition - the one ClassInit then
    // replaces with the deployed build's. A deploy that breaks the knowledge source therefore
    // sails past it and lands as a scored collapse instead of a named failure, which is
    // precisely how 2026-08-11 read (every reference came back with sourceData null and the
    // app answered 31 of 32 answerable questions with the buiten-scope fallback).
    //
    // One question through the production path, after the push, is what makes that visible.
    // Deliberately the same question the pipeline gate uses, for the same reason it picked it:
    // the corpus answers it in every environment.
    private static async Task VerifyKnowledgeBaseAnswersAsync(IndexerConfig config)
    {
        const string question = "Hoe moet ik mij ziekmelden?";

        var result = await _ragService.AskAsync(question);

        Assert.IsTrue(result.ChunksRetrieved > 0,
            $"Knowledge base '{config.KnowledgeBaseName}' returned no chunks for a question the corpus " +
            $"answers (\"{question}\") right after this build's knowledge source and base were pushed. " +
            "Retrieval side: check the knowledge source's searchFields and that the index is populated.");

        Assert.IsTrue(result.Citations.Count > 0,
            $"Knowledge base '{config.KnowledgeBaseName}' retrieved {result.ChunksRetrieved} chunk(s) for " +
            $"\"{question}\" but produced no citations, so every scored answer would be the buiten-scope " +
            "fallback rather than a real answer. This is the 2026-08-11 failure: check that the knowledge " +
            "source this build just pushed still lists 'content' in sourceDataFields, and that " +
            "AgenticRagQueryService still requests reference source data.");
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
    // One test case per golden row, exactly as before - what changed is that the row was
    // already scored by RunAllGoldenQueriesAsync (concurrently, with the other 55) and this
    // only reads the verdict. The .trx therefore still carries a pass/fail/inconclusive per
    // scenario; the per-test duration reported by the runner no longer means anything, since
    // the call it used to time happened in ClassInitialize. The real latency is on the row
    // (LatencyMs) and on the progress line.
    [TestMethod]
    [TestCategory("golden")]
    [DynamicData(nameof(GoldenQueries))]
    public void EvaluateGoldenQuery(TestQuery testQuery)
    {
        if (!_rows.TryGetValue(testQuery.Name, out var row))
        {
            // Reachable only if the run was cut short before this row was scored (a cancelled
            // or timed-out step), or if two rows share a Name - which
            // GoldenQuestionsDatasetTests.ScenarioNames_AreUnique fails on separately.
            Assert.Fail(
                $"No eval result was produced for '{testQuery.Name}'. The suite scores every row in " +
                "ClassInitialize, so this means the run was cut short before reaching this one (check " +
                "the step's timeout and the last progress line) rather than that the query failed.");
            return;
        }

        Console.WriteLine(Describe(row));

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

    private static string GoldenQueriesPath =>
        Path.Combine(AppContext.BaseDirectory, "testdata", "golden-questions.json");

    // Feeds [DynamicData] - i.e. which test cases exist. ClassInit reads the same file through
    // LoadFile(GoldenQueriesPath) to decide which rows to score, so the two can't disagree.
    public static IEnumerable<object[]> GoldenQueries =>
        LoadFile(GoldenQueriesPath).Select(q => new object[] { q });

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