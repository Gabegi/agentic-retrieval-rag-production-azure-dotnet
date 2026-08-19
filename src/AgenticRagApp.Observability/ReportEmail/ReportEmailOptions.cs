namespace AgenticRagApp.Observability.Reports;

// Bound from the ReportEmail__* app settings.
//
// The addressing settings (SenderAddress, Recipients) and AcsEndpoint are gone along with the
// Azure Communication Services sender: no transport is wired up any more (see
// IReportEmailSender), so what is left shapes the report itself - whether it is assembled at
// all, how it is flagged, and how large its attachment may get - not who receives it.
public sealed class ReportEmailOptions
{
    public const string SectionName = "ReportEmail";

    // Master switch. False makes the function no-op with an informational log - the escape
    // hatch for local/dev runs, where every StartIndexing would otherwise assemble a report.
    public bool Enabled { get; set; } = true;

    // While true, thresholds with no defensible source render their observed value but raise no
    // flag. See FlagEvaluator - shipping guessed thresholds teaches people to ignore flags.
    public bool CalibrationMode { get; set; } = true;

    // Kept at the old ACS-era budget (4 MB, comfortably under the 10 MB per-request cap that
    // sender worked to) so report sizing behaves exactly as it did. Over budget, the summary is
    // written to a blob and linked instead of attached.
    public int MaxAttachmentBytes { get; set; } = 4 * 1024 * 1024;
}
