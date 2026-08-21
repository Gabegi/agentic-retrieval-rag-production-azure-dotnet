using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

// AgenticRagApp.Indexing.Csv's own copy of the extraction-orchestrator contract — CSV's pipeline is
// fully self-contained, so this isn't shared with agentic-rag-app's PDF pipeline
// (which has its own identical-shaped copy). Both return the source-agnostic
// AgenticRagApp.Common.Models.ExtractionOutput.
public interface ICsvExtractionOrchestrator
{
    string Source { get; }  // e.g. "csv", "pdf"

    // overrideMagnitudeCheck: bypasses ONLY the magnitude-shift validation gate (a large,
    // legitimate import/removal), never the error-rate or reconciliation checks - those
    // indicate genuinely malformed data and must always block.
    Task<ExtractionOutput> ExtractDocumentsAsync(bool overrideMagnitudeCheck = false, CancellationToken ct = default);
}
