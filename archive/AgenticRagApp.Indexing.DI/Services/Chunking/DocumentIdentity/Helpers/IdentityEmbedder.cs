using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Infrastructure.Clients.Embedding;

namespace AgenticRagApp.Indexing.DI.Services;

// The only step that calls out to the embedding model: turns the identity texts whose hash moved
// into vectors, and leaves everything else to be reused from the store.
//
// Split out of DocumentIdentityResolver, which keeps the step order; this owns the re-embed rule
// and the pairing invariant between inputs and returned vectors.
public static class IdentityEmbedder
{
    // Embeds the documents whose persisted record is missing, stale, or vectorless. Returns a
    // vector per embedded SourceId; documents not in the result keep the vector already in the
    // store.
    public static async Task<Dictionary<string, float[]>> EmbedChangedAsync(
        IEmbeddingClient embeddingClient,
        ILogger logger,
        IReadOnlyList<DocumentIdentity> thisRun,
        IReadOnlyDictionary<string, DocumentIdentityRecord> persisted,
        int expectedDimensions,
        CancellationToken ct)
    {
        var toEmbed = thisRun
            .Where(d => !persisted.TryGetValue(d.SourceId, out var rec)
                        || rec.IdentityTextHash != d.Hash
                        || rec.Vector is not { Length: > 0 })
            .ToList();

        var vectors = new Dictionary<string, float[]>();
        if (toEmbed.Count == 0)
            return vectors;

        // One call for the whole run: identity texts are a title plus a heading list, so even
        // the full 51-document corpus is a single modest batch. If the corpus grows past what
        // one request accepts, batch here the way CsvEmbeddingService does.
        var (embedded, retries) = await embeddingClient.EmbedWithRetryAsync(
            toEmbed.Select(d => d.IdentityText).ToList(), ct);

        if (retries > 0)
            logger.LogInformation(
                "DocumentIdentityResolver: {Retries} embedding retries for {Count} identity vectors", retries, toEmbed.Count);

        // Results are matched to inputs positionally, so a short or long result set means the
        // pairing is unreliable. Failing here is much cheaper than persisting document A's
        // vector under document B's SourceId and having the wrong families silently stick.
        // This throws out of ChunkDocumentsAsync and fails the whole chunking activity, which
        // is deliberate: a partial identity pass writes durable, wrong records to the store.
        if (embedded is null || embedded.Length != toEmbed.Count)
            throw new InvalidOperationException(
                $"DocumentIdentityResolver: embedding client returned {embedded?.Length ?? 0} vectors for {toEmbed.Count} inputs; " +
                "cannot map vectors to documents.");

        for (int i = 0; i < toEmbed.Count; i++)
        {
            if (embedded[i] is not { Length: > 0 })
                throw new InvalidOperationException(
                    $"DocumentIdentityResolver: embedding client returned an empty vector for {toEmbed[i].SourceId}.");

            // Same reasoning as the count check above, for the same reason it throws rather
            // than logging: the sibling embedders can log and drop a bad chunk because the next
            // run re-embeds it, but a wrong-dimension vector persisted here stays in the
            // comparison set and quietly turns its document into a family of one.
            if (embedded[i].Length != expectedDimensions)
                throw new InvalidOperationException(
                    $"DocumentIdentityResolver: embedding client returned a {embedded[i].Length}-dimension vector for " +
                    $"{toEmbed[i].SourceId}, expected {expectedDimensions}.");

            vectors[toEmbed[i].SourceId] = embedded[i];
        }

        return vectors;
    }
}
