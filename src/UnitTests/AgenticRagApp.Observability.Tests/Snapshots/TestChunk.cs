using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Observability.Reports.Tests;

// The snapshot tests' stand-in for a real chunk type. Only the fields those tests actually
// assert on are constructor parameters. The rest of ISnapshotSource is satisfied with type
// defaults, because SnapshotService is being tested on pointer/diff behaviour - which fields a
// chunk carries is SnapshotChunk's contract, and it has its own tests. Kept as explicit members
// rather than left off so that ADDING a field to the interface breaks here loudly, which is
// exactly what the interface's own comment asks for.
//
// Shared between the snapshot test classes rather than nested in one of them, so that the
// SnapshotChunk factory below is the single place a schema change has to be answered.
internal sealed record TestChunk(
    string Id, string DocumentId, string? Title, DateTimeOffset? LastModifiedDate,
    string Content, string? HeadingText, int PageStart, int ChildIndex, string ContentHash)
    : ISnapshotSource
{
    public string  Prefix             => "";
    public string? SectionId          => null;
    public int     SectionIndex       => 0;
    public string  Grain              => "child";
    public string? ParentText         => null;

    public string? HeadingPath        => null;
    public int     HeadingDepth       => 0;
    public string? HeadingSource      => null;
    public bool    HeadingLocated     => false;
    public bool    IsOverlap          => false;

    public int     PageEnd            => 0;
    public bool    PageExtractionFlag => false;

    public string?               FamilyId       => null;
    public string?               DomainTag      => null;
    public IReadOnlyList<string> ConfusableWith => [];
    public string?               Population     => null;
    public string?               Language       => null;

    public int                   TokenCount     => 0;
    public int                   TableCount     => 0;
    public IReadOnlyList<string> FigureCaptions => [];

    public DateTimeOffset? CreatedAt => null;
    public DateTimeOffset? ModDate   => null;
    public int?            PageCount => null;
    public DateTimeOffset? ValidFrom => null;
    public DateTimeOffset? ValidTo   => null;
    public string?         Version   => null;

    public string? ZenyaDocumentId => null;
    public string? ZenyaVersion    => null;
    public string? ZenyaStatus     => null;
    public string? ZenyaUrl        => null;

    // SnapshotChunk has no optional parameters by design - the record's own comment says a
    // field added to the index schema is added here in the same change, because the nine-field
    // version of it is how a restore once rebuilt an index with no family_id and no domain_tag.
    // Building it through SnapshotChunk.From keeps these tests off the positional constructor
    // entirely, so the next field added to the schema lands here for free.
    public static SnapshotChunk Snapshot(
        string id, string documentId, string? title, string content, string contentHash) =>
        SnapshotChunk.From(new TestChunk(
            Id:               id,
            DocumentId:       documentId,
            Title:            title,
            LastModifiedDate: null,
            Content:          content,
            HeadingText:      null,
            PageStart:        0,
            ChildIndex:       0,
            ContentHash:      contentHash));
}
