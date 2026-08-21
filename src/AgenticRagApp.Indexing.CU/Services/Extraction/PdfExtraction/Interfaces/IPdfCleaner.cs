using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

public interface IPdfCleaner
{
    PdfCleanResult CleanPdf(IReadOnlyList<PdfPageRecord> pages);
}
