using AgenticRagApp.Infrastructure.Clients.ContentSafety;
using AgenticRagApp.Querying.Guards;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace RagApp.UnitTests.Querying;

// Prompt Shields' real limits (userPrompt <= 10K chars, up to 5 documents totaling 10K
// chars COMBINED - see PromptShieldClient's comment) are enforced client-side in
// PromptInjectionGuard.BudgetDocuments before every real call, since ChunkNeighborExpander's
// own 16K-char cap is sized for the LLM prompt, not this API's tighter combined budget.
// These tests only exercise that budgeting - not the local Obvious regex or the real
// Prompt Shields call, which need a live endpoint (see
// docs/2608/260806/remaining-acceptance-criteria-plan.md, item 1's Dutch-quality spike).
[TestClass]
public class PromptInjectionGuardTests
{
    private static (PromptInjectionGuard Guard, Mock<IPromptShieldClient> Client) BuildGuard()
    {
        var client = new Mock<IPromptShieldClient>();
        client.Setup(c => c.DetectAttackAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var guard = new PromptInjectionGuard(client.Object, NullLogger<PromptInjectionGuard>.Instance);
        return (guard, client);
    }

    [TestMethod]
    public async Task IsAttackAsync_FewShortDocuments_PassesAllThroughUnchanged()
    {
        var (guard, client) = BuildGuard();
        var documents = new[] { "short chunk one", "short chunk two" };

        await guard.IsAttackAsync("a normal question", documents);

        client.Verify(c => c.DetectAttackAsync(
            "a normal question",
            It.Is<IReadOnlyList<string>?>(d => d != null && d.SequenceEqual(documents)),
            It.IsAny<CancellationToken>()));
    }

    [TestMethod]
    public async Task IsAttackAsync_MoreThanFiveDocuments_OnlyFirstFiveSent()
    {
        var (guard, client) = BuildGuard();
        var documents = Enumerable.Range(1, 8).Select(i => $"chunk {i}").ToArray();

        await guard.IsAttackAsync("question", documents);

        client.Verify(c => c.DetectAttackAsync(
            "question",
            It.Is<IReadOnlyList<string>?>(d => d != null && d.Count == 5
                && d.SequenceEqual(documents.Take(5))),
            It.IsAny<CancellationToken>()));
    }

    [TestMethod]
    public async Task IsAttackAsync_CombinedLengthOverBudget_LaterDocumentsTruncatedOrDropped()
    {
        var (guard, client) = BuildGuard();
        // Three 6,000-char documents: 18,000 combined, well over the 10,000 budget.
        var documents = new[] { new string('a', 6_000), new string('b', 6_000), new string('c', 6_000) };

        await guard.IsAttackAsync("question", documents);

        client.Verify(c => c.DetectAttackAsync(
            "question",
            It.Is<IReadOnlyList<string>?>(d =>
                d != null &&
                d.Sum(x => x.Length) <= 10_000 &&
                d[0].Length == 6_000 &&           // first document untouched
                d[1].Length == 4_000 &&           // second truncated to what's left
                d.Count == 2),                    // budget exhausted before a third fit
            It.IsAny<CancellationToken>()));
    }

    [TestMethod]
    public async Task IsAttackAsync_NoDocuments_PassesNullThrough()
    {
        var (guard, client) = BuildGuard();

        await guard.IsAttackAsync("question");

        client.Verify(c => c.DetectAttackAsync(
            "question", null, It.IsAny<CancellationToken>()));
    }
}
