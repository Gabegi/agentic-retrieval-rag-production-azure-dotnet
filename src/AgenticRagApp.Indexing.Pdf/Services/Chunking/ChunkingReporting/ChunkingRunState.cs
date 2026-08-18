using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Utils;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Everything the chunking run knows about itself, in one object.
//
// This exists so ChunkDocumentsAsync does not: it used to declare twelve locals before its own
// try block - outcomes, identity, headingSummary, stats, failedAtStage, error, four heading
// counters, the residue total - every one of them there only to be read by the report at the
// end. That is the reporting slope the step-5 TODO named. The method now holds ONE of these and
// makes ONE call into ChunkingReporter.
//
// Deliberately mutable and deliberately dumb: it accumulates facts and answers questions about
// them. It never formats, never logs and never writes - ChunkingReporter does all three, from
// this. The division is what lets a run that throws half way through still report: whatever was
// accumulated before the throw is already in hand.
public sealed class ChunkingRunState
{
    private readonly Dictionary<string, DocumentRunFacts> _facts = new(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, DocumentFamily> _families        = new Dictionary<string, DocumentFamily>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string>         _vectorSources   = new Dictionary<string, string>(StringComparer.Ordinal);
    private IReadOnlyList<string>                       _identitySkipped = [];

    // The first document-level failure, kept whole so the stage's eventual exception can carry
    // it as an inner exception rather than only its message.
    private Exception? _firstDocumentFailure;

    // chunks is the service's own list, held by reference rather than copied: the service keeps
    // adding to it as the loop runs, and the report wants whatever is in it at the end - which
    // on a run that throws is not the same thing as "all of them".
    public ChunkingRunState(
        IReadOnlyList<PdfExtractionDocument> docs,
        IReadOnlyList<ChunkObject>           chunks,
        string?                              instanceId,
        DateTimeOffset                       startedAt)
    {
        Docs       = docs;
        Chunks     = chunks;
        InstanceId = instanceId;
        StartedAt  = startedAt;
    }

    public IReadOnlyList<PdfExtractionDocument> Docs       { get; }
    public IReadOnlyList<ChunkObject>           Chunks     { get; }
    public string?                              InstanceId { get; }
    public DateTimeOffset                       StartedAt  { get; }

    // How far the stage got. Null once it finished; whatever it last was when an exception
    // fires is what the report names as the failure point.
    public string? Stage { get; set; } = "identity-resolution";

    public string?                        Error    { get; private set; }
    public IdentityResolutionDiagnostics? Identity { get; private set; }
    public ChunkingStageMetrics?          Stats    { get; set; }

    // -- Run totals ---------------------------------------------------------
    // Accumulated per document as the loop runs rather than recovered from the chunks
    // afterwards: a document whose every heading failed to locate is exactly the case the >2%
    // escalation exists to catch, and it is also the likeliest document to emit no chunks at
    // all - so counters recovered from the chunks would drop it.

    public int HeadingsTotal         { get; private set; }
    public int HeadingsLocated       { get; private set; }
    public int PairedHeadingsMerged  { get; private set; }
    public int HeadingsWithoutOffset { get; private set; }
    public int ResidueDropped        { get; private set; }

    public IReadOnlyCollection<DocumentRunFacts> DocumentFacts => _facts.Values;

    public IReadOnlyList<string> FailedSourceIds =>
        _facts.Values.Where(f => f.Outcome == "failed").Select(f => f.SourceId).ToList();

    // -- What the service reports as it goes --------------------------------

    public void IdentityResolved(IdentityResolutionResult resolved)
    {
        Identity         = resolved.Diagnostics;
        _families        = resolved.Families;
        _vectorSources   = resolved.IdentityVectorSourceOf;
        _identitySkipped = resolved.Diagnostics.SkippedEmptyIdentity;
    }

    // Route 1 only. The recursive route deliberately does not use whatever headings a document
    // has, so nothing is recorded for it - reporting those headings as unlocated would fill the
    // failure metric with headings that were never attempted.
    public void HeadingsLocatedFor(PdfExtractionDocument doc, HeadingLocationResult located)
    {
        HeadingsTotal         += located.HeadingsTotal;
        HeadingsLocated       += located.HeadingsLocated;
        PairedHeadingsMerged  += located.PairedHeadingsMerged;
        HeadingsWithoutOffset += located.HeadingsWithoutOffset;

        var facts = FactsFor(doc);
        facts.HeadingsTotal         = located.HeadingsTotal;
        facts.HeadingsLocated       = located.HeadingsLocated;
        facts.HeadingsWithoutOffset = located.HeadingsWithoutOffset;
    }

    // One document survived the whole of steps 2-4. kept is what is actually indexed; dropped is
    // what the minimum-content rule discarded on the way, recorded per document because "which
    // file is shedding residue" is the question that gets asked, not "how much did the run shed".
    public void Chunked(
        PdfExtractionDocument doc, IReadOnlyList<ChunkObject> kept, int dropped, string route)
    {
        ResidueDropped += dropped;

        var facts = FactsFor(doc);
        facts.Route          = route;
        facts.Chunks         = kept;
        facts.ResidueDropped = dropped;

        // identity_skipped is reserved for a document that produced nothing AND had nothing to
        // resolve an identity from. A document with no title and no headings but with content
        // still chunks - the recursive route needs neither - and reports "chunked"; that its
        // FamilyId is null is what says identity was skipped for it.
        var identitySkipped = _identitySkipped.Contains(doc.SourceId, StringComparer.Ordinal);

        facts.Outcome = kept.Count > 0 ? "chunked"
                      : identitySkipped ? "identity_skipped"
                      : "zero_chunks";

        facts.Reason = kept.Count > 0   ? null
                     : dropped > 0      ? $"every cut ({dropped}) was dropped by the minimum-content rule"
                     : identitySkipped  ? "no title, no headings and no content to cut"
                     : "the route produced no cuts";
    }

    // Captured rather than propagated: every other document is still chunked and reported, and
    // the stage fails once, at the end - see ThrowIfAnyDocumentFailed. A partial result is never
    // silently indexed, and a single bad document never costs the corpus its report.
    public void DocumentFailed(PdfExtractionDocument doc, Exception ex)
    {
        _firstDocumentFailure ??= ex;

        var facts = FactsFor(doc);
        facts.Outcome   = "failed";
        facts.Reason    = ex.Message;
        facts.Exception = ex;
        facts.Chunks    = [];
    }

    public void Threw(Exception ex) => Error = ex.ToString();

    // Named "chunking" rather than left at "metrics": metrics ran fine, and the stage that
    // actually failed is the one whose rows say "failed".
    public void ThrowIfAnyDocumentFailed()
    {
        var failed = FailedSourceIds;
        if (failed.Count == 0) return;

        Stage = "chunking";
        throw new InvalidOperationException(
            $"{failed.Count} of {Docs.Count} document(s) failed to chunk: {string.Join(", ", failed)}. " +
            "Every other document was chunked and reported; see the chunking run report for the " +
            "per-document reason.",
            _firstDocumentFailure);
    }

    // -- What the reporter asks back ----------------------------------------

    public DocumentFamily?  FamilyOf(string sourceId)       => _families.GetValueOrDefault(sourceId);
    public string?          VectorSourceOf(string sourceId) => _vectorSources.GetValueOrDefault(sourceId);
    public DocumentRunFacts? FactsOrNull(string sourceId)   => _facts.GetValueOrDefault(sourceId);

    // A family of one is not a near-duplicate group, and this flag is the only thing that
    // distinguishes the two on a row - FamilyId is present either way.
    public bool IsInMultiMemberFamily(string sourceId)
    {
        var familyId = FamilyOf(sourceId)?.FamilyId;
        if (familyId is null) return false;

        return _families.Values.Count(f => string.Equals(f.FamilyId, familyId, StringComparison.Ordinal)) > 1;
    }

    private DocumentRunFacts FactsFor(PdfExtractionDocument doc)
    {
        if (_facts.TryGetValue(doc.SourceId, out var existing)) return existing;

        var facts = new DocumentRunFacts(doc.SourceId);
        _facts[doc.SourceId] = facts;
        return facts;
    }
}

// One document's run, as the loop saw it. Half of these are set on route 1 only, which is why
// they default to zero rather than to null - a recursive document genuinely located zero
// headings out of zero attempted.
public sealed class DocumentRunFacts(string sourceId)
{
    public string SourceId { get; } = sourceId;

    public string? Route   { get; set; }
    public string  Outcome { get; set; } = "not_reached";
    public string? Reason  { get; set; }

    // Kept whole rather than reduced to a count here: the row wants token percentiles, the
    // above-ceiling count and the section count, and all three come off the chunks themselves.
    public IReadOnlyList<ChunkObject> Chunks { get; set; } = [];

    public int ResidueDropped        { get; set; }
    public int HeadingsTotal         { get; set; }
    public int HeadingsLocated       { get; set; }
    public int HeadingsWithoutOffset { get; set; }

    // The failure itself, for the log line. The row carries only its message.
    public Exception? Exception { get; set; }
}
