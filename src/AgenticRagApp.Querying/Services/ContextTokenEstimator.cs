namespace AgenticRagApp.Querying.Services;

// Estimates the token count of the context returned to the model for one query — the
// cost axis first-split-design.md §5 asks to measure directly, since it's what the
// parent-grain (whole-document vs. section) decision actually spends, multiplied by k
// and again by subqueries. Deliberately a char-ratio estimate, not a real tokenizer:
// the corpus's measured prose ratio (ChunkingHelper.cs's ProseCharsPerToken, from
// tokenizer-redo-findings.md) is already validated on this content, and adding a
// tokenizer dependency for one metric isn't worth the extra lane. Shared between the
// production query path (AgenticRagQueryService) and the eval harness (RagEvaluator)
// so both report the same number for the same context.
public static class ContextTokenEstimator
{
    private const double ProseCharsPerToken = 3.1;

    public static long Estimate(string retrievedContext) =>
        retrievedContext.Length == 0 ? 0 : (long)Math.Ceiling(retrievedContext.Length / ProseCharsPerToken);
}
