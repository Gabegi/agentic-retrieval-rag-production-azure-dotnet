using System.ClientModel.Primitives;
using Azure.AI.DocumentIntelligence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class ResolveSectionElementsHelperTests
{
    // One of each collection DI's section pointers can reference, so every branch of
    // ResolveSectionElementsHelper.ResolveText has something real to resolve against:
    // - paragraphs[0]: "First paragraph."
    // - paragraphs[1]: longer than the label length, so truncation has something to cut
    // - tables[0]: 2x2
    // - figures[0]: captioned "A diagram"; figures[1]: neither caption nor id
    // - sections[0]/[1]: two sections, so "/sections/1" is a valid nested reference
    private const string LongParagraph =
        "This paragraph is deliberately longer than the label length the resolver truncates to, "
        + "so that the summary it produces can be asserted against something real rather than a "
        + "hand-made string.";

    private static AnalyzeResult BuildResult()
    {
        var json = $$"""
        {
          "apiVersion": "2024-11-30", "modelId": "prebuilt-layout", "content": "First paragraph.",
          "contentFormat": "markdown",
          "pages": [ { "pageNumber": 1, "words": [], "lines": [], "selectionMarks": [], "spans": [ { "offset": 0, "length": 16 } ] } ],
          "paragraphs": [
            { "content": "First paragraph.", "boundingRegions": [ { "pageNumber": 1, "polygon": [] } ], "spans": [ { "offset": 0, "length": 16 } ] },
            { "content": "{{LongParagraph}}", "boundingRegions": [ { "pageNumber": 1, "polygon": [] } ], "spans": [ { "offset": 0, "length": 16 } ] }
          ],
          "tables": [
            {
              "rowCount": 2, "columnCount": 2,
              "cells": [
                { "kind": "columnHeader", "rowIndex": 0, "columnIndex": 0, "content": "Name", "spans": [] },
                { "rowIndex": 1, "columnIndex": 0, "content": "Aspirin", "spans": [] }
              ],
              "spans": [ { "offset": 0, "length": 5 } ],
              "boundingRegions": [ { "pageNumber": 1, "polygon": [] } ]
            }
          ],
          "figures": [
            {
              "id": "1.1",
              "caption": { "content": "A diagram", "spans": [], "boundingRegions": [] },
              "spans": [ { "offset": 0, "length": 5 } ],
              "boundingRegions": [ { "pageNumber": 1, "polygon": [] } ],
              "elements": []
            },
            {
              "spans": [ { "offset": 0, "length": 5 } ],
              "boundingRegions": [ { "pageNumber": 1, "polygon": [] } ],
              "elements": []
            }
          ],
          "sections": [
            { "spans": [ { "offset": 0, "length": 16 } ], "elements": [ "/paragraphs/0", "/tables/0", "/figures/0", "/sections/1", "/paragraphs/99", "/unknown/format" ] },
            { "spans": [ { "offset": 0, "length": 16 } ], "elements": [] }
          ],
          "warnings": []
        }
        """;

        return ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(json))!;
    }

    [TestMethod]
    public void ParagraphPointer_ResolvesToParagraphContent()
    {
        var result = BuildResult();

        var resolved = ResolveSectionElementsHelper.Resolve(["/paragraphs/0"], result);

        Assert.AreEqual("paragraphs", resolved[0].Kind);
        Assert.AreEqual(0, resolved[0].Index);
        Assert.AreEqual("First paragraph.", resolved[0].Text);
    }

    [TestMethod]
    public void TablePointer_ResolvesToRowByColumnSummary()
    {
        var result = BuildResult();

        var resolved = ResolveSectionElementsHelper.Resolve(["/tables/0"], result);

        Assert.AreEqual("tables", resolved[0].Kind);
        Assert.AreEqual("table 2x2", resolved[0].Text);
    }

    [TestMethod]
    public void FigurePointer_ResolvesToCaption()
    {
        var result = BuildResult();

        var resolved = ResolveSectionElementsHelper.Resolve(["/figures/0"], result);

        Assert.AreEqual("figures", resolved[0].Kind);
        Assert.AreEqual("A diagram", resolved[0].Text);
    }

    [TestMethod]
    public void NestedSectionPointer_ResolvesToBareReference_NotWalkedRecursively()
    {
        var result = BuildResult();

        var resolved = ResolveSectionElementsHelper.Resolve(["/sections/1"], result);

        Assert.AreEqual("sections", resolved[0].Kind);
        Assert.AreEqual(1, resolved[0].Index);
        Assert.AreEqual("section 1", resolved[0].Text);
    }

    [TestMethod]
    public void LongParagraph_TruncatedToLabelLength_NotCarriedWhole()
    {
        var result = BuildResult();

        var resolved = ResolveSectionElementsHelper.Resolve(["/paragraphs/1"], result);

        Assert.IsNotNull(resolved[0].Text);
        Assert.IsTrue(resolved[0].Text!.Length < LongParagraph.Length,
            "A resolved paragraph is a label, not a second copy of the document - it is carried onto every chunk.");
        Assert.IsTrue(resolved[0].Text!.EndsWith('…'));
        Assert.IsTrue(LongParagraph.StartsWith(resolved[0].Text!.TrimEnd('…')));
    }

    [TestMethod]
    public void FigureWithoutCaptionOrId_ResolvesToBareLabel_NotNull()
    {
        var result = BuildResult();

        var resolved = ResolveSectionElementsHelper.Resolve(["/figures/1"], result);

        Assert.AreEqual("figures", resolved[0].Kind);
        Assert.AreEqual("figure 1", resolved[0].Text,
            "Null Text means out-of-range only; a figure that resolved but has no label must stay distinguishable.");
    }

    [TestMethod]
    public void IndexTooLargeForInt32_DoesNotThrow_TreatedAsUnrecognized()
    {
        var result = BuildResult();

        var resolved = ResolveSectionElementsHelper.Resolve(["/paragraphs/99999999999"], result);

        Assert.AreEqual("/paragraphs/99999999999", resolved[0].Kind);
        Assert.AreEqual(-1, resolved[0].Index);
        Assert.IsNull(resolved[0].Text);
    }

    [TestMethod]
    public void OutOfRangeIndex_DoesNotThrow_TextIsNull()
    {
        var result = BuildResult();

        var resolved = ResolveSectionElementsHelper.Resolve(["/paragraphs/99"], result);

        Assert.AreEqual("paragraphs", resolved[0].Kind);
        Assert.AreEqual(99, resolved[0].Index);
        Assert.IsNull(resolved[0].Text);
    }

    [TestMethod]
    public void UnrecognizedPointerShape_KeptVerbatim_IndexNegativeOne()
    {
        var result = BuildResult();

        var resolved = ResolveSectionElementsHelper.Resolve(["/unknown/format"], result);

        Assert.AreEqual("/unknown/format", resolved[0].Kind);
        Assert.AreEqual(-1, resolved[0].Index);
        Assert.IsNull(resolved[0].Text);
    }

    [TestMethod]
    public void EmptyElements_ReturnsEmptyList()
    {
        var result = BuildResult();

        var resolved = ResolveSectionElementsHelper.Resolve([], result);

        Assert.AreEqual(0, resolved.Count);
    }

    [TestMethod]
    public void MultipleElements_ResolvedInOrder()
    {
        var result = BuildResult();

        var resolved = ResolveSectionElementsHelper.Resolve(["/paragraphs/0", "/tables/0", "/figures/0"], result);

        Assert.AreEqual(3, resolved.Count);
        CollectionAssert.AreEqual(new[] { "paragraphs", "tables", "figures" }, resolved.Select(r => r.Kind).ToList());
    }

    // --- Wired through GetSectionsHelper --------------------------------------------

    [TestMethod]
    public void GetSections_PopulatesResolvedElements_AlongsideRawElements()
    {
        var result = BuildResult();

        var sections = GetSectionsHelper.GetSections(result);

        Assert.AreEqual(2, sections.Count);
        Assert.AreEqual(6, sections[0].Elements.Count);
        Assert.AreEqual(6, sections[0].ResolvedElements.Count);
        Assert.AreEqual("/paragraphs/0", sections[0].Elements[0]);
        Assert.AreEqual("First paragraph.", sections[0].ResolvedElements[0].Text);
    }

    [TestMethod]
    public void GetSections_NoSectionsInResult_ReturnsEmptyList()
    {
        const string json = """
        {
          "apiVersion": "2024-11-30", "modelId": "prebuilt-layout", "content": "x",
          "contentFormat": "markdown",
          "pages": [ { "pageNumber": 1, "words": [], "lines": [], "selectionMarks": [], "spans": [ { "offset": 0, "length": 1 } ] } ],
          "paragraphs": [], "tables": [], "figures": [], "sections": [], "warnings": []
        }
        """;
        var result = ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(json))!;

        var sections = GetSectionsHelper.GetSections(result);

        Assert.AreEqual(0, sections.Count);
    }
}
