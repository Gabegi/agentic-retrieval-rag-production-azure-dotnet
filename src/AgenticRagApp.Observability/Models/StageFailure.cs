namespace AgenticRagApp.Observability.Reports;

// Why a stage failed, in fields rather than prose (2026-09-17, D199 §8b item 3).
//
// It exists because of the Durable boundary. In the isolated worker an activity's exception does
// not reach the orchestrator as itself: the orchestrator receives TaskFailedException carrying
// FailureDetails - the original type as a STRING, the message, a stack - and every custom
// property is gone. So a typed exception carrying structured detail (TotalWithholdException's
// verdict breakdown) is useful only INSIDE the activity that caught it. The activity converts it
// to this and RETURNS it as data rather than throwing, which is the only way the detail survives
// to the report.
//
// The alternative was parsing it back out of ErrorMessage downstream, which would have put the
// classification in whatever read the report - in practice a regex in a PowerShell script outside
// the repo, where no test protects it.
public sealed record StageFailure(
    // The original exception's type, FULLY QUALIFIED - e.g.
    // "AgenticRagApp.Indexing.CU.Services.TotalWithholdException", not the wrapper type the
    // orchestrator would otherwise see.
    //
    // Fully qualified deliberately: the other failure path takes RunIdentity.ErrorType from
    // Durable's FailureDetails.ErrorType, which is the full type name, and one column carrying two
    // spellings of a type is a column nobody can filter on.
    string ExceptionType,
    string Message)
{
    // True when every withheld vector was the wrong width: the signature of
    // OPENAI_EMBEDDING_DIMENSIONS drifting from the index's content_vector field, as opposed to
    // the embedding deployment returning unusable vectors. The two have opposite remedies, which
    // is why this is a field and not something to infer from the message.
    public bool? IsDimensionDrift { get; init; }

    // The width the vectors were judged against - the configured dimensions at failure time.
    public int? ExpectedDimensions { get; init; }

    // Verdict name -> count, e.g. { "WrongWidth": 3711 }. "NoVector" covers chunks that had no
    // vector at all, which the health check returns no verdict for.
    public IReadOnlyDictionary<string, int>? VerdictCounts { get; init; }
}
