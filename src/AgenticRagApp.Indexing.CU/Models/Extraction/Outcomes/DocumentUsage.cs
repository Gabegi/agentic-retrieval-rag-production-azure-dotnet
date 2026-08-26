namespace AgenticRagApp.Indexing.CU.Models;

// What one document's analysis billed, paired with the blob it came from - in the units the
// service bills in (see CuUsage; no currency math here or anywhere).
//
// Carried out of extraction for reporting only, the third of the per-document lifts
// (DocumentContentHash, DocumentExtractDuration, this): the analyze call produces it,
// ExtractionOutputBuilder lifts it here, and ExtractionReporter writes it into the
// per-document facts report - DurationMs answers "which file takes the time", this answers
// "which file costs the money". Ok rides along for the same reason as on the other two:
// a failed analysis can still have billed (the failure can occur after the paid work).
public sealed record DocumentUsage(
    string BlobName,
    int?   BilledPagesStandard,
    int?   ContextualizationTokens,
    bool   Ok);
