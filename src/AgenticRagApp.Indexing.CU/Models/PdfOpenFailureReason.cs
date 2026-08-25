using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.CU.Models;

// Structured category for a file-level extraction failure. Lets a run report break down "how
// many files failed" by cause instead of grepping free-text messages.
//
// Several members below were set by the local PdfPig preflight that ran before every analyze
// call (Encrypted, MalformedFormat, EmptyFile, TooLarge, TooManyPages). That preflight is gone -
// documents go straight to the service now, and a file it cannot read comes back as a service
// error rather than a locally-diagnosed one. They are kept rather than deleted because they are
// serialized into stored reports and because a future preflight, or a richer mapping of the
// service's own error codes, would want exactly these categories back.
public sealed record PdfOpenFailureReason(string Code) : OpenFailureReasonBase(Code)
{
    public static readonly PdfOpenFailureReason Unknown = new(nameof(Unknown)); // unexpected exception with no more specific category
    public static readonly PdfOpenFailureReason Encrypted = new(nameof(Encrypted)); // password-protected/unsupported encryption (no longer detected locally)
    public static readonly PdfOpenFailureReason MalformedFormat = new(nameof(MalformedFormat)); // corrupt header, broken xref, malformed objects (no longer detected locally)
    public static readonly PdfOpenFailureReason EmptyDocument = new(nameof(EmptyDocument)); // analyzed fine but produced no markdown or no pages
    public static readonly PdfOpenFailureReason NoReadablePages = new(nameof(NoReadablePages)); // analyzed fine but every page failed extraction
    public static readonly PdfOpenFailureReason EmptyFile = new(nameof(EmptyFile)); // 0-byte input
    public static readonly PdfOpenFailureReason TooLarge = new(nameof(TooLarge)); // exceeds the service's max document size
    public static readonly PdfOpenFailureReason TooManyPages = new(nameof(TooManyPages)); // exceeds the service's max pages per analyze call (300 for async Content Understanding)
    public static readonly PdfOpenFailureReason Throttled = new(nameof(Throttled)); // the service returned 429 and poll retries were exhausted
    public static readonly PdfOpenFailureReason DiServiceError = new(nameof(DiServiceError)); // the service returned a non-429 request failure
    public static readonly PdfOpenFailureReason UnexpectedContentFormat = new(nameof(UnexpectedContentFormat)); // wrong string encoding, or YAML front matter - offsets/content would be untrustworthy
    public static readonly PdfOpenFailureReason MissingAnalysisResult = new(nameof(MissingAnalysisResult)); // the analyze call succeeded but carried no result - an internal bug, not a service failure
    public static readonly PdfOpenFailureReason TruncatedPages = new(nameof(TruncatedPages)); // the markdown could not be mapped onto the pages the service reported
}
