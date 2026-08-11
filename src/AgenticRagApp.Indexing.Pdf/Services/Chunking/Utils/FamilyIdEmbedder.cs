using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

namespace AgenticRagApp.Indexing.Pdf.Utils;

// Pre-chunking action items C2 (family/near-duplicate detection) and C3 (title-distance
// check) — docs/2608/260811/pre-chunking-action-items.md and chunking-signals-map.md §3c.
//
// Called once per ChunkActivity run, before PdfExtractionDocuments are handed to the
// splitter (ChunkingService.ChunkDocumentsAsync). Resolves a FamilyId/DomainTag/
// ConfusableWith set per SourceId, which the caller then stamps onto every chunk of that
// document: the same "resolve once at document level, carry onto every chunk" pattern
// DocumentRouting already uses.
//
// Family membership is corpus-wide, not run-scoped: a document processed today needs to
// cluster against every document ever indexed, not just whatever else happens to be in this
// run's incremental batch (see IDocumentIdentityStore). Deliberately assign-only, never
// retroactive on Search: if a new document should pull an older, already-indexed document
// into its family, only the identity STORE record for that older document gets its FamilyId
// corrected (so future clustering stays accurate). Its already-uploaded Search chunks keep
// their old family_id until that document happens to be reindexed for some other reason.
// Patching live Search documents on every merge was judged not worth the extra write path
// for a 51-document POC corpus that rarely changes.
//
// The same assign-only asymmetry applies to ConfusableWith, and more sharply: it is only
// computed for documents in the current run. An older document that is confusable with one
// being processed now does not get the flag until it is itself reindexed, because its chunks
// are already in Search and nothing here rewrites them.
//
// Comparisons only ever run between vectors from the same embedding model (see
// EmbeddingModelId on DocumentIdentityRecord). Persisted records from an older model are
// held out of the comparison set entirely rather than cosine-compared across incompatible
// embedding spaces; they rejoin on their next reindex. Changing the model therefore degrades
// clustering to the current run's documents until the corpus has been reprocessed, which is
// the intended trade against silently meaningless similarities.
//
// Thresholds below are a starting point, not calibrated against the real corpus yet (no
// live embedding run was done while writing this): same caveat as tokenizer-redo-findings.md
// had before its ratios were measured. Revisit all four constants once this has actually run
// against the 51-document corpus and the resulting clusters/confusable pairs can be checked
// by hand against the known families in chunking-signals-map.md §2. The cluster diagnostics
// logged by ClusterByCosineSimilarity exist specifically to make that calibration pass easy.
public class FamilyIdEmbedder
{
    private readonly IEmbeddingClient          _embeddingClient;
    private readonly IDocumentIdentityStore    _store;
    private readonly ILogger<FamilyIdEmbedder> _logger;

    // Identifies the embedding space the persisted vectors live in. Folded into the identity
    // hash so a model change forces a re-embed of everything this run touches, and stamped
    // onto each record so older-model vectors can be excluded from the comparison set rather
    // than silently mixed into it.
    //
    // Model name rather than deployment name — the deployment is an alias that can be
    // repointed without the embedding space changing — plus the requested dimension count,
    // since the same model at a different dimensionality is a different space too.
    private readonly string _embeddingModelId;

    // Cosine similarity above which two documents are considered the same family.
    private const double SimilarityThreshold = 0.90;

    // Normalized word-level Levenshtein distance below which two *different* words across
    // two titles are flagged as confusable (Medido/Medimo territory). See NormalizedLevenshtein.
    private const double ConfusableWordThreshold = 0.30;

    // Words shorter than this are skipped for the confusable-word check. Short Dutch
    // function words ("van", "een", "de") would otherwise swamp it with noise matches, and
    // at length 4 a single edit already scores 0.25, i.e. under the threshold, so any
    // one-character difference would match ("Zorg" vs "Zorn").
    private const int MinConfusableWordLength = 5;

