namespace AgenticRagApp.Indexing.Csv.Models;

public class IndexRecord
{
    public string DocumentId        { get; set; } = "";
    public string DocumentTypeName  { get; set; } = "";
    public string Summary           { get; set; } = "";
    public string Version           { get; set; } = "";
    public string CheckDateRaw      { get; set; } = "";
    public string AttentionFlagsRaw { get; set; } = "";
    public bool   Active             { get; set; }
}
