using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Indexing.CU.Utils;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The head a cut diagram fragment carries into its embedding (D214 §2.6): the figure's caption,
// else its description capped by tokens, found by matching the fence body to FigureInfo.Payload
// exactly. Nothing here decides WHETHER a chunk gets one - that is the callers' rule (cut
// fragments only) and is covered in ChunkingServiceTests.
[TestClass]
public class DiagramContextTests
{
    private const string Payload = "{\"type\":\"flowchart\",\"nodes\":[{\"id\":\"A\",\"label\":\"Signaal\"}]}";
    private const string Fence   = "```mermaid\n" + Payload + "\n```";

    private static FigureInfo Figure(string? caption = null, string? description = null, string? payload = Payload) =>
        new(Caption: caption, Offset: 0, PageNumber: 1, Id: "1.1", Elements: [],
            Description: description, Kind: "mermaid", Payload: payload);

    [TestMethod]
    public void TheCaptionWins_WhenThereIsOne()
    {
        var figures = new[] { Figure(caption: "Stepped Care Triageproces VGZ", description: "Flowchart with many boxes.") };

        Assert.AreEqual("Stepped Care Triageproces VGZ", DiagramContext.Resolve(Fence, figures));
    }

    [TestMethod]
    public void WithoutACaption_TheDescriptionIsUsed_CappedByTokens()
    {
        var description = Prose(200, "beschrijving");
        var figures     = new[] { Figure(description: description) };

        var context = DiagramContext.Resolve(Fence, figures);

        Assert.IsNotNull(context);
        Assert.IsTrue(TokenCounter.Count(context) <= DiagramContext.DescriptionTokenCap, "capped at " + DiagramContext.DescriptionTokenCap + " tokens, got " + TokenCounter.Count(context));
        StringAssert.StartsWith(description, context!, "a prefix of the description, not a rewrite");
        Assert.IsTrue(context.Length < description.Length);
    }

    [TestMethod]
    public void AShortDescription_ComesBackWhole()
    {
        var figures = new[] { Figure(description: "Stroomschema triage.") };

        Assert.AreEqual("Stroomschema triage.", DiagramContext.Resolve(Fence, figures));
    }

    [TestMethod]
    public void NoFigure_NoPayloadMatch_OrNeitherCaptionNorDescription_IsNull()
    {
        Assert.IsNull(DiagramContext.Resolve(Fence, figures: null), "no figures at all");
        Assert.IsNull(DiagramContext.Resolve(Fence, []), "empty figure list");

        // One character off - the 82 unmatched fences of run 260921/1 are this case. Exact
        // equality, no approximation: absent is counted, not guessed.
        Assert.IsNull(DiagramContext.Resolve(Fence, [Figure(caption: "X", payload: Payload.Replace("Signaal", "Signal"))]));

        Assert.IsNull(DiagramContext.Resolve(Fence, [Figure(payload: Payload)]), "a matched figure with nothing to say");
        Assert.IsNull(DiagramContext.Resolve(Fence, [Figure(caption: "  ", description: "", payload: Payload)]), "blank counts as nothing");
    }

    [TestMethod]
    public void TheMatchIgnoresSurroundingWhitespaceOnly()
    {
        var figures = new[] { Figure(caption: "Titel", payload: "  " + Payload + "\n") };

        Assert.AreEqual("Titel", DiagramContext.Resolve(Fence, figures));
    }

    [TestMethod]
    public void ForChunk_FindsTheFenceTheChunkStartsIn()
    {
        var before  = Prose(30) + "\n\n";
        var content = before + Fence + "\n\nNa.\n";
        var fences  = DiagramMarkup.Fences(content);
        var figures = new[] { Figure(caption: "Titel") };

        Assert.AreEqual("Titel", DiagramContext.ForChunk(content, fences, before.Length, figures),      "the first fragment starts at the opener");
        Assert.AreEqual("Titel", DiagramContext.ForChunk(content, fences, before.Length + 20, figures), "a later fragment starts inside the body");
        Assert.IsNull(DiagramContext.ForChunk(content, fences, 0, figures),                              "prose before the fence");
        Assert.IsNull(DiagramContext.ForChunk(content, fences, content.Length - 2, figures),             "prose after it");
    }
}
