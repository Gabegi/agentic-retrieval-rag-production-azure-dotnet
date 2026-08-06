namespace AgenticRagApp.Infrastructure.Clients.ContentSafety;

public interface IPromptShieldClient
{
    // True if either the user prompt or any of the supplied documents (e.g. retrieved
    // chunks) contains a direct or indirect prompt injection attack.
    Task<bool> DetectAttackAsync(string userPrompt, IReadOnlyList<string>? documents, CancellationToken ct = default);
}
