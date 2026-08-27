using Azure.AI.ContentUnderstanding;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

// One document in, the finished analysis out. The seam exists so ExtractionService can be tested
// without a real analyze call; there is nothing else to abstract over.
public interface IContentAnalysisClient
{
    Task<ContentAnalysis> AnalyzeAsync(byte[] bytes, CancellationToken ct = default);
}

// The analysis plus what it billed. Usage lives on the LRO Operation (via the SDK's GetUsage
// extension), not on AnalysisResult, so returning the result alone - as the first iteration did -
// silently zeroed every cost report. Usage is null only when the completed operation carried no
// readable usage payload AND the raw-response fallback could not find one either - see CuUsage.
//
// RawJson is the final poll's response body verbatim - the service's own JSON, before the SDK
// deserialized it. Diagnostics only: ExtractionService persists ONE of these per run as the
// cu-raw-response report, which is what CUHelper's typed mapping is designed and re-verified
// against. Null when the raw response was unreadable; nothing downstream depends on it.
public sealed record ContentAnalysis(AnalysisResult Result, CuUsage? Usage, string? RawJson = null);

// What one analysis billed, in the units the service bills in - this app's own shape rather
// than the SDK's AnalyzeUsageDetails, because it has two producers: the SDK's GetUsage()
// extension when it can read the operation envelope, and a fallback parse of the same envelope
// from the raw response body when it cannot (observability plan 1.2, decided 2026-08-26:
// SDK-first-with-fallback). Both read the same documented "usage" node - a sibling of "result"
// in the LRO response - so the shape is one shape regardless of which producer filled it.
//
// TokensByModel is the service's own per-model token map, keys verbatim as billed (e.g.
// "gpt-4.1-mini-input": 6178) - deliberately NOT split into model/direction parts here, since
// the key format is the service's to define, not this record's to guess. Empty when the
// response carried no token map.
//
// DocumentPagesMinimal/Basic are the two cheaper page meters of the documented usage node
// (minimal/basic/standard). prebuilt-documentSearch bills everything Standard, so both read
// null on every response today - they are captured so that an analyzer switch to a cheaper
// tier cannot silently zero the page bill, the exact failure mode this record exists to
// prevent (2026-08-27, cu-payload-usage-review.md). The audio/video hour meters stay out:
// this pipeline analyzes PDFs only.
public sealed record CuUsage(
    int? DocumentPagesStandard,
    int? ContextualizationTokens,
    IReadOnlyDictionary<string, int> TokensByModel)
{
    public int? DocumentPagesMinimal { get; init; }
    public int? DocumentPagesBasic   { get; init; }

    public static CuUsage From(AnalyzeUsageDetails details) => new(
        details.DocumentPagesStandard,
        details.ContextualizationTokens,
        details.Tokens ?? new Dictionary<string, int>())
    {
        DocumentPagesMinimal = details.DocumentPagesMinimal,
        DocumentPagesBasic   = details.DocumentPagesBasic,
    };
}
