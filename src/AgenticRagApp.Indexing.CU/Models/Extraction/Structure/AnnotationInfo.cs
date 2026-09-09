namespace AgenticRagApp.Indexing.CU.Models;

// One document annotation (highlight, underline, comment thread, ...) as CU reports it typed
// (DocumentContent.Annotations). Mapped by CuAnnotationHelper.
//
// Comments are flattened to "author: message" strings rather than a nested record: the two
// consumers are chunk metadata (a string list on the index row) and the report, and neither
// re-splits them. Kind is the service's own DocumentAnnotationKind value as a string, kept
// verbatim for the same traceability reason SectionInfo keeps its raw pointer refs.
//
// Offset is the annotation's first span's anchor into the markdown (utf16 - the SDK sends
// it, CUHelper checks the echo), nullable per this folder's convention: null means the service
// reported no span, never 0.
public sealed record AnnotationInfo(
    string?               Id,
    string                Kind,
    string?               Author,
    IReadOnlyList<string> Comments,
    int?                  Offset,
    int                   PageNumber,
    IReadOnlyList<string> Tags);
