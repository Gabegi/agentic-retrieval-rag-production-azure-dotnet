using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Indexing.DI.Utils;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.DI.Services;

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

    // familyId -> how many documents resolved into it. Built once when identity resolves; see
    // IsInMultiMemberFamily for why it is not recomputed per row.
    private IReadOnlyDictionary<string, int>            _familySizes     = new Dictionary<string, int>(StringComparer.Ordinal);

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
    public int TocDropped            { get; private set; }

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

        // Counted once here, read per row by IsInMultiMemberFamily.
        _familySizes = _families.Values
            .GroupBy(f => f.FamilyId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
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

    // One document survived the whole of steps 2-4. kept is what is actually indexed, cutCount is
    // what the strategy returned before the minimum-content rule - the difference is residue, and
    // it is recorded per document because "which file is shedding residue" is the question that
    // gets asked, not "how much did the run shed".
    //
    // tocDropped is the part of that difference the TOC filter took, passed in rather than
    // recomputed: both rules run inside the service and only it knows which cut went to which.
    // Subtracted out so ResidueDropped keeps meaning what it has always meant - cuts too thin
    // to be content - and a TOC drop does not read as a document shedding junk.
    public void Chunked(
        PdfExtractionDocument doc, IReadOnlyList<ChunkObject> kept, int cutCount, string route,
        int tocDropped = 0)
    {
        var dropped = cutCount - kept.Count - tocDropped;
        ResidueDropped += dropped;
        TocDropped     += tocDropped;

        var facts = FactsFor(doc);
        facts.Route          = route;
        facts.Chunks         = kept;
        facts.CutCount       = cutCount;
        facts.ResidueDropped = dropped;
        facts.TocDropped     = tocDropped;

        // identity_skipped is reserved for a document that produced nothing AND had nothing to
        // resolve an identity from. A document with no title and no headings but with content
        // still chunks - the recursive route needs neither - and reports "chunked"; that its
        // FamilyId is null is what says identity was skipped for it.
        var identitySkipped = _identitySkipped.Contains(doc.SourceId, StringComparer.Ordinal);

        // A route-1 document that located NONE of its headings is reported separately, because
        // otherwise it is indistinguishable from one that honoured its structure.
        //
        // The gate counts doc.Headings.Count BEFORE location runs, so a document can pass on two
        // declared headings, locate zero, and have HeadingLocator hand back a single
        // whole-document section - which route 1 then chunks as one unnamed section and reports as
        // Strategy "DeclaredBoundary" with HeadingsLocated 0. The chunks are real and worth
        // keeping; what is wrong is the claim that they follow declared boundaries. Reading two
        // columns together would also show it, but Outcome exists so that one column can be
        // grepped, and this is the exact document the >5% per-document escalation was written to
        // catch.
        var unanchored = kept.Count > 0
                      && string.Equals(route, RouteNames.DeclaredBoundary, StringComparison.Ordinal)
                      && facts.HeadingsLocated == 0;

        facts.Outcome = unanchored     ? "chunked_unanchored"
                      : kept.Count > 0 ? "chunked"
                      : identitySkipped ? "identity_skipped"
                      : "zero_chunks";

        // Said in the report rather than left to be inferred from a zero, and only where there is
        // something to explain: a reason on a "chunked" row would read as a complaint about it.
        // A route that emitted nothing and a document whose every cut was residue are different
        // faults - the first is a routing or anchoring failure, the second an extraction one.
        facts.Reason = unanchored       ? $"took the declared-boundary route on {facts.HeadingsTotal} declared heading(s) "
                                          + "but located none of them, so the whole document was chunked as one unnamed section"
                     : kept.Count > 0   ? null
                     : cutCount == 0    ? "the strategy produced no cuts for this document"
                     : $"all {dropped} cut(s) were dropped by the minimum-content rule as vector residue";
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
    //
    // Reads the sizes counted once in IdentityResolved rather than scanning every family per
    // document: the reporter asks this for every row, so a scan here is O(documents x families)
    // for an answer that does not change after identity resolution has run.
    public bool IsInMultiMemberFamily(string sourceId)
    {
        var familyId = FamilyOf(sourceId)?.FamilyId;
        if (familyId is null) return false;

        return _familySizes.GetValueOrDefault(familyId) > 1;
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

    // What the strategy returned, before the minimum-content rule took its cut. Kept alongside
    // the survivors so "dropped 3 of 4" stays sayable from the row.
    public int CutCount              { get; set; }
    public int ResidueDropped        { get; set; }
    public int TocDropped            { get; set; }
    public int HeadingsTotal         { get; set; }
    public int HeadingsLocated       { get; set; }
    public int HeadingsWithoutOffset { get; set; }

    // The failure itself, for the log line. The row carries only its message.
    public Exception? Exception { get; set; }
}
