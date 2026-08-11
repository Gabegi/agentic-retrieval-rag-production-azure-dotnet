using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class FamilyIdEmbedderTests
{
    // The embedding-space id FamilyIdEmbedder composes from IndexerConfig's defaults -
    // model name plus requested dimensions. Persisted records carrying anything else are
    // deliberately held out of the comparison set, so tests that supply persisted records
    // have to stamp this exact value.
    private const string ModelId = "text-embedding-3-large@3072";

    private static PdfExtractionDocument Doc(string sourceId, string title, IReadOnlyList<Heading>? headings = null) =>
        new(
            SourceId:         sourceId,
            Ordinal:          0,
            Content:          "content",
            Title:            title,
            Author:           null,
            CreatedAt:        null,
            ModDate:          null,
            PageCount:        null,
            LastModifiedDate: null,
            ZenyaDocumentId:  null,
            ZenyaVersion:     null,
            ZenyaStatus:      null,
            ZenyaUrl:         null,
            Bookmarks:        [],
            Sections:         [],
            Breadcrumb:       null,
            Headings:         headings ?? [],
            Boilerplate:      [],
            Tables:           [],
            Dimensions:       null,
            SelectionMarks:   [],
            Figures:          [],
            Lines:            []);

    // Each identity text gets whichever vector its title maps to - lets a test force known
    // cosine similarities between specific documents without depending on real embeddings.
    private static (Mock<IEmbeddingClient> Client, Mock<IDocumentIdentityStore> Store) BuildMocks(
        Dictionary<string, float[]> vectorByTitle, IReadOnlyList<DocumentIdentityRecord>? persisted = null)
    {
        var client = new Mock<IEmbeddingClient>();
        client
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<string>, CancellationToken>((texts, _) => Task.FromResult((
                texts.Select(t => vectorByTitle.Single(kv => t.StartsWith(kv.Key)).Value).ToArray(),
                0)));

        var store = new Mock<IDocumentIdentityStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(persisted ?? []);

        return (client, store);
    }

    private static FamilyIdEmbedder Build(Mock<IEmbeddingClient> client, Mock<IDocumentIdentityStore> store) =>
        new(client.Object, store.Object, new IndexerConfig(), NullLogger<FamilyIdEmbedder>.Instance);

    [TestMethod]
    public async Task ResolveAsync_TwoDocumentsWithIdenticalVectors_GetSameFamilyId()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["CAO GGZ"] = [1f, 0f, 0f],
            ["CAO GHZ"] = [1f, 0f, 0f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[] { Doc("cao-ggz.pdf", "CAO GGZ"), Doc("cao-ghz.pdf", "CAO GHZ") };

        var result = await Build(client, store).ResolveAsync(docs);

        Assert.AreEqual(result["cao-ggz.pdf"].FamilyId, result["cao-ghz.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_TwoDocumentsWithOrthogonalVectors_GetDifferentFamilyIds()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["CAO GGZ"]    = [1f, 0f, 0f],
            ["Aanbrengbonus"] = [0f, 1f, 0f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[] { Doc("cao-ggz.pdf", "CAO GGZ"), Doc("aanbrengbonus.pdf", "Aanbrengbonus") };

        var result = await Build(client, store).ResolveAsync(docs);

        Assert.AreNotEqual(result["cao-ggz.pdf"].FamilyId, result["aanbrengbonus.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_FamilyId_IsLexicographicallySmallestSourceIdInCluster()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["CAO GGZ"] = [1f, 0f, 0f],
            ["CAO GHZ"] = [1f, 0f, 0f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[] { Doc("z-doc.pdf", "CAO GGZ"), Doc("a-doc.pdf", "CAO GHZ") };

        var result = await Build(client, store).ResolveAsync(docs);

        Assert.AreEqual("a-doc.pdf", result["z-doc.pdf"].FamilyId);
        Assert.AreEqual("a-doc.pdf", result["a-doc.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_SetsDomainTagFromTitle()
    {
        var vectors = new Dictionary<string, float[]> { ["CAO GGZ"] = [1f, 0f, 0f] };
        var (client, store) = BuildMocks(vectors);
        var docs = new[] { Doc("cao-ggz.pdf", "CAO GGZ") };

        var result = await Build(client, store).ResolveAsync(docs);

        Assert.AreEqual("GGZ", result["cao-ggz.pdf"].DomainTag);
    }

    [TestMethod]
    public async Task ResolveAsync_ClustersAgainstPersistedDocumentsNotJustThisRunsBatch()
    {
        // Only "Checklist Inzet Medicijndispenser" is in this run; "Handleiding Medido" was
        // indexed in an earlier run and is already in the store - clustering must still
        // find the match, otherwise every incremental run would only ever produce
        // families of one (see class comment on IDocumentIdentityStore).
        var vectors = new Dictionary<string, float[]> { ["Checklist Inzet Medicijndispenser"] = [1f, 0f, 0f] };
        var persisted = new[]
        {
            new DocumentIdentityRecord("handleiding-medido.pdf", "Handleiding Medido", null, [1f, 0f, 0f], "handleiding-medido.pdf", "old-hash", ModelId),
        };
        var (client, store) = BuildMocks(vectors, persisted);
        var docs = new[] { Doc("checklist-medido.pdf", "Checklist Inzet Medicijndispenser") };

        var result = await Build(client, store).ResolveAsync(docs);

        Assert.AreEqual("checklist-medido.pdf", result["checklist-medido.pdf"].FamilyId);

        // The older document's persisted FamilyId is corrected to merge into the new
        // cluster (store-only - see class comment on why Search itself isn't patched).
        store.Verify(s => s.SetAsync(
            It.Is<DocumentIdentityRecord>(r => r.SourceId == "handleiding-medido.pdf" && r.FamilyId == "checklist-medido.pdf"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ResolveAsync_UnchangedIdentityText_SkipsReEmbedding()
    {
        var client = new Mock<IEmbeddingClient>();
        var doc    = Doc("cao-ggz.pdf", "CAO GGZ");

        // Persisted record's hash must match exactly what BuildIdentities would compute for
        // this document - model id + title + domain tag, no headings - for the skip to kick in.
        var identityText = $"{ModelId}\nCAO GGZ\nGGZ";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identityText)));
        var store = new Mock<IDocumentIdentityStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DocumentIdentityRecord("cao-ggz.pdf", "CAO GGZ", "GGZ", [1f, 0f, 0f], "cao-ggz.pdf", hash, ModelId)]);

        var result = await Build(client, store).ResolveAsync([doc]);

        client.Verify(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.AreEqual("cao-ggz.pdf", result["cao-ggz.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_LexicallyCloseTitlesInDifferentFamilies_AreFlaggedConfusable()
    {
        // Medido/Medimo - the motivating C3 case: lexically near-identical, semantically
        // distant, so deliberately given orthogonal vectors (different families) here.
        var vectors = new Dictionary<string, float[]>
        {
            ["Checklist Inzet Medicijndispenser (Medido)"] = [1f, 0f, 0f],
            ["Handleiding Medimo"]                          = [0f, 1f, 0f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[]
        {
            Doc("checklist-medido.pdf", "Checklist Inzet Medicijndispenser (Medido)"),
            Doc("handleiding-medimo.pdf", "Handleiding Medimo"),
        };

        var result = await Build(client, store).ResolveAsync(docs);

        Assert.AreNotEqual(result["checklist-medido.pdf"].FamilyId, result["handleiding-medimo.pdf"].FamilyId);
        CollectionAssert.Contains(result["checklist-medido.pdf"].ConfusableWith.ToList(), "handleiding-medimo.pdf");
        CollectionAssert.Contains(result["handleiding-medimo.pdf"].ConfusableWith.ToList(), "checklist-medido.pdf");
    }

    [TestMethod]
    public async Task ResolveAsync_SameFamilyDocuments_AreNeverFlaggedConfusable()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["CAO GGZ"] = [1f, 0f, 0f],
            ["CAO GHZ"] = [1f, 0f, 0f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[] { Doc("cao-ggz.pdf", "CAO GGZ"), Doc("cao-ghz.pdf", "CAO GHZ") };

        var result = await Build(client, store).ResolveAsync(docs);

        Assert.AreEqual(0, result["cao-ggz.pdf"].ConfusableWith.Count);
        Assert.AreEqual(0, result["cao-ghz.pdf"].ConfusableWith.Count);
    }

    [TestMethod]
    public async Task ResolveAsync_PersistedVectorFromDifferentModel_IsExcludedFromClustering()
    {
        // Same setup as the cross-run clustering test, except the persisted vector was
        // produced by an older embedding model. Cosine between two embedding spaces is not a
        // similarity, so the older document must be held out rather than merged - it rejoins
        // the comparison set when it is next reindexed.
        var vectors = new Dictionary<string, float[]> { ["Checklist Inzet Medicijndispenser"] = [1f, 0f, 0f] };
        var persisted = new[]
        {
            new DocumentIdentityRecord("handleiding-medido.pdf", "Handleiding Medido", null, [1f, 0f, 0f], "handleiding-medido.pdf", "old-hash", "text-embedding-ada-002@1536"),
        };
        var (client, store) = BuildMocks(vectors, persisted);
        var docs = new[] { Doc("checklist-medido.pdf", "Checklist Inzet Medicijndispenser") };

        var result = await Build(client, store).ResolveAsync(docs);

        Assert.AreEqual("checklist-medido.pdf", result["checklist-medido.pdf"].FamilyId);
        store.Verify(s => s.SetAsync(
            It.Is<DocumentIdentityRecord>(r => r.SourceId == "handleiding-medido.pdf"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ResolveAsync_PersistedRecordWithoutModelId_IsExcludedFromClustering()
    {
        // Records written before EmbeddingModelId existed deserialize with it null. Their
        // embedding space is unknown, so they get the same treatment as a known-stale one.
        var vectors = new Dictionary<string, float[]> { ["Checklist Inzet Medicijndispenser"] = [1f, 0f, 0f] };
        var persisted = new[]
        {
            new DocumentIdentityRecord("handleiding-medido.pdf", "Handleiding Medido", null, [1f, 0f, 0f], "handleiding-medido.pdf", "old-hash"),
        };
        var (client, store) = BuildMocks(vectors, persisted);
        var docs = new[] { Doc("checklist-medido.pdf", "Checklist Inzet Medicijndispenser") };

        var result = await Build(client, store).ResolveAsync(docs);

        Assert.AreEqual("checklist-medido.pdf", result["checklist-medido.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_StampsCurrentModelIdOnPersistedRecords()
    {
        var vectors = new Dictionary<string, float[]> { ["CAO GGZ"] = [1f, 0f, 0f] };
        var (client, store) = BuildMocks(vectors);

        await Build(client, store).ResolveAsync([Doc("cao-ggz.pdf", "CAO GGZ")]);

        store.Verify(s => s.SetAsync(
            It.Is<DocumentIdentityRecord>(r => r.SourceId == "cao-ggz.pdf" && r.EmbeddingModelId == ModelId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ResolveAsync_EmbeddingClientReturnsWrongVectorCount_Throws()
    {
        // Vectors are matched to inputs positionally, so a short result set would silently
        // pair document A's vector with document B's SourceId and persist the wrong family.
        // Failing the run is the cheaper outcome.
        var client = new Mock<IEmbeddingClient>();
        client
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new float[][] { [1f, 0f, 0f] }, 0));

        var store = new Mock<IDocumentIdentityStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var docs = new[] { Doc("a.pdf", "CAO GGZ"), Doc("b.pdf", "CAO GHZ") };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Build(client, store).ResolveAsync(docs));

        store.Verify(s => s.SetAsync(It.IsAny<DocumentIdentityRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ResolveAsync_NoDocuments_ReturnsEmpty()
    {
        var (client, store) = BuildMocks([]);

        var result = await Build(client, store).ResolveAsync([]);

        Assert.AreEqual(0, result.Count);
        store.Verify(s => s.SetAsync(It.IsAny<DocumentIdentityRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
