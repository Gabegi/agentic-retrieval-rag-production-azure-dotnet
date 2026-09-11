namespace AgenticRagApp.Indexing.CU.Models;

// One PDF blob's facts from a cheap container listing (name + LastModified + ContentLength) -
// no download, no analyze call. ExtractionService builds the full listing to diff against the
// index; only the entries it decides are new/updated get passed on to ExtractionService's
// extraction loop, which uses this same data instead of listing the container a second time.
//
// Zenya metadata rode here until 2026-08-26, when it was removed (cuhelper-typed-structure-plan.md):
// no blob carried the zenya_* keys and nothing was going to set them. That changed on 2026-09-11:
// the ZenyaSync tool writes zenya_* metadata on every blob in the zenya-documents container
// (Clients/Zenya/Sync/ZenyaBlobLayout, D185). Reviving the fields here belongs to the step that
// points this indexer at that container, not before.
public record PdfBlobInfo(DateTimeOffset LastModified, long? ContentLength);
