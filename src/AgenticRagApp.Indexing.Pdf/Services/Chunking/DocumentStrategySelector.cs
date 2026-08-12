using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Axis 1 selection: picks one strategy per document from the three first-split decisions.
//
// The decisions are evaluated independently and in order, NOT collapsed into a single tier.
// That fusion was the original modelling error: a four-way enum decided "picture" from density
// and "large/medium/small" from a token count in the same ternary, so it could not express
// "large but unstructured" - such a document was routed Large, handed the heading rule, and
// failed silently.
//
// See docs/2608/260812/chunking_flow_summary.md for the flow this implements.
public sealed class DocumentStrategySelector
{
    private readonly IDocumentChunkingStrategy       _sectionCascade;
    private readonly IDocumentChunkingStrategy?      _wholeDocument;
    private readonly ILogger<DocumentStrategySelector> _logger;

    public DocumentStrategySelector(
        SectionCascadeStrategy sectionCascade,
        ILogger<DocumentStrategySelector> logger,
        IDocumentChunkingStrategy? wholeDocument = null)
    {
        _sectionCascade = sectionCascade;
        _wholeDocument  = wholeDocument;
        _logger         = logger;
    }

    // Null means "no strategy handles this document" - the extraction gate rejected it, and
    // there is no fallback implementation yet. Deliberately not silently routed to the cascade:
    // a document with no extractable text produces vector-residue chunks (the literal "£ £"
    // 30-character chunk in the corpus), and emitting those is worse than emitting nothing.
    public IDocumentChunkingStrategy? Select(PdfExtractionDocument doc)
    {
        // Gate 1 - extraction gate. Routing is null only when extraction did not compute it;
        // treated as "has content" so a missing measurement never silently drops a document.
        if (doc.Routing is { HasExtractableContent: false })
        {
            _logger.LogInformation(
                "{Source} failed the extraction gate ({CharsPerPage:F0} chars/page, {BytesPerChar:F0} bytes/char) " +
                "- no chunking strategy applies until the Content Understanding branch exists",
                doc.SourceId, doc.Routing.CharsPerPage, doc.Routing.BytesPerChar);

            return null;
        }

        // Gate 2 - parent grain. Null until the return bound is measured, so this falls
        // through to the cascade today. That is intended: the line it replaces was reasoned
        // and never verified, and it decides what gets stored - an explicit "not yet known"
        // beats a plausible number nothing checked.
        if (doc.Routing is { DocumentIsSafeReturnUnit: true } && _wholeDocument is not null)
            return _wholeDocument;

        return _sectionCascade;
    }
}
