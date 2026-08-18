using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Utils;

// Names the clusters CosineSimilarityClusterer produced. Split from the clusterer because
// grouping and naming answer different questions: clustering is recomputed from vectors every
// run, whereas a family's NAME has to survive membership changing underneath it.
//
// Two defects in the old scheme (families.md §7), both from naming a family after the
// lexicographically smallest SourceId in it and re-deriving that every run:
//
//   7a Instability - adding one document that sorts earlier renamed the whole family. Combined
//      with the assign-only rule (the store is corrected, already-uploaded Search chunks are
//      not), one family then carried two different family_id values in the index at once, and
//      the "same family_id, different domain_tag" conflict test silently stopped firing.
//   7b Misattribution - the value reaches the LLM, where "cao-ggz.pdf" on a VVT chunk reads as
//      a provenance claim rather than an arbitrary cluster label.
//
// The fix for 7a is structural and matters more than the label: an id is minted ONCE and
// thereafter joined. A cluster whose members already carry a stored family id keeps that id,
// so membership can change without renaming anything. Only a genuinely new family mints.
//
// Migration is deliberately passive. Every existing document already carries an old-style id,
// so on the next run each existing family "keeps" it - nothing is renamed, nothing needs
// re-uploading. Semantic labels (7b) therefore only appear on families formed from here on.
// Forcing the old ones to be relabelled would mean exactly the mass rename 7a warns about, so
// it is not done; if it is ever wanted, it is a deliberate migration with a re-upload, not a
// side effect of this class.
public static class FamilyIdAssigner
{
    // Joined with '-' to form a minted label. Four is enough to stay descriptive
    // ("brochure-verstrekkingen") without turning a long shared prefix into an unreadable id.
    private const int MaxLabelTokens = 4;

    private const int MinLabelTokenLength = 2;

    private static readonly Regex TokenPattern = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    // Tokens that carry no identity: they appear in most of this corpus's titles, so a family
    // named after one would say nothing. "versie" is in nearly every filename in the corpus
    // (measured: it is part of the title of all but a handful of the 51 documents).
    private static readonly HashSet<string> LabelStopWords =
        new(StringComparer.OrdinalIgnoreCase) { "versie", "definitief", "concept", "def", "final" };

    // clusterKeyOf: SourceId -> any stable per-cluster key (the clusterer's own choice is fine).
    // storedFamilyIdOf: SourceId -> the family id already persisted for it, null when new.
    public static FamilyAssignment Assign(
        IReadOnlyDictionary<string, string>  clusterKeyOf,
        IReadOnlyDictionary<string, string>  titlesById,
        IReadOnlyDictionary<string, string?> storedFamilyIdOf)
    {
        var familyIdOf = new Dictionary<string, string>();
        var decisions  = new List<FamilyAssignmentDecision>();

        // Every id already in use anywhere, so a minted label can never collide with a family
        // that simply has no member in this run's comparison set.
        var taken = new HashSet<string>(
            storedFamilyIdOf.Values.Where(v => !string.IsNullOrEmpty(v))!, StringComparer.Ordinal);

        // Largest cluster first, so that when a family has SPLIT (its members no longer cluster
        // together - which N1's eviction of a bridging ghost document can cause) the bigger half
        // keeps the established id and the smaller half is the one that has to mint. Ordinal
        // tie-break keeps it deterministic.
        var clusters = clusterKeyOf
            .GroupBy(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(id => id, StringComparer.Ordinal).ToList())
            .OrderByDescending(m => m.Count)
            .ThenBy(m => m[0], StringComparer.Ordinal)
            .ToList();

        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var members in clusters)
        {
            // Candidate ids this cluster could inherit, most-held first. More than one means
            // documents that used to be in different families now cluster together - a merge.
            var candidates = members
                .Select(id => storedFamilyIdOf.GetValueOrDefault(id))
                .Where(id => !string.IsNullOrEmpty(id))
                .GroupBy(id => id!, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key)
                .ToList();

            var inherited = candidates.FirstOrDefault(id => !claimed.Contains(id));

            string familyId;
            FamilyAssignmentKind kind;
            string? detail = null;

            if (inherited is not null)
            {
                familyId = inherited;
                if (candidates.Count > 1)
                {
                    kind   = FamilyAssignmentKind.Merged;
                    detail = $"absorbed {string.Join(", ", candidates.Where(c => c != inherited))}";
                }
                else
                {
                    kind = FamilyAssignmentKind.Kept;
                }
            }
            else
            {
                familyId = Mint(members, titlesById, taken);
                // Candidates existed but were all claimed by other clusters: this half of a
                // split family had to take a new id.
                kind   = candidates.Count > 0 ? FamilyAssignmentKind.Split : FamilyAssignmentKind.Minted;
                detail = candidates.Count > 0
                    ? $"split from {string.Join(", ", candidates)}, which another cluster kept"
                    : null;
            }

            claimed.Add(familyId);
            taken.Add(familyId);

            foreach (var member in members)
                familyIdOf[member] = familyId;

            decisions.Add(new FamilyAssignmentDecision(familyId, members, kind, detail));
        }

