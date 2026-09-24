#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property NoWarn=IL2026;IL3050

using System.Globalization;
using System.Text.Json;

// Per-scenario comparison of two eval runs, with the relabel exclusion done by code (D228 step 7,
// 2026-09-23). Replaces the scratchpad join D227 §2 was read from.
//
// What it answers: on the rows both runs scored, how did each metric move - and which rows are
// NOT like-for-like because their labels changed between the two runs. Each EvalRow carries
// RelabelledOn (TestQuery.RelabelledOn, since 2026-09-23): the date its labels last changed, as
// the dataset stood when that run scored it. A row is dropped from the carried-over slice when
// the two runs' rows carry DIFFERENT values - the runs were scored against different labels.
// Same value (including both blank) = same labels = like for like. Rows written before the field
// existed have no value; for a pair of such rows the golden set (--dataset) supplies the date and
// the row is dropped only when that date lies strictly between the two runs' last-row dates -
// which for two pre-field runs is never, since every relabel postdates the field. The
// day-granular date is deliberately not compared against a same-day run: the row-level value is
// what says which labels a run saw.
//
// Usage:
//   dotnet run src/Tools/CompareEvalRuns.cs -- <older.jsonl> <newer.jsonl>
//       [--dataset <golden-questions.json>]   default: src/Evaluations/.../testdata/golden-questions.json
//       [--all-rows]                          also print the per-row table for unchanged rows
// Output: markdown on stdout - paste it under the run's D181 entry.

var positional = new List<string>();
string? datasetPath = null;
var allRows = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dataset": datasetPath = args[++i]; break;
        case "--all-rows": allRows = true; break;
        default: positional.Add(args[i]); break;
    }
}
if (positional.Count != 2)
{
    Console.Error.WriteLine("usage: CompareEvalRuns <older.jsonl> <newer.jsonl> [--dataset golden-questions.json] [--all-rows]");
    return 2;
}

datasetPath ??= FindDefaultDataset();

var older = Load(positional[0]);
var newer = Load(positional[1]);
var relabelDates = datasetPath is not null && File.Exists(datasetPath) ? LoadRelabelDates(datasetPath) : new Dictionary<string, DateOnly>();

var olderEnd = RunEnd(older);
var newerEnd = RunEnd(newer);

var olderBy = older.ToDictionary(r => r.ScenarioName);
var newerBy = newer.ToDictionary(r => r.ScenarioName);

// The value a run's row carries is what that run was scored with; a row from before the field
// existed carries null. For display, fall back to the dataset's date.
static string RowValue(EvalRowLite row) => row.RelabelledOn ?? "";

