using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

[TestClass]
public class RunReportWriterTests
{
    private static Mock<IBlobStore> MockBlobStore() => new();

    private static RunReportWriter BuildWriter(Mock<IBlobStore> blobStore) =>
        new(blobStore.Object, new Mock<BlobContainerClient>().Object);

    [TestMethod]
    public void IsEnabled_AlwaysTrue_RegardlessOfEnvironment()
    {
        // Regression test for finding #8: reports must write in every environment
        // (production included), not just Development.
        var writer = BuildWriter(MockBlobStore());

        Assert.IsTrue(writer.IsEnabled);
    }

    [TestMethod]
    public async Task WriteReportAsync_UploadsSerializedReportToTheGivenPath()
    {
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);

        await writer.WriteReportAsync("some/path.json", new { Foo = "bar" });

        // Streamed via IBlobStore.UploadJsonAsync now, not a string/BinaryData built by the
        // writer itself - see RunReportWriter.WriteAsync.
        blobStore.Verify(s => s.UploadJsonAsync(
            It.IsAny<BlobContainerClient>(), "some/path.json", It.IsAny<object>(),
            It.IsAny<JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetLastIndexStatsAsync_NoBaselineBlobYet_ReturnsNullRatherThanThrowing()
    {
        // No IBlobStore setup at all - Moq's default for an unconfigured generic call
        // returns default(T) (a null value tuple), mirroring "no baseline blob exists yet".
        var writer = BuildWriter(MockBlobStore());

        var stats = await writer.GetLastIndexStatsAsync("pdf");

        Assert.IsNull(stats);
    }

    [TestMethod]
    public async Task SaveLastIndexStatsAsync_UploadsToTheSourceScopedPath()
    {
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);

        await writer.SaveLastIndexStatsAsync("pdf", 100, 2048);

        blobStore.Verify(s => s.UploadJsonAsync(
            It.IsAny<BlobContainerClient>(), "indexing/_last-stats-pdf.json", It.IsAny<object>(),
            It.IsAny<JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task SaveLastIndexStatsAsync_ScopesPathPerSource_PdfAndCsvNeverShareABaseline()
    {
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);

        await writer.SaveLastIndexStatsAsync("pdf", 100, 2048);
        await writer.SaveLastIndexStatsAsync("csv", 50, 1024);

        blobStore.Verify(s => s.UploadJsonAsync(
            It.IsAny<BlobContainerClient>(), "indexing/_last-stats-pdf.json", It.IsAny<object>(),
            It.IsAny<JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
        blobStore.Verify(s => s.UploadJsonAsync(
            It.IsAny<BlobContainerClient>(), "indexing/_last-stats-csv.json", It.IsAny<object>(),
            It.IsAny<JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
