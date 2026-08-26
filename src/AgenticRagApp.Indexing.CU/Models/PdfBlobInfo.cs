namespace AgenticRagApp.Indexing.CU.Models;

// One PDF blob's facts from a cheap container listing (name + LastModified + ContentLength) -
// no download, no analyze call. ExtractionService builds the full listing to diff against the
// index; only the entries it decides are new/updated get passed on to ExtractionService's
// extraction loop, which uses this same data instead of listing the container a second time.
//
// Zenya metadata rode here until 2026-08-26, when it was removed outright (user decision,
// cuhelper-typed-structure-plan.md): no blob ever carried the zenya_* keys and no upload
// process was ever going to set them - the fields were a standing traceability red flag about
// a mechanism that did not exist.
public record PdfBlobInfo(DateTimeOffset LastModified, long? ContentLength);
