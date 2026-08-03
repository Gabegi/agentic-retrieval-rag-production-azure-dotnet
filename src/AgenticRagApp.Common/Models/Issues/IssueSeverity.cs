using System.Text.Json.Serialization;

namespace AgenticRagApp.Common.Models;

// Severity is a value, not a type. It used to be encoded two ways at once - in the type
// name (CleaningError vs CleaningWarning, which were otherwise byte-for-byte identical)
// and in a "Error"/"Warning" string on ValidationIssue. Neither survived contact with
// reality: JoinResult stored JoinError instances in its warnings bucket, and the string
// form had no compiler check at all.
public enum IssueSeverity
{
    [JsonStringEnumMemberName("Error")]   Error,
    [JsonStringEnumMemberName("Warning")] Warning,
}
