using Azure.AI.ContentUnderstanding;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

// Read-side checks on the Content Understanding account, plus the one write this side of the
// fence legitimately owns.
//
// THIS TYPE MUST NEVER CREATE OR UPDATE AN ANALYZER, even though the application as a whole now
// owns cap-pdf-layout's provisioning (decided 2026-08-21, see
// docs/2608/260821/cu-provisioning-ownership.md and the trailing note in
// infra/content_understanding.tf). Provisioning lives behind
// POST /api/content-understanding/provision; this type is the drift detector for it. One writer
// per object still holds - the point of the rule was never "Terraform specifically", it is that
// a verifier which repairs what it finds can only ever report success.
//
// So VerifyAnalyzerAsync is a GET and a comparison, nothing more. The account-wide default
// model-deployment mapping is the exception: it is a merge-PATCH against shared account-global
// state with no create/read/delete lifecycle and no drift visibility, which is why it is written
// here, opt-in, rather than owned as a provisioned object.
public interface IContentUnderstandingVerifier
{
    // GET-only. Reports what the account actually holds against what the pipeline needs, and never
    // writes. A missing analyzer is a reported failure, not something to fix by creating it -
    // the fix is the provisioning endpoint.
    Task<AnalyzerVerification> VerifyAnalyzerAsync(CancellationToken ct = default);

    // The one write. Opt-in rather than automatic: this is account-global shared state, and
    // content_understanding.tf notes that "a second analyzer, or another team, would silently
    // fight" whoever writes it. Safe to skip entirely - the analyzer pins its own
    // models.completion, which is what makes leaving the account defaults alone viable.
    Task<IReadOnlyDictionary<string, string>> EnsureDefaultsAsync(CancellationToken ct = default);

    // GET-only counterpart to the above.
    Task<IReadOnlyDictionary<string, string>> GetDefaultsAsync(CancellationToken ct = default);
}

// The drift report. Ok is false when anything the extraction pipeline depends on is absent or off,
// because every one of those failures is silent at analyze time: the call succeeds, is billed in
// full, and returns a document with the relevant collection empty.
public sealed record AnalyzerVerification(
    string                              AnalyzerId,
    bool                                Exists,
    ContentAnalyzerStatus?              Status,
    string?                             BaseAnalyzerId,
    IReadOnlyDictionary<string, string> Models,
    IReadOnlyList<string>               Problems)
{
    public bool Ok => Exists && Problems.Count == 0;
}
