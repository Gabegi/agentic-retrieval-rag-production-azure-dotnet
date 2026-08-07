using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Observability.Reports;

// Registered instead of AcsEmailSender when no ACS endpoint is configured — local development,
// and any environment where the Communication Services resources have not been applied yet.
//
// Exists so the absence of ACS config is an informational log rather than a resolution failure:
// the Functions host builds a function's dependencies before its body runs, so an unregistered
// IReportEmailSender would fail the invocation before the code that is written to no-op politely
// ever executes.
//
// Returns true, not false. A "send" that was never configured is not a failed send, and
// returning false would make the reconciliation check alert on every run in an environment
// that was never meant to send mail.
public sealed class NoOpReportEmailSender : IReportEmailSender
{
    private readonly ILogger<NoOpReportEmailSender> _logger;

    public NoOpReportEmailSender(ILogger<NoOpReportEmailSender> logger) => _logger = logger;

    public Task<bool> SendAsync(
        string subject, string htmlBody, IReadOnlyList<ReportAttachment> attachments, CancellationToken ct)
    {
        _logger.LogInformation(
            "No ACS endpoint configured — run report email not sent. Subject would have been: {Subject} "
            + "({BodyChars} chars, {Attachments} attachment(s))",
            subject, htmlBody.Length, attachments.Count);

        return Task.FromResult(true);
    }
}
