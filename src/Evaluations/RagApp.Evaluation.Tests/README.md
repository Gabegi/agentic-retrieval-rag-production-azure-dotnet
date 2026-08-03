# RagApp.Evaluation.Tests

RAG quality evaluation harness — runs golden queries against a live environment and scores the answers, separate from the unit test suite.

- `RagEvaluationTests.cs` — golden-query test cases
- `RagEvaluator.cs` — scores answer accuracy against expected results
- `RefusalEvaluator.cs` — scores whether the app correctly refuses out-of-scope questions
- `EvalResultWriter.cs` — appends scoring results to `eval-results/{date}/{executionId}.jsonl` in blob storage
- `testdata/` — golden query/answer fixtures

## Running

1. Copy `.env.example` to `.env` and fill in the environment's resource names (auth is via `DefaultAzureCredential` — no keys needed, just an identity with Search/OpenAI access)
2. `dotnet test src/Evaluations/RagApp.Evaluation.Tests`

Re-run this suite after any restore/reindex — see the root [ReadMe.md](../../../ReadMe.md#post-deployment-steps) post-deployment steps.
