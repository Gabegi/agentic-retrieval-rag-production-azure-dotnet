namespace AgenticRagApp.Indexing.Csv.Models;

public class JoinedPageRecord
{
    // from PageRecord
    public string DocumentId      { get; set; } = "";
    public string Title           { get; set; } = "";
    public string QuickCode       { get; set; } = "";
    public string FolderPath      { get; set; } = "";
    public string LastModifiedRaw { get; set; } = "";
    public int    PageIndex       { get; set; }
    public string PageContent     { get; set; } = "";
    public string Language        { get; set; } = "";
    public string RelativePath    { get; set; } = "";

    // from IndexRecord
    public string DocumentTypeName  { get; set; } = "";
    public string Summary           { get; set; } = "";
    public string Version           { get; set; } = "";
    public string CheckDateRaw      { get; set; } = "";
    public string AttentionFlagsRaw { get; set; } = "";
}
