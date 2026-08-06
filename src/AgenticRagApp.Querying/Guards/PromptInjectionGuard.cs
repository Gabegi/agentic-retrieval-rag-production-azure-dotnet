using System.Text.RegularExpressions;
using AgenticRagApp.Infrastructure.Clients.ContentSafety;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Querying.Guards;

public interface IPromptInjectionGuard
{
    Task<bool> IsAttackAsync(string question, IReadOnlyList<string>? documents = null, CancellationToken ct = default);
}

// Two layers. A local pattern check short circuits the obvious cases with zero cost or
// latency. Everything else goes to Azure AI Content Safety Prompt Shields (via
// IPromptShieldClient - no .NET SDK wraps this API, see that type's comment), which also
// inspects retrieved document text for indirect (embedded) injection.
public sealed class PromptInjectionGuard : IPromptInjectionGuard
{
    // Prompt Shields hard limits (confirmed against the published REST spec, not the .NET
    // SDK - see PromptShieldClient's comment): userPrompt <= 10K chars, and up to 5
    // documents totaling 10K chars COMBINED (not 10K each). Retrieved chunk text
    // (ChunkNeighborExpander caps at 16K chars, well over budget) needs its own truncation
    // here - it isn't pre-sized for this API's limits.
    private const int MaxDocuments = 5;
    private const int MaxDocumentsCombinedChars = 10_000;

    private readonly IPromptShieldClient _client;
    private readonly ILogger<PromptInjectionGuard> _logger;

    // Cheap first pass. Not a security boundary on its own, only a latency saver.
    private static readonly Regex Obvious = new(
        @"(negeer|vergeet|ignore|disregard|forget)\s+(alle|al\s+je|previous|prior|above|the)\s*(voorgaande|eerdere|instructies|instructions|rules|prompt)"
        + @"|system\s*(prompt|message)"
        + @"|(jij|je|you)\s+bent\s+nu|you\s+are\s+now"
        + @"|developer\s+mode|\bDAN\b|jailbreak"
        + @"|(herhaal|repeat|print|toon|show|reveal)\s+(je|your|the)\s*(instructies|instructions|prompt|systeemprompt)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public PromptInjectionGuard(IPromptShieldClient client, ILogger<PromptInjectionGuard> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> IsAttackAsync(
        string question,
        IReadOnlyList<string>? documents = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;

        if (Obvious.IsMatch(question))
        {
            _logger.LogWarning("Prompt injection blocked by local pattern check.");
            return true;
        }

        try
        {
            var detected = await _client.DetectAttackAsync(
                Truncate(question, 10_000),
                BudgetDocuments(documents),
                ct);

            if (detected) _logger.LogWarning("Prompt injection blocked by Prompt Shields.");
            return detected;
        }
        catch (HttpRequestException ex)
        {
            // Fail closed on a genuine service error; blocking is the safe default for an
            // acceptance criterion phrased as "catches". Confirm with PO/security — see
            // docs/2608/260806/pii-injection-guards-action-plan.md open questions.
            _logger.LogError(ex, "Prompt Shields call failed; blocking the request.");
            return true;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    // Keeps the first MaxDocuments in the given order (callers pass chunks already in
    // relevance order - see ChunkNeighborExpander), truncating from the tail once the
    // combined budget runs out. A chunk that gets cut off rather than dropped outright
    // still gives Prompt Shields a shot at whatever text fit, instead of silently skipping
    // it altogether.
    private static IReadOnlyList<string>? BudgetDocuments(IReadOnlyList<string>? documents)
    {
        if (documents is null || documents.Count == 0) return documents;

        var budgeted = new List<string>();
        var remaining = MaxDocumentsCombinedChars;
        foreach (var document in documents.Take(MaxDocuments))
        {
            if (remaining <= 0) break;
            budgeted.Add(Truncate(document, remaining));
            remaining -= budgeted[^1].Length;
        }
        return budgeted;
    }
}
