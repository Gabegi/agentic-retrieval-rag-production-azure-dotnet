using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Local, free structural gates run before/around opening a PDF, ahead of any paid
// Document Intelligence call. IsPDFValid is the entry point: pre-open size check
// (too-large/empty), then open/validate (encrypted/corrupt/malformed), then post-open
// page count (zero/too-many-pages) - in that order, cheapest first, so a too-large file
// never gets opened and an unopenable file never gets page-counted. The size/page-count
// limits are Document Intelligence's own hard limits, fixed at the service level
// regardless of pricing tier - see
// https://learn.microsoft.com/azure/ai-services/document-intelligence/service-limits
// ("Adjustable: No" for both max document size and max pages, even on Standard S0).
// Knows nothing about metadata - that's PdfNativeMetadataExtractor's job entirely.
public static class PdfDocumentValidator
{
    public const long MaxBytes = 500L * 1024 * 1024; // DI hard limit, all paid tiers
    public const int  MaxPages = 2000;                // DI hard limit per analyze call, all paid tiers

    // Soft-warning thresholds - predict a future hard failure or flag a likely-junk file,
    // without rejecting anything themselves.
    private const double NearLimitFraction     = 0.8;          // 80% of MaxBytes/MaxPages
    private const long   MinReasonableBytes    = 10 * 1024;    // below this, likely a scan-of-nothing/placeholder
    private const double MinRecommendedVersion = 1.4;          // older PDF spec versions correlate with extraction trouble

    // All three checks Document Intelligence needs before a paid analyze call, in the
    // right order. Returns the opened, page-count-validated PdfDocument on success so the
    // caller can read metadata/bookmarks from it - the caller owns disposing it from
    // there; on failure (including a page-count rejection after a successful open) this
    // disposes it internally, since pdf is null and the caller never gets a handle to it.
    //
    // diagnostics mirrors every hard failure into its own Errors list (same PipelineIssue
    // instance as the out error above) so "which step failed" is answerable the same way
    // for every pipeline step - see PdfStepDiagnostics's own doc comment for why this must
    // never also feed the validation gate.
    public static bool IsPDFValid(
        byte[] pdfBytes, string blobName, ILogger logger,
        [NotNullWhen(true)]  out PdfDocument?    pdf,
        [NotNullWhen(false)] out PipelineIssue? error,
        out PdfStepDiagnostics diagnostics)
    {
        pdf = null;
        var warnings = new List<PipelineIssue>();

        if (!IsPDFSizeOkForDI(pdfBytes, blobName, logger, warnings, out error))
        {
            diagnostics = new PdfStepDiagnostics(warnings, [error]);
            return false;
        }

        if (!PdfOpensAndValidates(pdfBytes, blobName, logger, warnings, out pdf, out error))
        {
            diagnostics = new PdfStepDiagnostics(warnings, [error]);
            return false;
        }

        if (!IsPDFPageCountOkForDI(pdf, blobName, logger, warnings, out error))
        {
            pdf.Dispose();
            pdf = null;
            diagnostics = new PdfStepDiagnostics(warnings, [error]);
            return false;
        }

        diagnostics = new PdfStepDiagnostics(warnings, []);
        return true;
    }

    // Opens the raw bytes with PdfPig and structurally validates the document. Exception
    // types caught here are PdfPig's own (confirmed via reflection against the referenced
    // 0.1.9 build, not just docs) - anything else falls through to the generic catch and
    // is reported as Unknown rather than mislabeled as a specific cause.
    public static bool PdfOpensAndValidates(
        byte[] pdfBytes, string blobName, ILogger logger, List<PipelineIssue> warnings,
        [NotNullWhen(true)]  out PdfDocument?    pdf,
        [NotNullWhen(false)] out PipelineIssue? error)
    {
        PdfDocument? opened = null;
        try
        {
            // Open document
            //  PdfPig parses the byte stream: reads the PDF header, cross-reference table/trailer, decodes the document catalog and page tree, etc.
            opened = PdfDocument.Open(pdfBytes);

            // LogDebug, not LogInformation - the IsEnabled guard was checking Debug while
            // logging at Information, so this fired on every successful open whenever
            // Information logging was on (the normal case), the exact per-file log volume
            // the guard looked like it was meant to prevent.
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(
                    "Opened PDF '{Blob}': {Pages} page(s), version {Version}",
                    blobName, opened.NumberOfPages, opened.Version);

            if (opened.Version < MinRecommendedVersion)
                warnings.Add(PipelineIssue.Warning(
                    PipelineStage.Validation,
                    blobName,
                    $"PDF spec version {opened.Version} is older than {MinRecommendedVersion} - older PDFs correlate with extraction trouble."));

            pdf   = opened;
            error = null;
            return true;
        }
        // Password-protected / unsupported-encryption PDFs. PdfPig can't recover
        // content here at all - the caller needs the actual password, so this is
        // worth telling apart from a plain corrupt file.
        catch (PdfDocumentEncryptedException ex)
        {
            opened?.Dispose();
            pdf   = null;
            error = OpenError(blobName, PdfOpenFailureReason.Encrypted, $"PDF is encrypted: {ex.Message}");
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogWarning(ex, "PDF '{Blob}' is encrypted and could not be opened.", blobName);
            return false;
        }
        // Structurally broken PDF: corrupt header, broken xref/trailer, malformed
        // object streams, truncated file, etc.
        catch (PdfDocumentFormatException ex)
        {
            opened?.Dispose();
            pdf   = null;
            error = OpenError(blobName, PdfOpenFailureReason.MalformedFormat, $"PDF structure is malformed: {ex.Message}");
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogWarning(ex, "PDF '{Blob}' has a malformed/corrupt structure.", blobName);
            return false;
        }
        // Anything else PdfPig (or the runtime) threw while opening/inspecting the
        // document - not confidently attributable to a specific cause above.
        catch (Exception ex)
        {
            opened?.Dispose();
            pdf   = null;
            error = OpenError(blobName, PdfOpenFailureReason.Unknown, $"Not a parseable PDF: {ex.Message}");
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogWarning(ex, "PDF '{Blob}' failed to open for an unrecognized reason.", blobName);
            return false;
        }
    }

