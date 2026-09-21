using System.Text;
using AgenticRagApp.Infrastructure.Clients.Zenya;
using AgenticRagApp.Infrastructure.Clients.Zenya.Models;
using AgenticRagApp.Infrastructure.Clients.Zenya.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace RagApp.UnitTests.Infrastructure.Zenya;

// ZenyaSyncService against an in-memory Zenya and an in-memory container. What is pinned: the
// container layout (pdf/ vs docs/, name = document_id), the metadata contract, that an unchanged
// version costs no metadata call and no download, that removal only touches blobs the sync
// wrote, that a dry run writes nothing, and that one failing document does not end the run.
[TestClass]
public class ZenyaSyncServiceTests
{
    private const string PdfId  = "11111111-1111-1111-1111-111111111111";
    private const string DocxId = "22222222-2222-2222-2222-222222222222";
    private const string HtmlId = "33333333-3333-3333-3333-333333333333";

    private static readonly byte[] PdfBytes  = Encoding.ASCII.GetBytes("%PDF-1.7 fake");
    private static readonly byte[] DocxBytes = Encoding.ASCII.GetBytes("PK fake docx");

    // ---- fakes -------------------------------------------------------------------------------

    private sealed class FakeZenya : IZenyaClient
    {
        public List<ZenyaDocumentListItem> Listing { get; } = [];
        public Dictionary<string, ZenyaDocumentMetadata> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (byte[] Bytes, string? ContentType)> Downloads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FailMetadataFor { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int MetadataCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public FakeZenya WithBinary(string id, int version, string title, string ext, string mime, byte[] bytes, string? quickCode = null)
        {
            Listing.Add(new ZenyaDocumentListItem(id, version, title, null, null));
            Metadata[id] = new ZenyaDocumentMetadata(id, version, null, title, "binary", new ZenyaDocumentTypeMini(3, "Protocol"), mime, ext, null, true, false, quickCode, true, "published", "20260901120000");
            Downloads[id] = (bytes, mime);
            return this;
        }

        public FakeZenya WithAuthored(string id, int version, string title)
        {
            Listing.Add(new ZenyaDocumentListItem(id, version, title, null, null));
            Metadata[id] = new ZenyaDocumentMetadata(id, version, null, title, "modern_structured_document", null, null, null, null, false, true, null, true, "published", null);
            return this;
        }

        public Task<ZenyaUser> GetCurrentUserAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ZenyaUser> EnsureAuthenticatedAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ZenyaDocumentListItem> ListDocumentsAsync(IReadOnlyCollection<string>? states = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in Listing) { await Task.Yield(); yield return item; }
        }

        public Task<ZenyaDocumentMetadata> GetDocumentAsync(string documentId, CancellationToken ct = default)
        {
            MetadataCalls++;
            if (FailMetadataFor.Contains(documentId))
                throw new ZenyaApiException(System.Net.HttpStatusCode.Forbidden, "forbidden", null, "no read rights");
            return Task.FromResult(Metadata[documentId]);
        }

        public Task<ZenyaDownload> DownloadAsync(string documentId, int version, CancellationToken ct = default)
        {
            DownloadCalls++;
            var (bytes, contentType) = Downloads[documentId];
            return Task.FromResult(new ZenyaDownload(new HttpResponseMessage(), new MemoryStream(bytes), contentType, bytes.Length));
        }

