using Azure.AI.ContentUnderstanding;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

// The WRITER half of CU bootstrap. IContentUnderstandingVerifier is the reader: it reports drift
// and deliberately never repairs it, so that a verify call cannot hide the thing it is meant to
// surface. Everything that creates or overwrites per-resource CU state lives behind this.
//
// Why the application owns this rather than Terraform (decided 2026-08-21, see
// infra/content_understanding.tf and docs/2608/260821/cu-provisioning-ownership.md): the two
// steps have to be ORDERED, the analyzer create is ASYNC, the effective state has to be READ
// BACK from the service rather than echoed, and the account is reachable over the Function App's
// private endpoint. One caller can do all four; two owners cannot.
public interface IContentUnderstandingProvisioner
{
    // Both bootstrap steps in the only order that works, then a verification of what actually
    // landed. Idempotent unless replaceAnalyzer is set: an analyzer that already exists and
    // verifies clean is left exactly as it is, which is what makes this safe to run on every
    // deploy.
    Task<ProvisioningResult> ProvisionAsync(bool replaceAnalyzer = false, CancellationToken ct = default);
}

// What provisioning did and what the account looks like now.
//
// AnalyzerAction is the field worth reading: it distinguishes "already correct, left alone" from
// "created" and from "replaced", which is the difference between a no-op deploy step and one that
// just rewrote shared state.
public sealed record ProvisioningResult(
    string                                 AnalyzerId,
    AnalyzerProvisioningAction             AnalyzerAction,
    ContentAnalyzerStatus?                 AnalyzerStatus,
    IReadOnlyDictionary<string, string>    Defaults,
    AnalyzerVerification                   Verification)
{
    // The analyzer is usable iff it reached Ready AND satisfies every assertion the verifier
    // makes. Both halves matter: a Ready analyzer with tableFormat=html is Ready and wrong.
    public bool Ok => AnalyzerStatus == ContentAnalyzerStatus.Ready && Verification.Ok;
}

public enum AnalyzerProvisioningAction
{
    // Existed and verified clean - not touched. The expected outcome of every deploy after the
    // first.
    LeftAsIs,

    // Did not exist; created from the definition in ContentUnderstandingProvisioner.
    Created,

    // Existed and was overwritten, either because it had drifted or because the caller asked.
    Replaced,
}
