using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Models;

// Structured category for a file-level CSV open/parse failure. Not wired into
// PipelineIssue.Reason yet - CsvExtractor.EnsureHeadersAreCorrect currently reports
// these as free-text InvalidOperationExceptions - but gives CSV the same structured-
// reason shape PDF uses (PdfOpenFailureReason), for whichever caller wants to break
// failures down by cause instead of grepping messages.
public sealed record CsvOpenFailureReason(string Code) : OpenFailureReasonBase(Code)
{
    public static readonly CsvOpenFailureReason Unknown = new(nameof(Unknown)); // unexpected exception with no dedicated category below
    public static readonly CsvOpenFailureReason WrongEncoding = new(nameof(WrongEncoding)); // not UTF-8 (CsvExtractor.EnsureHeadersAreCorrect)
    public static readonly CsvOpenFailureReason WrongDelimiter = new(nameof(WrongDelimiter)); // header parsed as a single column - likely wrong delimiter
    public static readonly CsvOpenFailureReason DuplicateHeader = new(nameof(DuplicateHeader)); // two columns share the same name
    public static readonly CsvOpenFailureReason MissingRequiredHeader = new(nameof(MissingRequiredHeader)); // a required column is absent
    public static readonly CsvOpenFailureReason UnreadableRow = new(nameof(UnreadableRow)); // csv.Read() threw - row too broken to tokenize
}
