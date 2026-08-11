using System.ClientModel.Primitives;
using Azure.AI.DocumentIntelligence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class LanguageDetectionHelperTests
{
    // languages: (locale, confidence, offset, length) - mirrors DI's own per-span shape,
    // so "dominant by characters covered" can actually be exercised.
    private static AnalyzeResult ResultWithLanguages(params (string Locale, float Confidence, int Offset, int Length)[] languages)
    {
        var languagesJson = string.Join(",", languages.Select(l => $$"""
            { "locale": "{{l.Locale}}", "confidence": {{l.Confidence}},
              "spans": [ { "offset": {{l.Offset}}, "length": {{l.Length}} } ] }
            """));

        var json = $$"""
        {
          "apiVersion": "2024-11-30", "modelId": "prebuilt-layout", "content": "placeholder",
          "contentFormat": "markdown",
          "pages": [ { "pageNumber": 1, "words": [], "lines": [], "selectionMarks": [], "spans": [ { "offset": 0, "length": 11 } ] } ],
          "paragraphs": [], "tables": [], "figures": [], "sections": [],
          "languages": [ {{languagesJson}} ], "warnings": []
        }
        """;

        return ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(json))!;
    }

    [TestMethod]
    public void SingleDutchLanguageEntry_DetectsNl()
    {
        var result = ResultWithLanguages(("nl", 0.95f, 0, 1000));

        Assert.AreEqual("nl", LanguageDetectionHelper.Detect(result));
    }

    [TestMethod]
    public void SingleEnglishLanguageEntry_DetectsEn()
    {
        var result = ResultWithLanguages(("en", 0.98f, 0, 1000));

        Assert.AreEqual("en", LanguageDetectionHelper.Detect(result));
    }

    [TestMethod]
    public void NoLanguagesInResult_DefaultsToNl()
    {
        var result = ResultWithLanguages();

        Assert.AreEqual("nl", LanguageDetectionHelper.Detect(result));
    }

    [TestMethod]
    public void MixedDocument_PicksLocaleCoveringMostCharacters_NotHighestConfidence()
    {
        // English span is short but reported with higher confidence than the much larger
        // Dutch span - dominance must be decided by characters covered, not confidence.
        var result = ResultWithLanguages(
            ("en", 0.99f, 0, 50),
            ("nl", 0.85f, 50, 9_000));

        Assert.AreEqual("nl", LanguageDetectionHelper.Detect(result));
    }

    [TestMethod]
    public void MultipleSpansSameLocale_AreSummedTogether()
    {
        // Two separate Dutch spans (e.g. either side of one English quote) sum to more
        // than the single English span between them.
        var result = ResultWithLanguages(
            ("nl", 0.9f, 0, 400),
            ("en", 0.9f, 400, 100),
            ("nl", 0.9f, 500, 400));

        Assert.AreEqual("nl", LanguageDetectionHelper.Detect(result));
    }

    [TestMethod]
    public void LocaleMatchingIsCaseInsensitive()
    {
        var result = ResultWithLanguages(("NL", 0.9f, 0, 100), ("nl", 0.9f, 100, 100));

        Assert.AreEqual("NL", LanguageDetectionHelper.Detect(result));
    }
}
