#:project C:/Users/gabriel.pirastru/Desktop/code/contoso/cap.lz.app/src/AgenticRagApp.Indexing.CU/AgenticRagApp.Indexing.CU.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property NoWarn=IL2026;IL3050

using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Indexing.CU.Utils;

// Per-chunk-id diff of a run's artifact against the CURRENT route-1 strategy (D224, 2026-09-23).
//
// What it answers: "if the chunker as it is in this working tree re-cut the documents of run X,
// which chunk ids would change content, appear, or disappear?" - the blast radius of whatever
// chunking change is in the tree, counted in the unit the index and the vector cache are keyed
// on. Chunk ids are (document, section, child) and CHILD IS ORDINAL: any change to a section's
// piece count renames every later piece in it. That is why this counts ids, not chunks.
//
// First uses: A2's churn (2,707 same-id changes + 708 deletions for 672 pieces - rejected on it),
// and the A3 precondition: after the forced run, diff A4's prediction against actual chunks and
// isolate the 76 documents the ladder replay could not explain before the A3 replay is read.
//
// Inputs (run folders downloaded from the pipeline; see reports\MAP.md for the layout):
//   args[0]  run folder holding *extraction-artifact*.json and *chunking-artifact*.json
//   args[1]  optional CSV path for the per-section rows (default: chunk-id-diff.csv)
//   args[2]  optional BASELINE run folder. The forced run re-extracts every document, so any
//            change in Content Understanding's markdown against the baseline shows up in the
//            id diff as if it were a chunking change. With a baseline given, each document's
//            markdown (extraction artifact Content) is hashed in both runs, every row carries
//            MarkdownIdentical, and the totals are split into documents whose markdown is
//            identical - where the diff IS chunking - and documents that drifted.
//
// Route and sector tag per document are read off the chunking artifact (metadata.route_name,
// the "[TAG]" on the prefix's title line), so the replay prices the same prefix the run did.
// Only route-1 documents are replayed; route-2 rows are listed but not diffed.
//
// Kept filters are the service's own public rules, in the service's order: residue
// (PageMarkup.StripFurniture), heading-only with its sibling guard (ChunkingService
// .IsDroppableHeadingOnly, live since D224 A5), TOC (TocFilter). Note the residue floor (4) is
// repeated here as a literal - ChunkingService.MinChunkAlphanumericChars is private.
//
// Run:  dotnet run src/Tools/ChunkIdDiff.cs -- <run folder> [out.csv]

string dir    = args[0];
string outCsv = args.Length > 1 ? args[1] : "chunk-id-diff.csv";
string? baseDir = args.Length > 2 ? args[2] : null;
string chunking   = Directory.GetFiles(dir, "*chunking-artifact*.json")[0];
string extraction = Directory.GetFiles(dir, "*extraction-artifact*.json")[0];

// Baseline markdown hashes, one per document, or null when no baseline was given.
Dictionary<string, string>? baseHash = null;
if (baseDir is not null)
{
    baseHash = new(StringComparer.Ordinal);
    using var bfs = File.OpenRead(Directory.GetFiles(baseDir, "*extraction-artifact*.json")[0]);
    using var bjd = JsonDocument.Parse(bfs, new JsonDocumentOptions { MaxDepth = 64 });
    foreach (var el in bjd.RootElement.GetProperty("Docs").EnumerateArray())
        baseHash[el.GetProperty("SourceId").GetString()!] = Sha(el.GetProperty("Content").GetString() ?? "");
    Console.WriteLine($"baseline: {baseHash.Count} documents hashed from {baseDir}");
}

var tagRx = new Regex(@" \[([^\]]+)\]$");
var art   = new Dictionary<(string Doc, int Sec), SortedDictionary<int, string>>();
var route = new Dictionary<string, string>(StringComparer.Ordinal);
var tag   = new Dictionary<string, string?>(StringComparer.Ordinal);

using (var fs = File.OpenRead(chunking))
using (var jd = JsonDocument.Parse(fs, new JsonDocumentOptions { MaxDepth = 64 }))
{
    foreach (var c in jd.RootElement.GetProperty("Chunks").EnumerateArray())
    {
        var md  = c.GetProperty("metadata");
        var doc = md.GetProperty("document_id").GetString()!;
        var key = (doc, c.GetProperty("section_index").GetInt32());
        if (!art.TryGetValue(key, out var d)) art[key] = d = new();
        d[c.GetProperty("child_index").GetInt32()] = c.GetProperty("content").GetString()!;
        route.TryAdd(doc, md.GetProperty("route_name").GetString() ?? "");
        if (!tag.ContainsKey(doc))
        {
            var first = (md.GetProperty("prefix").GetString() ?? "").Split('\n')[0];
            var m = tagRx.Match(first);
            tag[doc] = m.Success ? m.Groups[1].Value : null;
        }
    }
}
Console.WriteLine($"artifact: {art.Count} sections, {route.Count} documents");

var strategy = new DeclaredBoundaryStrategy();
var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