    // Absolute edit-distance ceiling on top of the normalized ratio. Without it, long words
    // pass on ratio alone: two 20-character titles differing in 5 characters score 0.25.
    private const int MaxConfusableEdits = 2;

    private static readonly Regex WordPattern = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    // Confusable candidates must be all-letter tokens. Numeric and alphanumeric tokens are
    // the dominant false-positive source in this corpus: "2024" vs "2025" scores 0.25 and
    // would flag every year, version number and article code as a confusable pair. Note this
    // filters whole tokens produced by WordPattern, which deliberately keeps letters and
    // digits together — tokenizing on letters alone would split "Medido2024" into a bare
    // "Medido" and reintroduce exactly the matches this is meant to suppress.
    private static readonly Regex LettersOnly = new(@"^\p{L}+$", RegexOptions.Compiled);

    public FamilyIdEmbedder(
        IEmbeddingClient embeddingClient,
        IDocumentIdentityStore store,
        IndexerConfig config,
        ILogger<FamilyIdEmbedder> logger)
    {
        ArgumentNullException.ThrowIfNull(config);

        _embeddingClient  = embeddingClient ?? throw new ArgumentNullException(nameof(embeddingClient));
        _store            = store           ?? throw new ArgumentNullException(nameof(store));
        _logger           = logger          ?? throw new ArgumentNullException(nameof(logger));
        _embeddingModelId = $"{config.OpenAiEmbeddingModelName}@{config.OpenAiEmbeddingDimensions}";
    }

    public async Task<IReadOnlyDictionary<string, DocumentFamily>> ResolveAsync(
        IReadOnlyList<PdfExtractionDocument> docs, CancellationToken ct = default)
    {
        var thisRun = BuildIdentities(docs);
        if (thisRun.Count == 0)
            return new Dictionary<string, DocumentFamily>();

        var persisted = (await _store.GetAllAsync(ct)).ToDictionary(r => r.SourceId);

        var freshVectors = await EmbedChangedAsync(thisRun, persisted, ct);

        // Working set = every persisted document with a usable, same-model vector, plus this
        // run's documents with their vector/title refreshed to current values. This is the
        // full comparison set that both clustering and the confusable check run over.
        //
        // Persisted records are dropped when they have no vector (nothing to compare) or
        // were embedded under a different model (comparable only by accident — cosine across
        // two embedding spaces is not a similarity). Documents in this run are unaffected:
        // the model id is part of the identity hash, so a model change puts them in toEmbed
        // and they come back with a current-model vector.
        var working          = new Dictionary<string, WorkingDoc>();
        var skippedNoVector  = 0;
        var skippedOtherModel = 0;

        foreach (var (sourceId, rec) in persisted)
        {
            if (rec.Vector is not { Length: > 0 })
            {
                skippedNoVector++;
                continue;
            }

            if (rec.EmbeddingModelId != _embeddingModelId)
            {
                skippedOtherModel++;
                continue;
            }

            working[sourceId] = new WorkingDoc(rec.Title, rec.Vector);
        }

        if (skippedNoVector > 0)
            _logger.LogWarning(
                "FamilyIdEmbedder: {Count} persisted identity records have no usable vector and were excluded from clustering",
                skippedNoVector);

        if (skippedOtherModel > 0)
            _logger.LogWarning(
                "FamilyIdEmbedder: {Count} persisted identity records were embedded under a different model than {ModelId} " +
                "and were excluded from clustering until their documents are reindexed",
                skippedOtherModel, _embeddingModelId);

        foreach (var d in thisRun)
        {
            // The persisted fallback is only reached when the identity hash matched, which
            // implies the same model (the model id is hashed in), so its vector is safe to
            // reuse. The check is repeated anyway to keep that invariant local.
            var vector = freshVectors.TryGetValue(d.SourceId, out var fresh)
                ? fresh
                : persisted.TryGetValue(d.SourceId, out var rec)
                  && rec.Vector is { Length: > 0 }
                  && rec.EmbeddingModelId == _embeddingModelId
                    ? rec.Vector
                    : null;

            if (vector is null)
            {
                // Defensive: EmbedChangedAsync throws rather than returning a document with
                // no vector, so this is unreachable today. Kept so that a future change there
                // degrades to skipping one document instead of putting a null into the cosine
                // loop. Remove() covers the case where a stale persisted entry was admitted
                // above under a rule that later diverges from the one used here.
                _logger.LogWarning("FamilyIdEmbedder: no vector available for {SourceId}, skipping", d.SourceId);
                working.Remove(d.SourceId);
                continue;
            }

            working[d.SourceId] = new WorkingDoc(d.Title, vector);
        }

        if (working.Count == 0)
        {
            _logger.LogWarning("FamilyIdEmbedder: no documents with usable vectors, returning no families");
            return new Dictionary<string, DocumentFamily>();
        }

        var familyIdOf   = ClusterByCosineSimilarity(working);
        var confusableOf = FindConfusableTitles(thisRun, working, familyIdOf);

        await PersistAsync(thisRun, working, persisted, familyIdOf, ct);

        return thisRun
            .Where(d => working.ContainsKey(d.SourceId))
            .ToDictionary(
                d => d.SourceId,
                d => new DocumentFamily(
                    familyIdOf[d.SourceId],
                    d.DomainTag,
                    confusableOf.TryGetValue(d.SourceId, out var c) ? c : []));
    }

