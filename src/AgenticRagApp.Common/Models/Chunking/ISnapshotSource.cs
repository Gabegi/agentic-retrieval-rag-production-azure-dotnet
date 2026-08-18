namespace AgenticRagApp.Common.Models;

// What SnapshotChunk.From needs from a chunk on top of the common IChunk shape.
// Implemented by each pipeline's own chunk type (e.g. ChunkObject) - Observability never
// references those types directly.
//
// The rule this interface has to satisfy: a snapshot is what an index would be rebuilt FROM, so
// it must carry every field that index holds. It previously carried nine, which meant a restore
// silently rebuilt an index without family_id or domain_tag - the two fields the knowledge agent
// filters on - so the wrong-population answer they exist to prevent came back with nothing
// marking it. Anything added to the index schema belongs here too.
//
// ContentHash is what lets a restore resolve a vector from the vector cache instead of paying to
// re-embed, which is why the vector itself is deliberately absent.
//
// Per-chunk structural payloads (table/figure objects) are equally deliberately absent: they are
// carried on a chunk but never Search-indexed, so a rebuild does not need them.
public interface ISnapshotSource : IChunk
{
    string?         Title            { get; }
    DateTimeOffset? LastModifiedDate { get; }
    string          ContentHash      { get; }

    // The derived context prepended to Content to form the embedded text. Carried for a reason
    // stronger than "the index holds it": ContentHash is computed from prefix + content, so a
    // restore that rebuilt a chunk without the prefix would recompute a hash different from the
    // one it just resolved the vector by - and nothing downstream could tell.
    string          Prefix           { get; }

    // -- Position and grain --------------------------------------------------

    string? SectionId    { get; }
    int     SectionIndex { get; }
    string  Grain        { get; }

    // The parent section's text, materialized onto the child. May have no producer in a given
    // pipeline; it is on the index schema, so it round-trips.
    string? ParentText   { get; }

    // -- Heading context and its provenance ----------------------------------
    // HeadingSource and HeadingLocated travel together. "Located" without a source reads as a
    // successful match in every aggregate - the contradiction that made unlocated headings
    // invisible - so restoring one without the other reintroduces it.

    string? HeadingPath    { get; }
    int     HeadingDepth   { get; }
    string? HeadingSource  { get; }
    bool    HeadingLocated { get; }

    bool    IsOverlap      { get; }

    // -- Pages ---------------------------------------------------------------

    int  PageEnd            { get; }
    bool PageExtractionFlag { get; }

    // -- Identity the retrieval side filters on ------------------------------
    // Not optional metadata: a restored index missing these answers confidently from the wrong
    // population, and no similarity score can flag that.

    string?               FamilyId       { get; }
    string?               DomainTag      { get; }
    IReadOnlyList<string> ConfusableWith { get; }
    string?               Population     { get; }
    string?               Language       { get; }

    // -- Counts and document lifecycle ---------------------------------------

    int             TokenCount { get; }

    // Page-scoped structural counts. Present because they are index fields that cannot be
    // recomputed on the far side: they derive from a per-chunk structural payload the snapshot
    // deliberately excludes, so without carrying them a restored row reports zero and empty.
    //
    // has_table is deliberately NOT here. It derives from Content, which the snapshot already
    // carries, so it recomputes correctly on restore - storing it would create a second copy
    // that could disagree with the text it describes.
    int                   TableCount     { get; }
    IReadOnlyList<string> FigureCaptions { get; }

    DateTimeOffset? CreatedAt { get; }
    DateTimeOffset? ModDate   { get; }
    int?            PageCount { get; }

    // When the document itself says it applies, and which version of it this is - parsed from
    // the title by the producing pipeline. Index fields with no other source, so a restore that
    // dropped them would rebuild an index that cannot tell a current CAO from a superseded one.
    DateTimeOffset? ValidFrom { get; }
    DateTimeOffset? ValidTo   { get; }
    string?         Version   { get; }

    string? ZenyaDocumentId { get; }
    string? ZenyaVersion    { get; }
    string? ZenyaStatus     { get; }
    string? ZenyaUrl        { get; }
}
