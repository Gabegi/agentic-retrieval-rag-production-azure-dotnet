using Microsoft.ML.Tokenizers;

namespace AgenticRagApp.Indexing.CU.Utils;

// The real cl100k_base tokenizer - the encoding text-embedding-3-large actually uses.
//
// Replaces the chars-per-token ratio estimate for anything that gets *stored* or *enforced*
// (action-plan.md C2). The ratios in ChunkingHelper are still correct for capacity planning,
// but two things made them wrong for this job:
//
//  - We enforce a 512-token ceiling through a character proxy. At the worst-case measured
//    ratio a table-heavy chunk crosses that ceiling undetected, because the proxy was built
//    from prose. Phase A measured the raw/cleaned ratio at 1.066-1.202 across the big four,
//    which is the same class of error stacked on top.
//  - The whole argument for storing a token count is that it cannot be reconstructed later
//    from Content.Length, since chars/token is not constant (prose ~3.1-3.3, table markdown
//    ~1.9-2.8). Storing an estimate of a number you keep *because it cannot be re-derived* is
//    self-defeating.
//
// The vocabulary ships in Microsoft.ML.Tokenizers.Data.Cl100kBase, so this never reaches the
// network at runtime - a tokenizer that downloads its vocab on first use would turn every
// cold start into a potential failure, inside a Durable activity that already retries.
public static class TokenCounter
{
    // Created once: building the tokenizer parses the full merge table, which is far too
    // expensive to repeat per chunk. TiktokenTokenizer is thread-safe for encoding, so a
    // single shared instance is safe across the parallel document loops.
    private static readonly Lazy<TiktokenTokenizer> Cl100k =
        new(() => TiktokenTokenizer.CreateForEncoding("cl100k_base"),
            LazyThreadSafetyMode.ExecutionAndPublication);

    // Exact token count of the text as the embedding model will see it.
    public static int Count(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : Cl100k.Value.CountTokens(text);
}
