namespace AgenticRagApp.Indexing.Csv.Models;

public class CleanedPageRecord
{
    public string        DocumentId       { get; set; } = "";
    public string        Title            { get; set; } = "";
    public string        QuickCode        { get; set; } = "";
    public string        FolderPath       { get; set; } = "";
    public DateTime      LastModified     { get; set; }
    public int           PageIndex        { get; set; }
    public string        PageContent      { get; set; } = "";
    public string        Language         { get; set; } = "";
    public string        RelativePath     { get; set; } = "";

    public string        DocumentTypeName { get; set; } = "";
    public string        Summary          { get; set; } = "";
    public string        Version          { get; set; } = "";
    public DateTime?     CheckDate        { get; set; }
    public List<string>  AttentionFlags   { get; set; } = [];
}
