namespace AgenticRagApp.Observability.Reports;

// Bound from the RunAnalysis__* app settings.
//
// Descended from the ReportEmail options. The addressing settings (SenderAddress, Recipients),
// AcsEndpoint and the attachment budget went with the Azure Communication Services sender and
// the email transport itself - the analysis is written to blob now (see SaveRunAnalysisActivity),
// so what is left shapes the analysis: whether it is assembled at all, and how it is flagged.
public sealed class RunAnalysisOptions
{
    public const string SectionName = "RunAnalysis";

    // Master switch. False makes the activity no-op with an informational log - the escape
    // hatch for local/dev runs, where every StartIndexing would otherwise assemble an analysis.
    public bool Enabled { get; set; } = true;

    // While true, thresholds with no defensible source render their observed value but raise no
    // flag. See FlagEvaluator - shipping guessed thresholds teaches people to ignore flags.
    public bool CalibrationMode { get; set; } = true;
}