DateOnly? RelabelledOn(EvalRowLite row) =>
    DateOnly.TryParseExact(RowValue(row), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        ? d
        : relabelDates.TryGetValue(row.ScenarioName, out var fromDataset) ? fromDataset : null;

bool RelabelledBetweenRuns(EvalRowLite newRow)
{
    var oldRow = olderBy[newRow.ScenarioName];
    if (newRow.RelabelledOn is not null || oldRow.RelabelledOn is not null)
        return RowValue(newRow) != RowValue(oldRow);          // the rows say which labels each run saw
    return relabelDates.TryGetValue(newRow.ScenarioName, out var d) && d > olderEnd && d < newerEnd;   // pre-field pair
}

var carried  = newer.Where(r => olderBy.ContainsKey(r.ScenarioName) && !RelabelledBetweenRuns(r)).ToList();
var excluded = newer.Where(r => olderBy.ContainsKey(r.ScenarioName) &&  RelabelledBetweenRuns(r)).ToList();
var added    = newer.Where(r => !olderBy.ContainsKey(r.ScenarioName)).ToList();
var dropped  = older.Where(r => !newerBy.ContainsKey(r.ScenarioName)).ToList();

var carriedAnswer = carried.Where(r => r.Type == "Answer" && r.Succeeded).ToList();
var carriedRefusal = carried.Where(r => r.Type == "Refusal" && r.Succeeded).ToList();

Console.WriteLine($"# Eval comparison: {Path.GetFileName(positional[0])} → {Path.GetFileName(positional[1])}");
Console.WriteLine();
Console.WriteLine($"Older run ends {olderEnd:yyyy-MM-dd}, newer run ends {newerEnd:yyyy-MM-dd}. " +
                  $"{newer.Count} rows in the newer run: {carried.Count} carried over, {excluded.Count} excluded as relabelled between the runs, {added.Count} new; {dropped.Count} rows of the older run are gone.");
Console.WriteLine();
if (relabelDates.Count == 0 && newer.All(r => string.IsNullOrEmpty(r.RelabelledOn)))
    Console.WriteLine("**No relabel dates available** (neither the newer run's rows nor a dataset carry RelabelledOn) - the exclusion could not be applied.\n");

Console.WriteLine("## Carried-over rows, like for like");
Console.WriteLine();
Console.WriteLine($"| Metric | older ({carriedAnswer.Count} Answer) | newer ({carriedAnswer.Count} Answer) | Δ |");
Console.WriteLine("|---|---|---|---|");
foreach (var (name, pick) in Metrics())
{
    var a = Mean(carriedAnswer.Select(r => pick(olderBy[r.ScenarioName])));
    var b = Mean(carriedAnswer.Select(pick));
    Console.WriteLine($"| {name} | {Fmt(a)} | {Fmt(b)} | {Delta(a, b)} |");
}
{
    var a = Mean(carriedRefusal.Select(r => olderBy[r.ScenarioName].RefusalScore));
    var b = Mean(carriedRefusal.Select(r => r.RefusalScore));
    Console.WriteLine($"| RefusalScore ({carriedRefusal.Count} Refusal) | {Fmt(a)} | {Fmt(b)} | {Delta(a, b)} |");
}
Console.WriteLine();
Console.WriteLine("Means over rows scored in BOTH runs (-1 sentinels excluded per metric). k = ReferencesRetrieved.");
Console.WriteLine();

// Per-metric movement over the carried Answer rows. Run twice on the same code and index, this
// table IS the noise floor for this corpus, one line per metric - each metric drifts by its own
// amount (judge scores carry their own run-to-run variance on top of planner drift), so one
// number for all of them would be wrong for most of them.
Console.WriteLine("## Per-metric movement on the carried-over Answer rows");
Console.WriteLine();
Console.WriteLine("Between two runs of the SAME code on the SAME index this table is the noise floor, per metric. " +
                  "Between different code it is the effect plus the floor. |Δ| = absolute per-row difference; rows moved = rows where the metric differs at all.");
Console.WriteLine();
Console.WriteLine("| Metric | rows scored in both | rows moved | mean Δ | mean abs Δ | p95 abs Δ | max abs Δ |");
Console.WriteLine("|---|---|---|---|---|---|---|");
foreach (var (name, pick) in PerRowMetrics())
{
    var deltas = carriedAnswer
        .Select(r => (Old: pick(olderBy[r.ScenarioName]), New: pick(r)))
        .Where(p => p.Old >= 0 && p.New >= 0)
        .Select(p => p.New - p.Old)
        .ToList();
    if (deltas.Count == 0) { Console.WriteLine($"| {name} | 0 | – | n/a | n/a | n/a | n/a |"); continue; }
    var abs = deltas.Select(Math.Abs).OrderBy(v => v).ToList();
    Console.WriteLine($"| {name} | {deltas.Count} | {deltas.Count(d => d != 0)} | {Delta(0, deltas.Average())} | {Fmt(abs.Average())} | {Fmt(abs[(int)Math.Floor((abs.Count - 1) * 0.95)])} | {Fmt(abs[^1])} |");
}
Console.WriteLine();

Console.WriteLine("## Rows that moved");
Console.WriteLine();
Console.WriteLine("Retrieval side: Cite = CitationMatch, rank = FirstRelevantRank (0 = expected document not retrieved), k = ReferencesRetrieved. " +
                  "Judge side: G = Groundedness, Ret = Retrieval judge, Eq = Equivalence - the metrics a change to the judged context (D228 step 4a) is read on, " +
                  "and the only ones it can move: Cite / rank / R@k read the reference list, which the context cap never touches.");
Console.WriteLine();
Console.WriteLine("| Scenario | Capability | moved on | Cite | rank | R@5 | R@50 | k | G | Ret | Eq |");
Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|");
var movedRetrieval = 0; var movedJudge = 0;
foreach (var n in carriedAnswer.OrderBy(r => r.ScenarioName))
{
    var o = olderBy[n.ScenarioName];
    var movedOn = new List<string>();
    if (o.CitationMatch != n.CitationMatch || o.FirstRelevantRank != n.FirstRelevantRank || o.RecallAt5 != n.RecallAt5 || o.RecallAt50 != n.RecallAt50) movedOn.Add("retrieval");
    if (o.Groundedness != n.Groundedness || o.Retrieval != n.Retrieval || o.Equivalence != n.Equivalence) movedOn.Add("judges");
    if (movedOn.Count == 0 && !allRows) continue;
    if (movedOn.Contains("retrieval")) movedRetrieval++;
    if (movedOn.Contains("judges")) movedJudge++;
    Console.WriteLine($"| `{n.ScenarioName}` | {n.Capability} | {(movedOn.Count == 0 ? "–" : string.Join(" + ", movedOn))} | {Pair(o.CitationMatch, n.CitationMatch)} | {Pair(o.FirstRelevantRank, n.FirstRelevantRank)} | {Pair(o.RecallAt5, n.RecallAt5)} | {Pair(o.RecallAt50, n.RecallAt50)} | {Pair(o.ReferencesRetrieved, n.ReferencesRetrieved)} | {Pair(o.Groundedness, n.Groundedness)} | {Pair(o.Retrieval, n.Retrieval)} | {Pair(o.Equivalence, n.Equivalence)} |");
}
Console.WriteLine();
Console.WriteLine($"{movedRetrieval} of {carriedAnswer.Count} carried-over Answer rows moved on a retrieval metric; {movedJudge} moved on a judge metric.");
Console.WriteLine();

// Deterministic, no floor to beat: an expected (or equivalent) document is among the k
// references but none of the chunks the judges were shown belongs to it. Needs the per-row
// ContextDocumentIds column (one document id per block of RetrievedContext); without it the
// judged context carries no document ids at all, which is why D227 §2 had to locate block text in
// the chunking artifact. Printed per run so the improve side of a context change reads as a
// count going to 0, not as a judge delta.
Console.WriteLine("## Expected document retrieved but absent from the judged context");
Console.WriteLine();
foreach (var (label, rows) in new[] { ("older", carriedAnswer.Select(r => olderBy[r.ScenarioName]).ToList()), ("newer", carriedAnswer) })
{
    if (rows.All(r => r.ContextDocumentIds is null))
    {
        Console.WriteLine($"- {label}: n/a - this run's rows carry no `ContextDocumentIds` column (predates it).");
        continue;
    }
    var hit = rows.Where(RetrievedButNotInContext).Select(r => $"`{r.ScenarioName}`").ToList();
    Console.WriteLine($"- {label}: **{hit.Count}** of {rows.Count(r => r.ContextDocumentIds is not null)} rows" + (hit.Count == 0 ? "." : ": " + string.Join(", ", hit)));
}
Console.WriteLine();

Console.WriteLine("## Excluded: relabelled between the two runs");
Console.WriteLine();
if (excluded.Count == 0)
    Console.WriteLine("None.");
else
{
    Console.WriteLine("Scored against different labels in the two runs, so their deltas are not retrieval deltas. Read them on their own.");
    Console.WriteLine();
    Console.WriteLine("| Scenario | RelabelledOn | Cite | rank | R@5 | R@50 | G | Eq |");
    Console.WriteLine("|---|---|---|---|---|---|---|---|");
    foreach (var n in excluded.OrderBy(r => r.ScenarioName))
    {
        var o = olderBy[n.ScenarioName];
        Console.WriteLine($"| `{n.ScenarioName}` | {RelabelledOn(n):yyyy-MM-dd} | {Pair(o.CitationMatch, n.CitationMatch)} | {Pair(o.FirstRelevantRank, n.FirstRelevantRank)} | {Pair(o.RecallAt5, n.RecallAt5)} | {Pair(o.RecallAt50, n.RecallAt50)} | {Pair(o.Groundedness, n.Groundedness)} | {Pair(o.Equivalence, n.Equivalence)} |");
    }
}
Console.WriteLine();

// Does the emitted document order differ from the reranker order in production? Decides whether
// D228 step 4a is one change (admission under the cap) or two (admission + reordering). The
// RetrievedDocumentRanking column is sorted by RerankerScores BY CONSTRUCTION (the service builds
// both from one ordered list), so checking that would prove nothing. What carries the service's
// return order is ContextDocumentIds: before 4a the judged blocks are emitted in document groups
// ranked by the position of each document's best hit in the service's return order. So the
// check is: per row, the order of first appearance of documents in ContextDocumentIds, restricted
// to documents that also appear in the ranking, equals their order of first appearance in
// RetrievedDocumentRanking. Equal on every row = the service already returns references in
// reranker order and 4a's reordering does nothing in production. Deterministic, per run.
Console.WriteLine("## Judged-context document order vs reranker order (is 4a one change or two?)");
Console.WriteLine();
foreach (var (label, rows) in new[] { ("older", carriedAnswer.Select(r => olderBy[r.ScenarioName]).ToList()), ("newer", carriedAnswer) })
{
    var checkable = rows.Where(r => r.ContextDocumentIds is { Length: > 0 } && r.RetrievedDocumentRanking is { Length: > 0 }).ToList();
    if (checkable.Count == 0)
    {
        Console.WriteLine($"- {label}: n/a - rows carry no `ContextDocumentIds` / `RetrievedDocumentRanking` columns.");
        continue;
    }
    var differing = checkable.Where(r => !ContextOrderFollowsRanking(r)).Select(r => $"`{r.ScenarioName}`").ToList();
    Console.WriteLine($"- {label}: **{differing.Count}** of {checkable.Count} rows emit documents in an order that differs from the reranker order" +
                      (differing.Count == 0 ? ". Reordering would change nothing here." : ": " + string.Join(", ", differing)));
}
Console.WriteLine();

if (added.Count > 0)
{
    Console.WriteLine("## New in the newer run");
    Console.WriteLine();
    Console.WriteLine(string.Join(", ", added.OrderBy(r => r.ScenarioName).Select(r => $"`{r.ScenarioName}`")));
    Console.WriteLine();
}
if (dropped.Count > 0)
{
    Console.WriteLine("## Gone from the older run");
    Console.WriteLine();
    Console.WriteLine(string.Join(", ", dropped.OrderBy(r => r.ScenarioName).Select(r => $"`{r.ScenarioName}`")));
    Console.WriteLine();
}
return 0;

// ---------------------------------------------------------------------------------------------

static IEnumerable<(string Name, Func<EvalRowLite, double> Pick)> Metrics() =>
[
    ("Groundedness", r => r.Groundedness),
    ("Relevance", r => r.Relevance),
    ("Coherence", r => r.Coherence),
    ("Equivalence", r => r.Equivalence),
    ("Retrieval (judge)", r => r.Retrieval),
    ("CitationMatch", r => r.CitationMatch),
    ("MRR", r => r.ReciprocalRank),
    ("R@5", r => r.RecallAt5),
    ("R@50", r => r.RecallAt50),
    ("References (k)", r => r.ReferencesRetrieved),
    ("Chunks to synthesis", r => r.ChunksRetrieved),
    ("Context tokens", r => r.ContextTokens),
    ("Searches per question", r => r.SubQueryCount),
    ("Latency ms", r => r.LatencyMs),
];

// The metrics the per-row floor is reported on: the retrieval side and the judge side a
// judged-context change is read on. Latency/tokens/cost are means only (above), not floors.
static IEnumerable<(string Name, Func<EvalRowLite, double> Pick)> PerRowMetrics() =>
[
    ("CitationMatch", r => r.CitationMatch),
    ("FirstRelevantRank", r => r.FirstRelevantRank),
    ("R@5", r => r.RecallAt5),
    ("R@50", r => r.RecallAt50),
    ("References (k)", r => r.ReferencesRetrieved),
    ("Groundedness", r => r.Groundedness),
    ("Retrieval (judge)", r => r.Retrieval),
    ("Equivalence", r => r.Equivalence),
    ("Relevance", r => r.Relevance),
    ("Coherence", r => r.Coherence),
];

static HashSet<string> Ids(string? joined, char separator) =>
    (joined ?? "").Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Normalize(System.Text.NormalizationForm.FormC))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

