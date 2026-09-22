#:project C:/Users/gabriel.pirastru/Desktop/code/contoso/cap.lz.app/src/AgenticRagApp.Indexing.CU/AgenticRagApp.Indexing.CU.csproj

using System.Text.Json;
using AgenticRagApp.Indexing.CU.Utils;

// Measures the chunking gate's two clauses over the 260921/1 corpus, using the SAME token
// counter the gate uses (TokenCounter -> cl100k_base). Emits one CSV row per document.

string dir = args.Length > 0 ? args[0] : @"C:\Users\gabriel.pirastru\Desktop\reports\9\260921\1";  // the run folder to measure
string extraction = Directory.GetFiles(dir, "*extraction-artifact*.json")[0];
string chunking   = Directory.GetFiles(dir, "*chunking-artifact*.json")[0];
string outCsv     = args.Length > 1 ? args[1] : "gate-measure.csv";

// ── chunking artifact: per-document outcome (streamed; the file is ~270 MB) ──
var chunk = new Dictionary<string, (string Strategy, int ChunkCount, int Sections, int P50, int P99, int Headings, string Outcome, double TableShare, bool TableShaped)>();
{
    using var fs = File.OpenRead(chunking);
    using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions { MaxDepth = 64 });
    foreach (var d in doc.RootElement.GetProperty("Documents").EnumerateArray())
    {
        string id = d.GetProperty("SourceId").GetString()!;
        chunk[id] = (
            Get(d, "Strategy")?.GetString() ?? "",
            GetInt(d, "ChunkCount"), GetInt(d, "SectionCount"),
            GetInt(d, "TokenP50"), GetInt(d, "TokenP99"), GetInt(d, "HeadingCount"),
            Get(d, "Outcome")?.GetString() ?? "",
            GetDouble(d, "TableCharShare"),
            Get(d, "IsTableShaped") is { ValueKind: JsonValueKind.True });
    }
}

// ── extraction artifact: content + headings, the gate's two real inputs ──
using var sw = new StreamWriter(outCsv);
sw.WriteLine("SourceId,Chars,Headings,Tokens,Density,ClauseA,ClauseB,PredictedRoute,ActualStrategy,ChunkCount,SectionCount,TokenP50,TokenP99,ChunkHeadings,TableShare,TableShaped,Outcome");

int n = 0;
{
    using var fs = File.OpenRead(extraction);
    using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions { MaxDepth = 64 });
    foreach (var d in doc.RootElement.GetProperty("Docs").EnumerateArray())
    {
        string id = d.GetProperty("SourceId").GetString()!;
        string content = Get(d, "Content")?.GetString() ?? "";
        int headings = Get(d, "Headings") is { ValueKind: JsonValueKind.Array } h ? h.GetArrayLength() : 0;
        int tokens = TokenCounter.Count(content);
        double density = content.Length == 0 ? 0 : headings / (content.Length / 1000.0);

        bool a = headings >= 2 && density >= 0.1;
        bool b = tokens < 4000 && headings >= 1;

        chunk.TryGetValue(id, out var c);
        sw.WriteLine($"{id},{content.Length},{headings},{tokens},{density.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)},{a},{b}," +
                     $"{(a || b ? "DeclaredBoundary" : "Recursive")},{c.Strategy},{c.ChunkCount},{c.Sections},{c.P50},{c.P99},{c.Headings}," +
                     $"{c.TableShare.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},{c.TableShaped},{c.Outcome}");
        n++;
    }
}
Console.WriteLine($"{n} documents -> {outCsv}");

static JsonElement? Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
static int GetInt(JsonElement e, string name) => Get(e, name) is { } v && v.TryGetInt32(out var i) ? i : 0;
static double GetDouble(JsonElement e, string name) => Get(e, name) is { } v && v.TryGetDouble(out var x) ? x : 0;