    // One identity per SourceId. PdfExtractionDocument is a per-page record, so headings
    // have to be gathered across every page of a document, not read off a single row.
    private List<DocumentIdentity> BuildIdentities(IReadOnlyList<PdfExtractionDocument> docs) =>
        docs.GroupBy(d => d.SourceId)
            .Select(g =>
            {
                var title      = g.First().Title;
                var domainTag  = DomainTagger.Tag(title);
                var headings   = g.SelectMany(d => d.Headings)
                                   .Select(h => h.Content)
                                   .Where(c => !string.IsNullOrWhiteSpace(c));
                var identityText = string.Join(
                    "\n",
                    new[] { title, domainTag }.Where(s => !string.IsNullOrWhiteSpace(s)).Concat(headings));

                // Hash covers the model id as well as the text, so a deployment or dimension
                // change forces a re-embed instead of leaving stale vectors looking current.
                var hash = HashText($"{_embeddingModelId}\n{identityText}");

                return new DocumentIdentity(g.Key, title, domainTag, identityText, hash);
            })
            .ToList();

    private async Task<Dictionary<string, float[]>> EmbedChangedAsync(
        List<DocumentIdentity> thisRun,
        Dictionary<string, DocumentIdentityRecord> persisted,
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
        var (embedded, retries) = await _embeddingClient.EmbedWithRetryAsync(
            toEmbed.Select(d => d.IdentityText).ToList(), ct);

        if (retries > 0)
            _logger.LogInformation(
                "FamilyIdEmbedder: {Retries} embedding retries for {Count} identity vectors", retries, toEmbed.Count);

        // Results are matched to inputs positionally, so a short or long result set means the
        // pairing is unreliable. Failing here is much cheaper than persisting document A's
        // vector under document B's SourceId and having the wrong families silently stick.
        // This throws out of ChunkDocumentsAsync and fails the whole chunking activity, which
        // is deliberate: a partial identity pass writes durable, wrong records to the store.
        if (embedded is null || embedded.Length != toEmbed.Count)
            throw new InvalidOperationException(
                $"FamilyIdEmbedder: embedding client returned {embedded?.Length ?? 0} vectors for {toEmbed.Count} inputs; " +
                "cannot map vectors to documents.");

        for (int i = 0; i < toEmbed.Count; i++)
        {
            if (embedded[i] is not { Length: > 0 })
                throw new InvalidOperationException(
                    $"FamilyIdEmbedder: embedding client returned an empty vector for {toEmbed[i].SourceId}.");

            vectors[toEmbed[i].SourceId] = embedded[i];
        }

        return vectors;
    }

