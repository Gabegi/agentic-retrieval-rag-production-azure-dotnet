# RagApp.Evaluation.Tests

RAG quality evaluation harness — runs golden queries against a live environment and scores the answers, separate from the unit test suite.

- `RagEvaluationTests.cs` — golden-query test cases
- `RagEvaluator.cs` — scores answer accuracy against expected results
- `RefusalEvaluator.cs` — scores whether the app correctly refuses out-of-scope questions
- `GoldenQuestionsDatasetTests.cs` — lints `testdata/golden-questions.json` (no Azure, no model calls)
- `EvalResultWriter.cs` — appends scoring results to `eval-results/{date}/{executionId}.jsonl` in blob storage
- `testdata/` — golden query/answer fixtures

## The golden set

Every row carries a `Capability` (`Models/TestQuery.cs`): what the question demands of
**retrieval**, not how hard its answer is to phrase. `SingleLookup` rows are the control
group — one fact, one passage, nothing for query planning to improve — and the rest
(`Decomposition`, `MultiHop`, `CrossDocCompare`, `DistantSections`, `ConflictDetection`,
`Staleness`, `VocabularyGap`, `Disambiguation`, `Abstention`) each need more than one search
or more than one document. `MinSubQueries` is the hand-judged lower bound on searches needed;
the run records what the knowledge base actually did in `SubQueryCount`/`SubQueries` and
`DistinctDocumentsCited`, and `.pipelines/scripts/eval-summary.jq` reports the run per
capability plus a control-versus-multi-source comparison.

That split is what makes the agentic-retrieval question answerable from a run: quality gains
that show up only on the control rows are not retrieval gains, and a multi-source row answered
with one search never had the chance to benefit whatever it scored. See
`docs/2608/260827/golden-questions-agentic-redesign.md` for the design and for the corpus
traps each row is built around.

When adding a row, run `dotnet test --filter GoldenQuestionsDatasetTests` — it checks the
labels are consistent and that every `ExpectedSources` entry is a real corpus filename, which
`CitationMatch` silently scores as 0 otherwise.

## Running

1. Copy `.env.example` to `.env` and fill in the environment's resource names (auth is via `DefaultAzureCredential` — no keys needed, just an identity with Search/OpenAI access)
2. `dotnet test src/Evaluations/RagApp.Evaluation.Tests`

Re-run this suite after any restore/reindex.

## See also

- [Rbac.md](Rbac.md) — which identity the eval suite runs as, what roles it needs, and known gaps
- root [ReadMe.md](../../../ReadMe.md#post-deployment-steps) — post-deployment steps
