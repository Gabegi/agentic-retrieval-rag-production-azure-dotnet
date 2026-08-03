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

        blobStore.Verify(s => s.EnsureContainerExistsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task WriteArtifactAsync_UploadsSerializedArtifactToTheGivenPath()
    {
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);

        await writer.WriteArtifactAsync("some/path.json", new { Foo = "bar" });

        blobStore.Verify(s => s.UploadAsync(
            It.IsAny<BlobContainerClient>(), "some/path.json", It.IsAny<BinaryData>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task WriteArtifactAsync_SerializesArtifactAsCompactJson()
    {
        // This artifact writes in every environment, on every run, with no size cap
        // (whole-corpus content) - see finding #8 - so it's deliberately compact, not
        // indented, unlike RunReportWriter's small diagnostic reports.
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);
        BinaryData? captured = null;
        blobStore
            .Setup(s => s.UploadAsync(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<BinaryData>(), true, It.IsAny<CancellationToken>()))
            .Callback<BlobContainerClient, string, BinaryData, bool, CancellationToken>((_, _, content, _, _) => captured = content)
            .Returns(Task.CompletedTask);

        await writer.WriteArtifactAsync("some/path.json", new { Foo = "bar" });

        Assert.IsNotNull(captured);
        var json = captured!.ToString();
        StringAssert.Contains(json, "\"Foo\"");
        StringAssert.Contains(json, "\"bar\"");
        Assert.IsFalse(json.Contains('\n'));
    }

    [TestMethod]
    public async Task WriteArtifactAsync_AlwaysOverwritesExistingArtifact()
    {
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);

        await writer.WriteArtifactAsync("some/path.json", new { Foo = "bar" });

        blobStore.Verify(s => s.UploadAsync(
            It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<BinaryData>(), overwrite: true, It.IsAny<CancellationToken>()), Times.Once);
    }
}
