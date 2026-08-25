using Azure.AI.ContentUnderstanding;
using Microsoft.Extensions.Logging.Abstractions;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using Moq;

namespace RagApp.UnitTests.PdfExtraction;

// What survives of response validation: the string-encoding assertion, and that an ordinary
// response passes.
//
// The rest (exactly-one-document, non-empty markdown, no YAML front matter, at least one page,
// the non-BMP tripwire) was removed along with the checks themselves - those responses now reach
// the mapper untouched.
[TestClass]
public class ContentUnderstandingAnalyzerTests
{
    private static ContentUnderstandingAnalyzer Analyzer() => new(
        new Mock<IContentAnalysisClient>().Object,
        new IndexerConfig
        {
            SearchEndpoint            = "https://search.example.com",
            OpenAiEndpoint            = "https://openai.example.com",
            OpenAiEmbeddingDeployment = "embedding",
            OpenAiGptDeployment       = "gpt",
            OpenAiGptModelName        = "gpt-5.4",
            StorageAccountUrl         = "https://storage.example.com",
            SearchIndexName           = "index",
            KnowledgeSourceName       = "ks",
            KnowledgeBaseName         = "kb",
            ContentSafetyEndpoint     = "https://cs.example.com",
            LanguageEndpoint          = "https://lang.example.com",
        },
        NullLogger<ContentUnderstandingAnalyzer>.Instance);

    private static DocumentPage Page(int number, int offset, int length) =>
        ContentUnderstandingModelFactory.DocumentPage(
            pageNumber: number, width: 8.5f, height: 11f,
            spans: [ContentUnderstandingModelFactory.ContentSpan(offset, length)],
            angle: 0f, words: [], lines: [], barcodes: [], formulas: []);

    private static AnalysisResult Result(
        string markdown, string stringEncoding = "utf16", DocumentPage[]? pages = null, bool anyContent = true)
    {
        pages ??= [Page(1, 0, markdown.Length)];

        var contents = anyContent
            ? new AnalysisContent[]
            {
                ContentUnderstandingModelFactory.DocumentContent(
                    mimeType: "application/pdf", analyzerId: "cap-pdf-layout", category: null, path: null,
                    markdown: markdown, fields: null, startPageNumber: 1, endPageNumber: pages.Length,
                    unit: LengthUnit.Inch, pages: pages, paragraphs: [], sections: [], tables: [],
                    figures: [], annotations: [], hyperlinks: [], segments: []),
            }
            : [];

        return ContentUnderstandingModelFactory.AnalysisResult(
            analyzerId: "cap-pdf-layout", apiVersion: "2025-11-01", createdAt: null,
            warnings: [], stringEncoding: stringEncoding, contents: contents);
    }

    [TestMethod]
    public void ValidResponse_IsOk()
    {
        var outcome = Analyzer().ValidateAnalyzeResult(Result("# Beleid\n\nInhoud."), "doc.pdf", null);

        Assert.IsTrue(outcome.Ok);
        Assert.IsNotNull(outcome.Result);
    }

    [TestMethod]
    public void WrongStringEncoding_FailsTheDocument()
    {
        // The single most important check here. We ask for utf16 through an HTTP pipeline policy
        // because the SDK has no typed parameter for it, and a query parameter set that way can
        // be silently ignored. Code-point offsets agree with UTF-16 offsets exactly until the
        // first non-BMP character, so accepting this would produce a document that is correct
        // until it suddenly is not.
        var outcome = Analyzer().ValidateAnalyzeResult(
            Result("Inhoud.", stringEncoding: "codePoint"), "doc.pdf", null);

        Assert.IsFalse(outcome.Ok);
        StringAssert.Contains(outcome.Error!.Message, "utf16");
        Assert.AreEqual(PdfOpenFailureReason.UnexpectedContentFormat, outcome.Error.Reason);
    }

    [TestMethod]
    public void EmptyMarkdown_IsNoLongerRejectedHere()
    {
        // Was a failure; passes now. Recorded rather than deleted so the change is visible if
        // this ever behaves differently again.
        var outcome = Analyzer().ValidateAnalyzeResult(Result("   "), "doc.pdf", null);

        Assert.IsTrue(outcome.Ok);
    }
}
