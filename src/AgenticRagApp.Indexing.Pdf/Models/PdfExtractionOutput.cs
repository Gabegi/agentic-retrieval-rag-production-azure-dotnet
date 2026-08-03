using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Models;

public sealed record PdfExtractionOutput(IReadOnlyList<PdfExtractionDocument> Docs) : ExtractionOutputBase;
