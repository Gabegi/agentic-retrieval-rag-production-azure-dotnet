using Microsoft.Extensions.Logging.Abstractions;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.Indexing;

// Direct tests for the admission rules. DocumentIdentityResolverTests reaches this class through
// the resolver's public API, which was the right net while it was an inline block - but the three
// exclusions are what a wrong family id usually traces back to, and each one deserves a test that
// fails for one reason only (chunking-done.md §14, §17 item 14).
[TestClass]
public class IdentityComparisonSetTests
{
    private const int    Dimensions = 3;
    private const string ModelId    = "text-embedding-3-large@3";

    private static DocumentIdentityRecord Record(
        string sourceId,
        float[]? vector = null,
        string?  modelId = ModelId,
        string   familyId = "fam-1") =>
        new(SourceId:         sourceId,
            Title:            $"Title of {sourceId}",
            DomainTag:        "ggz",
            Vector:           vector ?? [0.1f, 0.2f, 0.3f],
            FamilyId:         familyId,
            IdentityTextHash: $"hash-{sourceId}",
            EmbeddingModelId: modelId);

    private static DocumentIdentity Identity(string sourceId, string title = "Fresh title") =>
        new(SourceId: sourceId, Title: title, DomainTag: "ggz",
            IdentityText: title, Hash: $"hash-{sourceId}", IdentityTokens: 4);

    private static ComparisonSet Build(
        IReadOnlyList<DocumentIdentity>? thisRun = null,
        IReadOnlyDictionary<string, DocumentIdentityRecord>? persisted = null,
        IReadOnlyDictionary<string, float[]>? freshVectors = null) =>
        IdentityComparisonSet.Build(
            thisRun      ?? [],
            persisted    ?? new Dictionary<string, DocumentIdentityRecord>(),
            freshVectors ?? new Dictionary<string, float[]>(),
            ModelId,
            Dimensions,
            NullLogger.Instance);

    // ── The three exclusions ─────────────────────────────────────────────────

    [TestMethod]
    public void PersistedRecordWithNoVector_IsExcludedAndCounted()
    {
        var persisted = new Dictionary<string, DocumentIdentityRecord>
        {
            ["a.pdf"] = Record("a.pdf", vector: []),
        };

        var set = Build(persisted: persisted);

        Assert.AreEqual(0, set.Docs.Count, "nothing to compare it by");
        Assert.AreEqual(1, set.SkippedNoVector);
        Assert.AreEqual(0, set.SkippedOtherModel);
        Assert.AreEqual(0, set.SkippedWrongDimensions);
    }

    [TestMethod]
    public void PersistedRecordFromAnotherModel_IsExcludedAndCounted()
    {
        // Cosine across two embedding spaces is not a similarity - it is comparable only by
        // accident, which is worse than not comparing at all.
        var persisted = new Dictionary<string, DocumentIdentityRecord>
        {
            ["a.pdf"] = Record("a.pdf", modelId: "text-embedding-ada-002@1536"),
        };

        var set = Build(persisted: persisted);

        Assert.AreEqual(0, set.Docs.Count);
        Assert.AreEqual(1, set.SkippedOtherModel);
        Assert.AreEqual(0, set.SkippedNoVector);
    }

    [TestMethod]
    public void PersistedRecordWithTheWrongDimensionCount_IsExcludedAndCounted()
    {
        var persisted = new Dictionary<string, DocumentIdentityRecord>
        {
            ["a.pdf"] = Record("a.pdf", vector: [0.1f, 0.2f]),
        };

        var set = Build(persisted: persisted);

        Assert.AreEqual(0, set.Docs.Count);
        Assert.AreEqual(1, set.SkippedWrongDimensions);
    }

    [TestMethod]
    public void TheThreeExclusionsAreCountedSeparately()
    {
        // One of each, so a rule that swallowed another's population would show up here rather
        // than in a total that still adds up.
        var persisted = new Dictionary<string, DocumentIdentityRecord>
        {
            ["novector.pdf"]  = Record("novector.pdf",  vector: []),
            ["othermodel.pdf"] = Record("othermodel.pdf", modelId: "other@1"),
            ["wrongdim.pdf"]  = Record("wrongdim.pdf",  vector: [1f]),
            ["good.pdf"]      = Record("good.pdf"),
        };

        var set = Build(persisted: persisted);

        Assert.AreEqual(1, set.Docs.Count);
        Assert.IsTrue(set.Docs.ContainsKey("good.pdf"));
        Assert.AreEqual(1, set.SkippedNoVector);
        Assert.AreEqual(1, set.SkippedOtherModel);
        Assert.AreEqual(1, set.SkippedWrongDimensions);
    }

