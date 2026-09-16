namespace AgenticRagApp.Indexing.CU.Services;

// The "is this vector trustworthy" predicate, shared by the two places a vector arrives from
// outside: read back from the cache (VectorCacheSplitter) and returned by the embedding API
// (EmbeddingService.EmbedBatchAsync). Lives on its own rather than on either caller so neither
// owns a check the other depends on.
public static class VectorHealth
{
    // A vector of the right length that can never match anything: every component zero, or
    // any component NaN/infinite. Both pass the dimension check and upload without error -
    // Search stores them, and cosine similarity against them is undefined or zero - so the
    // chunk sits in the index and is never retrieved. The dimension check cannot see this;
    // it has to be a second check on the values.
    public static bool IsEmptyVector(float[] vector)
    {
        var allZero = true;
        foreach (var component in vector)
        {
            if (!float.IsFinite(component)) return true;
            if (component != 0f) allZero = false;
        }
        return allZero;
    }
}
