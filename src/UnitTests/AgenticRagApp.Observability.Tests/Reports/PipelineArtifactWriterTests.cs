using System.Text.Json;
using Azure.Storage.Blobs;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

[TestClass]
public class PipelineArtifactWriterTests
{
    private static Mock<IBlobStore> MockBlobStore() => new();

    private static PipelineArtifactWriter BuildWriter(Mock<IBlobStore> blobStore) =>
        new(blobStore.Object, new Mock<BlobContainerClient>().Object);

    [TestMethod]
    public async Task WriteArtifactAsync_EnsuresContainerExistsBeforeUploading()
    {
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);

        await writer.WriteArtifactAsync("some/path.json", new { Foo = "bar" });

        blobStore.Verify(s => s.AssertContainerExistsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Writes now go through IBlobStore.UploadJsonAsync (streamed - see that interface member's
    // own comment) rather than the writer building a string/BinaryData itself and calling
    // UploadAsync directly - see PipelineArtifactWriter.WriteArtifactAsync.
    [TestMethod]
    public async Task WriteArtifactAsync_UploadsSerializedArtifactToTheGivenPath()
    {
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);

        await writer.WriteArtifactAsync("some/path.json", new { Foo = "bar" });

        blobStore.Verify(s => s.UploadJsonAsync(
            It.IsAny<BlobContainerClient>(), "some/path.json", It.IsAny<object>(),
            It.IsAny<JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task WriteArtifactAsync_SerializesArtifactAsCompactJson()
    {
        // This artifact writes in every environment, on every run, with no size cap
        // (whole-corpus content) - see finding #8 - so it's deliberately compact, not
        // indented, unlike RunReportWriter's small diagnostic reports.
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);
        JsonSerializerOptions? captured = null;
        blobStore
            .Setup(s => s.UploadJsonAsync(
                It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<JsonSerializerOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<BlobContainerClient, string, object, JsonSerializerOptions?, CancellationToken>(
                (_, _, _, options, _) => captured = options)
            .Returns(Task.CompletedTask);

        await writer.WriteArtifactAsync("some/path.json", new { Foo = "bar" });

        Assert.IsNotNull(captured);
        Assert.IsFalse(captured!.WriteIndented);
    }

    [TestMethod]
    public async Task WriteArtifactAsync_AlwaysOverwritesExistingArtifact()
    {
        // UploadJsonAsync always overwrites (see BlobStore's implementation) - there is no
        // separate flag to assert here anymore; this test now covers that the write happens
        // at all, which the other tests in this file already establish more directly. Kept as
        // its own test only so a future UploadJsonAsync signature change that reintroduces an
        // overwrite parameter has an obvious place to add the real assertion.
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);

        await writer.WriteArtifactAsync("some/path.json", new { Foo = "bar" });

        blobStore.Verify(s => s.UploadJsonAsync(
            It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<object>(),
            It.IsAny<JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
