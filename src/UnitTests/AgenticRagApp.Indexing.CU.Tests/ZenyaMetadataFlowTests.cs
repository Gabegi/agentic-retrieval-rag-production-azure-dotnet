using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Clients.Zenya.Models;
using AgenticRagApp.Infrastructure.Clients.Zenya.Sync;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Indexing;

// The Zenya facts' whole path through the indexer (D204 §9, 2026-09-21):
//
//   ZenyaBlobLayout.BuildMetadata (the sync's WRITE side, Infrastructure)
//     -> blob metadata
//     -> ZenyaMetadata.FromBlobMetadata (the READ side, here)         [1]
//     -> IndexDiffService.ListDocumentsInBlobAsync -> PdfBlobInfo.Zenya  [2]
//     -> ExtractionOutputBuilder -> PdfExtractionDocument.Zenya          [3]
//     -> DocumentStamp.From / StampOnto -> ChunkMetadata                 [4]
//     -> SearchUploadChunk.From / SnapshotChunk.From                     [5]
//
// [1] is pinned as a ROUND TRIP through the real writer, so a renamed or re-encoded key on
// either side fails here rather than as a silently-null field months later. The rest pin that
// each hop carries the record, that the manual corpus (no keys) stays null end to end, and the
// one precedence rule DocumentStamp introduces (ModDate).
[TestClass]
public class ZenyaMetadataFlowTests
{
    private const string DocId = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
    private static readonly DateTimeOffset SyncedAt = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    // The full legacy DTO as the tenant returns it (D204 §2b), with Dutch text where the
    // percent-encoding matters.
    private static ZenyaDocumentMetadata FullDto() => new(
        DocId, 3, 7, "Handhygiëne protocol", "file", new ZenyaDocumentTypeMini(3, "Protocol"),
        "application/pdf", "pdf", false, true, false, "HH-01", true, "published", "20260315101500",
        Folder: new ZenyaFolderMini(42, "Hygiëne", "Contoso/Zorg/Hygiëne"),
        Summary: "Korte samenvatting van het protocol.",
        CheckDate: "20270101",
        AttentionRequiredFlags: ["check_date_approaches"],
        CanCheckDocument: true,
        CheckTaskDelegatedToUser: new ZenyaUserMini("u-9", "Eef Mol"),
        Language: "nl",
        OriginalType: "file",
        ParsedHeader: "Kop",
        UnparsedHeader: "Kop onbewerkt",
        PrintHeaderRequired: true,
        Authors: [new ZenyaUserMini("u-1", "Ana Jansen"), new ZenyaUserMini("u-2", "Bo de Vries")],
        Authorizers: [new ZenyaUserMini("u-3", "Cas Smit")],
        DocumentAdministrators: [new ZenyaUserMini("u-5", "Fay Kok")],
        LockInfo: new ZenyaLockInfo(true, "2026-09-20T10:00:00Z", new ZenyaUserMini("u-4", "Dex Bos")),
        MarkedAsFavorite: false,
        IsPrintable: true);

    private static IReadOnlyDictionary<string, string> WrittenMetadata() =>
        ZenyaBlobLayout.BuildMetadata(FullDto(), "application/pdf", SyncedAt);

    // ---- [1] read side, as a round trip through the writer ----------------------------------

