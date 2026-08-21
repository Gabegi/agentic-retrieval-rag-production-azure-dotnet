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
// time, and none of them is - Search has no join, so a filter on domain_tag or a citation
// showing zenya_url has to read it off the chunk row itself.
public sealed record DocumentStamp(
    string                DocumentId,
    string?               Title,
    string?               Language,
    string?               Author,
    string?               Route,
    string?               SizeClass,
    string?               FamilyId,
    string?               DomainTag,
    IReadOnlyList<string> ConfusableWith,
    DateTimeOffset?       LastModifiedDate,
    DateTimeOffset?       CreatedAt,
    DateTimeOffset?       ModDate,
    int?                  PageCount,
    string?               ZenyaDocumentId,
    string?               ZenyaVersion,
    string?               ZenyaStatus,
    string?               ZenyaUrl,
    DateTimeOffset?       ValidFrom,
    DateTimeOffset?       ValidTo,
    string?               Version)
{
    // route is the strategy's own Name, passed in by ChunkingService - it is step 2's answer
    // and this class has no way to re-derive it. SizeClass IS re-derived here, from the same
    // classifier the rest of the pipeline uses, because the gate no longer computes one.
    public static DocumentStamp From(PdfExtractionDocument doc, string route)
    {
        // Parsed once - the title answers three fields and the regexes are not free.
        var validity = DocumentValidityParser.Parse(doc.Title);

        return new DocumentStamp(
            DocumentId:       doc.SourceId,
            Title:            doc.Title,
            Language:         doc.Language,
            Author:           doc.Author,
            Route:            route,
            SizeClass:        DocumentSizeClassifier.Classify(doc.Profile).ToString(),

            // All three ride in on doc.Family, attached by ChunkingService from step 1. Null
            // when the resolver produced no family - which is a real state (a document with
            // neither title nor headings), not a failure to look one up.
            FamilyId:         doc.Family?.FamilyId,
            DomainTag:        doc.Family?.DomainTag,
            ConfusableWith:   doc.Family?.ConfusableWith ?? [],

            LastModifiedDate: doc.LastModifiedDate,
            CreatedAt:        doc.CreatedAt,
            ModDate:          doc.ModDate,
            PageCount:        doc.PageCount,

            ZenyaDocumentId:  doc.ZenyaDocumentId,
            ZenyaVersion:     doc.ZenyaVersion,
            ZenyaStatus:      doc.ZenyaStatus,
            ZenyaUrl:         doc.ZenyaUrl,

            // From the TITLE, and unrelated to ZenyaVersion above, which is blob metadata.
            // A document can have both, and they can disagree.
            ValidFrom:        validity.From,
            ValidTo:          validity.To,
            Version:          validity.Version);
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
        metadata.SizeClass        = SizeClass;

        metadata.FamilyId         = FamilyId;
        metadata.DomainTag        = DomainTag;
        metadata.ConfusableWith   = ConfusableWith;

        metadata.LastModifiedDate = LastModifiedDate;
        metadata.CreatedAt        = CreatedAt;
        metadata.ModDate          = ModDate;
        metadata.PageCount        = PageCount;

        metadata.ZenyaDocumentId  = ZenyaDocumentId;
        metadata.ZenyaVersion     = ZenyaVersion;
        metadata.ZenyaStatus      = ZenyaStatus;
        metadata.ZenyaUrl         = ZenyaUrl;

        metadata.ValidFrom        = ValidFrom;
        metadata.ValidTo          = ValidTo;
        metadata.Version          = Version;
    }
}