    // Errors/warnings are always written to the diagnostics object regardless of
    // environment (that's what feeds PdfQualityGateResult); the logger.IsEnabled(Debug)
    // gate below controls only whether this step also emits a log line - development-only,
    // same pattern as the content-hash debug log in PdfExtractionPipeline, so per-file
    // rejection detail doesn't add to Production log volume/cost.
    private static bool IsPDFSizeOkForDI(byte[] pdfBytes, string blobName, ILogger logger, List<PipelineIssue> warnings, [NotNullWhen(false)] out PipelineIssue? error)
    {
        if (pdfBytes.Length == 0)
        {
            error = PipelineIssue.Error(PipelineStage.Validation, blobName, "Empty file (0 bytes).", reason: PdfOpenFailureReason.EmptyFile);
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogWarning("PDF '{Blob}' rejected: empty file (0 bytes).", blobName);
            return false;
        }

        if (pdfBytes.Length > MaxBytes)
        {
            error = PipelineIssue.Error(
                PipelineStage.Validation,
                blobName,
                $"File is {pdfBytes.Length / 1024.0 / 1024.0:F1} MB, exceeds the {MaxBytes / 1024 / 1024} MB Document Intelligence limit.",
                reason: PdfOpenFailureReason.TooLarge);
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogWarning(
                    "PDF '{Blob}' rejected: {SizeMb:F1} MB exceeds the {MaxMb} MB Document Intelligence limit.",
                    blobName, pdfBytes.Length / 1024.0 / 1024.0, MaxBytes / 1024 / 1024);
            return false;
        }

        if (pdfBytes.Length < MinReasonableBytes)
        {
            warnings.Add(PipelineIssue.Warning(
                PipelineStage.Validation,
                blobName,
                $"File is only {pdfBytes.Length} byte(s) - often a scan-of-nothing or placeholder."));
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogWarning("PDF '{Blob}' is only {Bytes} byte(s) - likely a scan-of-nothing or placeholder.", blobName, pdfBytes.Length);
        }
        else if (pdfBytes.Length > MaxBytes * NearLimitFraction)
        {
            warnings.Add(PipelineIssue.Warning(
                PipelineStage.Validation,
                blobName,
                $"File is {pdfBytes.Length / 1024.0 / 1024.0:F1} MB, over {NearLimitFraction:P0} of the {MaxBytes / 1024 / 1024} MB Document Intelligence limit."));
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogWarning(
                    "PDF '{Blob}' is {SizeMb:F1} MB, over {Fraction:P0} of the {MaxMb} MB Document Intelligence limit.",
                    blobName, pdfBytes.Length / 1024.0 / 1024.0, NearLimitFraction, MaxBytes / 1024 / 1024);
        }

        error = null;
        return true;
    }

    private static PipelineIssue OpenError(string blobName, PdfOpenFailureReason reason, string message) =>
        PipelineIssue.Error(PipelineStage.Validation, blobName, message, reason: reason);

    private static bool IsPDFPageCountOkForDI(PdfDocument pdf, string blobName, ILogger logger, List<PipelineIssue> warnings, [NotNullWhen(false)] out PipelineIssue? error)
    {
        if (pdf.NumberOfPages == 0)
        {
            error = PipelineIssue.Error(PipelineStage.Validation, blobName, "PDF contains zero pages.", reason: PdfOpenFailureReason.EmptyDocument);
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogWarning("PDF '{Blob}' rejected: contains zero pages.", blobName);
            return false;
        }

        if (pdf.NumberOfPages > MaxPages)
        {
            error = PipelineIssue.Error(
                PipelineStage.Validation,
                blobName,
                $"{pdf.NumberOfPages} pages exceeds the {MaxPages}-page Document Intelligence limit per analyze call; split before submitting.",
                reason: PdfOpenFailureReason.TooManyPages);
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogWarning(
                    "PDF '{Blob}' rejected: {Pages} pages exceeds the {MaxPages}-page Document Intelligence limit per analyze call.",
                    blobName, pdf.NumberOfPages, MaxPages);
            return false;
        }

        if (pdf.NumberOfPages > MaxPages * NearLimitFraction)
        {
            warnings.Add(PipelineIssue.Warning(
                PipelineStage.Validation,
                blobName,
                $"{pdf.NumberOfPages} pages, over {NearLimitFraction:P0} of the {MaxPages}-page Document Intelligence limit per analyze call."));
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogWarning(
                    "PDF '{Blob}' has {Pages} pages, over {Fraction:P0} of the {MaxPages}-page Document Intelligence limit per analyze call.",
                    blobName, pdf.NumberOfPages, NearLimitFraction, MaxPages);
        }

        error = null;
        return true;
    }
}
