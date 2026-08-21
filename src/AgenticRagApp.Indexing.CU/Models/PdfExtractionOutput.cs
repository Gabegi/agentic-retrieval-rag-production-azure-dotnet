using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.CU.Models;

public sealed record PdfExtractionOutput(IReadOnlyList<PdfExtractionDocument> Docs) : ExtractionOutputBase;
