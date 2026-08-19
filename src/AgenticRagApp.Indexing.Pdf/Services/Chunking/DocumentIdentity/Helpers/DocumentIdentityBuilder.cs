using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The per-document fingerprint everything in DocumentIdentityResolver is derived from: title +
// domain tag + every heading, joined with newlines, hashed together with the embedding model
// id. Split out of DocumentIdentityResolver so it can be tested without mocking an embedding client
// or a store - the same pure-static shape as DomainTagger in Chunking/Utils.
//
// Because this is the single input to clustering, anything wrong here is wrong in FamilyId,
// DomainTag AND ConfusableWith simultaneously - there is no second source to disagree.
public static class DocumentIdentityBuilder
{
    // text-embedding-3-large's per-input ceiling. Past it the client either throws or silently
    // truncates, and silent truncation is the bad case: the document still gets a vector, the
    // vector is still compared, and the tail of its structure simply stopped counting.
    public const int InputTokenLimit = 8191;

    // Warn at 80%. Measured 2026-08-14 over the full 51-document corpus, the worst document
    // (Hygienecode, 310 headings) sits at 6,009 tokens = 73%, and the next three are the CAOs
    // at 3,800-4,900. So nothing is truncating today - this is a margin alarm that fires before
    // the failure, not a fix for a live fault. Deliberately NOT a cap: capping headings would
    // change every identity hash and force a full corpus re-embed to solve a problem that does
    // not exist yet. See docs/2608/260814/documentidentityresolver-fixes.md B1.
    public const double TokenWarningFraction = 0.8;

    public static int TokenWarningThreshold => (int)(InputTokenLimit * TokenWarningFraction);

    // One identity per document. This used to open with a GroupBy(SourceId) to gather headings
    // back across a document's pages - one of the three places that undid the per-page record
    // shape by hand. Extraction emits whole documents now (action-plan.md C8), so the grouping
    // is gone and the headings are simply the document's own.
    public static IdentityBuildResult Build(IReadOnlyList<PdfExtractionDocument> docs, string embeddingModelId)
    {
        // Duplicate SourceIds would otherwise survive all the way to the caller's final
        // ToDictionary(SourceId) and throw a bare ArgumentException there - after the embedding
        // call has been paid for, and after each overwrite silently discarded one document's
        // vector. Unreachable today (extraction emits one document per blob), which is exactly
        // why it is cheap to state.
        var duplicates = docs
            .GroupBy(d => d.SourceId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"DocumentIdentityBuilder: duplicate SourceId(s) in one run: {string.Join(", ", duplicates)}. " +
                "Identity resolution assumes one document per SourceId.");

        var identities = new List<DocumentIdentity>(docs.Count);
        var skipped    = new List<string>();

        foreach (var doc in docs)
        {
            var title     = doc.Title;
            var domainTag = DomainTagger.Tag(title);

            // Whitespace-normalized before it reaches the hash (B5): a heading that gains a
            // double space or a trailing tab between extraction runs is the same heading, but
            // an un-normalized hash reads it as changed and pays to re-embed the document.
            //
            // Headings are deliberately NOT sorted. Sorting would also make the hash immune to
            // order changes, but heading order is the document's structure - it is signal in
            // the embedded text, not incidental formatting - so stability would be bought by
            // degrading what gets clustered.
            var headings = doc.Headings
                              .Select(h => NormalizeWhitespace(h.Content))
                              .Where(c => !string.IsNullOrWhiteSpace(c));

            var identityText = string.Join(
                "\n",
                new[] { NormalizeWhitespace(title), domainTag }
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Concat(headings));

            // Each part is filtered for blank above, but nothing checked the result: a blank
            // title with no headings would be embedded as an empty string and then clustered on
            // whatever vector came back. Title has a filename fallback so this should be
            // unreachable - the same reasoning under which DocumentIdentityResolver keeps its
            // unreachable null-vector branch.
            if (string.IsNullOrWhiteSpace(identityText))
            {
                skipped.Add(doc.SourceId);
                continue;
            }

            // Hash covers the model id as well as the text, so a deployment or dimension change
            // forces a re-embed instead of leaving stale vectors looking current.
            var hash = HashText($"{embeddingModelId}\n{identityText}");

            // Counted with the real cl100k_base tokenizer - the encoding the embedding model
            // actually uses - so the warning threshold is compared against the same number the
            // service will. Cheap next to the embedding call it precedes.
            var tokens = TokenCounter.Count(identityText);

            identities.Add(new DocumentIdentity(doc.SourceId, title, domainTag, identityText, hash, tokens));
        }

        return new IdentityBuildResult(identities, skipped);
    }

    // Any run of whitespace (including the non-breaking spaces PDF extraction produces)
    // collapses to a single space, and the ends are trimmed. Newlines are included, which is
    // safe because they are also the separator between parts - a heading containing one would
    // otherwise look like two entries.
    private static string NormalizeWhitespace(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : WhitespaceRun.Replace(text, " ").Trim();

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public sealed record DocumentIdentity(
    string SourceId, string Title, string? DomainTag, string IdentityText, string Hash,
    // Exact cl100k_base token count of IdentityText - what the embedding model will charge and
    // measure against its per-input limit.
    int IdentityTokens);

// SkippedEmptyIdentity carries the documents dropped for having nothing to embed, so the
// caller can log them - the builder itself stays free of a logger dependency.
public sealed record IdentityBuildResult(
    IReadOnlyList<DocumentIdentity> Identities,
    IReadOnlyList<string>           SkippedEmptyIdentity);
