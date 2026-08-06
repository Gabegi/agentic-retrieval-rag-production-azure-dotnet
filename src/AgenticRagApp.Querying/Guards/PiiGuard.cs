using System.Text.RegularExpressions;
using Azure;
using Azure.AI.TextAnalytics;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Querying.Guards;

public interface IPiiGuard
{
    Task<bool> ContainsPiiAsync(string text, CancellationToken ct = default);
}

// Local checks for the categories a regex handles reliably (BSN via elfproef, Dutch
// postcode + house number), then Azure AI Language PII detection for names and free form
// addresses. Used on the question before the knowledge base call and on the synthesized
// answer afterwards.
public sealed class PiiGuard : IPiiGuard
{
    private const string Language = "nl";
    private const double MinConfidence = 0.75;

    // Azure AI Language: 5,120 chars per document, 5 documents per synchronous request.
    private const int MaxCharsPerChunk = 5_000;
    private const int MaxChunksPerRequest = 5;

    // Confirmed against the installed Azure.AI.TextAnalytics 5.3.0 assembly - there is no
    // Netherlands-specific national ID category, so the local elfproef check below is the
    // only BSN coverage. EUNationalIdentificationNumber is included as a generic backstop
    // but isn't guaranteed to catch a BSN specifically.
    private static readonly HashSet<PiiEntityCategory> BlockedCategories = new()
    {
        PiiEntityCategory.Person,
        PiiEntityCategory.Address,
        PiiEntityCategory.PhoneNumber,
        PiiEntityCategory.Email,
        PiiEntityCategory.CreditCardNumber,
        PiiEntityCategory.InternationalBankingAccountNumber,
        PiiEntityCategory.EUPassportNumber,
        PiiEntityCategory.EUNationalIdentificationNumber,
    };

    // Accepts common formatting variants: 123456789, 1234.56.789, 123 456 789.
    private static readonly Regex BsnCandidate = new(
        @"(?<!\d)\d{3}[.\s]?\d{2,3}[.\s]?\d{3,4}(?!\d)",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex DutchPostcode = new(
        @"\b\d{4}\s?[A-Za-z]{2}\b\s*\d{1,4}",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly TextAnalyticsClient _client;
    private readonly ILogger<PiiGuard> _logger;

    public PiiGuard(TextAnalyticsClient client, ILogger<PiiGuard> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> ContainsPiiAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (ContainsValidBsn(text) || DutchPostcode.IsMatch(text))
        {
            _logger.LogWarning("PII blocked by local check.");
            return true;
        }

        var chunks = SplitIntoSentenceChunks(text).ToList();

        try
        {
            foreach (var batch in chunks.Chunk(MaxChunksPerRequest))
            {
                var documents = batch.Select((c, i) => new TextDocumentInput(i.ToString(), c)
                {
                    Language = Language,
                });

                Response<RecognizePiiEntitiesResultCollection> response =
                    await _client.RecognizePiiEntitiesBatchAsync(documents, cancellationToken: ct);

                foreach (var doc in response.Value)
                {
                    if (doc.HasError)
                    {
                        _logger.LogError("PII detection error {Code}: {Message}",
                            doc.Error.ErrorCode, doc.Error.Message);
                        return true;
                    }

                    if (doc.Entities.Any(e =>
                            e.ConfidenceScore >= MinConfidence &&
                            BlockedCategories.Contains(e.Category)))
                    {
                        _logger.LogWarning("PII blocked by Azure AI Language.");
                        return true;
                    }
                }
            }

            return false;
        }
        catch (RequestFailedException ex)
        {
            // Fail closed on a genuine service error, to stay compliant. Confirm with
            // PO/security — see docs/2608/260806/pii-injection-guards-action-plan.md open
            // questions.
            _logger.LogError(ex, "PII detection failed; blocking to stay compliant.");
            return true;
        }
    }

    // Splits on sentence boundaries where possible so an entity is less likely to be cut in
    // half across a chunk edge, which would hide it from the detector. Local checks above
    // run against the full, unchunked text, so chunk edges can't hide a BSN or postcode from
    // those - only from the Azure-based Person/Address/etc. detection.
    private static IEnumerable<string> SplitIntoSentenceChunks(string text)
    {
        if (text.Length <= MaxCharsPerChunk)
        {
            yield return text;
            yield break;
        }

        var position = 0;
        while (position < text.Length)
        {
            var length = Math.Min(MaxCharsPerChunk, text.Length - position);

            if (position + length < text.Length)
            {
                var window = text.AsSpan(position, length);
                var breakAt = window.LastIndexOfAny('.', '\n', '!');
                if (breakAt > MaxCharsPerChunk / 2) length = breakAt + 1;
            }

            yield return text.Substring(position, length);
            position += length;
        }
    }

    // Dutch BSN elfproef: weights 9..2 for the first eight digits, then -1.
    // Note: roughly 1 in 11 random 9 digit numbers passes, so unlabeled invoice or order
    // numbers can trigger a false positive. Add a context anchor (require "BSN" or
    // "burgerservicenummer" nearby) if that shows up in testing.
    private static bool ContainsValidBsn(string text)
    {
        foreach (Match m in BsnCandidate.Matches(text))
        {
            var d = new string(m.Value.Where(char.IsDigit).ToArray());
            if (d.Length != 9) continue;

            var sum = 0;
            for (var i = 0; i < 8; i++) sum += (d[i] - '0') * (9 - i);
            sum -= d[8] - '0';

            if (sum != 0 && sum % 11 == 0) return true;
        }
        return false;
    }
}
