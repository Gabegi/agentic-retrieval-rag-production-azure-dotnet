using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.DI.Models;

public sealed record PdfExtractionOutput(IReadOnlyList<PdfExtractionDocument> Docs) : ExtractionOutputBase;
