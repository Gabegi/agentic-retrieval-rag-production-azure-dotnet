using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Observability.Reports;

public sealed record ReportAttachment(string FileName, string ContentType, BinaryData Content);

public interface IReportEmailSender
{
    // Returns false on a terminal failure rather than throwing. The caller must not hand the
    // event back to Event Grid for redelivery - see PipelineReportEmailFunction.
    Task<bool> SendAsync(string subject, string htmlBody, IReadOnlyList<ReportAttachment> attachments, CancellationToken ct);
}

public sealed class AcsEmailSender : IReportEmailSender
{
    // Bounded in-process retry on transient ACS failures (429/5xx/timeouts), inside the single
    // invocation. Deliberately small: this runs after the whole report has been assembled, and
    // a long retry loop would hold the invocation open for something a daily reconciliation
    // check already covers.
    private const int MaxAttempts = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);

    private readonly EmailClient        _client;
    private readonly ReportEmailOptions _options;
    private readonly ILogger<AcsEmailSender> _logger;

    public AcsEmailSender(EmailClient client, ReportEmailOptions options, ILogger<AcsEmailSender> logger)
    {
        _client  = client;
        _options = options;
        _logger  = logger;
    }

    public async Task<bool> SendAsync(
        string subject, string htmlBody, IReadOnlyList<ReportAttachment> attachments, CancellationToken ct)
    {
        var recipients = new EmailRecipients(
            _options.RecipientList.Select(a => new EmailAddress(a)).ToList());

        var message = new EmailMessage(
            senderAddress: _options.SenderAddress,
            recipients:    recipients,
            content:       new EmailContent(subject) { Html = htmlBody });

        foreach (var a in attachments)
            message.Attachments.Add(new Azure.Communication.Email.EmailAttachment(a.FileName, a.ContentType, a.Content));

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                // WaitUntil.Started, not Completed: ACS accepting the message is the contract
                // this function can meaningfully act on. Waiting for terminal delivery status
                // would block the invocation on the recipient's mail server.
                var operation = await _client.SendAsync(WaitUntil.Started, message, ct);
                _logger.LogInformation("Run report email accepted by ACS — operation {OperationId}, {Count} recipient(s)",
                    operation.Id, _options.RecipientList.Count);
                return true;
            }
            catch (RequestFailedException ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                var delay = BaseDelay * Math.Pow(2, attempt - 1);
                _logger.LogWarning(ex, "ACS send failed with {Status} (attempt {Attempt}/{Max}) — retrying in {Delay}s",
                    ex.Status, attempt, MaxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ACS send failed terminally on attempt {Attempt}", attempt);
                return false;
            }
        }

        _logger.LogError("ACS send failed after {Max} attempts — no email sent for this run", MaxAttempts);
        return false;
    }

    private static bool IsTransient(RequestFailedException ex) =>
        ex.Status == 429 || ex.Status >= 500;
}
