#:project C:/Users/gabriel.pirastru/Desktop/code/contoso/cap.lz.app/src/AgenticRagApp.Indexing.CU/AgenticRagApp.Indexing.CU.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Text.Json;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Indexing.CU.Utils;

// The counterfactual the gate's second clause cannot be argued out of: for every document the
// 4,000-token clause ADMITS (1 heading, fails the density clause), run BOTH routes over the
// real extracted content and compare what each produces.

string dir = args.Length > 0 ? args[0] : @"C:\Users\gabriel.pirastru\Desktop\reports\9\260921\1";  // the run folder to measure
string extraction = Directory.GetFiles(dir, "*extraction-artifact*.json")[0];
string outCsv = args.Length > 1 ? args[1] : "route-replay.csv";

var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var declared = new DeclaredBoundaryStrategy();
var recursive = new RecursiveStrategy();

using var sw = new StreamWriter(outCsv);
sw.WriteLine("SourceId,Tokens,Headings,Density,Sections,DeclChunks,RecChunks,DeclP50,RecP50,DeclMax,RecMax,DeclOver512,RecOver512,DeclHardCut,RecHardCut,IdenticalText");

using var fs = File.OpenRead(extraction);
using var jdoc = JsonDocument.Parse(fs, new JsonDocumentOptions { MaxDepth = 64 });

int considered = 0, failed = 0;
foreach (var el in jdoc.RootElement.GetProperty("Docs").EnumerateArray())
{
    PdfExtractionDocument doc;
    try { doc = el.Deserialize<PdfExtractionDocument>(opts)!; }
    catch (Exception ex) { failed++; if (failed <= 2) Console.WriteLine("deserialize failed: " + ex.Message); continue; }

    int headings = doc.Headings?.Count ?? 0;
    int tokens = TokenCounter.Count(doc.Content);
    double density = doc.Content.Length == 0 ? 0 : headings / (doc.Content.Length / 1000.0);

    bool clauseA = headings >= 2 && density >= 0.1;
    bool clauseB = tokens < 4000 && headings >= 1;
    if (clauseA || !clauseB) continue;   // only the documents the 4k clause alone admits
    considered++;

    var located = HeadingLocator.Locate(doc.Content, doc.Headings ?? [], doc.PageSpans ?? [], doc.Sections);
    var withSections = doc with { LocatedSections = located.Sections };

    var d = Keep(await declared.ChunkDocumentAsync(withSections));
    var r = Keep(await recursive.ChunkDocumentAsync(doc));

    var dt = d.Select(c => TokenCounter.Count(c.Content)).OrderBy(x => x).ToList();
    var rt = r.Select(c => TokenCounter.Count(c.Content)).OrderBy(x => x).ToList();
    bool same = d.Count == r.Count && d.Select(c => c.Content).SequenceEqual(r.Select(c => c.Content));

    sw.WriteLine($"{doc.SourceId},{tokens},{headings},{density:F4},{located.Sections.Count}," +
                 $"{d.Count},{r.Count},{P50(dt)},{P50(rt)},{(dt.Count > 0 ? dt[^1] : 0)},{(rt.Count > 0 ? rt[^1] : 0)}," +
                 $"{dt.Count(t => t > 512)},{rt.Count(t => t > 512)}," +
                 $"{d.Count(c => c.BoundaryLevel == BoundaryLevel.HardCut)},{r.Count(c => c.BoundaryLevel == BoundaryLevel.HardCut)},{same}");
}

Console.WriteLine($"{considered} clause-B-only documents replayed on both routes -> {outCsv} ({failed} deserialize failures)");

static List<ChunkObject> Keep(IReadOnlyList<ChunkObject> chunks) =>
    chunks.Where(c => c.Content.Count(char.IsLetterOrDigit) >= 4)
          .Where(c => !AgenticRagApp.Indexing.CU.Services.TocFilter.IsTableOfContents(c))
          .ToList();

static int P50(List<int> xs) => xs.Count == 0 ? 0 : xs[xs.Count / 2];
