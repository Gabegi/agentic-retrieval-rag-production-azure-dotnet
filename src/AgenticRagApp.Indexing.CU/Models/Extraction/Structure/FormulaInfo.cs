namespace AgenticRagApp.Indexing.CU.Models;

// A mathematical formula the service detected (DocumentPage.Formulas).
//
// This exists to MEASURE a known defect, not to use formulas. D161 found 36 formula
// occurrences in the corpus and every single one was a **euro sign misread as LaTeX**
// (`$$\epsilon$$`, `$$\in$$`) inside CAO salary-table cells. `EnableFormula` is fixed-on for
// prebuilt-documentSearch, so the misread cannot be switched off at the analyzer.
//
// Mapping them turns "36, all of them the euro misread" from a one-off manual grep of a raw
// capture into a run-over-run column. That number is precisely what the PARKED decision needs:
// substituting the euro back into the markdown at these offsets would be the pipeline rewriting
// service output on an assumption - a heuristic, and this codebase does not add those quietly
// (2026-08-26 decision). It stays parked until the HTML-table fix ships and an eval says the
// distortion still costs something; this record is the evidence it would be re-judged on.
//
// Kind is the service's own DocumentFormulaKind as a string ("inline" / "display"), and Value
// is the LaTeX expression verbatim - including the wrong ones, which is the point.
public sealed record FormulaInfo(
    string? Kind,
    string? Value,
    int? Offset,
    int PageNumber,
    double? Confidence);