// Same id comparison as RetrievalRankMetrics (NFC, case-insensitive). Expected ∪ equivalent
// documents that appear in the ranking, none of which appears among the judged blocks.
static bool RetrievedButNotInContext(EvalRowLite r)
{
    if (r.ContextDocumentIds is null) return false;
    var relevant = Ids(r.ExpectedSources, ';'); relevant.UnionWith(Ids(r.EquivalentSources, ';'));
    var ranking  = Ids(r.RetrievedDocumentRanking, '|');
    var context  = Ids(r.ContextDocumentIds, '|');
    var retrieved = relevant.Where(ranking.Contains).ToList();
    return retrieved.Count > 0 && !retrieved.Any(context.Contains);
}

// Order of first appearance of documents in the judged context (pre-4a: the service's return
// order of each document's best hit) versus in the reranker ranking, over the documents present
// in both. Equal = reordering the emission by reranker rank would not change this row.
static bool ContextOrderFollowsRanking(EvalRowLite r)
{
    static List<string> FirstAppearance(string? joined) =>
        (joined ?? "").Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Normalize(System.Text.NormalizationForm.FormC))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    var inContext = FirstAppearance(r.ContextDocumentIds);
    var inRanking = FirstAppearance(r.RetrievedDocumentRanking);
    var rankingSet = inRanking.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var contextSet = inContext.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var a = inContext.Where(rankingSet.Contains).ToList();
    var b = inRanking.Where(contextSet.Contains).ToList();
    return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
}

