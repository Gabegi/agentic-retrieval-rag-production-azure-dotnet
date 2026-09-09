using Azure;
using Azure.AI.TextAnalytics;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Language;

// Azure AI Language language detection over the TextAnalyticsClient this project already
// registers (the same client PiiGuard uses on the query side, against the same Foundry account
// endpoint - LANGUAGE_ENDPOINT in function_app.tf). No new resource, no new app setting, and no
// new role assignment: the managed identity's Cognitive Services User on that account already
// authorizes AI Language.
//
// Degrade-never-throw, the same shape as DomainClassifier: a service failure returns null and
// the extraction run continues with an unset language. The alternative - failing a document
// whose paid analysis already succeeded because a free-ish side call did not - would trade a
// missing facet value for a lost document.
public sealed class DocumentLanguageDetector : IDocumentLanguageDetector
{
    // Azure AI Language accepts 5,120 characters per document for language detection. Callers
    // send a much smaller sample than that (see ExtractionService); this is the hard service
    // bound, applied here so a caller cannot breach it by passing more.
    private const int MaxChars = 5_000;

    private readonly TextAnalyticsClient _client;
    private readonly ILogger<DocumentLanguageDetector> _logger;

    public DocumentLanguageDetector(TextAnalyticsClient client, ILogger<DocumentLanguageDetector> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<DocumentLanguage?> DetectAsync(string textSample, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(textSample)) return null;

        var text = textSample.Length <= MaxChars ? textSample : textSample[..MaxChars];

        try
        {
            // No countryHint. The corpus is Dutch care documentation with one English document
            // in it, and a hint of "nl" would bias exactly the case this field exists to
            // identify. The service's own default is used instead of a guess of ours.
            Response<DetectedLanguage> response = await _client.DetectLanguageAsync(
                text, cancellationToken: ct);

            var detected = response.Value;

            // The service reports undetermined input as "(Unknown)" with an empty ISO name
            // rather than as an error. Null here, so the report shows a blank instead of a
            // language code nobody can filter on.
            if (string.IsNullOrWhiteSpace(detected.Iso6391Name))
            {
                _logger.LogInformation(
                    "Language detection returned no ISO code (name '{Name}', score {Score}).",
                    detected.Name, detected.ConfidenceScore);
                return null;
            }

            return new DocumentLanguage(detected.Iso6391Name, detected.ConfidenceScore);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex,
                "Language detection failed ({Status}); the document's language stays unset.", ex.Status);
            return null;
        }
    }
}
