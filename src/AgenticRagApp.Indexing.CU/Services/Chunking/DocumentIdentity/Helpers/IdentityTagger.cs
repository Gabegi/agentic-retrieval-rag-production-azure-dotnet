using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Infrastructure.Clients.DomainClassification;

namespace AgenticRagApp.Indexing.CU.Services;

// Fills DocumentIdentity.DomainTag - the only step that decides what a document's population
// tag IS. Build emits every identity untagged (the tag is deliberately outside the identity
// hash; see DocumentIdentityBuilder), and this step resolves it from two sources, cheapest
// first:
//
//   1. The identity store: a persisted tag classified at the document's CURRENT identity hash
//      (TaggedAtHash == Hash) is reused verbatim. On a steady corpus this makes the LLM cost
//      of tagging zero - and, just as deliberately, makes it impossible for the model to flip
//      a tag on a document that has not changed.
//   2. IDomainClassifier: one batched call for everything else - new documents, changed
//      documents, and documents whose stored record predates tagging (TaggedAtHash null).
//
// A document the classifier fails to answer for keeps its stale persisted tag (a plausible
// tag beats none), stamped with the OLD TaggedAtHash so the next run retries the
// classification. A document with no persisted record stays untagged this run, which the
// Chunking.UntaggedFamilyMemberIds flag then surfaces when it matters (multi-member family).
public static class IdentityTagger
{
    // Title + the first headings carry the population signal; the full heading list of a
    // 134-page document is token spend without classification value.
    private const int TextSampleMaxChars = 1_200;

    public static async Task<IReadOnlyList<DocumentIdentity>> ApplyAsync(
        IDomainClassifier classifier,
        ILogger logger,
        IReadOnlyList<DocumentIdentity> thisRun,
        IReadOnlyDictionary<string, DocumentIdentityRecord> persisted,
        CancellationToken ct)
    {
        var reused = new Dictionary<string, DocumentIdentity>(StringComparer.Ordinal);
        var toClassify = new List<DocumentToClassify>();

        foreach (var d in thisRun)
        {
            if (persisted.TryGetValue(d.SourceId, out var rec) && rec.TaggedAtHash == d.Hash)
            {
                reused[d.SourceId] = d with { DomainTag = rec.DomainTag, TaggedAtHash = rec.TaggedAtHash };
                continue;
            }

            toClassify.Add(new DocumentToClassify(d.SourceId, d.Title, Sample(d.IdentityText)));
        }

        var classified = toClassify.Count == 0
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : await classifier.ClassifyAsync(toClassify, ct);

        var failed = 0;
        var result = new List<DocumentIdentity>(thisRun.Count);
        foreach (var d in thisRun)
        {
            if (reused.TryGetValue(d.SourceId, out var kept))
            {
                result.Add(kept);
            }
            else if (classified.TryGetValue(d.SourceId, out var tag))
            {
                result.Add(d with { DomainTag = tag, TaggedAtHash = d.Hash });
            }
            else
            {
                // Classification failed for this document. Keep whatever tag the store has -
                // stale-but-plausible beats untagged - under the OLD TaggedAtHash, so the
                // mismatch with d.Hash retries the classification next run.
                failed++;
                result.Add(persisted.TryGetValue(d.SourceId, out var stale)
                    ? d with { DomainTag = stale.DomainTag, TaggedAtHash = stale.TaggedAtHash }
                    : d);
            }
        }

        if (failed > 0)
            logger.LogWarning(
                "IdentityTagger: classification failed for {Failed} of {Requested} document(s) - stale or no tag this run, retried next run.",
                failed, toClassify.Count);

        logger.LogInformation(
            "IdentityTagger: {Reused} tag(s) reused from the store, {Classified} classified this run.",
            reused.Count, toClassify.Count - failed);

        return result;
    }

    private static string Sample(string identityText) =>
        identityText.Length <= TextSampleMaxChars ? identityText : identityText[..TextSampleMaxChars];
}
