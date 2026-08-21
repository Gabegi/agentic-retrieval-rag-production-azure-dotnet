using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

public interface IPdfCleaner
{
    PdfCleanResult CleanPdf(IReadOnlyList<PdfPageRecord> pages);
}
