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
        await _blobStore.AssertContainerExistsAsync(_container, ct);
        // Streamed - never buffers the whole (potentially whole-corpus-sized) payload as an
        // intermediate string or byte[]. This is what OOM'd in production on 2026-08-07; see
        // IBlobStore.UploadJsonAsync.
        await _blobStore.UploadJsonAsync(_container, path, artifact, s_opts, ct);
    }
}