    [TestMethod]
    public void ANullModelIdOnAPersistedRecord_CountsAsADifferentModel()
    {
        // Records written before the model id was stored. They are not known-good, and treating
        // an absent id as "probably the current one" is exactly the accidental comparison the
        // rule exists to refuse.
        var persisted = new Dictionary<string, DocumentIdentityRecord>
        {
            ["legacy.pdf"] = Record("legacy.pdf", modelId: null),
        };

        var set = Build(persisted: persisted);

        Assert.AreEqual(0, set.Docs.Count);
        Assert.AreEqual(1, set.SkippedOtherModel);
    }

    // ── This run's documents ─────────────────────────────────────────────────

    [TestMethod]
    public void ThisRunsDocuments_AreAdmittedOnTheirFreshVector()
    {
        var set = Build(
            thisRun:      [Identity("a.pdf", "Fresh title")],
            freshVectors: new Dictionary<string, float[]> { ["a.pdf"] = [1f, 0f, 0f] });

        Assert.AreEqual(1, set.Docs.Count);
        Assert.AreEqual("Fresh title", set.Docs["a.pdf"].Title);
        CollectionAssert.AreEqual(new[] { 1f, 0f, 0f }, set.Docs["a.pdf"].Vector);
    }

    [TestMethod]
    public void AFreshVectorAndTitle_OverwriteThePersistedOnes()
    {
        // The whole reason this run's documents are folded in after the persisted loop: a
        // document that was re-titled must be clustered on its new title, not its stored one.
        var persisted = new Dictionary<string, DocumentIdentityRecord>
        {
            ["a.pdf"] = Record("a.pdf", vector: [0f, 0f, 1f]),
        };

        var set = Build(
            thisRun:      [Identity("a.pdf", "Renamed")],
            persisted:    persisted,
            freshVectors: new Dictionary<string, float[]> { ["a.pdf"] = [1f, 0f, 0f] });

        Assert.AreEqual(1, set.Docs.Count);
        Assert.AreEqual("Renamed", set.Docs["a.pdf"].Title);
        CollectionAssert.AreEqual(new[] { 1f, 0f, 0f }, set.Docs["a.pdf"].Vector);
    }

    [TestMethod]
    public void ADocumentSkippedByTheHashGate_FallsBackToItsPersistedVector()
    {
        // No fresh vector because the identity hash matched, which implies the same model - so
        // the stored vector is safe to reuse and the document still takes part in clustering.
        var persisted = new Dictionary<string, DocumentIdentityRecord>
        {
            ["a.pdf"] = Record("a.pdf", vector: [0f, 1f, 0f]),
        };

        var set = Build(thisRun: [Identity("a.pdf", "Unchanged")], persisted: persisted);

        Assert.AreEqual(1, set.Docs.Count);
        CollectionAssert.AreEqual(new[] { 0f, 1f, 0f }, set.Docs["a.pdf"].Vector);
        Assert.AreEqual("Unchanged", set.Docs["a.pdf"].Title, "the run's title still wins");
    }

    [TestMethod]
    public void ADocumentWithNoVectorAnywhere_IsRemovedRatherThanCarried()
    {
        // Defensive path: IdentityEmbedder throws rather than returning a vectorless document, so
        // this is unreachable today. It is pinned because the failure it prevents - a null in the
        // cosine loop - is not one the clusterer can report intelligibly.
        var persisted = new Dictionary<string, DocumentIdentityRecord>
        {
            ["a.pdf"] = Record("a.pdf", modelId: "other@1"),
        };

        var set = Build(thisRun: [Identity("a.pdf")], persisted: persisted);

        Assert.AreEqual(0, set.Docs.Count);
        Assert.IsFalse(set.Docs.ContainsKey("a.pdf"));
    }

    [TestMethod]
    public void AnEmptyRunOverAnEmptyStore_IsAnEmptySetAndNotAFailure()
    {
        var set = Build();

        Assert.AreEqual(0, set.Docs.Count);
        Assert.AreEqual(0, set.SkippedNoVector);
        Assert.AreEqual(0, set.SkippedOtherModel);
        Assert.AreEqual(0, set.SkippedWrongDimensions);
    }
}
