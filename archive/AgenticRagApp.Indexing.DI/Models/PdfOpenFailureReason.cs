using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.DI.Models;

// Structured category for a file-level PDF open/parse failure, set by
// PdfDocumentValidator.TryOpenAndValidate. Lets PdfQualityGateResult break down
// "how many files failed" by cause instead of grepping free-text messages.
public sealed record PdfOpenFailureReason(string Code) : OpenFailureReasonBase(Code)
{
    public static readonly PdfOpenFailureReason Unknown = new(nameof(Unknown)); // unexpected exception PdfPig doesn't have a dedicated type for
    public static readonly PdfOpenFailureReason Encrypted = new(nameof(Encrypted)); // PdfDocumentEncryptedException - password-protected/unsupported encryption
    public static readonly PdfOpenFailureReason MalformedFormat = new(nameof(MalformedFormat)); // PdfDocumentFormatException - corrupt header, broken xref, malformed objects
    public static readonly PdfOpenFailureReason EmptyDocument = new(nameof(EmptyDocument)); // opened fine but has zero pages
    public static readonly PdfOpenFailureReason NoReadablePages = new(nameof(NoReadablePages)); // opened fine but every page failed extraction
    public static readonly PdfOpenFailureReason EmptyFile = new(nameof(EmptyFile)); // 0-byte input - never reaches PdfPig (PdfDocumentValidator)
    public static readonly PdfOpenFailureReason TooLarge = new(nameof(TooLarge)); // exceeds Document Intelligence's max document size (PdfDocumentValidator)
    public static readonly PdfOpenFailureReason TooManyPages = new(nameof(TooManyPages)); // exceeds Document Intelligence's max pages per analyze call (PdfDocumentValidator)
    public static readonly PdfOpenFailureReason Throttled = new(nameof(Throttled)); // Document Intelligence returned 429 and retries were exhausted
    public static readonly PdfOpenFailureReason DiServiceError = new(nameof(DiServiceError)); // Document Intelligence returned a non-429 request failure
    public static readonly PdfOpenFailureReason UnexpectedContentFormat = new(nameof(UnexpectedContentFormat)); // DI returned Text instead of the requested Markdown - offsets would be untrustworthy
    public static readonly PdfOpenFailureReason MissingAnalysisResult = new(nameof(MissingAnalysisResult)); // AnalyzeOutcome.Ok was true but Result was null - an internal bug, not a DI failure
    public static readonly PdfOpenFailureReason TruncatedPages = new(nameof(TruncatedPages)); // DI returned a different page count than the native PDF has - partial/truncated analysis
}
