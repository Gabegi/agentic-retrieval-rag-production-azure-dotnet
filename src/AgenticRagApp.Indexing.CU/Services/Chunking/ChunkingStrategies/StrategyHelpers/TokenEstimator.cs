using AgenticRagApp.Indexing.CU.Utils;

namespace AgenticRagApp.Indexing.CU.Services;

// What a piece of text costs against the ceiling.
//
// The REAL tokenizer (cl100k_base, what text-embedding-3-large actually uses), never the
// chars-per-token ratio in ChunkingHelper.EstimateTokens. That ratio is documented as capacity
// planning only, and for good reason: prose measures 3.10-3.28 chars/token while table markdown
// measures 1.88-2.79, so a ceiling enforced through a prose-derived proxy lets a table-heavy
// chunk cross it undetected by ~17%.
//
// Named Estimate rather than Count because that is what the strategies call it, and because the
// number is exact for the text passed in but only an estimate of the final embedded string -
// the prefix is priced separately and joined later.
public static class TokenEstimator
{
    public static int Estimate(string? text) => TokenCounter.Count(text);
}
