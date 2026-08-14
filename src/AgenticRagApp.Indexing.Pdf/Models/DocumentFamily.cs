namespace AgenticRagApp.Indexing.Pdf.Models;

// Per-document identity result DocumentIdentityResolver resolves before chunking - the same value
// on every chunk of one document, same carry-along pattern as DocumentProfile. FamilyId
// groups near-duplicate documents (embedding-clustered on title + heading list); DomainTag
// is the GGZ/GHZ/VVT/V&V/VGZ filename-pattern read off the title; ConfusableWith is the
// separate title-distance check (docs/2608/260811/pre-chunking-action-items.md C3) - titles
// close enough to be mistaken for each other without being embedding-similar (Medido/Medimo),
// so deliberately not folded into FamilyId itself.
public sealed record DocumentFamily(
    string                   FamilyId,
    string?                  DomainTag,
    IReadOnlyList<string>    ConfusableWith);

// DocumentIdentityRecord - the persisted form of the above - lives with its store in
// AgenticRagApp.Infrastructure.Clients.DocumentIdentity: Infrastructure cannot reference this
// project, so the stored record has to sit on the storage side of that boundary. This type
// stays here because it is the per-chunk carry-along value, not the stored one.
