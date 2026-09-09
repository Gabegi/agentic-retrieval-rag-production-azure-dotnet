namespace AgenticRagApp.Indexing.CU.Models;

// How well the service says it READ one document - the quality-of-extraction axis the pipeline
// had none of before 2026-08-27.
//
// Content Understanding reports a per-word Confidence (DocumentPage.Words[].Confidence, SDK
// 1.1.0). Nothing in this project touched it: CuPageHelper mapped spans, lines and dimensions,
// and the only Confidence field that existed anywhere sat on a selection-mark record CU could
// never populate (deleted 2026-09-09). So a document that OCR'd badly and one that read perfectly produced identical
// reports, on a pipeline whose whole justification was extraction quality.
//
// AGGREGATED IN THE MAPPER, NEVER STORED. The words themselves do not enter
// PdfDocumentStructure: 871 pages of DocumentWord is a report nobody can read and a serialized
// document nobody wants. Only this summary leaves CUHelper.
//
// NO THRESHOLD IS INVENTED HERE. There is no published Content Understanding word-confidence
// threshold to cite, and this codebase does not ship guessed ones (see FlagEvaluator's
// sourced-vs-awaiting-calibration split). So this reports the DISTRIBUTION - min, P5, P50, mean -
// and no "low confidence" count and no flag. Whoever calibrates a threshold does it against real
// corpus numbers from these fields, then adds the count and the rule.
//
// Percentiles use the same nearest-rank convention as DocumentRowBuilder.Percentile, for the
// same reason: two reports whose percentiles are computed differently cannot be read against
// each other.
public sealed record WordConfidenceSummary(
    int    WordCount,
    double Min,
    double P5,
    double P50,
    double Mean)
{
    // A document whose response carried no words at all - not zero confidence, no measurement.
    // Distinguished the same way every other blank in these reports is: absent, not 0.
    public static WordConfidenceSummary? From(IReadOnlyList<double> confidences)
    {
        if (confidences.Count == 0) return null;

        var sorted = confidences.OrderBy(c => c).ToList();

        return new WordConfidenceSummary(
            WordCount: sorted.Count,
            Min:       sorted[0],
            P5:        Percentile(sorted, 0.05),
            P50:       Percentile(sorted, 0.50),
            Mean:      sorted.Average());
    }

    // Nearest-rank on an already-sorted list, matching DocumentRowBuilder.Percentile.
    private static double Percentile(List<double> sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}

// The same value paired with the blob it came from - the fourth of the per-document lifts
// (DocumentContentHash, DocumentExtractDuration, DocumentUsage, this). Report-only: the analyze
// call produces it, ExtractionOutputBuilder lifts it onto PdfExtractionOutput, and
// ExtractionReporter writes it into the per-document facts report. Ok rides along for the same
// reason as on the other three - a failed analysis can still have read words.
public sealed record DocumentWordConfidence(
    string                 BlobName,
    WordConfidenceSummary  Summary,
    bool                   Ok);
