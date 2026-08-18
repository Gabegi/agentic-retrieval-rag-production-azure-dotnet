using AgenticRagApp.Common.Models;
namespace AgenticRagApp.Observability.Reports;

// One row per chunk believed to be live in the Search index right now, for one doc-type
// pipeline. The rolling snapshot
// (pipeline-reports/{yyyy}/{MM}/{dd}/{ts}-snapshot-{source}-{instanceId}.json, found via the
// _latest-snapshot-{source}.json pointer - see SnapshotService)
// is the union of these across every run of that pipeline, not a per-run diff - and never
// mixes chunks from a different source.
//
// Carries everything a rebuild needs to bulk-upsert straight into a fresh index: the real fields
// the upload path sends to Search, plus ContentHash so the vector resolves from the vector cache
// without re-embedding. That claim was once made of a nine-field version of this record, which
// is how a restore came to rebuild an index with no family_id and no domain_tag - a live index
// answering from the wrong sector, with nothing marking it. When a field is added to the index
// schema, it is added here in the same change.
//
// Two exclusions, both deliberate: the vector (ContentHash resolves it) and the per-chunk
// structural payload (carried on a chunk, never Search-indexed).
public record SnapshotChunk(
    string Id,
    string DocumentId,
    string? Title,
    DateTimeOffset? LastModifiedDate,
    string Content,
    // Names follow IChunk's vocabulary (action-plan.md §4.6). This changes the snapshot's
    // wire format, so snapshots written before the rename do not restore - acceptable
    // because the field rename lands with a full index rebuild anyway, and a snapshot only
    // describes what is currently live in the index.
    string? HeadingText,
    int PageStart,
    int ChildIndex,
    string ContentHash,

    // The derived context prepended to Content to form the embedded text. ContentHash above is
    // computed from prefix + content, so a restore that rebuilt a chunk without this would
    // recompute a hash different from the one it just resolved the vector by.
    string Prefix,

    // Position and grain.
    string? SectionId,
    int SectionIndex,
    string Grain,
    string? ParentText,

    // Heading context. Source and Located travel together - "located" with no source reads as a
    // successful match in every aggregate.
    string? HeadingPath,
    int HeadingDepth,
    string? HeadingSource,
    bool HeadingLocated,

    bool IsOverlap,

    int PageEnd,
    bool PageExtractionFlag,

    // What retrieval filters on. A restore that drops these produces a confidently wrong index.
    string? FamilyId,
    string? DomainTag,
    IReadOnlyList<string> ConfusableWith,
    string? Population,
    string? Language,

    int TokenCount,

    // Page-scoped structural counts, carried because they cannot be recomputed on the far side -
    // they derive from a per-chunk structural payload this snapshot deliberately excludes.
    // has_table is absent on purpose: it derives from Content, which is right here, so it
    // recomputes correctly and a stored copy could only ever disagree with the text.
    int TableCount,
    IReadOnlyList<string> FigureCaptions,

    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModDate,
    int? PageCount,

    // Parsed from the document title by the producing pipeline, so nothing on the far side can
    // re-derive them once the title is all that is left.
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    string? Version,

    string? ZenyaDocumentId,
    string? ZenyaVersion,
    string? ZenyaStatus,
    string? ZenyaUrl)
{
    public static SnapshotChunk From<T>(T doc) where T : ISnapshotSource => new(
        Id:                 doc.Id,
        DocumentId:         doc.DocumentId,
        Title:              doc.Title,
        LastModifiedDate:   doc.LastModifiedDate,
        Content:            doc.Content,
        HeadingText:        doc.HeadingText,
        PageStart:          doc.PageStart,
        ChildIndex:         doc.ChildIndex,
        ContentHash:        doc.ContentHash,
        Prefix:             doc.Prefix,
        SectionId:          doc.SectionId,
        SectionIndex:       doc.SectionIndex,
        Grain:              doc.Grain,
        ParentText:         doc.ParentText,
        HeadingPath:        doc.HeadingPath,
        HeadingDepth:       doc.HeadingDepth,
        HeadingSource:      doc.HeadingSource,
        HeadingLocated:     doc.HeadingLocated,
        IsOverlap:          doc.IsOverlap,
        PageEnd:            doc.PageEnd,
        PageExtractionFlag: doc.PageExtractionFlag,
        FamilyId:           doc.FamilyId,
        DomainTag:          doc.DomainTag,
        ConfusableWith:     doc.ConfusableWith,
        Population:         doc.Population,
        Language:           doc.Language,
        TokenCount:         doc.TokenCount,
        TableCount:         doc.TableCount,
        FigureCaptions:     doc.FigureCaptions,
        CreatedAt:          doc.CreatedAt,
        ModDate:            doc.ModDate,
        PageCount:          doc.PageCount,
        ValidFrom:          doc.ValidFrom,
        ValidTo:            doc.ValidTo,
        Version:            doc.Version,
        ZenyaDocumentId:    doc.ZenyaDocumentId,
        ZenyaVersion:       doc.ZenyaVersion,
        ZenyaStatus:        doc.ZenyaStatus,
        ZenyaUrl:           doc.ZenyaUrl);
}
