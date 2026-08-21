using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Indexing.CU.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class DocumentIdentityResolverTests
{
    // Vectors here are 3 floats rather than the configured default's 3072, so the config the
    // tests build has to agree - DocumentIdentityResolver rejects a vector whose length is not the
    // configured dimension count. Dimensions are part of the embedding-space id, so the id the
    // persisted-record tests stamp changes with them.
    private const int    Dimensions = 3;
    private const string ModelId    = "text-embedding-3-large@3";

    private static PdfExtractionDocument Doc(string sourceId, string title, IReadOnlyList<Heading>? headings = null) =>
        new(
            SourceId:         sourceId,
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
            PageSpans:        [new PageSpan(1, 0, "content".Length, null, false)],
            PageBreadcrumbs:  new Dictionary<int, string>(),
            Sections:         [],
            Headings:         headings ?? [],
            Boilerplate:      [],
            Tables:           [],
            SelectionMarks:   [],
            Figures:          [],
            Lines:            [],
            Profile:          null,
            Language:         null);

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

    private static DocumentIdentityResolver Build(Mock<IEmbeddingClient> client, Mock<IDocumentIdentityStore> store) =>
        new(client.Object, store.Object,
            new IndexerConfig { OpenAiEmbeddingDimensions = Dimensions },
            NullLogger<DocumentIdentityResolver>.Instance);

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

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreEqual(result.Families["cao-ggz.pdf"].FamilyId, result.Families["cao-ghz.pdf"].FamilyId);
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

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreNotEqual(result.Families["cao-ggz.pdf"].FamilyId, result.Families["aanbrengbonus.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_NewFamily_IsNamedAfterTheCommonTitlePrefix()
    {
        // Replaces the old "FamilyId is the lexicographically smallest SourceId" test
        // (families.md §7): a SourceId-shaped family id renames the whole family whenever a
        // document sorting earlier joins it, and reads as provenance to the model.
        var vectors = new Dictionary<string, float[]>
        {
            ["CAO GGZ"] = [1f, 0f, 0f],
            ["CAO GHZ"] = [1f, 0f, 0f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[] { Doc("z-doc.pdf", "CAO GGZ"), Doc("a-doc.pdf", "CAO GHZ") };

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreEqual("cao", result.Families["z-doc.pdf"].FamilyId);
        Assert.AreEqual("cao", result.Families["a-doc.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_NewFamilyWithNothingInCommon_FallsBackToTheSmallestSourceId()
    {
        // No shared leading token, so there is no honest label to mint - the fallback keeps the
        // id traceable to a real document rather than inventing an opaque one.
        var vectors = new Dictionary<string, float[]>
        {
            ["Aanbrengbonus"]  = [1f, 0f, 0f],
            ["Ziekmelden"]     = [1f, 0f, 0f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[] { Doc("z-doc.pdf", "Aanbrengbonus"), Doc("a-doc.pdf", "Ziekmelden") };

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreEqual("a-doc.pdf", result.Families["z-doc.pdf"].FamilyId);
        Assert.AreEqual("a-doc.pdf", result.Families["a-doc.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_TitlesSharingOnlyBoilerplate_DoNotNameTheFamilyAfterIt()
    {
        // "Versie" is in nearly every title in this corpus (measured: all but a handful of 51),
        // so a naive common-token rule would name half the families "versie".
        var vectors = new Dictionary<string, float[]>
        {
            ["Aanbrengbonus (Versie 5)"] = [1f, 0f, 0f],
            ["Ziekmelden (Versie 4)"]    = [1f, 0f, 0f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[]
        {
            Doc("z-doc.pdf", "Aanbrengbonus (Versie 5)"),
            Doc("a-doc.pdf", "Ziekmelden (Versie 4)"),
        };

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreEqual("a-doc.pdf", result.Families["z-doc.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_DocumentSortingEarlierJoinsAFamily_DoesNotRenameIt()
    {
        // The §7a defect in one test: under the old scheme a new member whose SourceId sorted
        // first renamed the entire family, so one family carried two different family_id values
        // in the index at once (the store was corrected, the uploaded chunks were not) and the
        // "same family_id, different domain_tag" conflict check silently stopped firing.
        var vectors = new Dictionary<string, float[]>
        {
            ["CAO GGZ"] = [1f, 0f, 0f],
            ["CAO GHZ"] = [1f, 0f, 0f],
        };
        var persisted = new[]
        {
            new DocumentIdentityRecord("z-cao-ggz.pdf", "CAO GGZ", "GGZ", [1f, 0f, 0f], "cao", "old-hash", ModelId),
        };
        var (client, store) = BuildMocks(vectors, persisted);

        // Sorts before every existing member.
        var result = await Build(client, store).ResolveDocumentIdentityAsync([Doc("a-cao-ghz.pdf", "CAO GHZ")]);

        Assert.AreEqual("cao", result.Families["a-cao-ghz.pdf"].FamilyId);
        store.Verify(s => s.SetAsync(
            It.Is<DocumentIdentityRecord>(r => r.SourceId == "z-cao-ggz.pdf"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ResolveAsync_ReportsIdentityTokenPressure_OnlyWhenNearingTheLimit()
    {
        // Identity text is uncapped and truncation past the model's per-input limit is silent,
        // so the margin is reported every run. Measured worst case in the real corpus is 73% of
        // the limit, so a normal document must report nothing.
        var vectors = new Dictionary<string, float[]> { ["CAO GGZ"] = [1f, 0f, 0f] };
        var (client, store) = BuildMocks(vectors);

        var result = await Build(client, store).ResolveDocumentIdentityAsync([Doc("cao-ggz.pdf", "CAO GGZ")]);

        Assert.AreEqual(0, result.Diagnostics.NearingTokenLimit.Count);
        Assert.IsTrue(result.Diagnostics.MaxIdentityTokens > 0);
        Assert.AreEqual(DocumentIdentityBuilder.InputTokenLimit, result.Diagnostics.IdentityTokenLimit);
        Assert.AreEqual(result.Diagnostics.MaxIdentityTokens, result.Diagnostics.TotalIdentityTokensEmbedded);
    }

    [TestMethod]
    public async Task ResolveAsync_HeadingDenseDocument_IsFlaggedBeforeItCanTruncate()
    {
        // Headings are what drive this: ~19 tokens each in the real corpus, so a document with
        // enough of them crosses the warning line while still being embeddable.
        var headings = Enumerable.Range(0, 1200)
            .Select(i => new Heading($"Artikel {i} over arbeidsvoorwaarden en vergoedingen", "sectionHeading", 0, 1, 1))
            .ToList();

        var vectors = new Dictionary<string, float[]> { ["CAO GGZ"] = [1f, 0f, 0f] };
        var (client, store) = BuildMocks(vectors);

        var result = await Build(client, store)
            .ResolveDocumentIdentityAsync([Doc("cao-ggz.pdf", "CAO GGZ", headings)]);

        var flagged = result.Diagnostics.NearingTokenLimit.Single();
        Assert.AreEqual("cao-ggz.pdf", flagged.SourceId);
        Assert.IsTrue(flagged.Tokens > DocumentIdentityBuilder.TokenWarningThreshold);

        // Still resolved - this is a warning about the margin, not a rejection.
        Assert.AreEqual("cao-ggz.pdf", result.Families["cao-ggz.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_NothingChanged_SkipsTheStoreWriteAsWellAsTheEmbedding()
    {
        // An unchanged document already skips the embedding call. It used to be persisted
        // anyway - one blob write per document per run, storing bytes identical to what was
        // already there. The hash says the identity is unchanged; the remaining fields are
        // compared so a family change (which does not touch the identity text) still writes.
        var client = new Mock<IEmbeddingClient>();
        var doc    = Doc("cao-ggz.pdf", "CAO GGZ");

        var identityText = $"{ModelId}\nCAO GGZ\nGGZ";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identityText)));

        var store = new Mock<IDocumentIdentityStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DocumentIdentityRecord(
                "cao-ggz.pdf", "CAO GGZ", "GGZ", [1f, 0f, 0f], "cao-ggz.pdf", hash, ModelId)]);

        var result = await Build(client, store).ResolveDocumentIdentityAsync([doc]);

        client.Verify(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.SetAsync(It.IsAny<DocumentIdentityRecord>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.AreEqual(0, result.Diagnostics.RecordsWritten);
        Assert.AreEqual(1, result.Diagnostics.RecordsUnchanged);
        Assert.AreEqual("cao-ggz.pdf", result.Families["cao-ggz.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_FamilyChangedButIdentityTextDidNot_StillWrites()
    {
        // The case the hash alone cannot see. This document's own title and headings are
        // untouched, so it is not re-embedded - but it now clusters with a larger established
        // family, which keeps its own id, so this document's FamilyId changes. Skipping the
        // write on an unchanged hash would leave the store disagreeing with what its chunks
        // carry.
        var identityText = $"{ModelId}\nCAO GGZ\nGGZ";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identityText)));

        var persisted = new[]
        {
            // In this run, identity unchanged, currently a family of one.
            new DocumentIdentityRecord("a-doc.pdf", "CAO GGZ", "GGZ", [1f, 0f, 0f], "family-of-one", hash, ModelId),
            // Two older members of a bigger family, clustering with it.
            new DocumentIdentityRecord("b-doc.pdf", "CAO GHZ", "GHZ", [1f, 0f, 0f], "cao", "b-hash", ModelId),
            new DocumentIdentityRecord("c-doc.pdf", "CAO VVT", "VVT", [1f, 0f, 0f], "cao", "c-hash", ModelId),
        };

        var (client, store) = BuildMocks(new Dictionary<string, float[]>(), persisted);

        var result = await Build(client, store).ResolveDocumentIdentityAsync([Doc("a-doc.pdf", "CAO GGZ")]);

        // Not re-embedded - the identity text is unchanged.
        client.Verify(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);

        // The bigger family keeps its id, so this document joins it rather than renaming it.
        Assert.AreEqual("cao", result.Families["a-doc.pdf"].FamilyId);

        // ...and that change is written, despite the hash matching.
        store.Verify(s => s.SetAsync(
            It.Is<DocumentIdentityRecord>(r => r.SourceId == "a-doc.pdf" && r.FamilyId == "cao"),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual(1, result.Diagnostics.RecordsWritten);
    }

    [TestMethod]
    public async Task ResolveAsync_SetsDomainTagFromTitle()
    {
        var vectors = new Dictionary<string, float[]> { ["CAO GGZ"] = [1f, 0f, 0f] };
        var (client, store) = BuildMocks(vectors);
        var docs = new[] { Doc("cao-ggz.pdf", "CAO GGZ") };

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreEqual("GGZ", result.Families["cao-ggz.pdf"].DomainTag);
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

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        // The new document JOINS the established family rather than renaming it after itself.
        // Under the old smallest-SourceId scheme this returned "checklist-medido.pdf" and
        // re-homed the older document - the §7a instability, which also meant the older
        // document's already-uploaded Search chunks disagreed with the store.
        Assert.AreEqual("handleiding-medido.pdf", result.Families["checklist-medido.pdf"].FamilyId);

        // And because the established id was kept, the older document's record needs no
        // correction at all.
        store.Verify(s => s.SetAsync(
            It.Is<DocumentIdentityRecord>(r => r.SourceId == "handleiding-medido.pdf"),
            It.IsAny<CancellationToken>()), Times.Never);
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

        var result = await Build(client, store).ResolveDocumentIdentityAsync([doc]);

        client.Verify(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.AreEqual("cao-ggz.pdf", result.Families["cao-ggz.pdf"].FamilyId);
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

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreNotEqual(result.Families["checklist-medido.pdf"].FamilyId, result.Families["handleiding-medimo.pdf"].FamilyId);
        CollectionAssert.Contains(result.Families["checklist-medido.pdf"].ConfusableWith.ToList(), "handleiding-medimo.pdf");
        CollectionAssert.Contains(result.Families["handleiding-medimo.pdf"].ConfusableWith.ToList(), "checklist-medido.pdf");
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

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreEqual(0, result.Families["cao-ggz.pdf"].ConfusableWith.Count);
        Assert.AreEqual(0, result.Families["cao-ghz.pdf"].ConfusableWith.Count);
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

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreEqual("checklist-medido.pdf", result.Families["checklist-medido.pdf"].FamilyId);
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

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreEqual("checklist-medido.pdf", result.Families["checklist-medido.pdf"].FamilyId);
    }

    [TestMethod]
    public async Task ResolveAsync_StampsCurrentModelIdOnPersistedRecords()
    {
        var vectors = new Dictionary<string, float[]> { ["CAO GGZ"] = [1f, 0f, 0f] };
        var (client, store) = BuildMocks(vectors);

        await Build(client, store).ResolveDocumentIdentityAsync([Doc("cao-ggz.pdf", "CAO GGZ")]);

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
            () => Build(client, store).ResolveDocumentIdentityAsync(docs));

        store.Verify(s => s.SetAsync(It.IsAny<DocumentIdentityRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ResolveAsync_NoDocuments_ReturnsEmpty()
    {
        var (client, store) = BuildMocks([]);

        var result = await Build(client, store).ResolveDocumentIdentityAsync([]);

        Assert.AreEqual(0, result.Families.Count);
        store.Verify(s => s.SetAsync(It.IsAny<DocumentIdentityRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ResolveAsync_EmbeddingClientReturnsWrongVectorDimensions_Throws()
    {
        // A wrong-dimension vector is silent without this check: CosineSimilarity bails to 0
        // when the lengths differ, so the document compares as maximally dissimilar to
        // everything and becomes its own family - indistinguishable from a corpus that simply
        // has no near-duplicates. Every sibling embedder validates this; this one didn't.
        var client = new Mock<IEmbeddingClient>();
        client
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new float[][] { [1f, 0f] }, 0));

        var store = new Mock<IDocumentIdentityStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Build(client, store).ResolveDocumentIdentityAsync([Doc("cao-ggz.pdf", "CAO GGZ")]));

        StringAssert.Contains(ex.Message, "cao-ggz.pdf");
        store.Verify(s => s.SetAsync(It.IsAny<DocumentIdentityRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ResolveAsync_PersistedVectorWithWrongDimensions_IsExcludedFromClustering()
    {
        // Same failure the check above prevents going forward, for records written before it
        // existed: held out rather than compared, since cosine against a different-length
        // vector is 0 by definition and would look like a real "not similar" answer.
        var vectors = new Dictionary<string, float[]> { ["Checklist Inzet Medicijndispenser"] = [1f, 0f, 0f] };
        var persisted = new[]
        {
            new DocumentIdentityRecord("handleiding-medido.pdf", "Handleiding Medido", null, [1f, 0f], "handleiding-medido.pdf", "old-hash", ModelId),
        };
        var (client, store) = BuildMocks(vectors, persisted);

        var result = await Build(client, store).ResolveDocumentIdentityAsync([Doc("checklist-medido.pdf", "Checklist Inzet Medicijndispenser")]);

        Assert.AreEqual("checklist-medido.pdf", result.Families["checklist-medido.pdf"].FamilyId);
        store.Verify(s => s.SetAsync(
            It.Is<DocumentIdentityRecord>(r => r.SourceId == "handleiding-medido.pdf"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ResolveAsync_DuplicateSourceIdsInOneRun_ThrowsBeforeEmbedding()
    {
        var (client, store) = BuildMocks(new Dictionary<string, float[]> { ["CAO GGZ"] = [1f, 0f, 0f] });
        var docs = new[] { Doc("cao-ggz.pdf", "CAO GGZ"), Doc("cao-ggz.pdf", "CAO GGZ") };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Build(client, store).ResolveDocumentIdentityAsync(docs));

        StringAssert.Contains(ex.Message, "cao-ggz.pdf");
        client.Verify(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ResolveAsync_DocumentWithNoTitleAndNoHeadings_IsSkipped()
    {
        // Nothing to embed, so there is nothing to cluster on. Should be unreachable - Title
        // has a filename fallback - which is why it is cheap to state rather than discover as
        // a junk single-member family later.
        var vectors = new Dictionary<string, float[]> { ["CAO GGZ"] = [1f, 0f, 0f] };
        var (client, store) = BuildMocks(vectors);
        var docs = new[] { Doc("cao-ggz.pdf", "CAO GGZ"), Doc("blank.pdf", "   ") };

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.IsFalse(result.Families.ContainsKey("blank.pdf"));
        Assert.AreEqual("cao-ggz.pdf", result.Families["cao-ggz.pdf"].FamilyId);
        store.Verify(s => s.SetAsync(
            It.Is<DocumentIdentityRecord>(r => r.SourceId == "blank.pdf"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ResolveAsync_DutchPluralOfTheSameWord_IsNotConfusable()
    {
        // "Handleiding" vs "Handleidingen" is 2 edits, ratio 2/13 = 0.15 - under both
        // thresholds, so the constants alone do not stop it. They are the same word inflected,
        // and flagging them would pair every manual in the corpus with every other.
        var vectors = new Dictionary<string, float[]>
        {
            ["Handleiding Medicijndispenser"] = [1f, 0f, 0f],
            ["Handleidingen Zorgdossier"]      = [0f, 1f, 0f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[]
        {
            Doc("handleiding.pdf",   "Handleiding Medicijndispenser"),
            Doc("handleidingen.pdf", "Handleidingen Zorgdossier"),
        };

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreNotEqual(result.Families["handleiding.pdf"].FamilyId, result.Families["handleidingen.pdf"].FamilyId);
        Assert.AreEqual(0, result.Families["handleiding.pdf"].ConfusableWith.Count);
        Assert.AreEqual(0, result.Families["handleidingen.pdf"].ConfusableWith.Count);
    }

    [TestMethod]
    public async Task ResolveAsync_TwoEditWordPairs_AreNotConfusable()
    {
        // Calibrated against the 2026-08-14 run, where 40 of 44 confusable flags were noise and
        // every one of them sat at exactly 2 edits: HANDREIKING/Handleiding (30x),
        // werken/inwerken (6x), Infografic/Infographic (4x). Only Medido/Medimo - 1 edit - was
        // real. See calibration-findings.md §3.
        var vectors = new Dictionary<string, float[]>
        {
            ["Handreiking begeleiden"] = [1f, 0f, 0f],
            ["Handleiding Medido"]     = [0f, 1f, 0f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[]
        {
            Doc("handreiking.pdf", "Handreiking begeleiden"),
            Doc("handleiding.pdf", "Handleiding Medido"),
        };

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreEqual(0, result.Families["handreiking.pdf"].ConfusableWith.Count);
        Assert.AreEqual(0, result.Families["handleiding.pdf"].ConfusableWith.Count);
    }

    [TestMethod]
    public async Task ResolveAsync_WordContainedInAnother_IsNotConfusable_AtEitherEnd()
    {
        // "werken" is a SUFFIX of "inwerken", which the original prefix-only guard missed - it
        // showed up 6 times in the live run.
        var vectors = new Dictionary<string, float[]>
        {
            ["Checklist inwerken Ouderenzorg"] = [1f, 0f, 0f],
            ["Gezond werken in de Wijk"]       = [0f, 1f, 0f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[]
        {
            Doc("inwerken.pdf", "Checklist inwerken Ouderenzorg"),
            Doc("werken.pdf",   "Gezond werken in de Wijk"),
        };

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        Assert.AreEqual(0, result.Families["inwerken.pdf"].ConfusableWith.Count);
        Assert.AreEqual(0, result.Families["werken.pdf"].ConfusableWith.Count);
    }

    [TestMethod]
    public async Task ResolveAsync_ConfusableWith_IsOrdinallySorted()
    {
        // Persisted onto every chunk of the document, so the order has to be a property of the
        // data rather than of dictionary enumeration.
        var vectors = new Dictionary<string, float[]>
        {
            ["Handleiding Medido"]  = [1f, 0f, 0f],
            ["Werkinstructie Medimo"] = [0f, 1f, 0f],
            ["Protocol Medivo"]     = [0f, 0f, 1f],
        };
        var (client, store) = BuildMocks(vectors);
        var docs = new[]
        {
            Doc("z-handleiding.pdf", "Handleiding Medido"),
            Doc("m-werkinstructie.pdf", "Werkinstructie Medimo"),
            Doc("a-protocol.pdf", "Protocol Medivo"),
        };

        var result = await Build(client, store).ResolveDocumentIdentityAsync(docs);

        var confusable = result.Families["z-handleiding.pdf"].ConfusableWith.ToList();
        CollectionAssert.AreEqual(
            confusable.OrderBy(id => id, StringComparer.Ordinal).ToList(), confusable);
        Assert.AreEqual(2, confusable.Count);
    }
}