    // Union-find over cosine similarity. O(n^2) pairwise comparisons, trivial at this
    // corpus's scale (dozens to low hundreds of documents). FamilyId is the lexicographically
    // smallest SourceId in the cluster, so it's deterministic and traceable back to a real
    // document rather than an opaque generated GUID.
    //
    // Note this is single-linkage: A~B and B~C merges A with C even when A and C are far
    // apart. That is the expected over-merge failure mode at a fixed threshold, so the
    // weakest link inside each cluster is logged to make it visible during calibration.
    private Dictionary<string, string> ClusterByCosineSimilarity(Dictionary<string, WorkingDoc> working)
    {
        var ids    = working.Keys.ToList();
        var parent = ids.ToDictionary(id => id, id => id);

        string Find(string x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x         = parent[x];
            }
            return x;
        }

        void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        for (int i = 0; i < ids.Count; i++)
        {
            for (int j = i + 1; j < ids.Count; j++)
            {
                if (CosineSimilarity(working[ids[i]].Vector, working[ids[j]].Vector) >= SimilarityThreshold)
                    Union(ids[i], ids[j]);
            }
        }

        var familyIdOf = new Dictionary<string, string>();
        foreach (var cluster in ids.GroupBy(Find))
        {
            var members  = cluster.OrderBy(id => id, StringComparer.Ordinal).ToList();
            var familyId = members[0];

            foreach (var member in members)
                familyIdOf[member] = familyId;

            // Weakest intra-family pair, recomputed over this cluster's members only rather
            // than kept from the pass above: clusters are small, so this is far cheaper than
            // holding all n^2 similarities in memory for the sake of a log line.
            if (members.Count > 1)
            {
                var weakest = double.MaxValue;
                for (int i = 0; i < members.Count; i++)
                    for (int j = i + 1; j < members.Count; j++)
                        weakest = Math.Min(
                            weakest,
                            CosineSimilarity(working[members[i]].Vector, working[members[j]].Vector));

                _logger.LogInformation(
                    "FamilyIdEmbedder: family {FamilyId} has {Size} members, weakest intra-family similarity {Weakest:F3}",
                    familyId, members.Count, weakest);
            }
        }

