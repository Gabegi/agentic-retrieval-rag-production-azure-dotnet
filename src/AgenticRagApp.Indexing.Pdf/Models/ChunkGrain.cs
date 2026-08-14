namespace AgenticRagApp.Indexing.Pdf.Models;

// Which of the two cut grains a chunk is (action-plan.md §4.6).
//
// Plain string constants rather than an enum: this value round-trips through Azure AI
// Search as a string, and an enum would serialize as an integer unless every write path
// remembered a converter - the same trap DocumentProfile's now-deleted ChunkRoute fell
// into, where a stored number told a later reader nothing about what it meant.
//
// Deliberately explicit rather than inferred from "SectionId == Id": Q3 option 2 (parents
// indexed but not embedded) filters parents out of ranking on exactly this field, and
// inferring it would make that filter depend on a naming convention holding everywhere.
public static class ChunkGrain
{
    // The whole document is the retrieval unit - no parent/child split was worth making.
    public const string Document = "document";

    // A whole heading section. Carries its own text; children point at it via SectionId.
    public const string Parent = "parent";

    // A sub-split of a section, or a section small enough that it is its own only child.
    public const string Child = "child";
}
