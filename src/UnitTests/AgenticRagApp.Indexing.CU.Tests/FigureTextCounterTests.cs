using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.Indexing;

// The two counts behind Chunking.FigureText (2026-09-15). D183 measured them by hand off the
// chunking artifact; these pin the parse so the per-run numbers mean the same thing.
[TestClass]
public class FigureTextCounterTests
{
    private static FigureInfo Figure(string? description, string? role) => new(
        Caption: null, Offset: 0, PageNumber: 1, Id: "1.1", Elements: [],
        Description: description, Role: role);

    [TestMethod]
    public void AltTextChars_CountsTheAltTextOfMarkdownImagesOnly()
    {
        var content = "Intro. ![The logo of Contoso](figures/1.1 \"logo\") body [a link](x) ![ab](figures/1.2)";

        Assert.AreEqual("The logo of Contoso".Length + 2, FigureTextCounter.AltTextChars(content));
    }

    [TestMethod]
    public void AltTextChars_NoImages_IsZero()
    {
        Assert.AreEqual(0, FigureTextCounter.AltTextChars("Plain prose with [a link](x) and no images."));
    }

    [TestMethod]
    public void HeaderFooterDescriptionChars_CountsOnlyHeaderAndFooterRoles()
    {
        var content = "![The logo of Contoso](figures/1.1) text ![A bar chart of costs](figures/1.2)";
        var figures = new[]
        {
            Figure("The logo of Contoso",  "pageHeader"),
            Figure("A bar chart of costs", "figure"),
        };

        Assert.AreEqual("The logo of Contoso".Length, FigureTextCounter.HeaderFooterDescriptionChars(content, figures));
    }

    [TestMethod]
    public void HeaderFooterDescriptionChars_CountsEveryOccurrence_ButEachDescriptionOnce()
    {
        // The same logo on each of a cut's pages is one FigureInfo per page with an identical
        // description. Two entries must not double-count the two occurrences in the text.
        var content = "![Logo](figures/1.1) page one ![Logo](figures/2.1) page two";
        var figures = new[] { Figure("Logo", "pageHeader"), Figure("Logo", "pageHeader") };

        Assert.AreEqual(2 * "Logo".Length, FigureTextCounter.HeaderFooterDescriptionChars(content, figures));
    }

    [TestMethod]
    public void HeaderFooterDescriptionChars_IgnoresEmptyDescriptions_AndIsCaseInsensitiveOnRole()
    {
        var content = "![Footer mark](figures/1.1)";
        var figures = new[] { Figure(null, "pageFooter"), Figure("Footer mark", "PAGEFOOTER") };

        Assert.AreEqual("Footer mark".Length, FigureTextCounter.HeaderFooterDescriptionChars(content, figures));
    }
}