        public Task<ZenyaDocumentContent> GetContentsAsync(string documentId, int version, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class InMemoryStore : IZenyaDocumentStore
    {
        public Dictionary<string, (byte[] Bytes, string? ContentType, IReadOnlyDictionary<string, string> Metadata)> Blobs { get; } = new(StringComparer.Ordinal);
        public List<string> Uploads { get; } = [];
        public List<string> Deletes { get; } = [];

        public InMemoryStore WithSynced(string blobName, string documentId, int version)
        {
            Blobs[blobName] = ([], null, new Dictionary<string, string>
            {
                [ZenyaBlobLayout.DocumentIdKey] = documentId,
                [ZenyaBlobLayout.VersionKey] = version.ToString(),
            });
            return this;
        }

        public InMemoryStore WithForeign(string blobName)
        {
            Blobs[blobName] = ([], null, new Dictionary<string, string>());
            return this;
        }

        public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<StoredZenyaBlob>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredZenyaBlob>>(Blobs.Select(kv => new StoredZenyaBlob(kv.Key, kv.Value.Metadata)).ToList());

        public async Task UploadAsync(string blobName, Stream content, string? contentType, IReadOnlyDictionary<string, string> metadata, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Blobs[blobName] = (ms.ToArray(), contentType, metadata);
            Uploads.Add(blobName);
        }

        public Task DeleteAsync(string blobName, CancellationToken ct = default)
        {
            Blobs.Remove(blobName);
            Deletes.Add(blobName);
            return Task.CompletedTask;
        }
    }

    private static ZenyaSyncService Build(FakeZenya zenya, InMemoryStore store, bool dryRun = false) =>
        new(zenya, store,
            new ZenyaSyncOptions { StorageAccountUrl = new Uri("https://acct.blob.core.windows.net"), StorageContainer = "zenya-documents", DryRun = dryRun },
            NullLogger<ZenyaSyncService>.Instance);

    // ---- layout + metadata ----------------------------------------------------------------------

    [TestMethod]
    public async Task NewPdf_LandsUnderPdfPrefix_WithEncodedMetadata()
    {
        var zenya = new FakeZenya().WithBinary(PdfId, 3, "Handhygiëne protocol", "pdf", "application/pdf", PdfBytes, quickCode: "HH-01");
        var store = new InMemoryStore();

        var result = await Build(zenya, store).RunAsync();

        Assert.AreEqual(1, result.New);
        Assert.AreEqual(0, result.Changed);
        var (bytes, contentType, metadata) = store.Blobs[$"pdf/{PdfId}.pdf"];
        CollectionAssert.AreEqual(PdfBytes, bytes);
        Assert.AreEqual("application/pdf", contentType);
        Assert.AreEqual(PdfId, metadata[ZenyaBlobLayout.DocumentIdKey]);
        Assert.AreEqual("3", metadata[ZenyaBlobLayout.VersionKey]);
        Assert.AreEqual("published", metadata[ZenyaBlobLayout.StatusKey]);
        Assert.AreEqual("HH-01", metadata[ZenyaBlobLayout.QuickCodeKey]);
        Assert.AreEqual("Protocol", metadata[ZenyaBlobLayout.DocumentTypeKey]);
        Assert.AreEqual(Uri.EscapeDataString("Handhygiëne protocol"), metadata[ZenyaBlobLayout.TitleKey]);
        Assert.IsTrue(metadata[ZenyaBlobLayout.TitleKey].All(c => c < 128), "metadata values must be ASCII");
        Assert.AreEqual("20260901120000", metadata[ZenyaBlobLayout.LastModifiedKey]);
        Assert.AreEqual(1, result.WrittenByExtension["pdf"]);
        Assert.AreEqual(0, result.PdfWithoutMagic);
    }

    [TestMethod]
    public async Task NewWord_LandsUnderDocsPrefix()
    {
        const string docxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        var zenya = new FakeZenya().WithBinary(DocxId, 1, "Werkinstructie", "docx", docxMime, DocxBytes);
        var store = new InMemoryStore();

        var result = await Build(zenya, store).RunAsync();

        Assert.IsTrue(store.Blobs.ContainsKey($"docs/{DocxId}.docx"));
        Assert.AreEqual(1, result.WrittenByExtension["docx"]);
        Assert.AreEqual(0, result.PdfWithoutMagic, "not routed to pdf/, so no magic check");
    }

    [TestMethod]
    public async Task ResponseContentType_WinsOverDeclaredExtension()
    {
        // Zenya rendered a Word document as PDF on download (download_as_pdf): the bytes and the
        // response say PDF, the metadata still says docx. The container follows the bytes.
        var zenya = new FakeZenya().WithBinary(DocxId, 1, "Rendered", "docx", "application/msword", PdfBytes);
        zenya.Downloads[DocxId] = (PdfBytes, "application/pdf");
        var store = new InMemoryStore();

        await Build(zenya, store).RunAsync();

        Assert.IsTrue(store.Blobs.ContainsKey($"pdf/{DocxId}.pdf"));
    }

    [TestMethod]
    public async Task PdfWithoutMagic_IsCountedButStillWritten()
    {
        var zenya = new FakeZenya().WithBinary(PdfId, 1, "Not really", "pdf", "application/pdf", Encoding.ASCII.GetBytes("<html>"));
        var store = new InMemoryStore();

        var result = await Build(zenya, store).RunAsync();

        Assert.AreEqual(1, result.PdfWithoutMagic);
        Assert.IsTrue(store.Blobs.ContainsKey($"pdf/{PdfId}.pdf"));
    }

    // ---- change detection ----------------------------------------------------------------------

    [TestMethod]
    public async Task SameVersion_CostsNoMetadataCallAndNoDownload()
    {
        var zenya = new FakeZenya().WithBinary(PdfId, 3, "Same", "pdf", "application/pdf", PdfBytes);
        var store = new InMemoryStore().WithSynced($"pdf/{PdfId}.pdf", PdfId, 3);

        var result = await Build(zenya, store).RunAsync();

        Assert.AreEqual(1, result.Unchanged);
        Assert.AreEqual(0, zenya.MetadataCalls);
        Assert.AreEqual(0, zenya.DownloadCalls);
        Assert.AreEqual(0, store.Uploads.Count);
    }

    [TestMethod]
    public async Task NewVersion_IsRewrittenAndCountedChanged()
    {
        var zenya = new FakeZenya().WithBinary(PdfId, 4, "Updated", "pdf", "application/pdf", PdfBytes);
        var store = new InMemoryStore().WithSynced($"pdf/{PdfId}.pdf", PdfId, 3);

        var result = await Build(zenya, store).RunAsync();

        Assert.AreEqual(1, result.Changed);
        Assert.AreEqual(0, result.New);
        Assert.AreEqual("4", store.Blobs[$"pdf/{PdfId}.pdf"].Metadata[ZenyaBlobLayout.VersionKey]);
        Assert.AreEqual(0, store.Deletes.Count, "same name: overwrite, nothing to delete");
    }

    [TestMethod]
    public async Task TypeChange_DeletesTheSupersededBlob()
    {
        const string docxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        var zenya = new FakeZenya().WithBinary(PdfId, 2, "Now a Word file", "docx", docxMime, DocxBytes);
        var store = new InMemoryStore().WithSynced($"pdf/{PdfId}.pdf", PdfId, 1);

        var result = await Build(zenya, store).RunAsync();

        Assert.AreEqual(1, result.Changed);
        Assert.IsTrue(store.Blobs.ContainsKey($"docs/{PdfId}.docx"));
        Assert.IsFalse(store.Blobs.ContainsKey($"pdf/{PdfId}.pdf"));
        CollectionAssert.AreEqual(new[] { $"pdf/{PdfId}.pdf" }, store.Deletes);
        Assert.AreEqual(0, result.Removed, "a superseded blob is not a removed document");
    }

    // ---- removal --------------------------------------------------------------------------------

    [TestMethod]
    public async Task DocumentGoneFromListing_IsDeleted_ForeignBlobIsNot()
    {
        var zenya = new FakeZenya().WithBinary(PdfId, 1, "Still there", "pdf", "application/pdf", PdfBytes);
        var store = new InMemoryStore()
            .WithSynced($"pdf/{PdfId}.pdf", PdfId, 1)
            .WithSynced($"pdf/{DocxId}.pdf", DocxId, 7)
            .WithForeign("readme.txt");

        var result = await Build(zenya, store).RunAsync();

        Assert.AreEqual(1, result.Removed);
        Assert.AreEqual(1, result.ForeignBlobs);
        CollectionAssert.AreEqual(new[] { $"pdf/{DocxId}.pdf" }, store.Deletes);
        Assert.IsTrue(store.Blobs.ContainsKey("readme.txt"));
        Assert.IsTrue(store.Blobs.ContainsKey($"pdf/{PdfId}.pdf"));
    }

    // ---- routing ---------------------------------------------------------------------------------

    [TestMethod]
    public async Task AuthoredOnly_IsSkippedAndCounted_NotWritten()
    {
        var zenya = new FakeZenya()
            .WithAuthored(HtmlId, 1, "Authored")
            .WithBinary(PdfId, 1, "Binary", "pdf", "application/pdf", PdfBytes);
        var store = new InMemoryStore();

        var result = await Build(zenya, store).RunAsync();

        Assert.AreEqual(1, result.AuthoredSkipped);
        Assert.AreEqual(1, result.New);
        Assert.AreEqual(1, store.Blobs.Count);
        Assert.AreEqual(1, zenya.DownloadCalls);
    }

    [TestMethod]
    public async Task NeitherFlag_IsCountedNotDownloadable()
    {
        var zenya = new FakeZenya();
        zenya.Listing.Add(new ZenyaDocumentListItem(HtmlId, 1, "Locked", null, null));
        zenya.Metadata[HtmlId] = new ZenyaDocumentMetadata(HtmlId, 1, null, "Locked", "binary", null, null, null, null, false, false, null, true, "published", null);
        var store = new InMemoryStore();

        var result = await Build(zenya, store).RunAsync();

        Assert.AreEqual(1, result.NotDownloadable);
        Assert.AreEqual(0, store.Blobs.Count);
    }

    // ---- dry run + failures ------------------------------------------------------------------------

    [TestMethod]
    public async Task DryRun_CountsEverything_WritesAndDeletesNothing_DownloadsNothing()
    {
        var zenya = new FakeZenya()
            .WithBinary(PdfId, 4, "Changed", "pdf", "application/pdf", PdfBytes)
            .WithBinary(DocxId, 1, "New", "pdf", "application/pdf", PdfBytes)
            .WithAuthored(HtmlId, 1, "Authored");
        var store = new InMemoryStore()
            .WithSynced($"pdf/{PdfId}.pdf", PdfId, 3)
            .WithSynced("pdf/44444444-4444-4444-4444-444444444444.pdf", "44444444-4444-4444-4444-444444444444", 1);

        var result = await Build(zenya, store, dryRun: true).RunAsync();

        Assert.IsTrue(result.DryRun);
        Assert.AreEqual(3, result.Listed);
        Assert.AreEqual(1, result.New);
        Assert.AreEqual(1, result.Changed);
        Assert.AreEqual(1, result.Removed);
        Assert.AreEqual(1, result.AuthoredSkipped);
        Assert.AreEqual(0, zenya.DownloadCalls);
        Assert.AreEqual(0, store.Uploads.Count);
        Assert.AreEqual(0, store.Deletes.Count);
        Assert.AreEqual(2, store.Blobs.Count, "container untouched");
    }

    [TestMethod]
    public async Task OneFailingDocument_IsRecorded_RunContinues()
    {
        var zenya = new FakeZenya()
            .WithBinary(PdfId, 1, "Forbidden", "pdf", "application/pdf", PdfBytes)
            .WithBinary(DocxId, 1, "Fine", "pdf", "application/pdf", PdfBytes);
        zenya.FailMetadataFor.Add(PdfId);
        var store = new InMemoryStore();

        var result = await Build(zenya, store).RunAsync();

        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(1, result.New);
        var failure = result.Failures.Single();
        Assert.AreEqual(PdfId, failure.DocumentId);
        Assert.AreEqual("metadata", failure.Stage);
        StringAssert.Contains(failure.Error, "no read rights");
        Assert.IsTrue(store.Blobs.ContainsKey($"pdf/{DocxId}.pdf"));
    }

    // ---- options + layout helpers -------------------------------------------------------------------

    [TestMethod]
    public void Options_DryRunDefaultsToTrue_AndParsesAdoBooleanSpelling()
    {
        var baseValues = new Dictionary<string, string?>
        {
            [ZenyaSyncOptions.StorageAccountUrlKey] = "https://acct.blob.core.windows.net",
            [ZenyaSyncOptions.StorageContainerKey] = "zenya-documents",
        };
        Assert.IsTrue(ZenyaSyncOptions.FromConfiguration(Config(baseValues)).DryRun, "absent = dry run");

        baseValues[ZenyaSyncOptions.DryRunKey] = "False";
        Assert.IsFalse(ZenyaSyncOptions.FromConfiguration(Config(baseValues)).DryRun);

        baseValues[ZenyaSyncOptions.DryRunKey] = "yes";
        Assert.ThrowsExactly<InvalidOperationException>(() => ZenyaSyncOptions.FromConfiguration(Config(baseValues)));

        static IConfiguration Config(Dictionary<string, string?> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [TestMethod]
    public void ExtensionFor_ResponseThenDeclaredExtensionThenMime_ElseBin()
    {
        Assert.AreEqual("pdf",  ZenyaBlobLayout.ExtensionFor("application/pdf; charset=binary", "docx", "application/msword"));
        Assert.AreEqual("docx", ZenyaBlobLayout.ExtensionFor(null, ".DOCX", "application/pdf"));
        Assert.AreEqual("xlsx", ZenyaBlobLayout.ExtensionFor("application/octet-stream", null, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        Assert.AreEqual("bin",  ZenyaBlobLayout.ExtensionFor(null, null, "application/x-unknown"));
        Assert.AreEqual("bin",  ZenyaBlobLayout.ExtensionFor(null, "../etc", null), "non-alphanumeric declared extension is not trusted");
    }

    // ---- D204 fields ----------------------------------------------------------------------------

    // Every field below rides the per-document GET the sync already makes; this pins that each one
    // reaches blob metadata, with the right encoding, and that the flattening of the non-scalar
    // shapes (folder, person lists, flags, lock info) is what the reader will have to undo.
    [TestMethod]
    public void BuildMetadata_CarriesEveryFieldFromTheDocumentDto()
    {
        var metadata = ZenyaBlobLayout.BuildMetadata(FullDocument(), "application/pdf", SyncedAt);

        Assert.AreEqual(Uri.EscapeDataString("Contoso/Zorg/Hygiëne"), metadata[ZenyaBlobLayout.FolderPathKey]);
        Assert.AreEqual(Uri.EscapeDataString("Hygiëne"), metadata[ZenyaBlobLayout.FolderNameKey]);
        Assert.AreEqual("42", metadata[ZenyaBlobLayout.FolderIdKey]);
        Assert.AreEqual(Uri.EscapeDataString("Korte samenvatting."), metadata[ZenyaBlobLayout.SummaryKey]);
        Assert.AreEqual("20270101", metadata[ZenyaBlobLayout.CheckDateKey]);
        Assert.AreEqual("check_date_approaches,needs_review", metadata[ZenyaBlobLayout.AttentionKey]);
        Assert.AreEqual("true", metadata[ZenyaBlobLayout.CanCheckKey]);
        Assert.AreEqual("nl", metadata[ZenyaBlobLayout.LanguageKey]);
        Assert.AreEqual("file", metadata[ZenyaBlobLayout.OriginalTypeKey]);
        Assert.AreEqual("7", metadata[ZenyaBlobLayout.RevisionKey]);
        Assert.AreEqual("true", metadata[ZenyaBlobLayout.ActiveKey]);
        Assert.AreEqual("false", metadata[ZenyaBlobLayout.DownloadAsPdfKey]);
        Assert.AreEqual(Uri.EscapeDataString("Kop"), metadata[ZenyaBlobLayout.ParsedHeaderKey]);
        Assert.AreEqual("true", metadata[ZenyaBlobLayout.PrintHeaderKey]);
        Assert.AreEqual(Uri.EscapeDataString("Ana Jansen; Bo de Vries"), metadata[ZenyaBlobLayout.AuthorsKey]);
        Assert.AreEqual(Uri.EscapeDataString("Cas Smit"), metadata[ZenyaBlobLayout.AuthorizersKey]);
        Assert.AreEqual("true", metadata[ZenyaBlobLayout.LockedKey]);
        Assert.AreEqual("2026-09-20T10:00:00Z", metadata[ZenyaBlobLayout.LockedSinceKey]);
        Assert.AreEqual(Uri.EscapeDataString("Dex Bos"), metadata[ZenyaBlobLayout.LockedByKey]);
        Assert.AreEqual("false", metadata[ZenyaBlobLayout.FavoriteKey]);
        Assert.IsTrue(metadata.Values.All(v => v.All(c => c < 128)), "every metadata value must be ASCII");
    }

    // A null attribute is omitted by Zenya, so an absent field must produce an absent KEY - never
    // an empty string, which a reader cannot tell from "Zenya said empty".
    [TestMethod]
    public void BuildMetadata_OmitsKeysForFieldsZenyaDidNotReturn()
    {
        var metadata = ZenyaBlobLayout.BuildMetadata(
            new ZenyaDocumentMetadata(PdfId, 1, null, "Titel", "file", null, null, null, null, true, false, null, null, null, null),
            "application/pdf", SyncedAt);

        foreach (var key in new[]
                 {
                     ZenyaBlobLayout.FolderPathKey, ZenyaBlobLayout.SummaryKey, ZenyaBlobLayout.CheckDateKey,
                     ZenyaBlobLayout.AttentionKey, ZenyaBlobLayout.LanguageKey, ZenyaBlobLayout.RevisionKey,
                     ZenyaBlobLayout.ActiveKey, ZenyaBlobLayout.AuthorsKey, ZenyaBlobLayout.LockedKey,
                 })
            Assert.IsFalse(metadata.ContainsKey(key), $"{key} should be absent, not empty");

        // An empty person list is the same case: no names means no key.
        Assert.IsFalse(
            ZenyaBlobLayout.BuildMetadata(FullDocument() with { Authors = [] }, null, SyncedAt)
                .ContainsKey(ZenyaBlobLayout.AuthorsKey));
    }

    // Azure rejects the whole upload over the 8 KiB cap, so an oversized summary must cost the
    // summary, not the document. The drop order is declared, so the same document always loses
    // the same key, and the caller is told which.
    [TestMethod]
    public void BuildMetadata_DropsDeclaredKeysInOrder_RatherThanBustingTheLimit()
    {
        // Dropping the header alone gets it under budget, so the summary survives: the aim is a
        // successful upload carrying as much as will fit, not a minimal one.
        var dropped = new List<string>();
        var metadata = ZenyaBlobLayout.BuildMetadata(
            FullDocument() with { Summary = new string('a', 6_000), UnparsedHeader = new string('b', 3_000) },
            "application/pdf", SyncedAt, dropped.Add);

        Assert.IsTrue(Size(metadata) <= ZenyaBlobLayout.MetadataByteBudget);
        CollectionAssert.AreEqual(new[] { ZenyaBlobLayout.UnparsedHeaderKey }, dropped, "headers go first, and nothing more is dropped once it fits");
        Assert.IsTrue(metadata.ContainsKey(ZenyaBlobLayout.SummaryKey));

        // A summary that busts the budget on its own costs the summary - never the document.
        dropped.Clear();
        metadata = ZenyaBlobLayout.BuildMetadata(
            FullDocument() with { Summary = new string('a', 9_000) }, "application/pdf", SyncedAt, dropped.Add);

        Assert.IsTrue(Size(metadata) <= ZenyaBlobLayout.MetadataByteBudget);
        Assert.AreEqual(ZenyaBlobLayout.SummaryKey, dropped[^1], "summary is the last resort, dropped only after the rest");
        Assert.IsFalse(metadata.ContainsKey(ZenyaBlobLayout.SummaryKey));
        Assert.IsTrue(dropped.All(k => k != ZenyaBlobLayout.DocumentIdKey && k != ZenyaBlobLayout.VersionKey),
            "identity and the change signal are never droppable");
        Assert.AreEqual(PdfId, metadata[ZenyaBlobLayout.DocumentIdKey]);
        Assert.AreEqual("3", metadata[ZenyaBlobLayout.VersionKey]);

        static int Size(IReadOnlyDictionary<string, string> m) =>
            m.Sum(kv => "x-ms-meta-".Length + kv.Key.Length + kv.Value.Length);
    }

    private static readonly DateTimeOffset SyncedAt = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    private static ZenyaDocumentMetadata FullDocument() => new(
        PdfId, 3, 7, "Handhygiëne protocol", "file", new ZenyaDocumentTypeMini(3, "Protocol"),
        "application/pdf", "pdf", false, true, false, "HH-01", true, "published", "20260901120000",
        Folder: new ZenyaFolderMini(42, "Hygiëne", "Contoso/Zorg/Hygiëne"),
        Summary: "Korte samenvatting.",
        CheckDate: "20270101",
        AttentionRequiredFlags: ["check_date_approaches", "needs_review"],
        CanCheckDocument: true,
        CheckTaskDelegatedToUser: new ZenyaUserMini("u-9", "Eef Mol"),
        Language: "nl",
        OriginalType: "file",
        ParsedHeader: "Kop",
        UnparsedHeader: "Kop onbewerkt",
        PrintHeaderRequired: true,
        Authors: [new ZenyaUserMini("u-1", "Ana Jansen"), new ZenyaUserMini("u-2", "Bo de Vries")],
        Authorizers: [new ZenyaUserMini("u-3", "Cas Smit")],
        LockInfo: new ZenyaLockInfo(true, "2026-09-20T10:00:00Z", new ZenyaUserMini("u-4", "Dex Bos")),
        MarkedAsFavorite: false);
}