    [TestMethod]
    public void FromBlobMetadata_RoundTripsEveryFieldTheSyncWrites()
    {
        var z = ZenyaMetadata.FromBlobMetadata(WrittenMetadata());

        Assert.IsTrue(z.IsPresent);
        Assert.AreEqual(DocId, z.DocumentId);
        Assert.AreEqual(3, z.Version);
        Assert.AreEqual(7, z.Revision);
        Assert.AreEqual("published", z.Status);
        Assert.AreEqual(true, z.Active);
        Assert.IsNull(z.Url, "the sync writes no zenya_url (D094) - absent, not constructed");

        // Percent-encoded on the way in, decoded on the way out: the Dutch survives.
        Assert.AreEqual("HH-01", z.QuickCode);
        Assert.AreEqual("Handhygiëne protocol", z.Title);
        Assert.AreEqual("Korte samenvatting van het protocol.", z.Summary);
        Assert.AreEqual("file", z.Type);
        Assert.AreEqual("Protocol", z.DocumentType);
        Assert.AreEqual("application/pdf", z.MimeType);
        Assert.AreEqual("file", z.OriginalType);
        Assert.AreEqual(false, z.DownloadAsPdf);
        Assert.AreEqual("Contoso/Zorg/Hygiëne", z.FolderPath);
        Assert.AreEqual("Hygiëne", z.FolderName);
        Assert.AreEqual(42, z.FolderId);
        Assert.AreEqual("nl", z.Language);

        // Zenya's two documented date shapes, parsed as UTC; raw kept alongside.
        Assert.AreEqual(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), z.CheckDate);
        Assert.AreEqual("20270101", z.CheckDateRaw);
        Assert.AreEqual(new DateTimeOffset(2026, 3, 15, 10, 15, 0, TimeSpan.Zero), z.LastModified);
        Assert.AreEqual("20260315101500", z.LastModifiedRaw);
        CollectionAssert.AreEqual(new[] { "check_date_approaches" }, z.AttentionFlags.ToList());
        Assert.AreEqual(true, z.CanCheckDocument);
        Assert.AreEqual("Eef Mol", z.CheckDelegatedTo);

        Assert.AreEqual("Kop", z.ParsedHeader);
        Assert.AreEqual("Kop onbewerkt", z.UnparsedHeader);
        Assert.AreEqual(true, z.PrintHeaderRequired);

        // "; "-joined by the writer, split back here.
        CollectionAssert.AreEqual(new[] { "Ana Jansen", "Bo de Vries" }, z.Authors.ToList());
        CollectionAssert.AreEqual(new[] { "Cas Smit" }, z.Authorizers.ToList());
        CollectionAssert.AreEqual(new[] { "Fay Kok" }, z.DocumentAdministrators.ToList());
        Assert.AreEqual(0, z.WritersGroup.Count, "not on the DTO -> no key -> empty, never null");

