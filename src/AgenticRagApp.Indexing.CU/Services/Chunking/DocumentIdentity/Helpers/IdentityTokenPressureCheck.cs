using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// What could not be identified at all, and what is close to the point where its identity text
// stops being fully embedded. Runs before anything is embedded, on the output of
// DocumentIdentityBuilder, because both answers are properties of the identity TEXT and neither
// needs a vector.
//
// Split out of DocumentIdentityResolver: the resolver owns the order of the steps, this owns the
// margin-watching.
public static class IdentityTokenPressureCheck
{
    // Warns about the documents with nothing to embed, and returns the documents whose identity
    // text is nearing the per-input token limit (also warning about each).
    public static IReadOnlyList<IdentityTokenPressure> Run(
        IReadOnlyList<DocumentIdentity> thisRun,
        IReadOnlyList<string> skippedEmptyIdentity,
        ILogger logger)
    {
        foreach (var sourceId in skippedEmptyIdentity)
            logger.LogWarning(
                "DocumentIdentityResolver: {SourceId} has no title and no headings, so there is nothing to embed - skipped",
                sourceId);

        // The identity text has no cap: every heading goes in. Measured over the real corpus
        // nothing is close to the limit (worst case 73%), so capping would force a full
        // re-embed for no benefit - but the failure past the limit is a SILENT truncation, so
        // the margin is watched rather than assumed. See DocumentIdentityBuilder's constants.
        var nearingTokenLimit = thisRun
            .Where(d => d.IdentityTokens > DocumentIdentityBuilder.TokenWarningThreshold)
            .OrderByDescending(d => d.IdentityTokens)
            .Select(d => new IdentityTokenPressure(d.SourceId, d.IdentityTokens))
            .ToList();

        foreach (var d in nearingTokenLimit)
            logger.LogWarning(
                "DocumentIdentityResolver: {SourceId}'s identity text is {Tokens} tokens, over {Percent:P0} of the " +
                "{Limit}-token per-input limit - past the limit the tail of its heading list is silently dropped " +
                "from clustering",
                d.SourceId, d.Tokens, DocumentIdentityBuilder.TokenWarningFraction,
                DocumentIdentityBuilder.InputTokenLimit);

        return nearingTokenLimit;
    }
}
