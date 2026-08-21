using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// The ladder's stop condition: did this level's cut produce pieces that all fit?
public static class CeilingCheck
{
    // An EMPTY result deliberately reports false. A cut level that produced nothing has not
    // succeeded - it has lost the text - and reporting true there would end the cascade with
    // zero pieces and no way to tell that from a block that genuinely had no content. Falling
    // through costs one wasted level and reaches HardCut, which always produces something.
    public static bool AllFit(IReadOnlyList<ContentPiece> pieces, int ceiling) =>
        pieces.Count > 0 && pieces.All(piece => TokenEstimator.Estimate(piece.Text) <= ceiling);
}
