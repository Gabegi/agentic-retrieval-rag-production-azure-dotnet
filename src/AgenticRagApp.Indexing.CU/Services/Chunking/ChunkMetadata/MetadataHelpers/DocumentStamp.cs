using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Scope 1: everything that is a property of the DOCUMENT rather than of any one cut.
//
// Read once per document and copied onto every chunk of it. Built as a record rather than
// stamped field-by-field in a loop for one reason: these values must be IDENTICAL across a
// document's chunks, and the only way to guarantee that is to compute them once. Two chunks of
// one file disagreeing about its family_id is not a bug anything downstream can detect.
//
// Denormalized on purpose. Every one of these could be looked up from the document id at query
// time, and none of them is - Search has no join, so a filter on domain_tag has to read it
// off the chunk row itself.
public sealed record DocumentStamp(
    string                DocumentId,
    string?               Title,
    string?               Language,
    string?               Author,
    string?               Route,
    string?               FamilyId,
    string?               DomainTag,
    IReadOnlyList<string> ConfusableWith,
    DateTimeOffset?       LastModifiedDate,
    DateTimeOffset?       CreatedAt,
    DateTimeOffset?       ModDate,
    int?                  PageCount,
    DateTimeOffset?       ValidFrom,
    DateTimeOffset?       ValidTo,
    string?               Version,

    // ── Source-system facts (2026-09-21, D204 §9) ──────────────────────────
    // Off doc.Zenya - the blob's zenya_* metadata, decoded by IndexDiffService. All null on
    // the manual corpus. Kept SEPARATE from the fields above that look like them, on purpose:
    //   SourceTitle    vs Title    - D182 holds the MissingTitle decision; not pre-empted here.
    //   SourceLanguage vs Language - Zenya's language FORMAT is unmeasured ("nl"? "Nederlands"?);
    //                                mixing it into a field whose vocabulary is ISO 639-1 would
    //                                corrupt the facet. Measured first, merged later, if ever.
    //   SourceVersion  vs Version  - D202 §6.2 decided this: the title-parsed "(Versie N)" and
    //                                Zenya's integer are different things.
    //   CheckDate      vs ValidTo  - "next due for review" is not "stops applying". Overloading
    //                                valid_to would be the same wrong-meaning mistake D202 rejected
    //                                for version, so check_date is its own field. This corrects
    //                                D204 §3a, which had mapped it onto valid_to.
    string?               SourceDocumentId,
    string?               SourceVersion,
    string?               SourceRevision,
    string?               SourceStatus,
    bool?                 SourceActive,
    string?               SourceTitle,
    string?               SourceLanguage,
    string?               QuickCode,
    string?               FolderPath,
    string?               FolderName,
    string?               SourceType,
    string?               SourceDocumentType,
    string?               Summary,
    DateTimeOffset?       CheckDate,
    IReadOnlyList<string> AttentionFlags,
    // Persons: on the chunk for traceability, never Search-indexed (D204 §3d) - the same
    // treatment Author has had all along.
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Authorizers,
    IReadOnlyList<string> DocumentAdministrators)
{
    // route is the strategy's own Name, passed in by ChunkingService - it is step 2's answer
    // and this class has no way to re-derive it. A size_class was stamped here too until
    // 2026-09-08, re-derived from a DocumentProfile that nothing produced - see ChunkObject.
    public static DocumentStamp From(PdfExtractionDocument doc, string route)
    {
        // Parsed once - the title answers three fields and the regexes are not free.
        var validity = DocumentValidityParser.Parse(doc.Title);
        var zenya    = doc.Zenya;

        return new DocumentStamp(
            DocumentId:       doc.SourceId,
            Title:            doc.Title,
            Language:         doc.Language,
            Author:           doc.Author,
            Route:            route,

            // All three ride in on doc.Family, attached by ChunkingService from step 1. Null
            // when the resolver produced no family - which is a real state (a document with
            // neither title nor headings), not a failure to look one up.
            FamilyId:         doc.Family?.FamilyId,
            DomainTag:        doc.Family?.DomainTag,
            ConfusableWith:   doc.Family?.ConfusableWith ?? [],

            LastModifiedDate: doc.LastModifiedDate,
            CreatedAt:        doc.CreatedAt,
            // The ONE place a Zenya value fills an existing slot. ModDate's documented meaning is
            // "when the content was actually last edited" - which is exactly Zenya's
            // last_modified_datetime - and its only previous producer (the PdfPig Info-dictionary
            // read) is gone, so this is a fact filling an empty field, not a substitution.
            ModDate:          doc.ModDate ?? zenya?.LastModified,
            PageCount:        doc.PageCount,

            // From the TITLE.
            ValidFrom:        validity.From,
            ValidTo:          validity.To,
            Version:          validity.Version,

            SourceDocumentId:   zenya?.DocumentId,
            SourceVersion:      zenya?.Version?.ToString(),
            SourceRevision:     zenya?.Revision?.ToString(),
            SourceStatus:       zenya?.Status,
            SourceActive:       zenya?.Active,
            SourceTitle:        zenya?.Title,
            SourceLanguage:     zenya?.Language,
            QuickCode:          zenya?.QuickCode,
            FolderPath:         zenya?.FolderPath,
            FolderName:         zenya?.FolderName,
            SourceType:         zenya?.Type,
            SourceDocumentType: zenya?.DocumentType,
            Summary:            zenya?.Summary,
            CheckDate:          zenya?.CheckDate,
            AttentionFlags:     zenya?.AttentionFlags ?? [],
            Authors:                zenya?.Authors ?? [],
            Authorizers:            zenya?.Authorizers ?? [],
            DocumentAdministrators: zenya?.DocumentAdministrators ?? []);
    }

    // No source_path: DocumentId already IS the blob name, and a second copy is a second thing
    // to keep in sync.
    public void StampOnto(ChunkMetadata metadata)
    {
        metadata.DocumentId       = DocumentId;
        metadata.Title            = Title;
        metadata.Language         = Language;
        metadata.Author           = Author;
        metadata.Route            = Route;

        metadata.FamilyId         = FamilyId;
        metadata.DomainTag        = DomainTag;
        metadata.ConfusableWith   = ConfusableWith;

        metadata.LastModifiedDate = LastModifiedDate;
        metadata.CreatedAt        = CreatedAt;
        metadata.ModDate          = ModDate;
        metadata.PageCount        = PageCount;

        metadata.ValidFrom        = ValidFrom;
        metadata.ValidTo          = ValidTo;
        metadata.Version          = Version;

        metadata.SourceDocumentId   = SourceDocumentId;
        metadata.SourceVersion      = SourceVersion;
        metadata.SourceRevision     = SourceRevision;
        metadata.SourceStatus       = SourceStatus;
        metadata.SourceActive       = SourceActive;
        metadata.SourceTitle        = SourceTitle;
        metadata.SourceLanguage     = SourceLanguage;
        metadata.QuickCode          = QuickCode;
        metadata.FolderPath         = FolderPath;
        metadata.FolderName         = FolderName;
        metadata.SourceType         = SourceType;
        metadata.SourceDocumentType = SourceDocumentType;
        metadata.Summary            = Summary;
        metadata.CheckDate          = CheckDate;
        metadata.AttentionFlags     = AttentionFlags;
        metadata.Authors                = Authors;
        metadata.Authorizers            = Authorizers;
        metadata.DocumentAdministrators = DocumentAdministrators;
    }
}
