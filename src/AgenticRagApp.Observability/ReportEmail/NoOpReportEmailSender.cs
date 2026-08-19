using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Observability.Reports;

// The only IReportEmailSender in the app. The Azure Communication Services sender that used to
// sit alongside it was removed with the Azure.Communication.Email dependency, and nothing has
// replaced it - so every run report is assembled, analysed and written to blob as before, and
// then logged here instead of being mailed.
//
// Still a registered implementation rather than nothing at all: the Functions host builds a
// function's dependencies before its body runs, so an unregistered IReportEmailSender would
// fail the invocation before the code that is written to no-op politely ever executes.
//
// Returns true, not false. A "send" that was never configured is not a failed send, and
// returning false would make the reconciliation check alert on every run.
public sealed class NoOpReportEmailSender : IReportEmailSender
{
    private readonly ILogger<NoOpReportEmailSender> _logger;

    public NoOpReportEmailSender(ILogger<NoOpReportEmailSender> logger) => _logger = logger;

    public Task<bool> SendAsync(
        string subject, string htmlBody, IReadOnlyList<ReportAttachment> attachments, CancellationToken ct)
    {
        _logger.LogInformation(
            "No email transport configured — run report not sent. Subject would have been: {Subject} "
            + "({BodyChars} chars, {Attachments} attachment(s))",
            subject, htmlBody.Length, attachments.Count);

        return Task.FromResult(true);
    }
}
