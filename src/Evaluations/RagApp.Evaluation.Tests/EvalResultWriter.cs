using System.Text.Json;
using System.Text.Json.Serialization;
using RagApp.Evaluation.Tests.Models;

namespace RagApp.Evaluation.Tests.Evaluation;

/// <summary>
/// Appends EvalRows as JSONL to a local file. Knows nothing about how scores
/// were computed — just persists whatever EvalRow it's given.
/// </summary>
/// <remarks>
/// Writes to the agent's filesystem rather than straight to blob storage
/// (2026-08-07). The blob write used to happen inline, per test, and a storage
/// firewall denial therefore failed the test itself: a run on 2026-08-06 lost
/// all 79 results and reported 79 quality regressions when every RAG query and
/// judge call had in fact succeeded — the only thing that failed was persistence.
/// Storage IP allowlisting can't fix that from a hosted agent: the eval account
/// is in westeurope and an ubuntu-latest agent allocated in the same region
/// reaches storage over an internal Azure address, so its public IP never
/// matches the rule the pipeline adds for it (verified against that run's
/// activity log — the runner IP was present and allowed throughout, and every
/// request was still refused with 403 AuthorizationFailure). Uploading the
/// finished file to blob is now a separate pipeline step that opens the
/// firewall for the seconds it takes, and can fail without destroying the run.
/// </remarks>
public sealed class EvalResultWriter
{
    // ScenarioType as "Answer"/"Refusal" rather than 0/1: the pipeline's summary
    // report step reads this file with jq, and a rename or reorder of the enum
    // would silently change what an integer means there.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EvalResultWriter(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public async Task WriteAsync(EvalRow row, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(row, SerializerOptions) + "\n";

        // MSTest runs test methods in parallel ([assembly: Parallelize] in
        // RagEvaluationTests.cs), so appends have to be serialized to keep one
        // whole JSON object per line.
        await _gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_filePath, line, ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
