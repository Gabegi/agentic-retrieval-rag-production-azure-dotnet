namespace AgenticRagApp.Observability.Reports;

public sealed record ReportAttachment(string FileName, string ContentType, BinaryData Content);

// The seam the run-report pipeline sends through. NoOpReportEmailSender is the only
// implementation today: the Azure Communication Services sender that used to sit behind this
// was removed along with the Azure.Communication.Email dependency, and no transport has
// replaced it. Everything upstream (RunReportAssembler, RunAnalysisAgent, RunEmailRenderer)
// still runs and still writes its reports to blob - only delivery is gone.
public interface IReportEmailSender
{
    // Returns false on a terminal failure rather than throwing. The caller must not hand the
    // event back to Event Grid for redelivery - see PipelineReportEmailFunction.
    Task<bool> SendAsync(string subject, string htmlBody, IReadOnlyList<ReportAttachment> attachments, CancellationToken ct);
}
