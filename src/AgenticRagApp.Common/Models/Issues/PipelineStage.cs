using System.Text.Json.Serialization;

namespace AgenticRagApp.Common.Models;

// Which pipeline step produced an issue.
//
// These were free-text strings until now, which drifted exactly as you would expect:
// ValidationIssue's own comment documented four values ("Parse:Pages", "Parse:Index",
// "Join", "Clean") while the code had grown to emit eight. The JsonStringEnumMemberName
// attributes preserve the original wire format, so existing reports stay readable and
// the stage names in a report don't change shape just because the C# side got typed.
public enum PipelineStage
{
    [JsonStringEnumMemberName("Parse:Pages")]    ParsePages,
    [JsonStringEnumMemberName("Parse:Index")]    ParseIndex,
    [JsonStringEnumMemberName("Join")]           Join,
    [JsonStringEnumMemberName("Clean")]          Clean,
    [JsonStringEnumMemberName("TextQuality")]    TextQuality,
    [JsonStringEnumMemberName("TableStructure")] TableStructure,
    [JsonStringEnumMemberName("Metadata")]       Metadata,
    [JsonStringEnumMemberName("Validation")]     Validation,
}