static double? Mean(IEnumerable<double> values)
{
    var scored = values.Where(v => v >= 0).ToList();
    return scored.Count == 0 ? null : scored.Average();
}

static string Fmt(double? v) => v is null ? "n/a" : v.Value.ToString(Math.Abs(v.Value) >= 100 ? "F0" : "F2", CultureInfo.InvariantCulture);
static string Delta(double? a, double? b) => a is null || b is null ? "n/a" : (b.Value - a.Value).ToString(Math.Abs(b.Value - a.Value) >= 100 ? "+0;-0;0" : "+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
static string Pair(double a, double b) => a == b ? Fmt(a) : $"{Fmt(a)} → **{Fmt(b)}**";

static DateOnly RunEnd(List<EvalRowLite> rows) =>
    DateOnly.FromDateTime(rows.Max(r => r.Timestamp).UtcDateTime);

static List<EvalRowLite> Load(string path)
{
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var rows = new List<EvalRowLite>();
    foreach (var line in File.ReadLines(path))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var row = JsonSerializer.Deserialize<EvalRowLite>(line, options);
        if (row is not null) rows.Add(row);
    }
    if (rows.Count == 0) throw new InvalidOperationException($"{path}: no rows");
    return rows;
}

static Dictionary<string, DateOnly> LoadRelabelDates(string datasetPath)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(datasetPath));
    var result = new Dictionary<string, DateOnly>();
    foreach (var q in doc.RootElement.EnumerateArray())
    {
        if (q.TryGetProperty("RelabelledOn", out var d) && d.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            result[q.GetProperty("Name").GetString()!] = date;
    }
    return result;
}

static string? FindDefaultDataset()
{
    // Walk up from the working directory to the repo root, then the known path.
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "src", "Evaluations", "RagApp.Evaluation.Tests", "testdata", "golden-questions.json");
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}

// Only the fields the comparison reads; unknown fields in the jsonl are ignored, so rows from
// before or after this file's schema still load.
sealed record EvalRowLite(
    string ScenarioName, string Type, string Capability, bool Succeeded, DateTimeOffset Timestamp,
    double Groundedness, double Relevance, double Coherence, double Equivalence, double Retrieval,
    double CitationMatch, double ReciprocalRank, double RecallAt5, double RecallAt50,
    int FirstRelevantRank, int ReferencesRetrieved, int ChunksRetrieved, long ContextTokens,
    int SubQueryCount, long LatencyMs, double RefusalScore, string? RelabelledOn,
    // For the deterministic "retrieved but absent from the judged context" count. The first
    // three exist since 2026-09-23 (steps 1 and 3); ContextDocumentIds is proposed in D228 step
    // 3b and null until a run carries it.
    string? ExpectedSources, string? EquivalentSources, string? RetrievedDocumentRanking, string? ContextDocumentIds);