        Assert.AreEqual(true, z.Locked);
        Assert.AreEqual("2026-09-20T10:00:00Z", z.LockedSince);
        Assert.AreEqual("Dex Bos", z.LockedBy);
        Assert.AreEqual(false, z.MarkedAsFavorite);
        Assert.AreEqual(true, z.IsPrintable);
        Assert.IsNull(z.IsEditableForm, "not on the DTO -> absent");
        Assert.AreEqual(SyncedAt, z.SyncedAt);
    }

    [TestMethod]
    public void FromBlobMetadata_OnAManualCorpusBlob_IsNotPresentAndAllNull()
    {
        var z = ZenyaMetadata.FromBlobMetadata(new Dictionary<string, string>());

        Assert.IsFalse(z.IsPresent);
        Assert.IsNull(z.DocumentId);
        Assert.IsNull(z.Version);
        Assert.IsNull(z.CheckDate);
        Assert.AreEqual(0, z.Authors.Count);
        Assert.IsTrue(z.IsActive, "fail open: an unannotated blob is active, not excluded");
        Assert.IsFalse(ZenyaMetadata.Empty.IsPresent);
    }

    [TestMethod]
    public void IsActive_AuthoritativeFlagBeatsTheStatusStringFallback()
    {
        // The flag is the fact; the status-string list is only the pre-2026-09-21 rule kept for a
        // blob that carries a status but no flag.
        Assert.IsFalse(new ZenyaMetadata { Active = false, Status = "published" }.IsActive);
        Assert.IsTrue(new ZenyaMetadata { Active = true, Status = "ingetrokken" }.IsActive);
        Assert.IsFalse(new ZenyaMetadata { Status = "ingetrokken" }.IsActive);
        Assert.IsFalse(new ZenyaMetadata { Status = "Withdrawn" }.IsActive);
        Assert.IsTrue(new ZenyaMetadata { Status = "published" }.IsActive);
    }

    [TestMethod]
    public void ParseZenyaDate_ExactDocumentedShapesOnly_NeverAGuess()
    {
        Assert.AreEqual(new DateTimeOffset(2026, 3, 15, 10, 15, 0, TimeSpan.Zero), ZenyaMetadata.ParseZenyaDate("20260315101500"));
        Assert.AreEqual(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),    ZenyaMetadata.ParseZenyaDate("20270101"));
        Assert.AreEqual(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),    ZenyaMetadata.ParseZenyaDate(" 20270101 "));
        Assert.IsNull(ZenyaMetadata.ParseZenyaDate("2026-03-15"),  "ISO is not one of Zenya's shapes");
        Assert.IsNull(ZenyaMetadata.ParseZenyaDate("202603"),      "partial");
        Assert.IsNull(ZenyaMetadata.ParseZenyaDate("73922491"),    "the swagger's placeholder is not a date");
        Assert.IsNull(ZenyaMetadata.ParseZenyaDate(""));
        Assert.IsNull(ZenyaMetadata.ParseZenyaDate(null));
    }

    // ---- [2] the listing no longer discards the metadata ------------------------------------

    [TestMethod]
    public async Task IndexDiff_CarriesDecodedZenyaOnTheEntry_AndNullForAManualBlob()
    {
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                ($"pdf/{DocId}.pdf", (DateTimeOffset?)DateTimeOffset.Parse("2026-09-20T18:00:00Z"), (long?)1000, WrittenMetadata()),
                ("Gedragscode (Versie 2).pdf", DateTimeOffset.Parse("2026-09-20T18:00:00Z"), 2000, new Dictionary<string, string>()),
                ("notes.docx", DateTimeOffset.Parse("2026-09-20T18:00:00Z"), 10, WrittenMetadata()),
            ]);
        var indexService = new Mock<IIndexDocumentService>();
        indexService.Setup(m => m.GetCurrentlyIndexedDocsIdsNDatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));

        var diff = await new IndexDiffService(
                new Mock<BlobContainerClient>().Object, blobStore.Object, indexService.Object,
                NullLogger<IndexDiffService>.Instance)
            .FindDocsNotInIndexAsync(forceReindex: false);

        Assert.AreEqual(2, diff.EntriesToProcess.Count, "the .docx never enters the PDF listing");
        var synced = diff.EntriesToProcess[$"pdf/{DocId}.pdf"];
        Assert.IsNotNull(synced.Zenya);
        Assert.AreEqual(3, synced.Zenya!.Version);
        Assert.AreEqual("HH-01", synced.Zenya.QuickCode);
        Assert.IsNull(diff.EntriesToProcess["Gedragscode (Versie 2).pdf"].Zenya, "no zenya_document_id -> null, not Empty");
    }

    // ---- [3] extraction carries it ----------------------------------------------------------

    [TestMethod]
    public void BuildDocuments_CarriesZenyaOffTheListingEntry()
    {
        var zenya = ZenyaMetadata.FromBlobMetadata(WrittenMetadata());
        var documents = ExtractionOutputBuilder.BuildDocuments(
            [OkFile("a.pdf"), OkFile("b.pdf")],
            new Dictionary<string, PdfBlobInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["a.pdf"] = new PdfBlobInfo(SyncedAt, 1, zenya),
                ["b.pdf"] = new PdfBlobInfo(SyncedAt, 1),
            });

        Assert.AreSame(zenya, documents[0].Zenya);
        Assert.IsNull(documents[1].Zenya);
    }

    // ---- [4] the stamp ------------------------------------------------------------------------

    [TestMethod]
    public void DocumentStamp_StampsEverySourceFieldOntoTheChunk_AndKeepsLookAlikesApart()
    {
        var zenya = ZenyaMetadata.FromBlobMetadata(WrittenMetadata());
        var doc = Doc("CAO GGZ 2024 2026 v2", zenya) with { Language = "en" };

        var metadata = new ChunkMetadata();
        DocumentStamp.From(doc, "DeclaredBoundary").StampOnto(metadata);

        Assert.AreEqual(DocId, metadata.SourceDocumentId);
        Assert.AreEqual("3", metadata.SourceVersion);
        Assert.AreEqual("7", metadata.SourceRevision);
        Assert.AreEqual("published", metadata.SourceStatus);
        Assert.AreEqual(true, metadata.SourceActive);
        Assert.AreEqual("HH-01", metadata.QuickCode);
        Assert.AreEqual("Contoso/Zorg/Hygiëne", metadata.FolderPath);
        Assert.AreEqual("Hygiëne", metadata.FolderName);
        Assert.AreEqual("file", metadata.SourceType);
        Assert.AreEqual("Protocol", metadata.SourceDocumentType);
        Assert.AreEqual("Korte samenvatting van het protocol.", metadata.Summary);
        Assert.AreEqual(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), metadata.CheckDate);
        CollectionAssert.AreEqual(new[] { "check_date_approaches" }, metadata.AttentionFlags.ToList());
        CollectionAssert.AreEqual(new[] { "Ana Jansen", "Bo de Vries" }, metadata.Authors.ToList());
        CollectionAssert.AreEqual(new[] { "Cas Smit" }, metadata.Authorizers.ToList());
        CollectionAssert.AreEqual(new[] { "Fay Kok" }, metadata.DocumentAdministrators.ToList());

        // The look-alikes are NOT overridden - each pair is kept apart on purpose (DocumentStamp).
        Assert.AreEqual("CAO GGZ 2024 2026 v2", metadata.Title,   "title stays CU's");
        Assert.AreEqual("Handhygiëne protocol", metadata.SourceTitle);
        Assert.AreEqual("en", metadata.Language,                 "language stays the detector's");
        Assert.AreEqual("nl", metadata.SourceLanguage);
        Assert.AreEqual("2", metadata.Version,                   "version stays the title regex");
        Assert.AreEqual(2026, metadata.ValidTo!.Value.Year, "valid_to stays the title's period (2026), not check_date (2027)");
    }

    [TestMethod]
    public void DocumentStamp_ModDate_FilledFromZenyaOnlyWhenTheDocumentHasNone()
    {
        var zenya = ZenyaMetadata.FromBlobMetadata(WrittenMetadata());
        var own   = DateTimeOffset.Parse("2020-01-01T00:00:00Z");

        Assert.AreEqual(zenya.LastModified, DocumentStamp.From(Doc("t", zenya), "r").ModDate,
            "the empty slot takes Zenya's last_modified_datetime");
        Assert.AreEqual(own, DocumentStamp.From(Doc("t", zenya) with { ModDate = own }, "r").ModDate,
            "a value the document already has wins");
        Assert.IsNull(DocumentStamp.From(Doc("t", zenya: null), "r").ModDate);
    }

    [TestMethod]
    public void DocumentStamp_WithoutZenya_LeavesEverySourceFieldNullOrEmpty()
    {
        var metadata = new ChunkMetadata();
        DocumentStamp.From(Doc("Titel", zenya: null), "r").StampOnto(metadata);

        Assert.IsNull(metadata.SourceDocumentId);
        Assert.IsNull(metadata.SourceVersion);
        Assert.IsNull(metadata.QuickCode);
        Assert.IsNull(metadata.FolderPath);
        Assert.IsNull(metadata.Summary);
        Assert.IsNull(metadata.CheckDate);
        Assert.AreEqual(0, metadata.AttentionFlags.Count);
        Assert.AreEqual(0, metadata.Authors.Count);
    }

    // ---- [5] projection and snapshot --------------------------------------------------------

    [TestMethod]
    public void SearchUploadChunk_ProjectsTheSourceFields_ButNotThePersons()
    {
        var chunk = Chunk(ZenyaMetadata.FromBlobMetadata(WrittenMetadata()));

        var upload = SearchUploadChunk.From(chunk);

        Assert.AreEqual(DocId, upload.SourceDocumentId);
        Assert.AreEqual("3", upload.SourceVersion);
        Assert.AreEqual("HH-01", upload.QuickCode);
        Assert.AreEqual("Contoso/Zorg/Hygiëne", upload.FolderPath);
        Assert.AreEqual("Korte samenvatting van het protocol.", upload.Summary);
        Assert.AreEqual(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), upload.CheckDate);
        CollectionAssert.AreEqual(new[] { "check_date_approaches" }, upload.AttentionFlags!.ToList());

        // D204 §3d: the persons are on the chunk and stop there.
        Assert.IsFalse(typeof(SearchUploadChunk).GetProperties().Any(p => p.Name is "Authors" or "Authorizers" or "DocumentAdministrators"));
    }

    [TestMethod]
    public void SnapshotChunk_CarriesTheSourceFields_SoARestoreRebuildsThem()
    {
        var snapshot = SnapshotChunk.From(Chunk(ZenyaMetadata.FromBlobMetadata(WrittenMetadata())));

        Assert.AreEqual(DocId, snapshot.SourceDocumentId);
        Assert.AreEqual("3", snapshot.SourceVersion);
        Assert.AreEqual("7", snapshot.SourceRevision);
        Assert.AreEqual("published", snapshot.SourceStatus);
        Assert.AreEqual(true, snapshot.SourceActive);
        Assert.AreEqual("Handhygiëne protocol", snapshot.SourceTitle);
        Assert.AreEqual("nl", snapshot.SourceLanguage);
        Assert.AreEqual("HH-01", snapshot.QuickCode);
        Assert.AreEqual("Contoso/Zorg/Hygiëne", snapshot.FolderPath);
        Assert.AreEqual("Hygiëne", snapshot.FolderName);
        Assert.AreEqual("file", snapshot.SourceType);
        Assert.AreEqual("Protocol", snapshot.SourceDocumentType);
        Assert.AreEqual("Korte samenvatting van het protocol.", snapshot.Summary);
        Assert.AreEqual(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), snapshot.CheckDate);
        CollectionAssert.AreEqual(new[] { "check_date_approaches" }, snapshot.AttentionFlags!.ToList());
    }

    // ---- fixtures -----------------------------------------------------------------------------

    private static ExtractedFile OkFile(string blobName) =>
        new(true, blobName,
            Content:   $"Inhoud van {blobName}",
            PageSpans: [new PageSpan(1, 0, 5, null)],
            Structure: new PdfDocumentStructure([], [], [], [], [], [], []),
            Title:     blobName,
            Language:  "nl",
            Usage:     null,
            Error:     null,
            Warnings:  []);

    private static PdfExtractionDocument Doc(string title, ZenyaMetadata? zenya) =>
        new(SourceId:         $"pdf/{DocId}.pdf",
            Content:          "Inhoud.",
            PageSpans:        [new PageSpan(1, 0, 7, null)],
            Title:            title,
            Author:           null,
            CreatedAt:        null,
            ModDate:          null,
            PageCount:        1,
            LastModifiedDate: SyncedAt,
            PageBreadcrumbs:  new Dictionary<int, string>(),
            Sections:         [],
            Headings:         [],
            Boilerplate:      [],
            Tables:           [],
            Figures:          [],
            Annotations:      [],
            Hyperlinks:       [],
            Language:         "nl",
            Zenya:            zenya);

    private static ChunkObject Chunk(ZenyaMetadata zenya)
    {
        var metadata = new ChunkMetadata { Id = "c1", DocumentId = $"pdf/{DocId}.pdf", Prefix = "" };
        DocumentStamp.From(Doc("Titel", zenya), "r").StampOnto(metadata);
        return new ChunkObject { Content = "Inhoud.", Start = 0, Length = 7, Metadata = metadata };
    }
}
