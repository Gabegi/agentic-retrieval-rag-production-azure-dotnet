using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.DomainClassification;

// The authoritative DomainTag source since 2026-08-27, replacing the DomainTagger regexes:
// filename tokens don't scale past the 51-doc corpus, and the CU-extracted titles the regexes
// ran over often carry no population token at all ("INLEIDING" on both Handreiking docs).
//
// Reuses the IChatClient already registered against config.OpenAiGptDeployment - no new AI
// resource, and deliberately NOT OpenAiMiniDeployment, which is Content-Understanding-only
// (IndexerConfig's own comment). Same degrade-never-throw shape as RunAnalysisAgent: a model
// failure returns an empty map and the run continues with untagged documents, which the
// Chunking.UntaggedFamilyMemberIds flag then reports.
//
// Callers cache the result per document identity hash (IdentityTagger/TaggedAtHash), so this
// runs once per NEW OR CHANGED document, not once per run - the model's nondeterminism can
// never flip a tag on an unchanged document.
public sealed partial class DomainClassifier : IDomainClassifier
{
    // Small batches bound the output the model must produce in one response; at corpus scale
    // (1000s of docs) the first run pays ceil(n/25) calls, every later run pays only for the
    // documents that actually changed.
    private const int BatchSize = 25;

    // ~20 output tokens per document plus JSON scaffolding, with slack for the model spelling
    // out an occasional longer tag. Not a correctness bound - an overflow fails the batch,
    // which degrades to "retry next run".
    private const int MaxOutputTokens = 900;

    // The known vocabulary, carried in the prompt. The model may go outside it (that is the
    // point - new populations in future corpora tag themselves), but anything non-canonical is
    // logged so vocabulary growth is a visible, reviewable event rather than silent drift.
    private static readonly string[] CanonicalTags = ["GGZ", "GHZ", "VVT", "VGZ", "LVB", "MVB"];

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    private readonly IChatClient _chat;
    private readonly ILogger<DomainClassifier> _logger;

    public DomainClassifier(IChatClient chat, ILogger<DomainClassifier> logger)
    {
        _chat   = chat;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, string?>> ClassifyAsync(
        IReadOnlyList<DocumentToClassify> documents, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (documents.Count == 0)
            return result;

        var wanted = documents.Select(d => d.SourceId).ToHashSet(StringComparer.Ordinal);

        for (var offset = 0; offset < documents.Count; offset += BatchSize)
        {
            var batch = documents.Skip(offset).Take(BatchSize).ToList();
            try
            {
                var payload = JsonSerializer.Serialize(
                    new { documents = batch.Select(d => new { id = d.SourceId, filename = d.SourceId, title = d.Title, text = d.TextSample }) },
                    s_json);

                var response = await _chat.GetResponseAsync(
                    [
                        new ChatMessage(ChatRole.System, SystemPrompt),
                        new ChatMessage(ChatRole.User, payload),
                    ],
                    new ChatOptions
                    {
                        MaxOutputTokens = MaxOutputTokens,
                        // Zero, unlike RunAnalysisAgent's 0.2: this is classification against a
                        // fixed vocabulary, and every avoidable source of run-to-run tag drift
                        // is one the identity store then has to absorb.
                        Temperature    = 0f,
                        ResponseFormat = ChatResponseFormat.Json,
                    },
                    ct);

                ParseInto(result, response.Text, wanted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // This batch's documents stay absent from the result - the caller keeps any
                // stale persisted tag and retries them on the next run. Deliberately per-batch,
                // not per-run: one bad batch must not discard the batches that succeeded.
                _logger.LogWarning(ex,
                    "Domain classification failed for a batch of {Count} document(s) - they stay untagged this run.",
                    batch.Count);
            }
        }

        var newTags = result.Values
            .Where(t => t is not null && !CanonicalTags.Contains(t))
            .Distinct()
            .ToList();
        if (newTags.Count > 0)
            _logger.LogWarning(
                "Domain classification proposed {Count} tag(s) outside the canonical vocabulary: {Tags}. " +
                "Review them; recurring ones belong in DomainClassifier.CanonicalTags.",
                newTags.Count, string.Join(", ", newTags));

        return result;
    }

    // Accepts only well-formed items for documents that were actually asked about; anything
    // else is dropped item-by-item (same enforcement-over-instruction stance as
    // RunAnalysisAgent's evidence check). "NONE"/"NULL" normalize to the null tag so a model
    // that answers in words rather than JSON null still parses.
    private void ParseInto(Dictionary<string, string?> result, string? text, IReadOnlySet<string> wanted)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(text));
            if (!doc.RootElement.TryGetProperty("documents", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;

                var id = idEl.GetString()!;
                if (!wanted.Contains(id)) continue;
                if (!item.TryGetProperty("tag", out var tagEl)) continue;

                switch (tagEl.ValueKind)
                {
                    case JsonValueKind.Null:
                        result[id] = null;
                        break;

                    case JsonValueKind.String:
                        var tag = tagEl.GetString()!.Trim().ToUpperInvariant();
                        if (tag is "NONE" or "NULL" or "")
                            result[id] = null;
                        else if (TagShape().IsMatch(tag))
                            result[id] = tag;
                        else
                            _logger.LogWarning(
                                "Domain classification returned a malformed tag {Tag} for {SourceId} - dropped, retried next run.",
                                tag, id);
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Domain classification response was not valid JSON - batch dropped, retried next run.");
        }
    }

    // Models occasionally wrap JSON in a fenced block despite ResponseFormat.Json - same
    // recovery as RunAnalysisAgent.ExtractJson.
    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end   = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    // Short uppercase code; '&' allowed for forms like V&V should the model echo one. Length
    // and shape bound what can end up as a facet value in the search index.
    [GeneratedRegex(@"^[A-Z][A-Z&]{1,9}$")]
    private static partial Regex TagShape();

    private const string SystemPrompt = """
        You classify documents of a Dutch health-and-care organisation by the population they
        govern, so that near-identical documents for different populations can be told apart
        (per-sector CAOs, per-doelgroep handreikingen, per-doelgroep brochures).

        For each input document (id, filename, title, text sample), assign exactly one tag:
        - a sector code: GGZ (geestelijke gezondheidszorg), GHZ (gehandicaptenzorg),
          VVT (verpleging, verzorging en thuiszorg), VGZ
        - or a doelgroep code: LVB (licht verstandelijke beperking),
          MVB (matig verstandelijke beperking)
        - or a NEW short uppercase code (2-10 letters) ONLY when the document is clearly
          specific to a population none of the codes above cover
        - or null when the document applies organisation-wide or to no specific population.

        Rules:
        - Prefer the known codes; invent a new one only when genuinely necessary.
        - Filenames often carry the decisive token verbatim; titles may not.
        - null is the correct answer for general documents (hygiene codes, privacy policy,
          onboarding checklists). Do not force a tag.

        Respond with JSON, nothing else:
        {"documents":[{"id":"<id verbatim from the input>","tag":"GGZ"},{"id":"...","tag":null}]}
        """;
}
