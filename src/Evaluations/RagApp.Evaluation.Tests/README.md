# RagApp.Evaluation.Tests

RAG quality evaluation harness — runs golden queries against a live environment and scores the answers, separate from the unit test suite.

- `RagEvaluationTests.cs` — golden-query test cases
- `RagEvaluator.cs` — scores answer accuracy against expected results
- `RefusalEvaluator.cs` — scores whether the app correctly refuses out-of-scope questions
- `RetrievalRankMetrics.cs` — the deterministic retrieval metrics as pure functions (no judge, no knowledge base)
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

## Retrieval metrics

`RetrievalRankMetrics` scores retrieval deterministically — no judge, so these move only when
retrieval moves. All four are **document-level**: the golden set labels `ExpectedSources`
(PDF filenames), not chunks, so a retrieved reference counts as relevant when its *document*
is one of the expected ones. Scoring at chunk grain needs a span label set that does not exist
yet (`docs/2608/260817/chunking-evaluations.md`).

| Column | What it answers |
|---|---|
| `CitationMatch` | Of the expected documents, how many the answer actually **cited** — measured after synthesis |
| `ReciprocalRank` | 1 / rank of the first expected document in the **retrieved** ranking; averaged over rows this is MRR |
| `RecallAt5` | Expected documents found in the first 5 retrieved references |
| `RecallAt50` | The same at k=50 — always ≥ `RecallAt5` |

The pair is the point, and `eval-summary.jq` splits a run on it under *Retrieval reach — cut
versus rank*: **R@50 > R@5** is ranking loss (the document was retrieved but sat below the
window synthesis reads — a reranker problem), while **R@50 < 1.0** is reach loss (it never came
back at all — a chunking/embedding/indexing problem no reranker fixes). The two have opposite
fixes and neither headline number can tell them apart alone.

One caveat that is expected rather than a bug: the production path sets no top-k — the
knowledge base decides how many references to return — so on a run whose mean
`ReferencesRetrieved` is under 50, `RecallAt50` is containment over the whole returned set
rather than a rank cutoff. The summary prints that mean next to it for exactly this reason.

`-1` means *not scorable* (a Refusal row, or a row with no ranking) and is excluded from every
mean; `0` is a real miss and counts.

## Running

1. Copy `.env.example` to `.env` and fill in the environment's resource names (auth is via `DefaultAzureCredential` — no keys needed, just an identity with Search/OpenAI access)
2. `dotnet test src/Evaluations/RagApp.Evaluation.Tests`

Re-run this suite after any restore/reindex.

## See also

- [Rbac.md](Rbac.md) — which identity the eval suite runs as, what roles it needs, and known gaps
- root [ReadMe.md](../../../ReadMe.md#after-a-deployment) — post-deployment steps
