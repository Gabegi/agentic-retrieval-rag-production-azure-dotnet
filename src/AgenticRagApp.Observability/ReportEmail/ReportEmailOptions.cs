namespace AgenticRagApp.Observability.Reports;

// Bound from the ReportEmail__* app settings (see infra/function_app.tf).
public sealed class ReportEmailOptions
{
    public const string SectionName = "ReportEmail";

    // Master switch. False makes the function no-op with an informational log - the escape
    // hatch for local/dev runs, where every StartIndexing would otherwise send mail.
    public bool Enabled { get; set; } = true;

    // Semicolon-separated. INTERNAL ADDRESSES ONLY - the body carries verbatim corpus excerpts
    // and the attachment carries the full run summary. Empty is treated as "disabled", not as
    // an error: a misconfigured recipient list must not fail a run.
    public string Recipients { get; set; } = "";

    public string SenderAddress { get; set; } = "";
    public string AcsEndpoint   { get; set; } = "";

    // While true, thresholds with no defensible source render their observed value but raise no
    // flag. See FlagEvaluator - shipping guessed thresholds teaches people to ignore flags.
    public bool CalibrationMode { get; set; } = true;

    // Below the ACS cap (10 MB per request, ~7.5 MB effective after base64) with room for the
    // HTML body. Over budget, the summary is written to a blob and linked instead of attached.
    public int MaxAttachmentBytes { get; set; } = 4 * 1024 * 1024;

    public IReadOnlyList<string> RecipientList =>
        Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsSendable => Enabled
        && RecipientList.Count > 0
        && !string.IsNullOrWhiteSpace(SenderAddress)
        && !string.IsNullOrWhiteSpace(AcsEndpoint);
}