        // Which documents ended up in a different family than the one they already carried.
        // Derived here because this is the only place both values exist at once - once Assign
        // returns, the stored id is gone.
        //
        // Documents with no stored id are excluded: they are new, not moved.
        var inRunMoves = familyIdOf
            .Select(kv => (SourceId: kv.Key, From: storedFamilyIdOf.GetValueOrDefault(kv.Key), To: kv.Value))
            .Where(m => !string.IsNullOrEmpty(m.From) && !string.Equals(m.From, m.To, StringComparison.Ordinal))
            .Select(m => new FamilyMove(m.SourceId, m.From, m.To))
            .OrderBy(m => m.SourceId, StringComparer.Ordinal)
            .ToList();

        return new FamilyAssignment(
            familyIdOf,
            decisions.OrderBy(d => d.FamilyId, StringComparer.Ordinal).ToList(),
            inRunMoves);
    }

    // A new family's name. The longest common leading token run across its members' titles -
    // the CAO trio share "CAO", the two verstrekkingen brochures share "Brochure
    // verstrekkingen" - which is both semantic and free.
    //
    // Deliberately the common PREFIX rather than the longest common token anywhere: nearly
    // every title in this corpus ends with "(Versie N)", so a longest-token rule would name
    // half the families "versie". The stop-word list is a second guard on the same problem.
    //
    // Falls back to the ordinal-smallest SourceId - the old scheme - for a family of one, for
    // members with nothing in common, and for a label that is already in use. That keeps the
    // fallback traceable to a real document rather than inventing an opaque id.
    private static string Mint(
        IReadOnlyList<string> members, IReadOnlyDictionary<string, string> titlesById, HashSet<string> taken)
    {
        var fallback = members[0];
        if (members.Count < 2) return Unique(fallback, taken);

        var tokenLists = members
            .Select(id => Tokenize(titlesById.GetValueOrDefault(id)))
            .ToList();

        if (tokenLists.Any(t => t.Count == 0)) return Unique(fallback, taken);

        var prefix = new List<string>();
        for (int i = 0; i < tokenLists.Min(t => t.Count) && prefix.Count < MaxLabelTokens; i++)
        {
            var token = tokenLists[0][i];
            if (!tokenLists.All(t => string.Equals(t[i], token, StringComparison.OrdinalIgnoreCase)))
                break;

            prefix.Add(token);
        }

        // Trailing stop words carry nothing; a label made only of them carries nothing at all.
        while (prefix.Count > 0 && LabelStopWords.Contains(prefix[^1]))
            prefix.RemoveAt(prefix.Count - 1);

        var label = string.Join("-", prefix).ToLowerInvariant();

        return string.IsNullOrEmpty(label) || taken.Contains(label)
            ? Unique(fallback, taken)
            : label;
    }

    // The fallback can itself already be taken (a document that is its own family's namesake
    // being pulled into a different cluster). Suffixing keeps it deterministic and traceable
    // rather than silently overwriting another family's id.
    private static string Unique(string candidate, HashSet<string> taken)
    {
        if (!taken.Contains(candidate)) return candidate;

        for (int i = 2; ; i++)
        {
            var next = $"{candidate}-{i}";
            if (!taken.Contains(next)) return next;
        }
    }

    private static List<string> Tokenize(string? title) =>
        TokenPattern.Matches(title ?? string.Empty)
            .Select(m => m.Value)
            .Where(t => t.Length >= MinLabelTokenLength && !LabelStopWords.Contains(t))
            .ToList();
}

public enum FamilyAssignmentKind
{
    // Inherited the id its members already carried - the common case, and the whole point:
    // membership changed, the name did not.
    Kept,
    // Formed from documents that carried no family id yet.
    Minted,
    // Two or more previously-distinct families now cluster as one.
    Merged,
    // Members of one stored family no longer cluster together; this is the half that had to
    // take a new id.
    Split,
}

public sealed record FamilyAssignmentDecision(
    string                FamilyId,
    IReadOnlyList<string> Members,
    FamilyAssignmentKind  Kind,
    string?               Detail);

public sealed record FamilyAssignment(
    IReadOnlyDictionary<string, string>       FamilyIdOf,
    IReadOnlyList<FamilyAssignmentDecision>   Decisions,

    // Documents IN this run's comparison set that came out carrying a different family id than
    // they went in with. The complement of PersistOutcome.Moves, which covers documents NOT in
    // this run that this run's clustering re-homed - together they are every document whose
    // family_id changed. See DocumentIdentityResolver's FamilyMoves for what consumes them.
    IReadOnlyList<FamilyMove>                 InRunMoves);
