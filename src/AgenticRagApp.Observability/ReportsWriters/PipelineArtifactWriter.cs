using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace AgenticRagApp.Observability.Reports;

public class PipelineArtifactWriter : IPipelineArtifactWriter
{
    // Not indented: this writes in every environment, on every run, and is the one payload
    // in the pipeline with no size cap (whole-corpus content). Indentation was pure overhead
    // on a file nobody reads by opening it raw in the portal - see the extraction-review's
    // finding on this artifact's size (duplicated per-page structure, WriteIndented=true).
    //
    // Enums as names, same reasoning as RunReportWriter: an artifact carrying PipelineIssue
    // values should say "Error", not 0.
    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented = false,
        Converters    = { new JsonStringEnumConverter() },
    };

    private readonly IBlobStore          _blobStore;
    private readonly BlobContainerClient _container;

    public PipelineArtifactWriter(IBlobStore blobStore, BlobContainerClient container)
    {
        _blobStore = blobStore;
        _container = container;
    }

    public async Task WriteArtifactAsync<T>(string path, T artifact, CancellationToken ct = default)
    {
        await _blobStore.EnsureContainerExistsAsync(_container, ct);
        var json = JsonSerializer.Serialize(artifact, s_opts);
        await _blobStore.UploadAsync(_container, path, BinaryData.FromString(json), overwrite: true, ct);
    }
}
