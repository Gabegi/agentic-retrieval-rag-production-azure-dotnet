namespace AgenticRagApp.Indexing.CU.Models;

// One PDF blob's facts from a cheap container listing (name + LastModified + ContentLength +
// custom metadata) - no download, no analyze call. ExtractionService builds the full listing to
// diff against the index; only the entries it decides are new/updated get passed on to
// ExtractionService's extraction loop, which uses this same data instead of listing the
// container a second time.
//
// Zenya metadata rode here until 2026-08-26, when it was removed (cuhelper-typed-structure-plan.md):
// no blob carried the zenya_* keys and nothing was going to set them. That changed on 2026-09-11:
// the ZenyaSync tool writes zenya_* metadata on every blob in the zenya-documents container
// (Clients/Zenya/Sync/ZenyaBlobLayout, D185), and since 2026-09-21 it writes every field the
// Zenya document DTO carries (D204 §9). Revived here the same day, as the whole decoded record
// rather than four loose strings: the listing already returned the metadata dictionary and the
// diff discarded it. Null on a blob the sync did not write (the entire "protocols" corpus), so
// nothing downstream changes until the indexer is pointed at zenya-documents (D202 §6).
//
// Deliberately NOT a diff input yet. CompareSourceListingToIndex still compares LastModified;
// switching it to Zenya.Version is D202's design and its own change.
public record PdfBlobInfo(DateTimeOffset LastModified, long? ContentLength, ZenyaMetadata? Zenya = null);
