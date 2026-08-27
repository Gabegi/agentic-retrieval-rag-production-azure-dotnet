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
//
// TokensByModel is the per-document half of the per-model bill, keys verbatim as the service
// bills them (see CuUsage.TokensByModel). The run TOTAL was already reported
// (PdfExtractionOutput.BilledTokensByModel, logged by ExtractionReporter); this is the
// per-document grain, added 2026-08-27 because the run total cannot answer "real tokens per
// PAGE" - the number that sizes MaxExtractionParallelism against the deployment's TPM ceiling.
// Note the two Billed* scalars above are CU METERS, not measurements: ContextualizationTokens
// reads a flat 1,000 per page on every document in the corpus. These are the real tokens.
// Empty when the analysis reported no token map.
public sealed record DocumentUsage(
    string BlobName,
    int?   BilledPagesStandard,
    int?   ContextualizationTokens,
    IReadOnlyDictionary<string, int> TokensByModel,
    bool   Ok);
