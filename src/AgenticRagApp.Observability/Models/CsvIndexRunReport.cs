namespace AgenticRagApp.Observability.Reports;

// Same composed shape as PdfIndexRunReport, plus three CSV-only fields that always have
// real values for CSV, unlike PDF where they have no equivalent concept at all.
//
// CSV is dormant (see AgenticRagApp.FunctionApp.csproj - the ProjectReference is
// commented out), so this type was carried across to the composed shape mechanically to
// keep it compiling, not redesigned. Whether CSV keeps its own report type at all is a
// question for whenever that pipeline is revived.
public sealed record CsvIndexRunReport
{
    public required RunIdentity Run { get; init; }

    // Each null when that stage never ran. Null is not zero: it means "no measurement",
    // not "measured nothing".
    public ExtractionStageMetrics?  Extraction { get; init; }
    public ChunkingStageMetrics?    Chunking   { get; init; }
    public EmbedUploadStageMetrics? Embedding  { get; init; }

    // Quality signal: docs past their check_date — live but potentially stale guidance in
    // the index. Retrieval will surface it as if it were current — flag to content owners.
    public required int StaleDocCount { get; init; }
    public required int MissingVersionCount { get; init; }
    public required int MissingDepartmentCount { get; init; }

    public string InstanceId => Run.InstanceId;
    public bool   Success    => Run.Success;
}