        return familyIdOf;
    }

    // C3, lexically close but semantically distant (Medido/Medimo): compares individual words
    // across two titles rather than whole titles, since the confusable pair is usually one
    // product-name-shaped word buried in otherwise-unrelated titles, not the titles overall.
    // Only checked against documents outside this document's own family: two titles in the
    // same family aren't "confusable," they're already correctly grouped as the same thing.
    //
    // Only documents in this run get an entry, so the relation is one-directional against
    // older documents (see class comment).
    private static Dictionary<string, List<string>> FindConfusableTitles(
        List<DocumentIdentity> thisRun,
        Dictionary<string, WorkingDoc> working,
        Dictionary<string, string> familyIdOf)
    {
        var result = new Dictionary<string, List<string>>();

        // Tokenized once per document rather than once per comparison. The inner loop runs
        // over the whole corpus for every document in the run, so re-running the regex there
        // is the difference between n and n^2 tokenizations.
        var wordsOf = working.ToDictionary(kv => kv.Key, kv => ConfusableWords(kv.Value.Title));

        foreach (var d in thisRun)
        {
            // familyIdOf, working and wordsOf all carry the same key set, so this one guard
            // covers the lookups below.
            if (!familyIdOf.TryGetValue(d.SourceId, out var ownFamily))
                continue;

            var ownWords = wordsOf[d.SourceId];
            if (ownWords.Count == 0)
                continue;

            var confusable = new List<string>();
            foreach (var otherId in working.Keys)
            {
                if (otherId == d.SourceId || familyIdOf.GetValueOrDefault(otherId) == ownFamily)
                    continue;

                var otherWords = wordsOf[otherId];

                var isConfusable = ownWords.Any(ow => otherWords.Any(other =>
                    !string.Equals(ow, other, StringComparison.OrdinalIgnoreCase) &&
                    IsConfusablePair(ow, other)));

                if (isConfusable)
                    confusable.Add(otherId);
            }

            if (confusable.Count > 0)
                result[d.SourceId] = confusable;
        }

        return result;
    }

    private static List<string> ConfusableWords(string title) =>
        WordPattern.Matches(title ?? string.Empty)
            .Select(m => m.Value)
            .Where(w => w.Length >= MinConfusableWordLength && LettersOnly.IsMatch(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Both the ratio and the absolute edit count have to be small. The ratio alone lets long
    // words through on several edits; the absolute cap alone flags every short word pair.
    private static bool IsConfusablePair(string a, string b)
    {
        // Cheap length prefilter before the O(n*m) matrix: the length difference is itself a
        // lower bound on the edit distance.
        if (Math.Abs(a.Length - b.Length) > MaxConfusableEdits)
            return false;

        var distance = LevenshteinDistance(a, b);
        if (distance > MaxConfusableEdits)
            return false;

        return NormalizedLevenshtein(a, b, distance) <= ConfusableWordThreshold;
    }

    // Persists this run's documents unconditionally (family/vector may have changed), plus
    // any older, not-touched-this-run document whose FamilyId shifted because a document
    // processed in this run merged it into a bigger cluster. Store-only, not re-uploaded to
    // Search (see class comment).
    //
    // Writes are sequential: at 51 documents that is a handful of round trips. If the corpus
    // grows into the thousands this is the first place to batch.
    private async Task PersistAsync(
        List<DocumentIdentity> thisRun,
        Dictionary<string, WorkingDoc> working,
        Dictionary<string, DocumentIdentityRecord> persisted,
        Dictionary<string, string> familyIdOf,
        CancellationToken ct)
    {
        foreach (var d in thisRun)
        {
            if (!working.TryGetValue(d.SourceId, out var w)) continue;
            if (!familyIdOf.TryGetValue(d.SourceId, out var familyId)) continue;

            await _store.SetAsync(
                new DocumentIdentityRecord(
                    d.SourceId, d.Title, d.DomainTag, w.Vector, familyId, d.Hash, _embeddingModelId), ct);
        }

        // Older documents only ever reach this loop via familyIdOf, which is keyed on the
        // working set, so their EmbeddingModelId already matches the current one and the
        // `with` expression carries it through unchanged.
        var thisRunIds = thisRun.Select(d => d.SourceId).ToHashSet();
        foreach (var (sourceId, rec) in persisted)
        {
            if (thisRunIds.Contains(sourceId)) continue;
            if (familyIdOf.TryGetValue(sourceId, out var newFamilyId) && newFamilyId != rec.FamilyId)
            {
                _logger.LogInformation(
                    "FamilyIdEmbedder: {SourceId} moved from family {Old} to {New} (store only, Search chunks unchanged)",
                    sourceId, rec.FamilyId, newFamilyId);

                await _store.SetAsync(rec with { FamilyId = newFamilyId }, ct);
            }
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a is null || b is null || a.Length != b.Length || a.Length == 0)
            return 0;

        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot  += (double)a[i] * b[i];
            magA += (double)a[i] * a[i];
            magB += (double)b[i] * b[i];
        }

        return magA == 0 || magB == 0 ? 0 : dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    private static double NormalizedLevenshtein(string a, string b, int distance)
    {
        var maxLen = Math.Max(a.Length, b.Length);
        return maxLen == 0 ? 0 : (double)distance / maxLen;
    }

    // Two-row variant: the full matrix is never needed, only the distance.
    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current  = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var ca = char.ToLowerInvariant(a[i - 1]);

            for (int j = 1; j <= b.Length; j++)
            {
                var cost = ca == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private readonly record struct WorkingDoc(string Title, float[] Vector);

    private sealed record DocumentIdentity(string SourceId, string Title, string? DomainTag, string IdentityText, string Hash);
}