int docs = 0, sections = 0, sectionsChanged = 0, drifted = 0;
long same = 0, added = 0, removed = 0, removedResidue = 0;
// Same totals, for the documents whose markdown DRIFTED against the baseline.
long dSame = 0, dAdded = 0, dRemoved = 0, dSections = 0;
using var sw = new StreamWriter(outCsv);
sw.WriteLine("Doc,Section,OldPieces,NewPieces,ContentChangedSameId,IdsAdded,IdsRemoved,RemovedAreResidue,MarkdownIdentical");

using var fs2  = File.OpenRead(extraction);
using var jdoc = JsonDocument.Parse(fs2, new JsonDocumentOptions { MaxDepth = 64 });
foreach (var el in jdoc.RootElement.GetProperty("Docs").EnumerateArray())
{
    var doc = el.Deserialize<PdfExtractionDocument>(opts)!;
    if (string.IsNullOrWhiteSpace(doc.Content)) continue;
    if (route.GetValueOrDefault(doc.SourceId) != RouteNames.DeclaredBoundary) continue;
    docs++;

    // "" = no baseline; otherwise True/False per document.
    var identical = baseHash is null ? ""
        : (baseHash.TryGetValue(doc.SourceId, out var bh) && bh == Sha(doc.Content)) ? "True" : "False";
    if (identical == "False") drifted++;

    var located = HeadingLocator.Locate(doc.Content, doc.Headings ?? [], doc.PageSpans ?? [], doc.Sections);
    var t = tag.GetValueOrDefault(doc.SourceId);
    var replayed = doc with
    {
        LocatedSections = located.Sections,
        Family = t is null ? null : new DocumentFamily("replay", t, []),
    };

    // The three drop rules in the service's order: residue, TOC, then heading-only with its
    // sibling guard judged on what survived the first two (D224 A5). Same public rules the
    // service applies, so a change there is a change here. The order matters: judged before the
    // TOC rule, 80 "## Inhoudsopgave" headings on 260922/2 had TOC-table siblings, were dropped
    // for it, and then lost those siblings to the TOC rule - gone after all.
    var afterToc = (await strategy.ChunkDocumentAsync(replayed))
        .Where(c => PageMarkup.StripFurniture(c.Content).Count(char.IsLetterOrDigit) >= 4)
        .Where(c => !TocFilter.IsTableOfContents(c))
        .ToList();
    var now = afterToc
        .Where(c => !ChunkingService.IsDroppableHeadingOnly(c, afterToc))
        .GroupBy(c => c.SectionIndex)
        .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.ChildIndex, c => c.Content));

    var secIds = new HashSet<int>(now.Keys);
    foreach (var k in art.Keys.Where(k => k.Doc == doc.SourceId)) secIds.Add(k.Sec);

    foreach (var sec in secIds.OrderBy(x => x))
    {
        sections++;
        art.TryGetValue((doc.SourceId, sec), out var oldPieces);
        now.TryGetValue(sec, out var newPieces);
        var o = oldPieces ?? new SortedDictionary<int, string>();
        var n = newPieces ?? new Dictionary<int, string>();

        int ch = 0, ad = 0, rm = 0, rmRes = 0;
        foreach (var id in o.Keys.Union(n.Keys))
        {
            var hasO = o.TryGetValue(id, out var ot); var hasN = n.TryGetValue(id, out var nt);
            if (hasO && hasN) { if (!string.Equals(ot, nt, StringComparison.Ordinal)) ch++; }
            else if (hasN) ad++;
            else { rm++; if (PageMarkup.StripFurniture(ot!).Count(char.IsLetterOrDigit) < 4) rmRes++; }
        }
        if (ch + ad + rm > 0)
        {
            sectionsChanged++;
            sw.WriteLine($"{doc.SourceId},{sec},{o.Count},{n.Count},{ch},{ad},{rm},{rmRes},{identical}");
            if (identical == "False") { dSections++; dSame += ch; dAdded += ad; dRemoved += rm; }
        }
        same += ch; added += ad; removed += rm; removedResidue += rmRes;
    }
}

Console.WriteLine($"route-1 documents replayed {docs}; sections {sections}; sections with any id change {sectionsChanged}");
Console.WriteLine($"content changed at the same id {same} | ids added {added} | ids removed {removed} (of which residue under the current rule {removedResidue})");
if (baseHash is not null)
{
    Console.WriteLine($"markdown drifted against the baseline: {drifted} of {docs} route-1 documents");
    Console.WriteLine($"  in DRIFTED documents:   sections changed {dSections} | same-id changes {dSame} | ids added {dAdded} | ids removed {dRemoved}");
    Console.WriteLine($"  in IDENTICAL documents: sections changed {sectionsChanged - dSections} | same-id changes {same - dSame} | ids added {added - dAdded} | ids removed {removed - dRemoved}  <- attributable to chunking");
}
Console.WriteLine($"rows -> {outCsv}");

static string Sha(string s) =>
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));
